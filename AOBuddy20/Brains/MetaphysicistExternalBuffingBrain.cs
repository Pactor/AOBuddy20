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
using BuffEntry = AOBuddy20.Controlling.BuffCatalog.BuffEntry;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     METAPHYSICIST EXTERNAL BUFFING BRAIN - the staged buff-first dance, driven from the
///     buffing side (PETBRAIN-DESIGN.md port order 4; the sequence is the owner's, per the
///     L50 froob walkthrough of 2026-10-05/06). Owner, 2026-10-07: the dance CALCULATES EVERY
///     STEP BEFOREHAND and prints the whole plan before anything happens. The planner walks a
///     simulated ledger - clean slate, the ncu gain, each line's asks applied, each line's
///     cancels freed - and decides, per pet line, the target formula, the template tier the
///     budget reaches, the exact asks, and the frees; then the floor set and the comfort fill
///     on what the plan leaves. THE PEAK IS THE GOAL when casting a pet (owner, 2026-10-07) -
///     the obedience floor only KEEPS the summoned tiers alive. A line whose top reachable
///     tier would not beat its standing pet is SKIPPED, never downgraded (the "casted
///     Valentyia instead of Restite" bug: the old stage fell back to the best summon castable
///     RIGHT NOW, which is by definition the weak tier).
///     The executed sequence (the plan's steps, in order):
///       0. TRAVEL to the bots - the tells go nowhere from another playfield, and a dance
///          whose travel fails aborts BEFORE anything is torn down.
///       1. TERMINATE ALL BUFFS - the plan already assumed this clean slate.
///       2. ASK 'ncu' - the fixer NCU nanos; the tier that lands is LEVEL-LOCKED (owner,
///          2026-10-07: "use the ncu fixer nanos, the trigger is the level lock"), so the plan
///          predicts the gain from the lock table and budgets against it.
///       3-7. PER LINE (attack, heal, support): ASK its peak stack, then SUMMON at peak - the
///          swap runs only when the stack is verified in, and only this line's old pet dies -
///          with the plan's CANCEL steps freeing the previous line's non-serving asks in
///          between.
///       8. PET BUFFS: cast the best learned pet nanos ON the pets - lines 216/217/225/810/
///          816/817/843, best StackingOrder per line among the learned and castable, no
///          overequip math; a pet already carrying a line at equal or better stacking is
///          skipped (owner, 2026-10-06).
///       9. CANCEL all peak asks (the 3-minute wrangle exists to be cancelled), then ASK the
///          floor set - planned from the TARGETED tiers, not from whatever landed - and the
///          comfort fill from conf.
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

    // The plan's step kinds - the dance executes a pre-computed list of these, one session
    // (or one synchronous action) at a time.
    private enum StepKind
    {
        Travel,
        TerminateBuffs,
        Ask,
        Cancel,
        Summon,
        PetBuffs
    }

    // One pre-computed dance step: WHAT happens, plus everything the logs and the
    // after-session checks need (the expected asks, the why, the planned tier).
    private sealed class DanceStep
    {
        public StepKind Kind;

        /// <summary>The log/planner reason - "attack-first for X - template 9", "peak asks - the obedience floor comes next".</summary>
        public string Why = "";

        /// <summary>ASK: the tells. ASK and SUMMON: the ids that must be RUNNING - a summon
        /// step checks its own list and drops itself when the stack it feeds on is not in.</summary>
        public List<string> Tells = new();
        public List<int> ExpectIds = new();

        /// <summary>CANCEL: the ids to cancel (each only when it actually runs).</summary>
        public List<int> CancelIds = new();

        /// <summary>SUMMON: the line, the formula, and the tier these stats are planned to reach.</summary>
        public PetType Role;
        public int TargetNanoId;
        public string TargetName = "";
        public int TargetTemplate;
    }

    private enum DanceStage
    {
        Idle,
        SessionOpen,
        WaitingPet,
        PetBuffing
    }

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

    // The dance: the pre-computed plan, executed one step (one session, cancel batch, summon
    // or pet-buff pump) at a time. Built - and PRINTED - before the first tell goes out.
    private readonly List<DanceStep> _queue = new();
    private int _queueIndex = -1;
    private bool _inDance;
    private DanceStage _danceStage = DanceStage.Idle;
    private PetType _pendingRole;
    private double _waitDeadline;
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

        // The travel step re-runs every tick until we are near the bots (the movement ticks on
        // its own; the brain only sets goals and polls).
        if (_inDance && CurrentStep()?.Kind == StepKind.Travel && _danceStage == DanceStage.Idle)
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
    ///     The SILENT pre-gate (the plan and its printout are expensive - the planners log -
    ///     so a cheap verdict decides whether to build one at all): any line whose top
    ///     candidate's gate is unmet on the current stats (a missing line OR an up pet weaker
    ///     than it could be), or any up pet below its obedience floor.
    /// </summary>
    private bool NeedsWork(LocalPlayer me)
    {
        foreach (var (line, role, _, _) in Lines)
        {
            var cands = LineCandidates(me, line);
            if (cands.Count == 0)
            {
                continue;
            }

            var top = cands[0];
            foreach (var kv in top.Mins)
            {
                if (!me.TryGetStat((Stat)kv.Key, out var v) || v < kv.Value)
                {
                    return true;
                }
            }
        }

        foreach (var (line, role, _, _) in Lines)
        {
            var standing = StandingTemplate(me, role, line);
            var floor = FloorOfMins(TierMins(me, line, standing));
            if (floor == null)
            {
                continue;
            }

            foreach (var kv in floor)
            {
                if (!me.TryGetStat((Stat)kv.Key, out var v) || v < kv.Value)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     The dance trigger: build the WHOLE plan first (the plan IS the buff-state assessment
    ///     - a plan with no work steps means nothing needs doing), and only then print and
    ///     execute it. TRAVEL and TERMINATE-BUFFS come first (owner, 2026-10-06: a dance whose
    ///     travel fails aborts before anything is torn down; the clean slate is what the plan
    ///     budgeted against). The PETS are not terminated up front: each line's old pet dies
    ///     only at its own summon step, with that line's peak stack verified in (owner,
    ///     2026-10-06) - the rest of the roster keeps fighting the whole dance.
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

        if (!NeedsWork(me))
        {
            return; // nothing to reach for and no floor short - silent, no plan, no logs
        }

        DancePlan plan;
        try
        {
            plan = BuildDancePlan(me);
        }
        catch (Exception e)
        {
            // The planner is PURE - a failure (a malformed pack entry, say) must never take the
            // tick down with it: log, buy the cooldown, and let the next pass try again.
            _lastAskAt = _clock;
            _logger.LogError($"EXTBUFF: the dance planner failed - no dance this pass ({e.GetType().Name}: {e.Message}).");
            return;
        }

        _lastAskAt = _clock; // a built plan - even an empty one - buys the cooldown (the
                             // planners log; a no-work verdict must not re-plan every second)
        if (plan.Work.Count == 0)
        {
            return; // nothing needs doing - no dance, and no plan print (there is no plan)
        }

        _inDance = true;
        pet.SetSummonHold(true); // the auto cadence stays shut; the dance summons through requests
        _queue.Clear();
        _queue.Add(new DanceStep { Kind = StepKind.Travel, Why = "to the buff bots" });
        _queue.Add(new DanceStep { Kind = StepKind.TerminateBuffs, Why = "a clean slate (the plan budgeted against it)" });
        _queue.AddRange(plan.Work);
        _queueIndex = -1;
        _keptIds.Clear();
        foreach (var id in plan.KeptIds)
        {
            _keptIds.Add(id);
        }

        // THE WHOLE PLAN, printed before it happens (owner, 2026-10-07).
        foreach (var line in plan.Print)
        {
            _logger.LogInformation(line);
        }

        _logger.LogInformation(
            $"EXTBUFF: dance starting - {string.Join("; ", plan.Whys)}. Bot: {_catalog.BotName ?? _config.BuffBotName ?? "(none)"}, spot: " +
            (_catalog.BotLocation.HasValue ? Zoning.Name(_catalog.BotLocation.Value.Playfield) : "(unmapped)") +
            ".");
        Advance(me, pet);
    }

    /// <summary>One built dance plan: the executable steps, the printout lines, the kept set
    /// for the renewal watch, and the trigger reasons.</summary>
    private sealed class DancePlan
    {
        public readonly List<DanceStep> Work = new();
        public readonly List<string> Print = new();
        public readonly List<int> KeptIds = new();
        public readonly List<string> Whys = new();

        /// <summary>The printout's step counter - work steps AND skips number monotonically.</summary>
        public int PrintNo;
    }

    /// <summary>
    ///     THE PLANNER (owner, 2026-10-07: "calculate each step beforehand and print out the
    ///     whole plan before it happens"). It walks a simulated LEDGER - the NCU and stat state
    ///     the plan's own steps produce - and decides per pet line: the target formula (best
    ///     learned by branch-table top), the highest template branch the ledger covers, the
    ///     asks for it, and the cancels of earlier lines' asks that serve none of this line's
    ///     gates. THE PEAK IS THE GOAL; a line whose best reachable tier would not BEAT its
    ///     standing pet is skipped, never downgraded. Then all peak asks are freed, the
    ///     obedience floor is planned from the TARGETED tiers, the comfort fill runs on the
    ///     rest, and the pet-buff stage dry-runs. PURE: nothing here touches the character -
    ///     the caller decides whether the plan is worth executing.
    /// </summary>
    private DancePlan BuildDancePlan(LocalPlayer me)
    {
        var plan = new DancePlan();

        // The ledger starts on the clean slate the dance's TerminateBuffs step creates; the
        // fixer's ncu ask is the first thing applied to it.
        var ncuTell = _catalog.NcuTell();
        var ncuEntry = ncuTell != null ? _catalog.Find(ncuTell) : null;
        var ncu = PredictNcuMax(me);
        var ledger = new Ledger(
            _catalog.CleanSlateBase(me, new[] { 127, 128, 130, 131 }),
            ncu.Max,
            ncu.Tier != null
                ? $"level lock {ncu.Lock}: '{ncu.Tier}' (+{ncu.Gain} max NCU) over the measured max"
                : "as measured - no level-locked ncu gain predicted");
        ledger.PrintHeader(plan, ncuEntry?.Ncu ?? 0);
        if (ncuEntry != null)
        {
            plan.Work.Add(new DanceStep { Kind = StepKind.Ask, Why = "NCU headroom (the fixer ncu nanos)", Tells = { ncuTell! } });
            ledger.Apply(ncuEntry, peakAsk: false);
        }
        else
        {
            plan.Print.Add($"  {++plan.PrintNo}: SKIP ncu - no ncu tell in the menu - the budget stays as measured.");
        }

        var plannedTier = new Dictionary<PetType, int>();
        foreach (var (line, role, _, label) in Lines)
        {
            // A line's candidates are (formula, branch) PAIRS (owner, 2026-10-07: "casting a
            // heal PET is not a heal nano formula" - the pack carries two shapes). The heal
            // and support lines are ONE FORMULA PER TIER with the skill gate on the FORMULA's
            // own cast requirements ("Calling of Restite": SpaceTime>518, BioMet>518; its
            // branches are seven identical template-99 appearance pickers). The attack line
            // self-scales INSIDE one formula ("Summon Frenzy Embodiment": branches 101-137,
            // each branch gated - that is the tier pick). So the gate of a candidate is always
            // the formula's own reqs UNION the branch's reqs on the four pet-tier skills, and
            // the walk runs over every learned formula, top template down.
            var cands = LineCandidates(me, line);
            if (cands.Count == 0)
            {
                plan.Print.Add($"  {++plan.PrintNo}: SKIP {label} - nothing learned of this line.");
                continue;
            }

            // The stats this line's candidates gate on: MatCrea+TS for the attack pet,
            // BioMet+TS for the heal pet, MatMet+TS for the support pet.
            var needed = new HashSet<int>();
            foreach (var c in cands)
            {
                foreach (var s in c.Mins.Keys)
                {
                    needed.Add(s);
                }
            }

            // Free the earlier lines' asks that serve none of THIS line's gates (the old peak
            // stage's cancel, planned ahead) - their NCU joins the budget before the ask.
            var drop = ledger.PeakAsks().Where(e => !e.Gains.Keys.Any(needed.Contains)).ToList();
            if (drop.Count > 0)
            {
                var why = $"the {label} pet needs none of the {string.Join("/", drop.SelectMany(e => e.Gains.Keys).Distinct().Select(StatName))} asks - NCU freed";
                plan.Work.Add(new DanceStep { Kind = StepKind.Cancel, Why = why, CancelIds = drop.Select(e => e.NanoId!.Value).ToList() });
                plan.Print.Add($"  {++plan.PrintNo}: CANCEL {string.Join(" ", drop.Select(e => e.Tell))} - {why} ({drop.Sum(e => e.Ncu)} NCU back, {ledger.Free + drop.Sum(e => e.Ncu)} free).");
                foreach (var e in drop)
                {
                    ledger.Revoke(e);
                }
            }

            var standing = StandingTemplate(me, role, line);

            // Walk the candidates top template down (cheapest gate first at a tie): the first
            // one the ledger can carry - gates met, or the gaps covered within the free NCU -
            // is the line's target. THE PEAK IS THE GOAL; each rejected tier's gaps are logged
            // by the planner, so the printout shows exactly why it fell to the one below.
            SummonCandidate? winner = null;
            var winnerAsks = new List<BuffEntry>();
            foreach (var c in cands)
            {
                if (c.Mins.All(kv => ledger.Stats.GetValueOrDefault(kv.Key) >= kv.Value))
                {
                    winner = c; // the gates are met on the ledger as it stands - just summon
                    break;
                }

                var asks = _catalog.GoalPlanAgainst(me, ledger.Free, c.Mins, longTermOnly: false, ledger.Stats, ledger.Lines);
                if (asks.Count > 0)
                {
                    winner = c;
                    winnerAsks = asks.ToList();
                    break;
                }
                // not coverable within the budget - the planner logged the gaps; the tier below
            }

            if (winner == null)
            {
                var top = cands[0];
                plan.Print.Add($"  {++plan.PrintNo}: SKIP {label} - top template {top.Template} of '{top.Name}' needs " +
                    $"{Shortfalls(top.Mins, ledger.Stats)} within {ledger.Free} NCU - keeping the standing pet (template {standing}).");
                plan.Whys.Add($"{label} peak out of NCU reach");
                continue;
            }

            if (winner.Template <= standing)
            {
                // THE PEAK IS THE GOAL - and the standing pet already sits at or above the best
                // tier the budget reaches: no asks, no re-summon (the downgrade is what casted
                // Valentyia instead of Restite).
                plan.Print.Add($"  {++plan.PrintNo}: SKIP {label} - the best reachable template ({winner.Template}) " +
                    $"does not beat the standing pet (template {standing}) - no asks, no re-summon.");
                continue;
            }

            if (winnerAsks.Count > 0)
            {
                var ask = new DanceStep
                {
                    Kind = StepKind.Ask,
                    Why = $"{label}-first for '{winner.Name}' - template {winner.Template}",
                    Tells = winnerAsks.Select(a => a.Tell).ToList(),
                    ExpectIds = winnerAsks.Where(a => a.NanoId.HasValue).Select(a => a.NanoId!.Value).ToList(),
                };
                plan.Work.Add(ask);
                plan.Print.Add($"  {++plan.PrintNo}: ASK {string.Join(" ", ask.Tells)} - {ask.Why} " +
                    $"({winnerAsks.Sum(a => a.Ncu)} NCU; grants {GainsText(winnerAsks)}; {ledger.Free} free after).");
                foreach (var e in winnerAsks)
                {
                    ledger.Apply(e, peakAsk: true);
                }
            }

            PlanSummon(plan, role, label, winner.FormulaId, winner.Name, winner.Template,
                winnerAsks.Where(a => a.NanoId.HasValue).Select(a => a.NanoId!.Value).ToList());
            plan.Whys.Add($"{label} pet {(standing > 0 ? $"below its best template ({standing} -> {winner.Template})" : "missing")}");
            plannedTier[role] = winner.Template;
        }

        // ALL peak asks come off (the 3-minute wrangle exists to be cancelled) before the floor.
        var peaks = ledger.PeakAsks().ToList();
        if (peaks.Count > 0)
        {
            plan.Work.Add(new DanceStep
            {
                Kind = StepKind.Cancel,
                Why = "peak asks - the obedience floor comes next",
                CancelIds = peaks.Select(e => e.NanoId!.Value).ToList(),
            });
            plan.Print.Add($"  {++plan.PrintNo}: CANCEL {string.Join(" ", peaks.Select(e => e.Tell))} - peak asks, the obedience floor comes next ({peaks.Sum(e => e.Ncu)} NCU back, {ledger.Free + peaks.Sum(e => e.Ncu)} free).");
            foreach (var e in peaks)
            {
                ledger.Revoke(e);
            }
        }

        // The floor from the TARGETED tiers (plus the standing pets the plan did not re-summon)
        // - the tiers the plan itself decided, not whatever happened to land.
        var tierBits = new List<string>();
        var totals = new Dictionary<int, int>();
        foreach (var (line, role, _, label) in Lines)
        {
            var template = plannedTier.TryGetValue(role, out var t) ? t : StandingTemplate(me, role, line);
            if (template <= 0)
            {
                continue;
            }

            tierBits.Add($"{label} {template}");
            var floor = FloorOfMins(TierMins(me, line, template));
            if (floor == null)
            {
                continue;
            }

            foreach (var kv in floor)
            {
                totals[kv.Key] = Math.Max(totals.GetValueOrDefault(kv.Key), kv.Value);
            }
        }

        if (totals.Count == 0)
        {
            plan.Print.Add("  - SKIP obedience floor - no gated tier up or planned.");
        }
        else
        {
            var floorPlan = _catalog.GoalPlanAgainst(me, ledger.Free, totals, longTermOnly: true, ledger.Stats, ledger.Lines);
            if (floorPlan.Count == 0)
            {
                plan.Print.Add("  - SKIP obedience floor - not coverable (the planner logged the gaps); the floors stay open.");
                plan.Whys.Add("obedience floor short");
            }
            else
            {
                var ask = new DanceStep
                {
                    Kind = StepKind.Ask,
                    Why = $"the obedience floor (80% of tiers {string.Join(", ", tierBits)})",
                    Tells = floorPlan.Select(a => a.Tell).ToList(),
                    ExpectIds = floorPlan.Where(a => a.NanoId.HasValue).Select(a => a.NanoId!.Value).ToList(),
                };
                plan.Work.Add(ask);
                plan.Print.Add($"  {++plan.PrintNo}: ASK {string.Join(" ", ask.Tells)} - {ask.Why} ({floorPlan.Sum(a => a.Ncu)} NCU).");
                foreach (var e in floorPlan)
                {
                    ledger.Apply(e, peakAsk: false);
                    if (e.NanoId.HasValue)
                    {
                        plan.KeptIds.Add(e.NanoId.Value);
                    }
                }
            }
        }

        // The SELF-DECIDED comfort fill, on what the plan leaves (owner, 2026-10-06: the skill
        // buffs by conf mode - weapon mode matches the WIELDED weapon's own skills, nano mode
        // the attack-nano skill).
        var attackNano = string.Equals((_config.ComfortMode ?? "").Trim(), "nano", StringComparison.OrdinalIgnoreCase);
        var weaponKeys = attackNano ? Array.Empty<string>() : EquippedWeaponSkillKeys().ToArray();
        var comfort = _catalog.PlanForComfort(me, attackNano, weaponKeys, ledger.Free, ledger.Lines);
        if (comfort.Count > 0)
        {
            plan.Work.Add(new DanceStep
            {
                Kind = StepKind.Ask,
                Why = $"the comfort fill ({(attackNano ? "attack-nano skills" : "weapon skills")})",
                Tells = comfort,
                ExpectIds = comfort.Select(t => _catalog.Find(t)?.NanoId).Where(id => id.HasValue).Select(id => id!.Value).ToList(),
            });
            plan.Print.Add($"  {++plan.PrintNo}: ASK {string.Join(" ", comfort)} - the comfort fill ({(attackNano ? "attack-nano skills" : "weapon skills")}).");
            foreach (var tell in comfort)
            {
                var id = _catalog.Find(tell)?.NanoId;
                if (id.HasValue)
                {
                    plan.KeptIds.Add(id.Value);
                }
            }
        }
        else
        {
            plan.Print.Add("  - SKIP comfort fill - nothing to add.");
        }

        // The pet-buff stage, dry-run (built for real when its step runs - the roster may still
        // change under the plan's summons).
        var (petBuffs, diag) = BuildPetBuffQueue(me);
        if (petBuffs.Count > 0)
        {
            plan.Work.Add(new DanceStep { Kind = StepKind.PetBuffs, Why = diag });
            plan.Print.Add($"  {++plan.PrintNo}: PET BUFFS - {diag}.");
        }
        else
        {
            plan.Print.Add($"  - SKIP pet buffs ({diag}).");
        }

        var kept = plan.KeptIds.Select(id => _catalog.FindById(id)?.Tell ?? $"id{id}").ToList();
        plan.Print.Add($"EXTBUFF: plan end - kept for renewal ({kept.Count}): {string.Join(" ", kept)}.");
        return plan;
    }

    /// <summary>Add a line's summon step to the plan (and its printout line). The ids it
    /// requires are the peak stack it feeds on - the empty list for the gate-less summons.</summary>
    private static void PlanSummon(DancePlan plan, PetType role, string label, int nanoId, string name, int template,
        List<int>? requiresIds = null)
    {
        plan.Work.Add(new DanceStep
        {
            Kind = StepKind.Summon,
            Why = $"{label} pet at peak",
            Role = role,
            TargetNanoId = nanoId,
            TargetName = name,
            TargetTemplate = template,
            ExpectIds = requiresIds ?? new List<int>(),
        });
        plan.Print.Add($"  {++plan.PrintNo}: SUMMON {label} - terminate the old pet, request '{name}' (template {template} planned).");
    }

    /// <summary>
    ///     The planner's simulated ledger: what the plan's own steps have produced so far. The
    ///     four pet-tier skills move with the parsed gains, the budget with the NCU costs; the
    ///     PEAK asks are tracked apart - only they are cancelled by the later steps (the ncu
    ///     gain stays, it serves every line).
    /// </summary>
    private sealed class Ledger
    {
        public readonly List<BuffEntry> Running = new();
        public readonly HashSet<int> Lines = new();
        public readonly Dictionary<int, int> Stats;
        public readonly int MaxNcu;
        public readonly string NcuBasis;
        private readonly HashSet<BuffEntry> _peak = new();

        public Ledger(Dictionary<int, int> cleanStats, int maxNcu, string ncuBasis)
        {
            Stats = cleanStats;
            MaxNcu = maxNcu;
            NcuBasis = ncuBasis;
            Free = maxNcu;
        }

        public int Free { get; private set; }

        /// <summary>The plan's first print line - the budget the whole plan runs under.</summary>
        public void PrintHeader(DancePlan plan, int ncuCost)
        {
            var basis = NcuBasis;
            if (ncuCost > 0)
            {
                basis += $"; {Free - ncuCost} free once the ncu ask ({ncuCost} NCU) sits";
            }

            plan.Print.Add($"EXTBUFF: dance plan - clean slate, predicted max {MaxNcu} NCU ({basis}).");
        }

        public IEnumerable<BuffEntry> PeakAsks()
        {
            return Running.Where(_peak.Contains);
        }

        public void Apply(BuffEntry e, bool peakAsk)
        {
            Running.Add(e);
            if (peakAsk)
            {
                _peak.Add(e);
            }

            if (e.NanoLine is > 0)
            {
                Lines.Add(e.NanoLine.Value);
            }

            Free -= e.Ncu;
            foreach (var g in e.Gains)
            {
                Stats[g.Key] = Stats.GetValueOrDefault(g.Key) + g.Value;
            }
        }

        public void Revoke(BuffEntry e)
        {
            Running.Remove(e);
            _peak.Remove(e);
            if (e.NanoLine is > 0 && !Running.Any(o => o.NanoLine == e.NanoLine))
            {
                Lines.Remove(e.NanoLine.Value);
            }

            Free += e.Ncu;
            foreach (var g in e.Gains)
            {
                Stats[g.Key] = Stats.GetValueOrDefault(g.Key) - g.Value;
            }
        }
    }

    /// <summary>
    ///     The NCU headroom the fixer's ncu nanos will give - the +MaxNCU family scanned out of
    ///     the pack (the Use modifier on stat MaxNCU), each tier LEVEL-LOCKED by its recipient
    ///     gate (owner, 2026-10-07: "use the ncu fixer nanos, the trigger is the level lock").
    ///     The predicted tier is the strongest gain whose lock sits below our level; a running
    ///     family member's gain is already inside the live MaxNCU and is subtracted back out.
    /// </summary>
    private (int Max, string? Tier, int Gain, int Lock) PredictNcuMax(LocalPlayer me)
    {
        var family = new List<(int Id, int Gain, int Lock)>();
        foreach (var nano in NanoLibrary.Nanos)
        {
            if (UseModifier(nano.NanoId, Stat.MaxNCU) is not { } gain || gain <= 0)
            {
                continue;
            }

            // The recipient level lock: the target-side Greater-than Level gate (stat 54).
            var lockLevel = -1;
            foreach (var action in nano.Actions)
            {
                foreach (var r in action.Requirements)
                {
                    if (r.Target == 18 && r.Stat == (int)Stat.Level && r.Operator == 2)
                    {
                        lockLevel = Math.Max(lockLevel, r.Value);
                    }
                }
            }

            if (lockLevel >= 0)
            {
                family.Add((nano.NanoId, gain, lockLevel));
            }
        }

        var familyIds = family.Select(f => f.Id).ToHashSet();
        var running = 0;
        foreach (var b in me.Buffs)
        {
            if (familyIds.Contains(b.Id) && UseModifier(b.Id, Stat.MaxNCU) is { } up)
            {
                running += up;
            }
        }

        var level = me.TryGetStat(Stat.Level, out var lv) ? lv : 0;
        var measured = me.TryGetStat(Stat.MaxNCU, out var max) ? max - running : 0;
        var best = family.Where(f => level > f.Lock).OrderByDescending(f => f.Gain).ThenByDescending(f => f.Lock).FirstOrDefault();
        return best.Id == 0
            ? (measured, null, 0, 0)
            : (measured + best.Gain, NanoLibrary.NameOf(best.Id), best.Gain, best.Lock);
    }

    /// <summary>The nano's Use-modifier on a stat, or null when it has none. The raw
    /// <see cref="ItemBase.UseModifiers" /> is a DIRECT indexer - most nanos carry no Use list
    /// at all, and touching it threw KeyNotFoundException across the whole pack scan.</summary>
    private static int? UseModifier(int nanoId, Stat stat)
    {
        if (!ItemData.Find(nanoId, out NanoItem ni) || ni == null ||
            !ni.Modifiers.TryGetValue(SpellListType.Use, out var use) ||
            !use.TryGetValue(stat, out var value))
        {
            return null;
        }

        return value;
    }

    /// <summary>One summon candidate: a (formula, branch) pair with its MERGED gate - the
    /// formula's own Greater-than pet-tier-skill reqs UNION the branch's - as absolute TOTALS.</summary>
    private sealed class SummonCandidate
    {
        public int Template;
        public Dictionary<int, int> Mins = new();
        public int FormulaId;
        public string Name = "";
        public int Ql;
    }

    /// <summary>
    ///     A line's candidates: EVERY learned formula of the line, crossed with its branch
    ///     templates. The heal and support lines are one formula per tier (gate on the formula,
    ///     branches fixed-template appearance pickers); the attack line self-scales inside one
    ///     formula (gate on each branch). The merged gate covers both shapes. Ordered top
    ///     template first, then cheapest total gate, then highest QL.
    /// </summary>
    private List<SummonCandidate> LineCandidates(LocalPlayer me, PetLine line)
    {
        var cands = new List<SummonCandidate>();
        if (!_summonIds.TryGetValue(line, out var ids))
        {
            return cands;
        }

        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            var nano = NanoLibrary.Find(nanoId);
            if (nano == null || nano.Summons.Count == 0)
            {
                continue;
            }

            var formulaMins = SkillMins(nano.Actions.SelectMany(a => a.Requirements));
            foreach (var group in nano.Summons.GroupBy(b => b.TemplateLevel))
            {
                var mins = new Dictionary<int, int>(formulaMins);
                foreach (var branch in group)
                {
                    foreach (var kv in BuffCatalog.BranchMins(branch))
                    {
                        mins[kv.Key] = Math.Max(mins.GetValueOrDefault(kv.Key), kv.Value);
                    }
                }

                if (mins.Count == 0)
                {
                    continue; // gates on nothing we can lift - not a tier the planner can aim at
                }

                cands.Add(new SummonCandidate
                {
                    Template = group.Key,
                    Mins = mins,
                    FormulaId = nanoId,
                    Name = ni.Name,
                    Ql = ni.Ql,
                });
            }
        }

        return cands
            .OrderByDescending(c => c.Template)
            .ThenBy(c => c.Mins.Values.Sum())
            .ThenByDescending(c => c.Ql)
            .ToList();
    }

    /// <summary>The Greater-than pet-tier-skill gates of a requirement list, as absolute
    /// TOTALS (strict gate ⇒ value + 1 - the client's "required to be at least 519").</summary>
    private static Dictionary<int, int> SkillMins(IEnumerable<NanoRequirement> reqs)
    {
        var mins = new Dictionary<int, int>();
        foreach (var r in reqs)
        {
            if (r.Operator == 2 && r.Stat is 127 or 128 or 130 or 131)
            {
                mins[r.Stat] = Math.Max(mins.GetValueOrDefault(r.Stat), r.Value + 1);
            }
        }

        return mins;
    }

    /// <summary>
    ///     The gate of a tier: the learned formula + branch sitting at exactly this template
    ///     level (highest QL wins a tie) - the pet's wire LEVEL identifies its tier the same
    ///     way (template ≈ level; the pet never tells which formula made it, owner: IMPORTANT).
    ///     Formula reqs UNION branch reqs, so a heal tier's gate comes off the formula, an
    ///     attack tier's off its branch.
    /// </summary>
    private Dictionary<int, int>? TierMins(LocalPlayer me, PetLine line, int level)
    {
        if (!_summonIds.TryGetValue(line, out var ids))
        {
            return null;
        }

        Dictionary<int, int>? best = null;
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

            var formulaMins = SkillMins(nano.Actions.SelectMany(a => a.Requirements));
            foreach (var branch in nano.Summons)
            {
                if (branch.TemplateLevel != level || ni.Ql <= matchQl)
                {
                    continue;
                }

                matchQl = ni.Ql;
                var mins = new Dictionary<int, int>(formulaMins);
                foreach (var kv in BuffCatalog.BranchMins(branch))
                {
                    mins[kv.Key] = Math.Max(mins.GetValueOrDefault(kv.Key), kv.Value);
                }

                best = mins;
            }
        }

        return best is { Count: > 0 } ? best : null;
    }

    /// <summary>The tier of the line's standing pet, via its wire level against the branch
    /// tables - 0 when the line is empty or the tier does not match any branch.</summary>
    private int StandingTemplate(LocalPlayer me, PetType role, PetLine line)
    {
        var pet = me.Pets.FirstOrDefault(p => p.Role == role);
        if (pet == null || !pet.TryGetStat(Stat.Level, out var level))
        {
            return 0;
        }

        return TierMins(me, line, level) != null ? level : 0;
    }

    /// <summary>The obedience floor of a tier gate: 80% of each total (PetBrain.ControlFloor)
    /// - what keeps a pet of this tier obedient.</summary>
    private static Dictionary<int, int>? FloorOfMins(IReadOnlyDictionary<int, int>? mins)
    {
        if (mins == null || mins.Count == 0)
        {
            return null;
        }

        var totals = new Dictionary<int, int>();
        foreach (var kv in mins)
        {
            var floor = (int)Math.Ceiling(kv.Value * 0.80);
            totals[kv.Key] = Math.Max(totals.GetValueOrDefault(kv.Key), floor);
        }

        return totals.Count > 0 ? totals : null;
    }

    /// <summary>The step the dance currently sits on, or null past the end.</summary>
    private DanceStep? CurrentStep()
    {
        return _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;
    }

    /// <summary>
    ///     Execute the next planned step. The plan was printed at dance start - this only
    ///     carries it out, logging each step's number as it goes.
    /// </summary>
    private void Advance(LocalPlayer me, PetBrain pet)
    {
        _queueIndex++;
        if (_queueIndex >= _queue.Count)
        {
            Finish(me, pet);
            return;
        }

        var step = _queue[_queueIndex];
        switch (step.Kind)
        {
            case StepKind.Travel:
                _travelDeadline = -1;
                _travelGoalSet = false;
                RunTravelStage(me, pet);
                break;

            case StepKind.TerminateBuffs:
                // EVERYTHING on me comes off (owner, 2026-10-06) - the clean slate the plan
                // budgeted against. The kept set was decided by the PLAN and stays.
                foreach (var buff in me.Buffs.ToList())
                {
                    me.CancelNano(buff.Id);
                    _logger.LogInformation($"EXTBUFF: cancelling {NanoLibrary.NameOf(buff.Id)} (terminate-buffs - a clean slate).");
                }

                Advance(me, _bank.Pet!);
                break;

            case StepKind.Ask:
                _logger.LogInformation($"EXTBUFF: step {_queueIndex + 1}: ASK - {step.Why}.");
                OpenOrSkip(me, step.Tells, step.Why);
                break;

            case StepKind.Cancel:
                _logger.LogInformation($"EXTBUFF: step {_queueIndex + 1}: CANCEL - {step.Why}.");
                foreach (var id in step.CancelIds)
                {
                    if (!me.Buffs.Find(id, out _))
                    {
                        continue; // never landed (a windowed-out session) - nothing to cancel, no noise
                    }

                    me.CancelNano(id);
                    _logger.LogInformation($"EXTBUFF: cancelling {NanoLibrary.NameOf(id)} ({step.Why}).");
                }

                Advance(me, _bank.Pet!);
                break;

            case StepKind.Summon:
                // The swap EXACTLY here (owner, 2026-10-06): terminate this line's OLD pet, then
                // summon its replacement - which casts fresh at the peak stats the plan stacked.
                // The same-line summon gate ("they don't overwrite") is what forces the
                // terminate. The next step's budget assumed this pet's NCU spent.
                // GUARDED: the stack this summon feeds on must be in - a session that failed to
                // open, or landed short, must not end in a pet cast at base stats (the
                // buff-first dance exists to prevent exactly that, owner 2026-10-06). The line
                // waits for the next dance.
                var shortStack = step.ExpectIds.Where(id => !me.Buffs.Find(id, out _)).ToList();
                if (shortStack.Count > 0)
                {
                    _logger.LogWarning(
                        "EXTBUFF: step " + (_queueIndex + 1) + $": SUMMON {LineOf(step.Role).Label} dropped - the stack it feeds on is not in (" +
                        string.Join(", ", shortStack.Select(NanoLibrary.NameOf)) + ").");
                    Advance(me, _bank.Pet!);
                    break;
                }

                _logger.LogInformation(
                    $"EXTBUFF: step {_queueIndex + 1}: SUMMON {LineOf(step.Role).Label} - terminate + request '{step.TargetName}' (template {step.TargetTemplate} planned).");
                _pendingRole = step.Role;
                pet.TerminateRole(me, step.Role);
                _danceStage = DanceStage.WaitingPet;
                _waitDeadline = _clock + WaitPetSec;
                _sinceResummon = 0;
                pet.RequestSummon(step.Role);
                break;

            case StepKind.PetBuffs:
                // Built for real now - the roster just changed under the plan's summons.
                _logger.LogInformation($"EXTBUFF: step {_queueIndex + 1}: PET BUFFS - {step.Why}.");
                _petBuffQueue.Clear();
                _petBuffQueue.AddRange(BuildPetBuffQueue(me).Queue);
                if (_petBuffQueue.Count > 0)
                {
                    _danceStage = DanceStage.PetBuffing;
                }
                else
                {
                    Advance(me, _bank.Pet!);
                }

                break;
        }
    }

    /// <summary>
    ///     The PET BUFF stage (owner, 2026-10-06): buff the PETS themselves. Per pet-buff line
    ///     (216/217/225/810/816/817/843): the best LEARNED nano by StackingOrder that we can
    ///     cast - no overequip math here - cast on the line's target pets (810 the support pet,
    ///     the damage/initiative lines the attack pet, 816/817/843 all pets). A pet already
    ///     carrying the line at equal or better stacking is skipped. PURE: the planner dry-runs
    ///     it for the printed plan; the PetBuffs step builds it for real (the roster may still
    ///     have changed under the plan's summons) and pumps it one cast at a time
    ///     (DanceStage.PetBuffing).
    /// </summary>
    private (List<PetBuffCast> Queue, string Diag) BuildPetBuffQueue(LocalPlayer me)
    {
        var queue = new List<PetBuffCast>();
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

                queue.Add(new PetBuffCast
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

        return (queue, string.Join("; ", diag));
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

    /// <summary>
    ///     The step after a dance session closes. Only ASK steps open sessions; the plan
    ///     promised what each ask would land, so a shortfall is a PLAN DEVIATION - logged here,
    ///     and acted on by the summon step itself (it checks the same ids and drops itself - a
    ///     session that closed on its window cast nothing, and summoning then brings the pet
    ///     out at base stats, exactly what the buff-first dance exists to prevent, owner
    ///     2026-10-06). The dropped summon's line waits for the next dance.
    /// </summary>
    private void AfterSession(LocalPlayer me, PetBrain pet)
    {
        var step = CurrentStep();
        if (step is { Kind: StepKind.Ask })
        {
            var missing = step.ExpectIds.Where(id => !me.Buffs.Find(id, out _)).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "EXTBUFF: the ask stack did not land (" + string.Join(", ", missing.Select(NanoLibrary.NameOf)) +
                    $") - plan deviation at step {_queueIndex + 1} ({step.Why}).");
            }
        }

        Advance(me, pet);
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

    /// <summary>Whether the TARGET formula itself is castable on the CURRENT stats - kept for
    /// diagnostics: the planner's gates come from the pack's requirements, this asks the same
    /// req checker the client uses. This names ONE formula, never "whatever is castable": the
    /// plan never downgrades.</summary>
    private static bool SummonCastable(LocalPlayer me, int nanoId)
    {
        if (!ItemData.Find(nanoId, out NanoItem ni) || ni == null)
        {
            return false;
        }

        try
        {
            return ni.MeetsUseReqs(me, false, true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The planner's stat names (127 MatMet, 128 BioMet, 130 MatCrea, 131 SpaceTime).</summary>
    private static string StatName(int stat)
    {
        return stat switch
        {
            127 => "MatMet",
            128 => "BioMet",
            130 => "MatCrea",
            131 => "SpaceTime",
            _ => $"stat {stat}",
        };
    }

    /// <summary>The net gains of a plan step's entries, per stat - "BioMet +140, SpaceTime +140".</summary>
    private static string GainsText(IReadOnlyList<BuffEntry> entries)
    {
        var per = new Dictionary<int, int>();
        foreach (var e in entries)
        {
            foreach (var g in e.Gains)
            {
                per[g.Key] = per.GetValueOrDefault(g.Key) + g.Value;
            }
        }

        return per.Count == 0 ? "no stat gains" : string.Join(", ", per.OrderBy(kv => kv.Key).Select(kv => $"{StatName(kv.Key)} +{kv.Value}"));
    }

    /// <summary>A branch's shortfalls against the ledger - "BioMet +118, SpaceTime met".</summary>
    private static string Shortfalls(IReadOnlyDictionary<int, int> mins, IReadOnlyDictionary<int, int> stats)
    {
        return string.Join(", ", mins.OrderBy(kv => kv.Key).Select(kv =>
        {
            var gap = kv.Value - stats.GetValueOrDefault(kv.Key);
            return gap > 0 ? $"{StatName(kv.Key)} +{gap}" : $"{StatName(kv.Key)} met";
        }));
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