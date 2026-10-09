// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MetaphysicistExternalBuffingBrain.cs
//
// Last modified: 2026-10-07
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     METAPHYSICIST EXTERNAL BUFFING BRAIN - the buff-first summon, driven from the buffing
///     side (PETBRAIN-DESIGN.md port order 4: "the buff step depends on the ExternalBuffingBrain
///     family... PetBrain waits on whichever applies"). The MP pet lines gap on THREE skill
///     pairs - attack MC(130)+TS(131), heal BioMet(128)+TS(131), mezz MatMet(127)+TS(131) - and
///     once a second this brain looks for a line whose best learned summon is one nano-skill gap
///     away (level/expansion/credit gaps are NOT buffable - no ask). When one is found and the
///     ask gates pass (PetAutoBuff on, a bot name, un-teamed, past the retry window), it opens
///     the PET-FIRST BUFF CYCLE (PetFirstBuffCycle) as a GUIDED BuffBotController session:
///     clear roster, strip every running buff, the best NCU buff (and REMEMBER the NCU), then
///     per line the best primary+TS stack as ONE multi-code tell, wait ALL landed, cast the
///     line's pet, next line. The pet brain is held petless the whole way (the session gate in
///     PetBrain) and the per-line casts punch that hold via CastLineRequest.
///     Coordination runs even with PetAutoBuff off (an owner-started 'buffs pet' session gets
///     the same hold/release/request treatment around it); ASKING is what PetAutoBuff gates.
///     The MP contributes the LineSpec table; the cycle itself is shared with the Engineer and
///     Bureaucrat pet classes and extends by appending steps. RE-ENTRY (owner, 2026-10-08) is
///     gated by the cycle's OUTCOME ledger: the persistent buffs it left on the bot (NCU, floor,
///     comfort) are compared against the actual buff state and the pipeline only re-opens when an
///     entry is gone or worn to 25% of its landing baseline - the skill want alone must not
///     re-open it, since the cycle leaves its own gap behind and would chase itself forever
///     (unless a line needs MORE than that steady gap: a better pet became learnable). Runs on
///     the update thread (BotLoop), like every brain.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.ExternalBuffing, Profession.Metaphysicist)]
public sealed class MetaphysicistExternalBuffingBrain : ExternalBuffingBrain
{
    /// <summary>The MP's three pet summon lines.</summary>
    private enum PetLine
    {
        Attack,
        Heal,
        Mezz
    }

    // One row per line: the summon line (strain), the wire role it fills, the PRIMARY skill
    // beside SpaceTime, and the log label. Order = the pet brain's fill priority.
    private static readonly (PetLine Line, PetType Role, NanoLine Strain, Stat Primary, string Label)[] Lines =
    {
        (PetLine.Attack, PetType.Attack, NanoLine.AttackPets, Stat.MaterialCreation, "attack"),
        (PetLine.Heal, PetType.Heal, NanoLine.HealPets, Stat.BiologicalMetamorphosis, "heal"),
        (PetLine.Mezz, PetType.Support, NanoLine.SupportPets, Stat.MaterialMetamorphosis, "mezz"),
    };

    // Decide roughly once a second, not every frame.
    private const double DecideEverySec = 1.0;

    // Don't re-ask after a closed session more often than this (Engineer BuffRetrySec parity):
    // a landed buff clears the want; an unlanded one waits out the cooldown.
    private const double AskRetrySec = 300.0;

    private readonly BrainBank _bank;
    private readonly BuffBotController _buffBot;
    private readonly BuffCatalog _catalog;
    private readonly AccountInfo _config;
    private readonly HealController _heal; // the pet-buff step's recharge demand lands here

    private readonly Dictionary<PetLine, HashSet<int>> _summonIds = new();
    private double _sinceDecide;
    private double _askedAt = -1e9;
    private int _opportunityLoggedFor; // the want id we last logged a buff opportunity for
    private int _unreachableFor; // the want id we last logged a beyond-the-ceiling stay-out for
    private int _lockWaitFor; // the want id we last logged a nanoline-lock wait for
    private bool _sessionSeen;         // a buff session ran last tick - its end releases the hold
    private bool _claimed;             // the arbiter is ours at ControlPriority.ExternalBuffing

    // THE OUTCOME GATE (owner, 2026-10-08): what the last AUTO cycle left on the bot (the cycle's
    // Outcome ledger - NCU buff, floor set, comfort fill) and the per-line skill gap it leaves
    // behind ON PURPOSE (the floor step strips the summon stacks). That steady gap equals itself
    // forever, so the want alone must not re-open the pipeline: the brain re-enters only when an
    // outcome buff is gone or worn to <=25% of its landing baseline, or a line needs MORE than its
    // steady gap (a better pet became learnable - the upgrade must not wait out the decay).
    private const double RebuffFraction = 0.25;
    private IReadOnlyList<PetFirstBuffCycle.OutcomeEntry> _outcome = Array.Empty<PetFirstBuffCycle.OutcomeEntry>();
    private readonly Dictionary<PetLine, int> _steadyNeeds = new Dictionary<PetLine, int>();
    private int _suppressedFor; // the want id we last logged the outcome gate's stay-out for

    // THE CYCLE (owner, 2026-10-07): the pet-first pipeline (strip -> NCU -> per line buff+cast)
    // running as a guided session; the brain only decides WHEN to open it and hands its turns to
    // the controller. The MP's LineSpec table is fixed in the ctor.
    private PetFirstBuffCycle? _cycle;
    private IReadOnlyList<PetFirstBuffCycle.LineSpec> _lineSpecs = new List<PetFirstBuffCycle.LineSpec>();
    private const double CycleCapSec = 600.0; // strip + NCU + three (tell, land, summon) passes outlives the plain 6 min

    public MetaphysicistExternalBuffingBrain(ILogger<MetaphysicistExternalBuffingBrain> logger,
        ControlArbiter controlArbiter, BrainBank bank, BuffBotController buffBot, BuffCatalog catalog,
        AccountInfo config, HealController heal)
        : base(logger, controlArbiter)
    {
        _bank = bank;
        _buffBot = buffBot;
        _catalog = catalog;
        _config = config;
        _heal = heal;
        _lineSpecs = Lines.Select(l => new PetFirstBuffCycle.LineSpec(l.Role, l.Label, l.Primary)).ToList();
        ScanSummonLines();
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        var pet = _bank.Pet;
        if (pet == null)
        {
            return false; // no pet brain this session - nothing to hand the buffs to
        }

        // The cycle advances every frame (its steps time against their own clock); the guided
        // session machinery below carries its wire turns.
        _cycle?.Advance(me, dt);

        // A buff session is running (ours, or the owner's 'buffs pet'): the pet brain stays
        // petless until it resolves.
        if (_buffBot.Active)
        {
            _sessionSeen = true;
            pet.SetSummonHold(true);
            return Claim();
        }

        // The session just ended - buffs landed, or the window closed without them. Either way
        // the pet brain may cast again, and NOW (not on its cadence): the whole point of the
        // dance is summoning at the new skills.
        if (_sessionSeen)
        {
            _sessionSeen = false;
            if (_cycle != null)
            {
                // OUR auto cycle is over: adopt its outcome ledger (the persistent buffs it left
                // on us) and record the per-line gap it leaves behind - the steady state every
                // later want is measured against by the outcome gate below.
                _outcome = _cycle.Outcome;
                _steadyNeeds.Clear();
                foreach (var (line, _, _, primary, _) in Lines)
                {
                    var (_, need) = BestLearned(me, line, primary);
                    _steadyNeeds[line] = need;
                }
            }

            _cycle = null;
            _suppressedFor = 0; // a fresh episode gets a fresh stay-out line
            pet.SetSummonHold(false);
            pet.RequestSummon();
            Release();
            return false;
        }

        // Watching: once a second, is a line one nano-skill gap from a better pet?
        _sinceDecide += dt;
        if (_sinceDecide < DecideEverySec)
        {
            return false;
        }

        _sinceDecide = 0;

        var want = FindWant(me);
        if (want == null)
        {
            return MaybeQoLFill(me, pet);
        }

        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            if (_opportunityLoggedFor != want.Value.Pet.Id)
            {
                _opportunityLoggedFor = want.Value.Pet.Id;
                _logger.LogInformation(
                    $"EXTBUFF: '{want.Value.Pet.Name}' (ql {want.Value.Pet.Ql}) needs +{want.Value.Need} nano skills - " +
                    "buffs would unlock it ('buffs pet' near the buff bot, or set PetAutoBuff).");
            }

            return false; // the owner controls buffing; the pet brain runs its own cadence
        }

        if (Team.IsInTeam)
        {
            return false; // the handshake needs us un-teamed - the bot must be the one to team us
        }

        if (_clock - _askedAt < AskRetrySec)
        {
            return false; // asked recently - give the last session's outcome time to show
        }

        // THE OUTCOME GATE (owner, 2026-10-08): compare the ACTUAL buff state against the outcome
        // the last cycle precalculated. The steady gap equals itself forever (the floor step strips
        // the summon stacks to make room for the floor set), so without this the want re-opened the
        // pipeline every retry window and the bot stripped its own fresh buffs over and over.
        // Re-enter only when an outcome buff is gone or worn to the rebuff fraction - or when a
        // line needs MORE than its steady gap (UpgradeOpened): a better pet became learnable.
        var upgrade = UpgradeOpened(me);
        var outcomeAlive = _outcome.Count > 0 && PetFirstBuffCycle.OutcomeAlive(me, _outcome, RebuffFraction);
        if (!upgrade && outcomeAlive)
        {
            if (_suppressedFor != want.Value.Pet.Id)
            {
                _suppressedFor = want.Value.Pet.Id;
                _logger.LogInformation(
                    $"EXTBUFF: '{want.Value.Pet.Name}' still needs +{want.Value.Need} nano skills, but the last " +
                    $"cycle's outcome is standing ({PetFirstBuffCycle.OutcomeReport(me, _outcome)}) - staying out " +
                    "until it wears to 25%.");
            }

            return false;
        }

        if (!upgrade && _outcome.Count > 0)
        {
            _logger.LogInformation(
                $"EXTBUFF: the outcome has worn to the rebuff line ({PetFirstBuffCycle.OutcomeReport(me, _outcome)}) - " +
                "re-entering the pipeline.");
        }

        // THE LINE LOCKS (owner, 2026-10-08): a pet cast just before the pipeline - the pet brain's
        // own cadence, or one inherited across a relog - locks its nano line for 120 s (the wire's
        // own LockDuration, seeded by every FullCharacter and taught by our own casts), and the
        // cycle's peak re-summon of that line was refused mid-dance. Open only with all three pet
        // lines free; the decide tick re-checks every second. The QoL fill is NOT held by this: it
        // casts no pets, and it is exactly what should run while a lock runs out.
        var lockLeft = Lines.Max(l => me.PetLineLockLeft(l.Strain));
        if (lockLeft > 0)
        {
            if (_lockWaitFor != want.Value.Pet.Id)
            {
                _lockWaitFor = want.Value.Pet.Id;
                _logger.LogInformation(
                    $"EXTBUFF: a pet nanoline is still locked ({lockLeft:0}s of the 120s left) - holding the pipeline until it clears.");
            }

            return false;
        }

        // THE PET-FIRST CYCLE (owner, 2026-10-07): the pipeline runs as a GUIDED session - clear
        // roster, strip buffs, NCU buff (and remember the NCU), then per line: the best
        // primary+TS stack as ONE multi-code tell, wait ALL landed, cast the line's pet, next
        // line. The pet brain is held petless by the session gate; the per-line casts punch that
        // hold (CastLineRequest). The want above was only the TRIGGER - the cycle re-plans per
        // line against the remembered NCU.
        _cycle = new PetFirstBuffCycle(pet, _catalog, _logger, _lineSpecs, _heal);
        if (!_buffBot.RequestGuidedBuffs(
                $"pet-first cycle ({want.Value.Label}-first for {want.Value.Pet.Name})",
                _cycle.NextTurn, CycleCapSec))
        {
            _cycle = null; // cannot start (active/teamed) - the pet brain summons what it can
            return false;
        }

        _askedAt = _clock;
        pet.SetSummonHold(true);
        _logger.LogInformation(
            $"EXTBUFF: opening the pet-first cycle - trigger: '{want.Value.Pet.Name}' " +
            $"({want.Value.Label}, needs +{want.Value.Need} nano skills).");
        return Claim();
    }

    /// <summary>
    ///     THE QoL FILL (owner, 2026-10-08): once the ROSTER is up - however it came up, the full
    ///     cycle or the pet brain's own cadence (a fresh login, a re-summon after a pet death) -
    ///     the obedience floor and then the comfort fill (musts, then nice-to-haves) must be
    ///     asked for. The full cycle leaves them behind as its outcome; with no tier want there
    ///     was nothing to open a session, and the bot stood there with three pets and zero QoL
    ///     buffs (10:42, 2026-10-08). Same gates as a want (config, team, retry) and the SAME
    ///     outcome ledger: while the set stands the fill stays quiet, and it re-enters when it
    ///     wears to the rebuff line. The pets stay out through it - no swaps.
    /// </summary>
    private bool MaybeQoLFill(LocalPlayer me, PetBrain pet)
    {
        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName) || Team.IsInTeam)
        {
            return false; // the same handshakes a want needs: the bot configured, and us un-teamed
        }

        if (_clock - _askedAt < AskRetrySec)
        {
            return false; // asked recently - give the last session's outcome time to show
        }

        if (!Lines.All(l => me.Pets.Any(p => p.Role == l.Role)))
        {
            return false; // the roster is still filling - the pet brain owns the body until it is up
        }

        if (PetFirstBuffCycle.OutcomeAlive(me, _outcome, RebuffFraction))
        {
            return false; // the floor + comfort set is standing - quiet until it wears
        }

        var fill = new PetFirstBuffCycle(pet, _catalog, _logger, _lineSpecs, _heal, floorOnly: true);
        foreach (var (_, role, _, _, _) in Lines)
        {
            if (pet.LastSummonNanoFor(role) is int formula)
            {
                fill.RecordSnapshot(role, formula, me); // the floor math keys on the formulas worn
            }
        }

        if (!_buffBot.RequestGuidedBuffs("floor + QoL fill (roster up)", fill.NextTurn, CycleCapSec))
        {
            return false; // cannot start (active/teamed) - the next decide tick tries again
        }

        _cycle = fill;
        _askedAt = _clock;
        pet.SetSummonHold(true); // no pet-brain summons mid-fill: the step clears every buff first
        _logger.LogInformation("EXTBUFF: roster up, the floor/QoL set is missing - opening the floor + comfort fill (no pet swaps).");
        return Claim();
    }

    // ---- The want ---------------------------------------------------------------------------

    /// <summary>
    ///     The first line (pet-brain fill order: attack, heal, mezz) whose best learned summon is
    ///     blocked by its skill pair alone - and only when asking can CHANGE something:
    ///       - the wanted tier must be BETTER than the tier the line already wears (the last
    ///         summon's formula): an up-but-inferior pet is the roster the swap replaces, an equal
    ///         one is the designed steady state - the 0.8 floor leaves exactly that residual gap on
    ///         purpose, and chasing it re-opened the pipeline a minute after the very pets cast
    ///         (owner, 2026-10-08 10:27: all three pets up at 10:26, then 'Calling of Restite
    ///         needs +92' tore them down again; after a restart the outcome ledger is empty, so
    ///         nothing else vetoed it);
    ///       - and the line's best affordable stack must REACH the wanted tier: current skills
    ///         plus the lift of the not-yet-running plan entries, on BOTH stats of the pair. A
    ///         tier beyond the buff bot's ceiling is not ours to summon however learnable it is.
    ///     The scan's math is stat-based, so without a skill gap it stays quiet and a maxed
    ///     roster is never disturbed.
    /// </summary>
    private (NanoItem Pet, int Need, string Label)? FindWant(LocalPlayer me)
    {
        foreach (var (line, role, _, primary, label) in Lines)
        {
            var (pet, need) = BestLearned(me, line, primary);
            if (pet == null || need <= 0)
            {
                continue;
            }

            // ALREADY OUT, EQUAL OR BETTER: the pet the line wears was cast at skills that met
            // this very formula - a want at or below it is the floor's own steady gap.
            if (me.Pets.Any(p => p.Role == role)
                && _bank.Pet?.LastSummonNanoFor(role) is int lastId
                && ItemData.Find(lastId, out NanoItem worn) && pet.Ql <= worn.Ql)
            {
                continue;
            }

            var (reaches, liftPrimary, liftTs) = StackReaches(me, primary, pet);
            if (!reaches)
            {
                if (_unreachableFor != pet.Id)
                {
                    _unreachableFor = pet.Id;
                    _logger.LogInformation(
                        $"EXTBUFF: '{pet.Name}' (ql {pet.Ql}) needs +{need} nano skills - beyond even the fresh " +
                        $"stack (+{liftPrimary} {primary}/+{liftTs} TS on top of current); not asking. A level or " +
                        "a new formula has to close in first.");
                }

                continue;
            }

            return (pet, need, label);
        }

        return null;
    }

    /// <summary>
    ///     Can the line's best affordable stack close the wanted formula's gap? Current stats
    ///     already carry every running buff, so only the plan entries NOT running lift further
    ///     (PlanPetLine is the same planner the cycle asks with, so the ceiling here is the
    ///     ceiling the cycle could actually buy).
    /// </summary>
    private (bool Reaches, int LiftPrimary, int LiftTs) StackReaches(LocalPlayer me, Stat primary, NanoItem pet)
    {
        if (!_catalog.Loaded)
        {
            return (true, 0, 0); // no catalog to reason with - keep the old trigger
        }

        var budget = BuffCatalog.FreeNcu(me, me.TryGetStat(Stat.MaxNCU, out var maxNcu) ? maxNcu : 0);
        var plan = _catalog.PlanPetLine(me, (int)primary, (int)Stat.SpaceTime, budget, PetFirstBuffCycle.IsSlOrLe(me));
        var liftPrimary = 0;
        var liftTs = 0;
        foreach (var e in plan)
        {
            if (me.Buffs != null && e.LandIds.Count > 0 && me.Buffs.Any(r => e.LandIds.Contains(r.Id)))
            {
                continue; // already running: its lift is inside the current stats
            }

            liftPrimary += BuffCatalog.GainFor(e, (int)primary);
            liftTs += BuffCatalog.GainFor(e, (int)Stat.SpaceTime);
        }

        var curPrimary = me.TryGetStat(primary, out var p) ? p : 0;
        var curTs = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
        var reaches = SkillReq(pet, primary) - curPrimary <= liftPrimary &&
                      SkillReq(pet, Stat.SpaceTime) - curTs <= liftTs;
        return (reaches, liftPrimary, liftTs);
    }

    /// <summary>
    ///     True when any line's summon needs MORE skill lift than the steady gap the last cycle
    ///     left behind (empty table: treat any want as an upgrade - nothing is recorded yet).
    ///     That is a better pet having become learnable - levelled, new formula uploaded, a debuff
    ///     wore off - and the upgrade must not wait out the outcome's 25% decay. Lines sitting at
    ///     their steady gap keep quiet; that gap is the cycle's own design, not a problem to fix.
    /// </summary>
    private bool UpgradeOpened(LocalPlayer me)
    {
        foreach (var (line, _, _, primary, _) in Lines)
        {
            var (_, need) = BestLearned(me, line, primary);
            if (need > _steadyNeeds.GetValueOrDefault(line, 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The best learned summon of the line (highest QL - the Engineer's wrangle parity) and
    ///     how much nano-skill lift it still needs. A non-skill gap (level, expansion, credits,
    ///     NCU) reads as need 0 only when the skills are already met - buffs fix exactly the
    ///     skill part, so that is all this brain asks for.
    /// </summary>
    private (NanoItem?, int) BestLearned(LocalPlayer me, PetLine line, Stat primary)
    {
        if (!_summonIds.TryGetValue(line, out var ids) || ids.Count == 0)
        {
            return (null, 0);
        }

        NanoItem? best = null;
        var need = 0;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            if (best != null && ni.Ql <= best.Ql)
            {
                continue;
            }

            var curPrimary = me.TryGetStat(primary, out var p) ? p : 0;
            var curTs = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
            need = Math.Max(Math.Max(0, SkillReq(ni, primary) - curPrimary),
                Math.Max(0, SkillReq(ni, Stat.SpaceTime) - curTs));
            best = ni;
        }

        return (best, need);
    }

    // ---- Data -------------------------------------------------------------------------------

    /// <summary>The summon formulas per line, scanned from the whole nano library by strain.</summary>
    private void ScanSummonLines()
    {
        foreach (var (line, _, _, _, _) in Lines)
        {
            _summonIds[line] = new HashSet<int>();
        }

        if (!NanoLibrary.Loaded || NanoLibrary.Nanos.Count == 0)
        {
            _logger.LogWarning("EXTBUFF: nano library empty - the MP buff-first watcher stays off.");
            return;
        }

        foreach (var (line, _, strain, _, _) in Lines)
        {
            foreach (var nano in NanoLibrary.InStrain((int)strain))
            {
                _summonIds[line].Add(nano.NanoId);
            }
        }
    }

    // A summon's requirement on one stat, from its cast criteria (GreaterThan means stat > N,
    // so the requirement is N + 1). 0 when the nano has no such criterion. (The Engineer's
    // PetReq, generalized to any stat.)
    private static int SkillReq(NanoItem nano, Stat stat)
    {
        var req = 0;
        if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            foreach (var c in use)
            {
                if (c.Operator == UseCriteriaOperator.GreaterThan && c.Param1 == (int)stat)
                {
                    req = Math.Max(req, c.Param2 + 1);
                }
            }
        }

        return req;
    }

    // ---- The arbiter ------------------------------------------------------------------------

    /// <summary>Open the episode: hold the arbiter at ControlPriority.ExternalBuffing (idempotent).</summary>
    private bool Claim()
    {
        if (!_claimed)
        {
            _claimed = true;
            _controlArbiter.TakeControl(ControlPriority.ExternalBuffing);
        }

        return true;
    }

    /// <summary>Close the episode: release the arbiter if we were holding it (idempotent).</summary>
    private void Release()
    {
        if (_claimed)
        {
            _claimed = false;
            _controlArbiter.ReleaseControl();
        }
    }
}