// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MetaphysicistExternalBuffingBrain.cs
//
// Last modified: 2026-10-06
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Components;
using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Nav;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     METAPHYSICIST EXTERNAL BUFFING BRAIN - the staged buff-first dance, driven from the
///     buffing side (PETBRAIN-DESIGN.md port order 4; the sequence is the owner's, per the
///     L50 froob walkthrough of 2026-10-05/06). One dance:
///       The stages are the owner's, verbatim (2026-10-06):
///       0. TRAVEL to the bots - the tells go nowhere from another playfield, and a dance
///          whose travel fails aborts BEFORE anything is torn down.
///       1. TERMINATE ALL BUFFS - every stage plans against real free NCU, from a clean slate.
///       2. GET THE NCU BUFF (the ncu tell) - "make sure you have NCU free first".
///       3. ATTACK PET: get its buffs, TERMINATE the attack pet, then cast it. The swap runs
///          only when the stack is verified in, and only this line's old pet dies - the rest
///          of the roster keeps fighting the whole dance.
///       4. CANCEL what the heal pet does not need (computed from the heal line's plan, with
///          the attack stage's buffs still up and counted).
///       5. HEAL PET: get its buffs, terminate it, recast it - same swap rule.
///       6. CANCEL what the support pet does not need (frees its NCU first).
///       7. SUPPORT PET: get its buffs (the MatMet mocham), terminate it, recast it.
///       8. PET BUFFS: cast the best learned pet nanos ON the pets - lines 216/217/225/810/
///          816/817/843, best StackingOrder per line among the learned and castable, no
///          overequip math; 810 on the support pet, the damage/initiative lines on the attack
///          pet, 816/817/843 on all pets; a pet already carrying a line at equal or better
///          stacking is skipped (owner, 2026-10-06).
///       9. FLOOR BUFFS and QoL: cancel all landed peak asks (the 3-minute wrangle exists to
///          be cancelled), then the cheapest 1hr+ set lifting every summoned pet to 80% of
///          ITS OWN tier's requirements - the tiers come from the cast snapshots, because the
///          pet never tells (owner: IMPORTANT) - then the comfort fill from conf
///          (PetQoLTells: long HoT, runspeed, essence, Omni-Med...).
///       10. DONE.
///     After the dance: the kept set renews when any of it nears its end - "rebuff at ~30min
///     left" (owner); the holds are 4h, the timer is read off me.Buffs. Obedience is
///     REVERSIBLE (pets persist, they just disobey below 80%, and obey again once the floor
///     is back) - so nothing here ever terminates; a failed renew costs temporary
///     disobedience and a retry. PetAutoBuff gates ASKING; an owner-started 'buffs pet'
///     session still gets the hold-around-and-summon treatment. All stages plan under
///     BuffCatalog.FreeNcu - NCU is the hard cap.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.ExternalBuffing, Profession.Metaphysicist)]
public sealed class MetaphysicistExternalBuffingBrain : ExternalBuffingBrain
{
    private enum PetLine
    {
        Attack,
        Heal,
        Mezz
    }

    // One row per line: the summon line (strain), the wire role it fills, and the log label.
    // Order = the dance's stage priority.
    private static readonly (PetLine Line, PetType Role, NanoLine Strain, string Label)[] Lines =
    {
        (PetLine.Attack, PetType.Attack, NanoLine.AttackPets, "attack"),
        (PetLine.Heal, PetType.Heal, NanoLine.HealPets, "heal"),
        (PetLine.Mezz, PetType.Support, NanoLine.SupportPets, "mezz"),
    };

    private enum Phase
    {
        Travel,
        TerminateBuffs,
        Ncu,
        Peak,
        PetBuffs,
        Obedience,
        Qol
    }

    private enum DanceStage
    {
        Idle,
        SessionOpen,
        WaitingPet,
        PetBuffing
    }

    // The Time&Space stat - the one skill every line shares; swap-cancels keep its buffs.
    private const int TsStat = 131;

    // Decide roughly once a second, not every frame.
    private const double DecideEverySec = 1.0;

    // Don't start a dance (or a renewal) more often than this (Engineer BuffRetrySec parity).
    private const double AskRetrySec = 300.0;

    // A staged summon gets this long to appear before the dance moves on without it.
    private const double WaitPetSec = 45.0;

    // While waiting, a bounced request (seated, recast window) is re-issued on this cadence.
    private const double ResummonEverySec = 12.0;
    private double _sinceResummon;

    // A pet-buff cast gets this long to land before the pump re-sends it.
    private const double PetBuffRecastSec = 4.0;

    // The renewal watch: check the kept set this often, renew what has this little left.
    private const double RenewCheckSec = 30.0;
    private const double RenewAtSeconds = 1800.0; // "~30min left"

    // Travel to the bots (ICC, 4a.1b): the whole dance gets this budget, "near" is this close.
    private const double TravelBudgetSec = 300.0;
    private const float NearBotReach = 25f;

    private readonly BrainBank _bank;
    private readonly BuffBotController _buffBot;
    private readonly BuffCatalog _catalog;
    private readonly AccountInfo _config;
    private readonly MovementController _movement;
    private readonly MissionController _mission;
    private readonly HealController _heal;

    private readonly Dictionary<PetLine, HashSet<int>> _summonIds = new();

    // The PET BUFF lines (owner, 2026-10-06): cast on the pets themselves, best stacking order
    // per line wins. 216 MP pet damage, 217 MP pet initiative, 225 pet short-term damage (4h),
    // 810 Shadowlands-only support-pet buff, 816 pet defensive, 817 pet DoT-resist, 843 pet
    // heal delta. 810 targets the support pet; the damage/initiative lines ride the attack
    // pet; 816/817/843 go on ALL pets.
    private static readonly int[] PetBuffLines = { 216, 217, 225, 810, 816, 817, 843 };

    // line -> the pack's nanos of that line (nano id + its StackingOrder, stat 551)
    private readonly Dictionary<int, List<(int NanoId, int Stacking)>> _petBuffNanos = new();

    // The pet-buff casts still to send: one cast at a time, drained while the dance holds.
    // An entry stays until its buff LANDS on the pet (same line at equal or better stacking) -
    // a cast lost to a seated body or a failed pay is re-sent when the pump can pay again.
    private sealed class PetBuffCast
    {
        public Identity Pet;
        public int NanoId;
        public int Line;
        public int Stacking;
        public double LastCastAt = -1;
    }

    private readonly List<PetBuffCast> _petBuffQueue = new();

    // The dance: a work queue of phases, executed one session (or skip) at a time.
    private readonly List<(Phase Phase, PetType Role)> _queue = new();
    private int _queueIndex = -1;
    private bool _inDance;
    private DanceStage _danceStage = DanceStage.Idle;
    private Phase _phase;
    private PetType _pendingRole;
    private double _waitDeadline;
    private readonly List<int> _peakAsked = new(); // every peak-stage id - the obedience stage cancels them
    private readonly List<int> _lastPeakAsked = new(); // the previous peak stage's ids - the swap-cancel
    private readonly HashSet<int> _keptIds = new(); // the post-dance set - the renewal watch

    // A foreign session ('buffs pet' owner command) while we are idle: hold around it.
    private bool _foreign;

    // A renewal session (kept-set top-up) - its end is not a queue step.
    private bool _renewing;

    private bool _danceDone;
    private double _sinceDecide;
    private double _sinceRenewCheck;
    private double _lastAskAt = -1e9;
    private double _travelDeadline = -1;
    private bool _travelGoalSet;
    private bool _claimed; // the arbiter is ours at ControlPriority.ExternalBuffing

    public MetaphysicistExternalBuffingBrain(ILogger<MetaphysicistExternalBuffingBrain> logger,
        ControlArbiter controlArbiter, BrainBank bank, BuffBotController buffBot, BuffCatalog catalog,
        AccountInfo config, MovementController movement, MissionController mission, HealController heal)
        : base(logger, controlArbiter)
    {
        _bank = bank;
        _buffBot = buffBot;
        _catalog = catalog;
        _config = config;
        _movement = movement;
        _mission = mission;
        _heal = heal;
        ScanSummonLines();
        ScanPetBuffLines();
        _logger.LogInformation(
            $"EXTBUFF: MP buff watcher up - PetAutoBuff {(_config.PetAutoBuff ? "on" : "off (the dance stays owner-driven; 'buffs pet' still works)")}, " +
            $"bots: {_catalog.BotName ?? _config.BuffBotName ?? "(none)"}, {_catalog.Buffs.Count} menu entries.");
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        var pet = _bank.Pet;
        if (pet == null)
        {
            return false; // no pet brain this session - nothing to hand the buffs to
        }

        // ExternalBuffing sits ABOVE mission (400 over 300): the buff-up runs before a mission
        // goes, and a dance already in flight makes the run wait at its lower priority. The
        // mirror side of that rank: while a run IS active we claim nothing and start nothing -
        // our re-asserting claim would otherwise strip the run's arbiter mid-blitz, and a
        // renewal would yank the bot from the mission to ICC. Buffing happens BEFORE missions -
        // and DURING one the pet cadence is the fight's business again: release the hold so a
        // lost pet refills normally mid-blitz.
        if (_mission.Active && !_inDance && _danceStage == DanceStage.Idle && !_foreign)
        {
            pet.SetSummonHold(false);
            return Done();
        }

        // A buff session is open. Ours: the queue waits for it. A foreign one ('buffs pet'
        // while we were idle): hold the pet cadence around it, hand back with a summon pass.
        if (_buffBot.Active)
        {
            if (_danceStage == DanceStage.Idle && !_inDance && !_renewing && !_foreign)
            {
                _foreign = true;
                pet.SetSummonHold(true);
                _logger.LogInformation("EXTBUFF: foreign buff session opened - holding the pet cadence around it.");
            }

            return Claim();
        }

        if (_foreign)
        {
            _foreign = false;
            pet.SetSummonHold(false);
            pet.RequestSummon(); // whatever line is missing, best castable
            return Done();
        }

        // A dance/renewal session just ended - run its after-step.
        if (_danceStage == DanceStage.SessionOpen)
        {
            _danceStage = DanceStage.Idle;
            if (_renewing)
            {
                _renewing = false;
                _logger.LogInformation("EXTBUFF: renewal session closed - the kept set is topped up.");
            }
            else
            {
                AfterSession(me, pet);
            }
        }

        // The PET BUFF stage's cast pump: one cast at a time, and an entry stays queued until
        // its buff LANDS on the pet. When the pool cannot pay the next cast, the pump pauses
        // and asks the heal side for a recharger rest (owner, 2026-10-06: the drain is faster
        // than the percentage triggers - handled manually here); while seated nothing casts.
        if (_danceStage == DanceStage.PetBuffing)
        {
            if (_petBuffQueue.Count == 0)
            {
                _logger.LogInformation("EXTBUFF: pet buffs done.");
                _danceStage = DanceStage.Idle;
                Advance(me, pet);
                return Claim();
            }

            if (me.IsCasting)
            {
                return Claim(); // one cast at a time
            }

            var entry = _petBuffQueue[0];
            var target = me.Pets.FirstOrDefault(p => p.Identity == entry.Pet);
            if (target == null)
            {
                _petBuffQueue.RemoveAt(0); // the pet is gone - its buffs do not matter
                return Claim();
            }

            if (target.Buffs.Any(b => b?.NanoItem != null && b.NanoItem.NanoLine == (NanoLine)entry.Line
                                      && b.NanoItem.StackingOrder >= entry.Stacking))
            {
                _petBuffQueue.RemoveAt(0); // landed
                return Claim();
            }

            if (!ItemData.Find(entry.NanoId, out NanoItem ni) || ni == null)
            {
                _petBuffQueue.RemoveAt(0); // lost its item data - never casts
                return Claim();
            }

            var cost = AdjustedCastCost(me, ni);
            if (me.TryGetStat(Stat.CurrentNano, out var pool) && pool < cost)
            {
                _heal.EnsureNano(cost, "pet buffs");
                return Claim(); // paused - the heal side sits and recharges; the pump resumes paid
            }

            if (_heal.Resting)
            {
                return Claim(); // seated - the server refuses casts from a seated body
            }

            if (entry.LastCastAt >= 0 && _clock - entry.LastCastAt < PetBuffRecastSec)
            {
                return Claim(); // give the cast time to land before re-sending
            }

            entry.LastCastAt = _clock;
            me.Cast(entry.Pet, entry.NanoId);
            _logger.LogInformation($"EXTBUFF: casting {NanoLibrary.NameOf(entry.NanoId)} on '{target.Name}'.");
            return Claim();
        }

        // A staged summon is on its way - wait for it, RE-ISSUING the request when it bounced
        // (a sit, a recast window swallow the one-shot cast silently - owner, 2026-10-06: the
        // pet then "never appeared" and the stage starved). The next stage's NCU math needs
        // the pet's NCU spent, and the next cancel must not strand the summon.
        if (_danceStage == DanceStage.WaitingPet)
        {
            if (me.Pets.Any(p => p.Role == _pendingRole))
            {
                Advance(me, pet);
            }
            else if (_clock >= _waitDeadline)
            {
                _logger.LogWarning($"EXTBUFF: the {_pendingRole} pet never appeared - the dance moves on.");
                Advance(me, pet);
            }
            else
            {
                _sinceResummon += dt;
                if (_sinceResummon >= ResummonEverySec)
                {
                    _sinceResummon = 0;
                    _logger.LogInformation($"EXTBUFF: the {_pendingRole} pet is not up yet - re-requesting the summon.");
                    pet.RequestSummon(_pendingRole);
                }

                return Claim();
            }
        }

        // The travel phase re-runs every tick until we are near the bots (the movement ticks on
        // its own; the brain only sets goals and polls).
        if (_inDance && _phase == Phase.Travel && _danceStage == DanceStage.Idle)
        {
            RunTravelStage(me, pet);
        }

        // Idle: the renewal watch, then the dance trigger - and the PETLESS AUTHORITY first of
        // all (owner, 2026-10-06: the pet brain cast the attack pet the same second the dance
        // started arranging buffs). While PetAutoBuff has us watching - un-teamed, no run
        // Idle: the renewal watch, then the dance trigger. The anti-race protection is NOT a
        // hold here any more (owner, 2026-10-06: a dance that ends empty-handed must hand the
        // petless lines back to the pet brain's cadence) - the pet brain runs a short
        // petless-grace instead, which this brain's one-second decide window always wins.
        if (_danceStage == DanceStage.Idle && !_inDance)
        {
            TryRenewal(me, dt);
            if (_danceStage == DanceStage.Idle && !_inDance)
            {
                _sinceDecide += dt;
                if (_sinceDecide >= DecideEverySec)
                {
                    _sinceDecide = 0;
                    TryStartDance(me, pet);
                }
            }
        }

        return _inDance || _danceStage != DanceStage.Idle ? Claim() : Done();
    }

    // ---- The dance --------------------------------------------------------------------------

    /// <summary>
    ///     The dance trigger is a CONTINUOUS buff-state assessment, not an event: any line whose
    ///     best learned summon's top template is out of reach on the current stats (a missing
    ///     line, OR an up pet that is weaker than it could be), or an up pet below its obedience
    ///     floor - the desired buffs ran out, or he logged on without them (owner, 2026-10-06).
    ///     Every dance REBUILDS the roster: the pets are terminated first (a summon while one is
    ///     up is blocked by the pet-limit gate - they don't overwrite), so the peak stages cast
    ///     fresh at peak stats. Up-pet floors for the trigger come from the cast snapshot when
    ///     the dance summoned them, else from matching the pet's wire LEVEL against the line's
    ///     branch tables (template ≈ pet level) - a login with pets already out has no snapshot.
    /// </summary>
    private void TryStartDance(LocalPlayer me, PetBrain pet)
    {
        if (!_config.PetAutoBuff)
        {
            return; // the master switch; the mode is logged once at construction
        }

        if (Team.IsInTeam && !_buffBot.TeamedWithBot)
        {
            return; // the handshake needs us un-teamed - UNLESS the standing team IS the bot's
                    // (its early invite; the dance tells its requests over that team)
        }

        if (_clock - _lastAskAt < AskRetrySec)
        {
            return; // asked recently - give the last outcome time to show
        }

        var (needed, why) = AssessBuffState(me);
        if (!needed)
        {
            return;
        }

        _inDance = true;
        pet.SetSummonHold(true); // the auto cadence stays shut; the dance summons through requests
        _queue.Clear();

        // TERMINATE-BUFFS clears OUR OWN buffs so the peak stages plan against real free NCU,
        // from a clean slate. Travel comes FIRST (owner, 2026-10-06) - a dance whose travel
        // fails aborts before anything is torn down. The PETS are not terminated up front any
        // more: each line's old pet dies only at its own swap, at the summon moment with that
        // line's peak stack verified in (owner, 2026-10-06) - the rest of the roster keeps
        // fighting the whole dance.
        var peakLines = new List<PetType>();
        foreach (var (line, role, _, _) in Lines)
        {
            if (BestLearnedNano(me, line) != null)
            {
                peakLines.Add(role); // lines with nothing learned are skipped at run time too
            }
        }

        _queue.Add((Phase.Travel, default));
        _queue.Add((Phase.TerminateBuffs, default));

        _queue.Add((Phase.Ncu, default));
        _queue.AddRange(peakLines.Select(role => (Phase.Peak, role)));
        _queue.Add((Phase.PetBuffs, default));
        _queue.Add((Phase.Obedience, default));
        _queue.Add((Phase.Qol, default));
        _queueIndex = -1;
        _peakAsked.Clear();
        _lastPeakAsked.Clear();
        _lastAskAt = _clock;
        _logger.LogInformation(
            $"EXTBUFF: dance starting - {why}. Bot: {_catalog.BotName ?? _config.BuffBotName ?? "(none)"}, spot: " +
            (_catalog.BotLocation.HasValue ? Zoning.Name(_catalog.BotLocation.Value.Playfield) : "(unmapped)") +
            ".");
        Advance(me, pet);
    }

    /// <summary>
    ///     The buff-state assessment behind the trigger, as list of reasons: any line (missing
    ///     OR up) whose best learned summon's top template is out of reach, and any up pet below
    ///     its obedience floor. Floors prefer the cast snapshot (the dance's own summons) and
    ///     fall back to pet-level matching (login-time pets).
    /// </summary>
    private (bool Needed, string Why) AssessBuffState(LocalPlayer me)
    {
        var reasons = new List<string>();
        foreach (var (line, role, _, label) in Lines)
        {
            var nanoId = BestLearnedNano(me, line);
            if (nanoId == null)
            {
                continue; // nothing learned for the line - nothing to reach for
            }

            var branches = NanoLibrary.Find(nanoId.Value)?.Summons;
            if (branches == null || branches.Count == 0)
            {
                continue; // no branch table in the pack - nothing the dance could add
            }

            if (!TopReachable(me, branches))
            {
                var up = me.Pets.Any(p => p.Role == role);
                reasons.Add($"{label} pet {(up ? "below its best template" : "missing")}");
            }
        }

        var pet = _bank.Pet!;
        foreach (var kv in FloorTotals(me, pet))
        {
            if (!me.TryGetStat((Stat)kv.Key, out var v) || v < kv.Value)
            {
                reasons.Add($"obedience floor short on stat {kv.Key}");
            }
        }

        return (reasons.Count > 0, string.Join("; ", reasons));
    }

    /// <summary>
    ///     The union of the up pets' obedience floors: the cast snapshot when we summoned them,
    ///     else the pet's wire LEVEL matched against the line's branch tables (template ≈ level -
    ///     how a login with pets already out gets its floors).
    /// </summary>
    private Dictionary<int, int> FloorTotals(LocalPlayer me, PetBrain pet)
    {
        var totals = new Dictionary<int, int>();
        foreach (var (line, role, _, _) in Lines)
        {
            if (!me.Pets.Any(p => p.Role == role))
            {
                continue;
            }

            var floor = pet.ObedienceFloor(role) ?? FloorFromPetLevel(me, role, line);
            if (floor == null)
            {
                continue;
            }

            foreach (var kv in floor)
            {
                totals[kv.Key] = Math.Max(totals.GetValueOrDefault(kv.Key), kv.Value);
            }
        }

        return totals;
    }

    /// <summary>
    ///     The obedience floor of a pet we have no cast snapshot for (a login with pets already
    ///     out): the pet's wire LEVEL identifies the template tier - the learned line formula
    ///     with a branch at exactly that level (highest QL wins a tie) - and 80% of that
    ///     branch's Greater-than skill gates is the floor.
    /// </summary>
    private Dictionary<int, int>? FloorFromPetLevel(LocalPlayer me, PetType role, PetLine line)
    {
        var pet = me.Pets.FirstOrDefault(p => p.Role == role);
        if (pet == null || !pet.TryGetStat(Stat.Level, out var level))
        {
            return null;
        }

        if (!_summonIds.TryGetValue(line, out var ids))
        {
            return null;
        }

        SummonBranch? match = null;
        var matchQl = -1;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId))
            {
                continue;
            }

            var nano = NanoLibrary.Find(nanoId);
            if (nano == null || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            foreach (var branch in nano.Summons)
            {
                if (branch.TemplateLevel == level && ni.Ql > matchQl)
                {
                    matchQl = ni.Ql;
                    match = branch;
                }
            }
        }

        if (match == null)
        {
            return null;
        }

        var totals = new Dictionary<int, int>();
        foreach (var r in match.Requirements)
        {
            if (r.Operator == 2 && r.Stat is 127 or 128 or 130 or 131)
            {
                var floor = (int)Math.Ceiling((r.Value + 1) * 0.80); // PetBrain.ControlFloor
                totals[r.Stat] = Math.Max(totals.GetValueOrDefault(r.Stat), floor);
            }
        }

        return totals.Count > 0 ? totals : null;
    }

    private void Advance(LocalPlayer me, PetBrain pet)
    {
        _queueIndex++;
        if (_queueIndex >= _queue.Count)
        {
            Finish(me, pet);
            return;
        }

        var (phase, role) = _queue[_queueIndex];
        _logger.LogInformation($"EXTBUFF: Entering phase {_queue[_queueIndex].Phase}");
        _phase = phase;
        switch (phase)
        {
            case Phase.TerminateBuffs:
                // Then our own: EVERYTHING on me comes off (owner, 2026-10-06) - the peak
                // stages plan against real free NCU, and nothing stale survives the rebuild.
                // The kept set (obedience floor + comfort fill) is rebuilt by the later
                // stages, so the renewal watch starts from what those plan.
                foreach (var buff in me.Buffs.ToList())
                {
                    me.CancelNano(buff.Id);
                    _logger.LogInformation($"EXTBUFF: cancelling {NanoLibrary.NameOf(buff.Id)} (terminate-buffs - a clean slate).");
                }

                _keptIds.Clear();
                Advance(me, _bank.Pet!);
                break;

            case Phase.Travel:
                _travelDeadline = -1;
                _travelGoalSet = false;
                RunTravelStage(me, pet);
                break;

            case Phase.Ncu:
            {
                var ncu = _catalog.NcuTell();
                _logger.LogInformation("EXTBUFF: stage 1 - NCU headroom.");
                OpenOrSkip(me, ncu != null ? new List<string> { ncu } : new List<string>(), "NCU headroom");
                break;
            }

            case Phase.Peak:
                RunPeakStage(me, role);
                break;

            case Phase.PetBuffs:
                RunPetBuffStage(me);
                break;

            case Phase.Obedience:
                RunObedienceStage(me);
                break;

            case Phase.Qol:
            {
                // The SELF-DECIDED comfort fill (owner, 2026-10-06: PetQoLTells is obsolete -
                // the pets are not buffed by the buffbots): best long-term entry per category,
                // the skill buffs by conf mode - weapon mode matches the WIELDED weapon's own
                // skills, nano mode the attack-nano skill (MatMet).
                var attackNano = string.Equals(
                    (_config.ComfortMode ?? "").Trim(), "nano", StringComparison.OrdinalIgnoreCase);
                var weaponKeys = attackNano ? Array.Empty<string>() : EquippedWeaponSkillKeys().ToArray();
                var tells = _catalog.PlanForComfort(me, attackNano, weaponKeys);
                foreach (var tell in tells)
                {
                    var id = _catalog.Find(tell)?.NanoId;
                    if (id.HasValue)
                    {
                        _keptIds.Add(id.Value);
                    }
                }

                OpenOrSkip(me, tells,
                    $"the comfort fill ({(attackNano ? "attack-nano skills" : "weapon skills")})");
                break;
            }
        }
    }

    /// <summary>
    ///     One line's peak stage: FIRST cancel the previous stage's asks that grant none of
    ///     this line's gate stats (their NCU is freed - owner, 2026-10-06: "did you recalculate
    ///     free ncu when cancelling another buff?" - the plan runs AFTER the cancels, against
    ///     the freed budget), then ask for the highest template branch that budget can reach.
    ///     A kept ask (it grants a gate stat) stays up without a re-tell; a never-landed ask
    ///     cancels nothing.
    /// </summary>
    private void RunPeakStage(LocalPlayer me, PetType role)
    {
        var (line, _, _, label) = LineOf(role);
        var previous = _lastPeakAsked.ToList();
        _lastPeakAsked.Clear();

        var nanoId = BestLearnedNano(me, line);
        if (nanoId == null)
        {
            Advance(me, _bank.Pet!);
            return;
        }

        var branches = NanoLibrary.Find(nanoId.Value)?.Summons;
        if (branches == null || branches.Count == 0)
        {
            Advance(me, _bank.Pet!);
            return;
        }

        // The stats this line's branches gate on: MatCrea+TS for the attack pet, BioMet+TS for
        // the heal pet, MatMet+TS for the support pet - as the branch tables carry them.
        var needed = new HashSet<int>();
        foreach (var branch in branches)
        {
            foreach (var r in branch.Requirements)
            {
                if (r.Operator == 2 && r.Stat is 127 or 128 or 130 or 131)
                {
                    needed.Add(r.Stat);
                }
            }
        }

        if (needed.Count == 0)
        {
            // The branches gate on nothing we can buff (some heal formulas carry no nano-skill
            // gates at all): no cancel judgment is possible - keep every running ask. The
            // obedience stage still cleans them up afterwards.
            foreach (var id in previous)
            {
                if (me.Buffs.Find(id, out _))
                {
                    _lastPeakAsked.Add(id);
                }
            }
        }
        else
        {
            foreach (var id in previous)
            {
                if (!me.Buffs.Find(id, out _))
                {
                    continue; // never landed (a windowed-out session) - nothing to cancel
                }

                var entry = _catalog.FindById(id);
                if (entry != null && entry.Gains.Keys.Any(needed.Contains))
                {
                    _lastPeakAsked.Add(id); // serves this line - stays up; the next stage re-judges it
                    continue;
                }

                me.CancelNano(id);
                _peakAsked.Remove(id);
                _logger.LogInformation($"EXTBUFF: cancelling {NanoLibrary.NameOf(id)} (the {label} pet needs none of it - NCU freed).");
            }
        }

        // The plan runs AFTER the cancels: FreeNcu now includes what they freed.
        var plan = _catalog.PlanForPeakEntries(me, BuffCatalog.FreeNcu(me), branches);

        var tells = new List<string>();
        foreach (var entry in plan)
        {
            if (!entry.NanoId.HasValue || me.Buffs.Find(entry.NanoId.Value, out _))
            {
                continue; // already up (kept from the previous stage) - no re-tell
            }

            _peakAsked.Add(entry.NanoId.Value);
            _lastPeakAsked.Add(entry.NanoId.Value);
            tells.Add(entry.Tell);
        }

        if (tells.Count == 0)
        {
            // "Reachable" either by the branch gates (the planner verified them) or - for the
            // gate-less formulas (some heal pets carry no nano-skill gates at all) - by the
            // best learned summon's own use reqs being met on the CURRENT stats: the swap is
            // safe exactly then, because CastBest will land that summon. Checked NOW, before
            // the swap - the obedience stage cancels the peak asks afterwards, and on the
            // dropped stats a higher tier (Restite) falls back to the next castable (the
            // owner's "it casted Valentyia instead of Restite").
            var castableNow = BestSummonCastableNow(me, line);
            if (!_catalog.LastPeakReachable && !castableNow.HasValue)
            {
                _logger.LogInformation(
                    $"EXTBUFF: {label}-first: nothing coverable and the summon is not castable - the stage passes.");
                Advance(me, _bank.Pet!);
                return;
            }

            // The swap runs right here, without a session - terminate the line's old pet, cast
            // the new one at the peak stats.
            _pendingRole = role;
            _bank.Pet!.TerminateRole(me, role);
            _danceStage = DanceStage.WaitingPet;
            _waitDeadline = _clock + WaitPetSec;
            _sinceResummon = 0;
            _bank.Pet!.RequestSummon(role);
            return;
        }

        var why = $"{label}-first for {NanoLibrary.NameOf(nanoId.Value)}";
        OpenOrSkip(me, tells, why);
    }

    /// <summary>
    ///     The obedience stage: ALL peak buffs come off (including the wrangle - "longterm
    ///     buffs here only"), then the cheapest 1hr+ set lifting every summoned pet to 80% of
    ///     its own tier's requirements. The floor totals come from the pet brain's cast
    ///     snapshots per line with a pet actually up.
    /// </summary>
    private void RunObedienceStage(LocalPlayer me)
    {
        foreach (var id in _peakAsked)
        {
            if (!me.Buffs.Find(id, out _))
            {
                continue; // never landed (a windowed-out session) - nothing to cancel, no noise
            }

            me.CancelNano(id);
            _logger.LogInformation($"EXTBUFF: cancelling {NanoLibrary.NameOf(id)} (peak buff - the obedience floor comes next).");
        }

        _peakAsked.Clear();
        _lastPeakAsked.Clear();

        var pet = _bank.Pet!;
        var totals = FloorTotals(me, pet); // snapshots where we summoned, pet-level floors where we didn't
        if (totals.Count == 0)
        {
            _logger.LogWarning(
                "EXTBUFF: obedience stage has no floors (no pets up, and none carry snapshots or level-matched tiers) - skipped.");
            Advance(me, pet);
            return;
        }

        var free = BuffCatalog.FreeNcu(me);
        var plan = _catalog.PlanForObedienceEntries(me, free, totals);
        if (plan.Count == 0)
        {
            _logger.LogWarning(
                $"EXTBUFF: no 1hr+ cover for the obedience floors within {free} NCU " +
                $"(totals: {string.Join(", ", totals.Select(kv => $"{kv.Key}<{kv.Value}"))}) - skipped, floors stay open.");
        }

        foreach (var entry in plan)
        {
            if (entry.NanoId.HasValue)
            {
                _keptIds.Add(entry.NanoId.Value);
            }
        }

        OpenOrSkip(me, plan.Select(c => c.Tell).ToList(), "the obedience floor (80% of the summoned tiers)");
    }

    /// <summary>
    ///     The PET BUFF stage (owner, 2026-10-06): buff the PETS themselves. Per pet-buff line
    ///     (216/217/225/810/816/817/843): the best LEARNED nano by StackingOrder that we can
    ///     cast - no overequip math here - cast on the line's target pets (810 the support pet,
    ///     the damage/initiative lines the attack pet, 816/817/843 all pets). A pet already
    ///     carrying the line at equal or better stacking is skipped. The casts are queued and
    ///     pumped one at a time (DanceStage.PetBuffing).
    /// </summary>
    private void RunPetBuffStage(LocalPlayer me)
    {
        _petBuffQueue.Clear();
        var learned = new HashSet<int>(me.SpellList ?? Array.Empty<int>());
        var diag = new List<string>();
        foreach (var line in PetBuffLines)
        {
            if (!_petBuffNanos.TryGetValue(line, out var entries) || entries.Count == 0)
            {
                diag.Add($"{line}: no nanos in pack");
                continue;
            }

            // NO QL tie-break (owner, 2026-10-06: "I need it by Stat.StackingOrder - there might
            // be multiple nanos with the same QL which have different StackingOrders"): the
            // strongest learned+castable nano of the line is simply the highest stacking order.
            (int NanoId, int Stacking) best = default;
            var have = false;
            foreach (var (nanoId, stacking) in entries)
            {
                if (!learned.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
                {
                    continue;
                }

                bool castable;
                try
                {
                    castable = ni.MeetsUseReqs(me, false, true);
                }
                catch
                {
                    castable = false;
                }

                if (!castable)
                {
                    continue;
                }

                if (!have || stacking > best.Stacking)
                {
                    best = (nanoId, stacking);
                    have = true;
                }
            }

            if (!have)
            {
                diag.Add($"{line}: pack {entries.Count}, learned {entries.Count(e => learned.Contains(e.NanoId))}, castable 0");
                continue; // nothing learned/castable of this line
            }

            var targetRole = PetBuffTargetRole(line);
            var targets = 0;
            foreach (var p in me.Pets.Where(p => targetRole == null || p.Role == targetRole))
            {
                var up = false;
                foreach (var b in p.Buffs)
                {
                    if (b?.NanoItem != null && b.NanoItem.NanoLine == (NanoLine)line
                        && b.NanoItem.StackingOrder >= best.Stacking)
                    {
                        up = true; // already running at this strength or better
                        break;
                    }
                }

                if (up)
                {
                    continue;
                }

                _petBuffQueue.Add(new PetBuffCast
                {
                    Pet = p.Identity,
                    NanoId = best.NanoId,
                    Line = line,
                    Stacking = best.Stacking,
                });
                targets++;
            }

            diag.Add($"{line}: best so{best.Stacking}, queued {targets}");

            if (targets > 0)
            {
                _logger.LogInformation(
                    $"EXTBUFF: pet buffs - {NanoLibrary.NameOf(best.NanoId)} queued for {targets} pet(s) (line {line}).");
            }
        }

        if (_petBuffQueue.Count == 0)
        {
            _logger.LogInformation("EXTBUFF: pet buffs - nothing to cast: " + string.Join("; ", diag) + ".");
            Advance(me, _bank.Pet!);
            return;
        }

        _logger.LogInformation("EXTBUFF: pet buffs - " + string.Join("; ", diag) + ".");
        _danceStage = DanceStage.PetBuffing;
    }

    /// <summary>Which pets a pet-buff line goes on: 810 the support pet only, the
    /// damage/initiative lines the attack pet, the defensive/DoT-resist/heal-delta lines all
    /// pets (owner, 2026-10-06).</summary>
    private static PetType? PetBuffTargetRole(int line)
    {
        return line switch
        {
            810 => PetType.Support,
            216 or 217 or 225 => PetType.Attack,
            _ => null,
        };
    }

    /// <summary>What casting <paramref name="ni" /> actually costs from OUR pool - the breed
    /// and cost-modifier rule Buff.GetCost encodes (the pet-buff casts drain our nano, and the
    /// pump budgets against the adjusted price, not the raw one).</summary>
    private static int AdjustedCastCost(LocalPlayer me, NanoItem ni)
    {
        if (ni.Cost <= 0)
        {
            return 0;
        }

        var costModifier = me.TryGetStat(Stat.NPCostModifier, out var mod) ? mod : 100;
        switch ((Breed)(me.TryGetStat(Stat.Breed, out var breed) ? breed : 0))
        {
            case Breed.Nanomage:
                costModifier = Math.Max(costModifier, 45);
                break;
            case Breed.Atrox:
                costModifier = Math.Max(costModifier, 55);
                break;
            default:
                costModifier = Math.Max(costModifier, 50);
                break;
        }

        return (int)(ni.Cost * (costModifier / 100.0));
    }

    /// <summary>The weapon-skill stat -> the menu's textual keys (Chewy writes "+14 1hb",
    /// "+120 Rifle", "+110 Burst"; the wrangle family's "+N Weap/Nano" covers them all at once
    /// but is short-term and out). Stats per the pack's own criteria ids.</summary>
    private static readonly Dictionary<int, string[]> WeaponSkillKeys = new()
    {
        { 100, new[] { "martial arts" } },
        { 102, new[] { "1hb" } },
        { 103, new[] { "1he" } },
        { 104, new[] { "melee e" } },
        { 105, new[] { "2he" } },
        { 106, new[] { "pierce" } },
        { 107, new[] { "2hb" } },
        { 108, new[] { "sharp" } },
        { 109, new[] { "grenade" } },
        { 110, new[] { "heavy" } },
        { 111, new[] { "bow" } },
        { 112, new[] { "pistol" } },
        { 113, new[] { "rifle" } },
        { 114, new[] { "smg", "mgs" } },
        { 115, new[] { "shotgun" } },
        { 116, new[] { "assault rifle" } },
        { 148, new[] { "burst" } },
        { 150, new[] { "fling" } },
        { 151, new[] { "aimed" } },
    };

    /// <summary>The equipped RIGHT-HAND weapon's attack skills, read from the weapon's own use
    /// criteria (the skill stats it demands) - the keys the comfort planner matches weapon
    /// buffs against. Empty = nothing wielded worth buffing (shield only, bare hands).</summary>
    private HashSet<string> EquippedWeaponSkillKeys()
    {
        var keys = new HashSet<string>();
        var weapon = Inventory.Items?.FirstOrDefault(x =>
            x.Slot.Type == IdentityType.WeaponPage && x.Slot.Instance == (int)EquipSlot.Weap_RightHand);
        if (weapon == null)
        {
            return keys;
        }

        foreach (var criteria in weapon.Criteria.Values)
        {
            foreach (var req in criteria)
            {
                if (WeaponSkillKeys.TryGetValue(req.Param1, out var texts))
                {
                    foreach (var t in texts)
                    {
                        keys.Add(t);
                    }
                }
            }
        }

        _logger.LogInformation(
            $"EXTBUFF: comfort - wielding '{weapon.Name}' (skill keys: {(keys.Count > 0 ? string.Join(", ", keys.OrderBy(k => k)) : "none mapped")}).");
        return keys;
    }

    /// <summary>The pack's nanos of the pet-buff lines, with their stacking orders - read once
    /// at construction (the library is loaded before the session starts).</summary>
    private void ScanPetBuffLines()
    {
        foreach (var line in PetBuffLines)
        {
            _petBuffNanos[line] = new List<(int NanoId, int Stacking)>();
        }

        if (!NanoLibrary.Loaded || NanoLibrary.Nanos.Count == 0)
        {
            _logger.LogWarning("EXTBUFF: nano library empty - the pet-buff stage stays off.");
            return;
        }

        foreach (var nano in NanoLibrary.Nanos)
        {
            var line = nano.Stat(75);
            if (!PetBuffLines.Contains(line))
            {
                continue;
            }

            _petBuffNanos[line].Add((nano.NanoId, nano.Stat(551)));
        }

        _logger.LogInformation(
            "EXTBUFF: pet-buff lines - " +
            string.Join(", ", PetBuffLines.Select(l => $"{l}: {_petBuffNanos[l].Count} in pack")) + ".");
    }

    /// <summary>Open a session for the tells (as ONE combined tell - "tsi mci"), told to the
    /// CATALOG's bot (dimension-picked), or skip ahead when there is nothing to ask.</summary>
    private void OpenOrSkip(LocalPlayer me, List<string> codes, string why)
    {
        if (codes.Count == 0)
        {
            _logger.LogInformation($"EXTBUFF: nothing to ask for ({why}) - stage skipped.");
            Advance(me, _bank.Pet!);
            return;
        }

        var bot = _catalog.BotName ?? _config.BuffBotName;
        if (string.IsNullOrWhiteSpace(bot))
        {
            _logger.LogWarning($"EXTBUFF: no buff bot known (the menu JSON's location block has no botName) - {why} skipped.");
            Advance(me, _bank.Pet!);
            return;
        }

        // One COMBINED tell per stage (owner, 2026-10-06: no 1.5 s spacing - "cast mcmo 132"
        // in one line), then the session just waits until every asked buff has landed.
        if (_buffBot.RequestBuffs(bot, new List<string> { string.Join(" ", codes) }, why))
        {
            _lastAskAt = _clock;
            _danceStage = DanceStage.SessionOpen;
            _logger.LogInformation($"EXTBUFF: session open ({why}): {string.Join(" ", codes)}.");
            return;
        }

        _logger.LogWarning($"EXTBUFF: could not open a session for {why} - stage skipped (the dance degrades).");
        Advance(me, _bank.Pet!);
    }

    /// <summary>The step after a dance session closes.</summary>
    private void AfterSession(LocalPlayer me, PetBrain pet)
    {
        switch (_phase)
        {
            case Phase.Ncu:
            case Phase.Obedience:
                Advance(me, pet); // the next stage (or the finish) does the planning against what landed
                break;

            case Phase.Peak:
                // The summon goes out ONLY when this stage's peak stack actually LANDED: a
                // session that closed on its window ("no invite came in the window") cast
                // nothing, and summoning then brings the pet out at base stats - exactly what
                // the buff-first dance exists to prevent (owner, 2026-10-06: the attack pet was
                // cast right after the terminate, with no buffs anywhere). The line stays
                // empty; the next dance re-asks it.
                var missing = _lastPeakAsked.Where(id => !me.Buffs.Find(id, out _)).ToList();
                if (missing.Count > 0)
                {
                    _logger.LogWarning(
                        "EXTBUFF: the peak stack did not land (" +
                        string.Join(", ", missing.Select(NanoLibrary.NameOf)) +
                        $") - no {_queue[_queueIndex].Role} summon; the line waits for the next dance.");
                    Advance(me, pet);
                    break;
                }

                // The peak stack is in: the swap happens EXACTLY here (owner, 2026-10-06) -
                // terminate the OLD pet of this line, then summon its replacement, which casts
                // fresh at peak stats. The same-line summon gate ("they don't overwrite") is
                // what forces the terminate; doing it per line, only now, keeps the rest of
                // the roster up and fighting the whole dance. Then wait for the new pet - the
                // next stage's cancel and NCU math need it out and its NCU spent.
                _pendingRole = _queue[_queueIndex].Role;
                pet.TerminateRole(me, _pendingRole);
                _danceStage = DanceStage.WaitingPet;
                _waitDeadline = _clock + WaitPetSec;
                _sinceResummon = 0;
                pet.RequestSummon(_pendingRole);
                break;

            case Phase.Qol:
                Finish(me, pet);
                break;
        }
    }

    private void Finish(LocalPlayer me, PetBrain pet)
    {
        _inDance = false;
        _queue.Clear();
        _queueIndex = -1;
        _danceStage = DanceStage.Idle; // was left at WaitingPet - the cleared queue then
                                       // re-finished EVERY FRAME (owner, 2026-10-06)
        _danceDone = true;
        _lastAskAt = _clock;
        // The hold comes OFF: the dance is concluded, and whatever lines it could not fill -
        // a failed leg, a stack that never landed - belong to the pet brain's cadence again
        // (owner, 2026-10-06: "the pets weren't cast at all now"). The cadence's petless
        // grace keeps it out of the way while a dance is ARRANGING; concluded, it stands down.
        pet.SetSummonHold(false);
        _logger.LogInformation(
            $"EXTBUFF: dance done - {_keptIds.Count} kept buff(s) under renewal watch (renewed at {RenewAtSeconds / 60:0} min left).");
    }

    // ---- The travel (ICC, 4a.1b) ------------------------------------------------------------

    /// <summary>
    ///     The dance's first phase, re-run every tick: without it the tells go out to a bot that
    ///     cannot see us and the window dies ("we need the travel to icc first", owner). The
    ///     bot spot comes from the menu JSON's location block (Chewy - RubiKa, Doc - RK2019,
    ///     both ICC Andromeda): playfield travel first, then the walk to the coordinates when
    ///     the JSON carries them. A dance that cannot travel aborts - it degrades worse than it
    ///     helps.
    /// </summary>
    private void RunTravelStage(LocalPlayer me, PetBrain pet)
    {
        var loc = _catalog.BotLocation;
        if (loc == null)
        {
            _logger.LogInformation("EXTBUFF: the menu JSON carries no bot location - travel skipped.");
            Advance(me, pet);
            return;
        }

        if (NearBot(me))
        {
            if (_travelDeadline >= 0)
            {
                _logger.LogInformation("EXTBUFF: near the buff bots - the dance continues.");
            }

            _travelDeadline = -1;
            _travelGoalSet = false;
            Advance(me, pet);
            return;
        }

        if (_travelDeadline < 0)
        {
            _travelDeadline = _clock + TravelBudgetSec;
            _logger.LogInformation($"EXTBUFF: heading for the buff bots at {Zoning.Name(loc.Value.Playfield)}.");
        }

        if (_clock >= _travelDeadline)
        {
            _logger.LogWarning("EXTBUFF: the travel budget ran out - dance aborted (will retry after the cooldown).");
            AbortDance(me, pet);
            return;
        }

        var (playfield, x, y, z) = loc.Value;
        if ((int)Playfield.ModelId != playfield)
        {
            if (_movement.TravelTargetPf != playfield)
            {
                var line = _movement.PlanTravel(playfield, null);
                if (_movement.TravelTargetPf != playfield)
                {
                    _logger.LogWarning($"EXTBUFF: travel to {Zoning.Name(playfield)} refused ({line}) - dance aborted.");
                    AbortDance(me, pet);
                }
            }

            return; // in transit - the movement ticks on its own
        }

        // On the playfield: walk to the bot spot when the JSON pins it.
        if (x.HasValue && !_travelGoalSet)
        {
            _movement.SetDesiredGoal(new Vector3(x.Value, y ?? 0, z ?? 0), playfield,
                ControlPriority.ExternalBuffing, NearBotReach);
            _travelGoalSet = true;
        }
    }

    /// <summary>On the bots' playfield and, when the JSON pins a spot, close to it.</summary>
    private bool NearBot(LocalPlayer me)
    {
        var loc = _catalog.BotLocation;
        if (loc == null)
        {
            return true; // nowhere known to go - travel is not our business
        }

        if ((int)Playfield.ModelId != loc.Value.Playfield)
        {
            return false;
        }

        if (loc.Value.X == null)
        {
            return true; // playfield-level location only
        }

        var spot = new Vector3(loc.Value.X.Value, loc.Value.Y ?? 0, loc.Value.Z ?? 0);
        return Movement.Flat(me.Transform.Position, spot) <= NearBotReach;
    }

    /// <summary>The dance cannot proceed (travel failed) - release the hold and try again later.</summary>
    private void AbortDance(LocalPlayer me, PetBrain pet)
    {
        _inDance = false;
        _queue.Clear();
        _queueIndex = -1;
        _danceStage = DanceStage.Idle;
        _travelDeadline = -1;
        _travelGoalSet = false;
        _lastAskAt = _clock;
        // Concluded the same way a finish is: the cadence gets the petless lines back.
        pet.SetSummonHold(false);
        Done();
    }

    // ---- The renewal watch ------------------------------------------------------------------

    /// <summary>
    ///     The kept set (obedience floor + comfort fill) holds the pets' 80% floors. Buffs are
    ///     constant while they run, so the only threat is the end of a hold: renew whatever has
    ///     less than ~30 minutes left, as one combined tell.
    /// </summary>
    private void TryRenewal(LocalPlayer me, double dt)
    {
        if (!_danceDone || _keptIds.Count == 0)
        {
            return;
        }

        if (!_config.PetAutoBuff || (Team.IsInTeam && !_buffBot.TeamedWithBot))
        {
            return;
        }

        var bot = _catalog.BotName ?? _config.BuffBotName;
        if (string.IsNullOrWhiteSpace(bot))
        {
            return;
        }

        if (!NearBot(me))
        {
            return; // a renewal far from the bots burns a session on a tell nobody answers
        }

        if (_clock - _lastAskAt < AskRetrySec)
        {
            return;
        }

        _sinceRenewCheck += dt;
        if (_sinceRenewCheck < RenewCheckSec)
        {
            return;
        }

        _sinceRenewCheck = 0;

        var codes = new List<string>();
        foreach (var id in _keptIds)
        {
            var remaining = 0.0;
            if (me.Buffs.Find(id, out var buff))
            {
                remaining = buff.Cooldown?.RemainingTime ?? 0;
            }

            if (remaining < RenewAtSeconds)
            {
                var entry = _catalog.FindById(id);
                if (entry != null)
                {
                    codes.Add(entry.Tell);
                }
            }
        }

        if (codes.Count == 0)
        {
            return;
        }

        if (_buffBot.RequestBuffs(bot, new List<string> { string.Join(" ", codes) },
                $"renewal ({RenewAtSeconds / 60:0} min left: {string.Join(" ", codes)})"))
        {
            _renewing = true;
            _danceStage = DanceStage.SessionOpen;
            _lastAskAt = _clock;
            _logger.LogInformation($"EXTBUFF: renewing the kept set: {string.Join(" ", codes)}.");
        }
    }

    // ---- The data ---------------------------------------------------------------------------

    /// <summary>The summon formulas per line, scanned from the whole nano library by strain.</summary>
    private void ScanSummonLines()
    {
        foreach (var (line, _, _, _) in Lines)
        {
            _summonIds[line] = new HashSet<int>();
        }

        if (!NanoLibrary.Loaded || NanoLibrary.Nanos.Count == 0)
        {
            _logger.LogWarning("EXTBUFF: nano library empty - the MP buff-first watcher stays off.");
            return;
        }

        foreach (var (line, _, strain, _) in Lines)
        {
            foreach (var nano in NanoLibrary.InStrain((int)strain))
            {
                _summonIds[line].Add(nano.NanoId);
            }
        }
    }

    /// <summary>
    ///     The dance's target formula per line: the learned summon with the highest branch-table
    ///     top (the pack's true strength order - QL does NOT track power within these lines),
    ///     QL only as the tie-break.
    /// </summary>
    private int? BestLearnedNano(LocalPlayer me, PetLine line)
    {
        if (!_summonIds.TryGetValue(line, out var ids) || ids.Count == 0)
        {
            return null;
        }

        int? best = null;
        var bestTop = -1;
        var bestQl = -1;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            var top = NanoLibrary.Find(nanoId)?.TopTemplateLevel ?? 0;
            if (top > bestTop || (top == bestTop && ni.Ql > bestQl))
            {
                bestTop = top;
                bestQl = ni.Ql;
                best = nanoId;
            }
        }

        return best;
    }

    /// <summary>
    ///     The best learned summon of the line that is castable RIGHT NOW - the pet brain's
    ///     CastBest selection, read-only (same ranking: branch-table top, then QL; same gate:
    ///     the formula's use reqs on the current stats). Null when none is castable. This is
    ///     the swap-safety check for the GATE-LESS formulas: their branches cannot tell the
    ///     planner anything, but the use reqs on the live stats can.
    /// </summary>
    private int? BestSummonCastableNow(LocalPlayer me, PetLine line)
    {
        if (!_summonIds.TryGetValue(line, out var ids) || ids.Count == 0)
        {
            return null;
        }

        int? best = null;
        var bestTop = -1;
        var bestQl = -1;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            bool castable;
            try
            {
                castable = ni.MeetsUseReqs(me, false, true);
            }
            catch
            {
                castable = false;
            }

            if (!castable)
            {
                continue;
            }

            var top = NanoLibrary.Find(nanoId)?.TopTemplateLevel ?? 0;
            if (best == null || top > bestTop || (top == bestTop && ni.Ql > bestQl))
            {
                bestTop = top;
                bestQl = ni.Ql;
                best = nanoId;
            }
        }

        return best;
    }

    /// <summary>Whether the TOP template branch of a formula is reachable on the current stats
    /// (Greater-than gates on the four pet-tier skills only).</summary>
    private static bool TopReachable(LocalPlayer me, IReadOnlyList<SummonBranch> branches)
    {
        foreach (var branch in branches.OrderByDescending(b => b.TemplateLevel))
        {
            var gates = false;
            foreach (var r in branch.Requirements)
            {
                if (r.Operator != 2 || r.Stat is not (127 or 128 or 130 or 131))
                {
                    continue;
                }

                gates = true;
                if (me.TryGetStat((Stat)r.Stat, out var v) && v <= r.Value)
                {
                    return false; // the top branch is gated and we are short
                }
            }

            return gates; // the top skill-gated branch - reachable iff nothing above blocked earlier
        }

        return true;
    }

    private (PetLine Line, PetType Role, NanoLine Strain, string Label) LineOf(PetType role)
    {
        foreach (var row in Lines)
        {
            if (row.Role == role)
            {
                return row;
            }
        }

        return Lines[0];
    }

    // ---- Cross-brain ------------------------------------------------------------------------

    /// <summary>
    ///     Busy = the cast window is ours: a dance phase in flight (travel, sessions, waits), a
    ///     renewal session, or a foreign session being coordinated. The chain gates self-buffing
    ///     on this - its casts would collide with the dance's pet summons.
    /// </summary>
    public override bool BuffingInProgress =>
        _inDance || _renewing || _foreign || _buffBot.Active || _danceStage != DanceStage.Idle;

    // ---- The arbiter ------------------------------------------------------------------------

    /// <summary>
    ///     Hold the arbiter at ControlPriority.ExternalBuffing - RE-ASSERTED every tick, because
    ///     TakeControl is last-write: a higher system's episode (combat fighting back mid-dance)
    ///     overwrites the active priority, and without the re-assert our 300 would stay gone
    ///     after it releases. <see cref="_claimed" /> only remembers whether WE ever held it, so
    ///     <see cref="Done" /> releases only what we own.
    /// </summary>
    private bool Claim()
    {
        _controlArbiter.TakeControl(ControlPriority.ExternalBuffing);
        _claimed = true;
        return true;
    }

    /// <summary>Close the episode: release the arbiter if we were holding it (idempotent).</summary>
    private bool Done()
    {
        if (_claimed)
        {
            _claimed = false;
            _controlArbiter.ReleaseControl();
        }

        return false;
    }
}