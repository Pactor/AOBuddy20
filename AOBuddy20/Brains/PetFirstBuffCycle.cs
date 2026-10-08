// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PetFirstBuffCycle.cs
//
// Last modified: 2026-10-07
// Created:       2026-10-07
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Controlling;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
// MinLogLevel

namespace AOBuddy20.Brains;

/// <summary>
///     THE PET-FIRST BUFF CYCLE (owner, 2026-10-07) - the extendable step pipeline a pet class's
///     external-buffing brain runs against a public buff bot, shared by MP / Engineer / Bureaucrat
///     (each contributes its own <see cref="LineSpec" /> table; non-pet external buff brains never
///     touch this - their plain BuffBotController sessions are unchanged). The cycle, in order:
///     1. CLEAR BUFFS   - strip every running nano (RemoveFriendlyNano, the click-a-buff-away
///     packet; wire-verified per buff, stragglers tolerated) so the plan starts from a clean
///     NCU and skill slate,
///     2. NCU BUFF      - tell the bot for the best Max-NCU tier, and when it lands REMEMBER the
///     NCU - that remembered Max is the budget every line plan spends against,
///     3. PER LINE (attack, heal, support - the owner's table): plan the buff combination that
///     lifts the line's skill pair (e.g. MatCrea+TS) the most OVERALL and BALANCED
///     (BuffCatalog.PlanPetLine), send it as ONE multi-code tell ("cast tsmo mcmo 131 c2"),
///     wait until ALL landed, 500ms settle, then TERMINATE THIS LINE'S OLD PET - the slot
///     gate wants it free and a same-line recast is refused while it lives - while the REST
///     of the roster keeps fighting the whole cycle (owner, 2026-10-07) - and only then cast
///     the new pet at the peak, waiting for it on the wire with bounded re-issues. Before
///     each NEXT line the leftovers are FIXED (owner 2026-10-07): running singles that raise
///     neither of the line's skills are CANCELLED (the attack line's MatCrea chain before
///     the heal line; on froob there are no composites to cover both), the freed NCU is
///     tracked and priced into the budget, and the tell only asks for what is not already
///     up - composites, wrangles and attribute buffs raise both lines' skills and stay.
///     EXTENDABLE BY DESIGN: the cycle is just a list of <see cref="IStep" />s (BuildSteps) - a
///     later step (aura, perk setup, a second cast pass) is a new IStep appended there; a pet
///     class plugs in via its LineSpec table and the pet brain's CastLineRequest override. The
///     steps run through the BuffBotController's GUIDED session (invites, walking, timeouts,
///     end-recovery stay in one place); the brain that owns the cycle ticks Advance every frame
///     and hands NextTurn to the controller.
///     Runs on the update thread (BotLoop), like every brain.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public class PetFirstBuffCycle
{
    // Per-step waits. A tell batch lands in seconds; the team-cast NCU waits out invite + cast;
    // a summon needs its recast window before the pet shows on the wire. The per-line landing
    // wait has NO timeout by design - the cast waits for the whole ask (see WaitLand).
    private const double CastDelayTime = 0.5;
    private const double RosterTimeoutSec = 15.0, StripTimeoutSec = 10.0;
    private const double NcuWaitSec = 90.0, PetWaitSec = 30.0;
    private const double WaitLogEverySec = 30.0; // progress line cadence while a line waits for its stack
    private const double AskSettleSec = CastDelayTime; // after the LAST landing registers: the 500ms beat before the summon
    private const double PreCastWaitSec = CastDelayTime; // one more beat right before the summon: the server applies the last buff's stats
    private const double CastRetrySec = 1.0; // a refused cast never sets IsCasting - pace the retries

    // Expansion flag bits (Stat.Expansion): 2 = Shadowlands (PetBrain.IsPaid), 8 = Lost Eden.
    // Owner, 2026-10-07: "SL and/or LE flag set" is the entitlement for the SL-era composite
    // families (composite mochams / infuses / masteries / teachings).
    internal const int ExpShadowlands = 0x2, ExpLostEden = 0x8;

    // WHICH PET-BUFF LINES LAND ON WHICH PET (owner, 2026-10-07): the damage/initiative families
    // (216 Instill, 217 Chant, 225 Evocation) gate on NPCFamily==97 - the ATTACK pet only; the
    // defensive (816), nano-resist (817) and heal-delta (843) lines land on every manifestation.
    private static readonly IReadOnlyDictionary<PetType, int[]> PetBuffLines =
        new Dictionary<PetType, int[]>
        {
            [PetType.Attack] = new[] { 216, 217, 225, 816, 817, 843, },
            [PetType.Heal] = new[] { 816, 817, 843, },
            [PetType.Support] = new[] { 816, 817, 843, },
        };

    private readonly BuffCatalog _catalog;
    private readonly HealController _heal;
    private readonly List<LineSpec> _lines;
    private readonly ILogger _logger;

    private readonly PetBrain _pet;

    // THE CAST SNAPSHOTS (owner, 2026-10-07): per line, the formula cast and the caster's gate
    // stats AT the cast. The pet's own requirement per stat = min(formula gate, cast-time stat)
    // - fixed formulas are gate-bound, self-scaling ones are bound by the stats they were cast
    // with - and the floor phase lifts 80% of THAT (the obedience floor).
    private readonly Dictionary<PetType, Dictionary<int, int>> _petFloorByRole = new Dictionary<PetType, Dictionary<int, int>>();

    private readonly List<IStep> _steps;

    // THE CAST WINDOW (owner, 2026-10-07): after ANY of our nanos - the summon included - the
    // next cast waits that nano's attack+decay (+0.5s); while it runs the server refuses with
    // "Already executing nanoprogram". Set by the summon AND every pet-buff/floor cast; shared
    // so a cast in one step holds the steps after it.
    internal double GateUntil = double.NegativeInfinity;
    private string _doneWhy = "";
    private int _idx;

    private int? _rememberedMaxNcu;
    private double _t;

    public PetFirstBuffCycle(PetBrain pet, BuffCatalog catalog, ILogger logger, IReadOnlyList<LineSpec> lines,
        HealController heal)
    {
        _pet = pet;
        _catalog = catalog;
        _logger = logger;
        _lines = lines.ToList();
        _heal = heal;
        _steps = BuildSteps();
        _logger.LogInformation(
            $"PETCYCLE: open - {_steps.Count} steps: {string.Join(" -> ", _steps.Select(s => s.Describe))}.");
    }

    public bool Done { get; private set; }

    internal static bool IsSlOrLe(LocalPlayer me)
    {
        return me.TryGetStat(Stat.Expansion, out var e) && (e & (ExpShadowlands | ExpLostEden)) != 0;
    }

    /// <summary>
    ///     THE PIPELINE. Subclasses (Engineer / Bureaucrat cycles) extend here: insert or
    ///     append steps around these; the contract is only <see cref="IStep" />. No whole-roster
    ///     clear: each line terminates its OWN old pet right before the new summon (the swap), so
    ///     the rest of the roster keeps fighting the whole cycle (owner, 2026-10-07).
    /// </summary>
    protected virtual List<IStep> BuildSteps()
    {
        var ncu = new NcuStep(this);
        var steps = new List<IStep> { new ClearBuffsStep(this), ncu, };
        foreach (var l in _lines)
        {
            steps.Add(new PetLineStep(this, ncu, l));
            steps.Add(new PetBuffStep(this, ncu, l)); // lift + the pet's own buffs + the recharge beat
        }

        steps.Add(new FloorStep(this, ncu)); // the obedience floor + the comfort fill (owner, 2026-10-07)
        return steps;
    }

    /// <summary>
    ///     Advance the pipeline one frame (dt 0 allowed: NextTurn re-checks completions so a
    ///     landing is seen at consult time, not a frame late).
    /// </summary>
    public void Advance(LocalPlayer me, double dt)
    {
        if (Done || me == null)
        {
            return;
        }

        _t += dt;
        while (_idx < _steps.Count)
        {
            var step = _steps[_idx];
            if (!step.Update(me, dt))
            {
                return; // still working - the session stays on this step
            }

            _idx++;
            _logger.LogInformation($"PETCYCLE: '{step.Describe}' done.");
        }

        var mc = me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0;
        var ts = me.TryGetStat(Stat.SpaceTime, out var v) ? v : 0;
        var roles = string.Join(", ", _lines.Select(l =>
            $"{l.Label}:{(me.Pets.Any(p => p.Role == l.Role) ? "up" : "MISSING")}"));
        _doneWhy = $"cycle complete ({roles}; MC {mc}, TS {ts})";
        Done = true; // ONCE - without this the completion tail re-ran every tick (log spam) and
        // NextTurn indexed past the step list (ArgumentOutOfRangeException per tick)
        _logger.LogInformation($"PETCYCLE: {_doneWhy}.");
    }

    /// <summary>
    ///     The turn the guided session may send now: the current step's wire action, a wait,
    ///     or Done when the pipeline ran off its end.
    /// </summary>
    public BuffBotController.GuidedTurn NextTurn(LocalPlayer me)
    {
        Advance(me, 0);
        if (Done)
        {
            return new BuffBotController.GuidedTurn { Done = true, DoneWhy = _doneWhy, };
        }

        return _steps[_idx].NextTurn(me) ?? new BuffBotController.GuidedTurn();
    }

    // ---- Shared context the steps read ------------------------------------------------------

    internal int RememberedMaxNcu(LocalPlayer me)
    {
        return _rememberedMaxNcu ?? (me.TryGetStat(Stat.MaxNCU, out var n) ? n : 0);
    }

    /// <summary>
    ///     THE INTER-LINE FIX, shared (owner, 2026-10-07): running single-skill nano buffs that
    ///     raise NONE of the needed stats are cancelled - the attack line's MatCrea chain before
    ///     the heal line's ask, the same before a pet-buff lift. Composites, wrangles and
    ///     attribute buffs raise the needed stats (directly or by trickle) and stay; the NCU buff
    ///     and survival buffs raise no nano skill at all and stay. Returns the NCU the cancels
    ///     free (logged) so the caller prices it into its budget.
    /// </summary>
    internal int CancelForeignSingles(LocalPlayer me, IReadOnlyCollection<int> neededStats, string why)
    {
        var cancel = new List<int>();
        if (me.Buffs != null)
        {
            foreach (var b in me.Buffs)
            {
                if (!cancel.Contains(b.Id) && BuffCatalog.IsLineForeignNanoSkill(b.Id, neededStats))
                {
                    cancel.Add(b.Id);
                }
            }
        }

        if (cancel.Count == 0)
        {
            return 0;
        }

        var freed = cancel.Sum(id => _catalog.NcuOfNano(id));
        foreach (var id in cancel)
        {
            me.RemoveFriendlyNano(me.Identity, id);
        }

        _logger.LogInformation(
            $"PETCYCLE: {why} - cancelling {cancel.Count} foreign buff(s) (frees {freed} NCU): " +
            $"{string.Join(", ", cancel.Select(id => BuffCatalog.StrainOf(id) + "#" + id))}.");
        return freed;
    }

    internal void RememberNcu(LocalPlayer me, string source)
    {
        _rememberedMaxNcu = me.TryGetStat(Stat.MaxNCU, out var n) ? n : 0;
        _logger.LogInformation($"PETCYCLE: NCU remembered ({source}): Max {_rememberedMaxNcu}, " +
                               $"free {BuffCatalog.FreeNcu(me, _rememberedMaxNcu.Value)}.");
    }

    // ---- THE OUTCOME LEDGER (owner, 2026-10-08) ----------------------------------------------
    // The persistent buffs the cycle leaves on the bot - the NCU buff, the floor set, the comfort
    // fill - recorded as each lands (the summon stacks are NOT here: the floor step strips them on
    // purpose, so they are part of the means, not the outcome). The external-buffing brain compares
    // this ledger against the ACTUAL buff state and only re-enters the pipeline when an entry is
    // gone or worn to the rebuff fraction - the skill want alone must not re-open the cycle, since
    // the cycle leaves its own gap behind (the stripped summon stacks) and would chase itself every
    // retry window. Another buff of the same strain at equal-or-better gains counts as covering an
    // entry, so an owner-cast or manually requested buff stands in for the cycle's own.

    /// <summary>
    ///     One persistent buff the cycle leaves on the bot (the NCU buff, a floor rung, a comfort
    ///     pick): the landed ids that mark it on the wire, the gains a same-strain stand-in must
    ///     match to count as covering it, and the remaining-time baseline taken at landing - the
    ///     brain's rebuff line is a fraction of THAT, so a fresh replacement is measured against
    ///     its own clock, not the original nano's.
    /// </summary>
    public sealed class OutcomeEntry
    {
        public readonly double BaselineSec;
        public readonly IReadOnlyDictionary<int, int> Gains;
        public readonly IReadOnlyList<int> LandIds;
        public readonly string Name;
        public readonly int Ncu;
        public readonly int Strain;

        public OutcomeEntry(string name, IReadOnlyList<int> landIds, int strain,
            IReadOnlyDictionary<int, int> gains, double baselineSec, int ncu)
        {
            Name = name;
            LandIds = landIds;
            Strain = strain;
            Gains = gains;
            BaselineSec = baselineSec;
            Ncu = ncu;
        }
    }

    private readonly List<OutcomeEntry> _outcome = new List<OutcomeEntry>();

    /// <summary>The persistent buffs the cycle left on the bot, in landing order - filled as it runs.</summary>
    public IReadOnlyList<OutcomeEntry> Outcome => _outcome;

    /// <summary>
    ///     Ledger a landed persistent buff: its cover ids, the gains a same-strain stand-in must
    ///     match, and the baseline = the running buff's remaining time at this instant (its full
    ///     length, fresh off the cast). Recorded only for buffs that DID land - a step that moved
    ///     on past a never-landing action leaves no entry, so the gate cannot chase a ghost.
    /// </summary>
    internal void RecordOutcome(string name, IReadOnlyCollection<int> landIds,
        IReadOnlyDictionary<int, int> gains, int ncu, LocalPlayer me)
    {
        var ids = landIds.Distinct().ToList();
        var baseline = 0.0;
        if (me.Buffs != null)
        {
            foreach (var b in me.Buffs)
            {
                if (ids.Contains(b.Id))
                {
                    baseline = Math.Max(baseline, b.Cooldown?.RemainingTime ?? 0);
                }
            }
        }

        _outcome.Add(new OutcomeEntry(name, ids, ids.Count > 0 ? BuffCatalog.StrainOf(ids[0]) : 0,
            gains, baseline, ncu));
        _logger.LogInformation($"PETCYCLE: outcome + '{name}' (baseline {baseline / 60:0}m, {ncu} NCU).");
    }

    /// <summary>
    ///     The best covering running buff's remaining share of the entry's baseline (-1: nothing
    ///     covers it): the exact nano, or a same-strain buff contributing at least the recorded
    ///     gains per stat.
    /// </summary>
    private static double CoverShare(LocalPlayer me, OutcomeEntry e)
    {
        if (me.Buffs == null)
        {
            return -1;
        }

        var best = -1.0;
        foreach (var b in me.Buffs)
        {
            var covers = e.LandIds.Contains(b.Id) ||
                         (BuffCatalog.StrainOf(b.Id) == e.Strain && e.Gains.Count > 0 &&
                          e.Gains.All(g => BuffCatalog.SkillContributionOf(b.Id, g.Key) >= g.Value));
            if (covers)
            {
                best = Math.Max(best, b.Cooldown?.RemainingTime ?? 0);
            }
        }

        return best < 0 ? -1 : best / Math.Max(1.0, e.BaselineSec);
    }

    /// <summary>
    ///     Is the whole outcome standing: every entry covered (exact nano or equal-or-better
    ///     same-strain stand-in) and above the rebuff fraction of its baseline. An empty ledger
    ///     is NOT alive - there is nothing standing, the caller may run.
    /// </summary>
    internal static bool OutcomeAlive(LocalPlayer me, IReadOnlyList<OutcomeEntry> outcome, double minFraction)
    {
        if (outcome.Count == 0)
        {
            return false;
        }

        foreach (var e in outcome)
        {
            var share = CoverShare(me, e);
            if (share < 0 || share <= minFraction)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Per-entry freshness for the log: "name 82%" (??: nothing covers it any more).</summary>
    internal static string OutcomeReport(LocalPlayer me, IReadOnlyList<OutcomeEntry> outcome)
    {
        return string.Join(", ", outcome.Select(e =>
        {
            var share = CoverShare(me, e);
            return share < 0 ? $"{e.Name} ??" : $"{e.Name} {share * 100:F0}%";
        }));
    }

    internal void RecordSnapshot(PetType role, int formulaId, LocalPlayer me)
    {
        var floors = BuffCatalog.PetFloor(formulaId, me);
        _petFloorByRole[role] = floors;
        _logger.LogInformation($"PETCYCLE: {role} floor snapshot - " +
                               (floors.Count > 0
                                   ? string.Join(", ", floors.Select(r => $"{(Stat)r.Key} {r.Value}"))
                                   : "(no skill-gated variants)") +
                               ".");
    }

    internal static bool Low(LocalPlayer me)
    {
        return Pct(me, Stat.CurrentNano, Stat.MaxNanoEnergy) < 50 || Pct(me, Stat.Health, Stat.MaxHealth) < 50;
    }

    internal static double Pct(LocalPlayer me, Stat cur, Stat max)
    {
        return me.TryGetStat(max, out var m) && m > 0 && me.TryGetStat(cur, out var v) ? v * 100.0 / m : 100.0;
    }

    internal static bool Landed(LocalPlayer me, BuffCatalog.BuffEntry b)
    {
        return me.Buffs != null && b.LandIds.Count > 0 && me.Buffs.Any(r => b.LandIds.Contains(r.Id));
    }

    internal static bool ActionLanded(LocalPlayer me, BuffCatalog.BuffAction a)
    {
        return me.Buffs != null && a.LandIds.Length > 0 && me.Buffs.Any(r => a.LandIds.Contains(r.Id));
    }

    internal void SetGate(double seconds)
    {
        GateUntil = Math.Max(GateUntil, _t + seconds);
    }

    /// <summary>
    ///     One pet line of the owner's table: the wire role it fills, its log label, and the
    ///     PRIMARY skill beside SpaceTime (attack MatCrea+TS, heal BioMet+TS, mezz MatMeta+TS).
    /// </summary>
    public sealed class LineSpec
    {
        public readonly string Label;
        public readonly int PrimaryStat;
        public readonly PetType Role;

        public LineSpec(PetType role, string label, Stat primary)
        {
            Role = role;
            Label = label;
            PrimaryStat = (int)primary;
        }
    }

    /// <summary>
    ///     One step of the pipeline. Update advances the step's own state machine (timers,
    ///     self-actions, completion checks) and returns true ONCE when the step is done; NextTurn is
    ///     what the session may send RIGHT NOW (null = nothing, keep the session waiting).
    /// </summary>
    public interface IStep
    {
        string Describe { get; }
        bool Update(LocalPlayer me, double dt);
        BuffBotController.GuidedTurn? NextTurn(LocalPlayer me);
    }

    // ---- The steps ---------------------------------------------------------------------------

    /// <summary>
    ///     Step 1: strip every running nano (RemoveFriendlyNano - what the game client sends
    ///     clicking a buff icon away; owner 2026-10-07, wire behaviour to be watched on first live
    ///     runs) so the NCU budget and the plan start clean. Buffs that refuse to leave are logged and
    ///     tolerated - the budget simply reads smaller.
    /// </summary>
    private sealed class ClearBuffsStep : IStep
    {
        private readonly PetFirstBuffCycle _c;
        private double _at;
        private int _sent;
        private bool _started;

        public ClearBuffsStep(PetFirstBuffCycle c)
        {
            _c = c;
        }

        public string Describe => "clear buffs";

        public bool Update(LocalPlayer me, double dt)
        {
            var buffs = me.Buffs;
            if (buffs == null || buffs.Count == 0)
            {
                return true;
            }

            if (!_started)
            {
                _started = true;
                _at = _c._t;
                foreach (var id in buffs.Select(b => b.Id).Distinct())
                {
                    me.RemoveFriendlyNano(me.Identity, id);
                    _sent++;
                }

                _c._logger.LogInformation($"PETCYCLE: stripping {_sent} running buff(s) - clean slate for the cycle.");
            }

            if (buffs.Count == 0)
            {
                return true; // every strip confirmed
            }

            if (_c._t - _at <= StripTimeoutSec)
            {
                return false;
            }

            _c._logger.LogWarning($"PETCYCLE: {buffs.Count} buff(s) survived the strip " +
                                  $"({string.Join(", ", buffs.Select(b => b.NanoItem?.Name ?? b.Id.ToString()))}) - " +
                                  "tolerated; the NCU budget reads smaller.");
            return true;
        }

        public BuffBotController.GuidedTurn? NextTurn(LocalPlayer me)
        {
            return null; // a self action - no wire turn
        }
    }

    /// <summary>
    ///     Step 3: the best Max-NCU tier (the Fixer ladder - team-cast on Chewys, so the
    ///     invite flow runs here), and when it lands the NCU is REMEMBERED: that Max is the budget
    ///     every line plan spends against (owner 2026-10-07). A menu without an NCU entry, or a tier
    ///     that never lands, degrades to the raw Max NCU - the cycle continues.
    /// </summary>
    private sealed class NcuStep : IStep
    {
        private readonly PetFirstBuffCycle _c;
        private BuffCatalog.NcuPick? _pick;
        private double _startedAt = double.NegativeInfinity, _toldAt;
        private bool _told;

        public NcuStep(PetFirstBuffCycle c)
        {
            _c = c;
        }

        /// <summary>The landed nano id of the chosen NCU tier (the floor clear spares it).</summary>
        public int? LandId => _pick?.LandId;

        public string Describe => "ncu buff";

        public bool Update(LocalPlayer me, double dt)
        {
            if (_startedAt == double.NegativeInfinity)
            {
                _startedAt = _c._t;
                _pick = _c._catalog.BestNcuBuff(me, IsSlOrLe(me));
                if (_pick == null)
                {
                    _c._logger.LogWarning("PETCYCLE: no NCU buff in the bot's menu - planning against the raw Max NCU.");
                    _c.RememberNcu(me, "raw, no menu entry");
                    return true;
                }
            }

            if (_pick != null && BuffUp(me, _pick.LandId))
            {
                _c.RememberNcu(me, $"'{_pick.Name}' landed");
                // THE OUTCOME LEDGER: the NCU buff survives the whole cycle (the floor clear spares
                // it) - it is outcome, its +Max NCU the gain a same-strain stand-in must match.
                _c.RecordOutcome(_pick.Name, new[] { _pick.LandId, },
                    new Dictionary<int, int> { [(int)Stat.MaxNCU] = _pick.MaxNcuAdded }, 0, me);
                return true;
            }

            if (_told && _c._t - _toldAt > NcuWaitSec)
            {
                _c._logger.LogWarning($"PETCYCLE: the NCU buff '{_pick!.Name}' did not land in {NcuWaitSec:0}s - " +
                                      "planning against the raw Max NCU.");
                _c.RememberNcu(me, "raw, tier never landed");
                return true;
            }

            return false;
        }

        public BuffBotController.GuidedTurn? NextTurn(LocalPlayer me)
        {
            if (_pick == null || _told || BuffUp(me, _pick.LandId))
            {
                return null;
            }

            _told = true;
            _toldAt = _c._t;
            return new BuffBotController.GuidedTurn
            {
                Action = new BuffCatalog.BuffAction
                {
                    Source = BuffCatalog.BuffSource.BuffBot,
                    Name = $"+{_pick.MaxNcuAdded} Max NCU",
                    BotName = _c._catalog.BotName,
                    Tell = _pick.Tell,
                    NeedsTeam = _pick.NeedsTeam,
                    LandIds = _pick.LandId != 0 ? new[] { _pick.LandId, } : Array.Empty<int>(),
                },
            };
        }

        private static bool BuffUp(LocalPlayer me, int nanoId)
        {
            return me.Buffs?.Any(b => b.Id == nanoId) ?? false;
        }
    }

    /// <summary>
    ///     Step 4, one per line: fix the leftovers (cancel the previous line's singles that
    ///     raise neither of this line's skills, tracking the freed NCU into the budget), plan the
    ///     line's stack (PlanPetLine - most overall lift on the skill pair, balanced, NCU-budgeted),
    ///     tell the bot only what is NOT already up as ONE multi-code tell, wait until ALL landed -
    ///     however long that takes, never casting a half-stacked pet (owner 2026-10-07: a 107 pet
    ///     because c2 was still incoming is a downgrade for good) - ONLY THEN cast the line's pet
    ///     (CastLineRequest - the wrangle is freshest now) and wait for it on the wire. An empty ask
    ///     (nothing affordable / everything already up) goes straight to the cast at current
    ///     skills.
    /// </summary>
    private sealed class PetLineStep : IStep
    {
        private const int MaxSummonRetries = 3;

        private readonly PetFirstBuffCycle _c;
        private readonly LineSpec _line;
        private readonly NcuStep _ncu;
        private List<BuffCatalog.BuffEntry> _ask = new List<BuffCatalog.BuffEntry>(); // the planned entries NOT already up - what we tell the bot
        private Phase _phase = Phase.Plan;
        private double _phaseAt;
        private List<BuffCatalog.BuffEntry> _plan = new List<BuffCatalog.BuffEntry>();
        private int _summonRetries; // re-issues of the summon while the buffed window is still open
        private bool _terminated; // the line's old pet terminate is out
        private BuffBotController.GuidedTurn? _turn;
        private int _waitLogs; // progress lines emitted while waiting for the ask to land

        public PetLineStep(PetFirstBuffCycle c, NcuStep ncu, LineSpec line)
        {
            _c = c;
            _ncu = ncu;
            _line = line;
        }

        public string Describe => $"{_line.Label} pet line";

        public bool Update(LocalPlayer me, double dt)
        {
            // THE SHARED CAST WINDOW: no new cast while a previous nano's attack/recharge runs -
            // whether that was the previous line's last buff or our own earlier cast.
            if (_c._t < _c.GateUntil)
            {
                return false;
            }

            // HOLD WHILE A REST IS WANTED OR RUNNING (owner, 2026-10-07): a seated summon is
            // refused ("You must be standing up") - whoever sat (our recharge demand or the
            // HealController's own want), the summon waits for it to end.
            if (_c._heal.RestActive)
            {
                return false;
            }

            switch (_phase)
            {
                case Phase.Plan:
                {
                    // THE INTER-LINE FIX (owner, 2026-10-07): singles from the previous line that
                    // raise neither of THIS line's skills are cancelled (the shared helper), the
                    // freed NCU tracked and priced into the plan's budget.
                    var freed = _c.CancelForeignSingles(me,
                        new[] { _line.PrimaryStat, (int)Stat.SpaceTime, }, $"{_line.Label} line");

                    var budget = BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me)) + freed;

                    _plan = _c._catalog.PlanPetLine(me, _line.PrimaryStat, (int)Stat.SpaceTime, budget, IsSlOrLe(me));
                    _ask = _plan.Where(b => !Satisfied(me, b, _line.PrimaryStat, (int)Stat.SpaceTime)).ToList();
                    _phaseAt = _c._t;

                    if (_ask.Count == 0)
                    {
                        if (_plan.Count == 0)
                        {
                            _c._logger.LogInformation(
                                $"PETCYCLE: {_line.Label} line - no affordable stack (free NCU {budget}); casting at current skills.");
                        }
                        else
                        {
                            _c._logger.LogInformation(
                                $"PETCYCLE: {_line.Label} line - the whole stack is already up; casting straight away.");
                        }

                        Cast(me);
                        return false; // into WaitPet
                    }

                    var codes = string.Join(" ", _ask.Select(b => b.Tell));
                    var ncu = _ask.Sum(b => b.Ncu);
                    _c._logger.LogInformation(
                        $"PETCYCLE: {_line.Label} line - telling '{_c._catalog.BotName} cast {codes}' " +
                        $"({_ask.Count} of {_plan.Count} planned, {ncu} NCU of {budget} free) then the {_line.Label} pet.");
                    _turn = new BuffBotController.GuidedTurn
                    {
                        Action = new BuffCatalog.BuffAction
                        {
                            Source = BuffCatalog.BuffSource.BuffBot,
                            Name = $"{_line.Label} pet stack ({_ask.Count} buffs, {ncu} NCU)",
                            BotName = _c._catalog.BotName,
                            Tell = codes,
                            NeedsTeam = _ask.Any(b => b.NeedsTeam),
                            LandIds = _ask.SelectMany(b => b.LandIds).Distinct().ToArray(),
                            RequireAll = true, // the WHOLE ask before the pet goes up
                        },
                    };
                    _phase = Phase.WaitLand;
                    return false;
                }

                case Phase.WaitLand:
                {
                    var missing = _ask.Where(b => !Landed(me, b)).ToList();
                    if (missing.Count == 0)
                    {
                        _phase = Phase.Settle;
                        _phaseAt = _c._t;
                        _c._logger.LogInformation(
                            $"PETCYCLE: {_line.Label} line - the whole ask is in; settling {AskSettleSec * 1000:0}ms before the {_line.Label} pet.");
                        return false; // into Settle
                    }

                    // THE CAST WAITS FOR THE WHOLE ASK (owner, 2026-10-07): a pet summoned before
                    // its stack is fully in is a downgrade for good - a 107 where the still-
                    // incoming c2 would have given 131. There is NO timeout cast: however long the
                    // bot takes, the summon waits; the session cap is the only bound, and a
                    // progress line keeps a long wait (or a code the bot never casts) visible.
                    if (_c._t - _phaseAt > WaitLogEverySec * _waitLogs)
                    {
                        _waitLogs++;
                        _c._logger.LogInformation(
                            $"PETCYCLE: {_line.Label} line - still waiting for {missing.Count}/{_ask.Count} " +
                            $"({string.Join(", ", missing.Select(b => b.Name))}) before the {_line.Label} pet.");
                    }

                    return false;
                }

                case Phase.Settle:
                {
                    if (_c._t - _phaseAt >= AskSettleSec)
                    {
                        _phase = Phase.Terminate;
                        _phaseAt = _c._t;
                    }

                    return false;
                }

                case Phase.Terminate:
                {
                    // THE PER-LINE SWAP (owner, 2026-10-07): THIS line's old pet dies right before
                    // the new summon - the slot gate wants it free and a same-line recast is
                    // refused while it lives; the REST of the roster keeps fighting the cycle.
                    if (me.Pets.Any(p => p.Role == _line.Role))
                    {
                        if (!_terminated)
                        {
                            _terminated = true;
                            _phaseAt = _c._t;
                            _c._pet.TerminatePet(me, _line.Role);
                            return false;
                        }

                        if (_c._t - _phaseAt < RosterTimeoutSec)
                        {
                            return false; // the server is processing the kill
                        }

                        _c._logger.LogWarning(
                            $"PETCYCLE: the old {_line.Label} pet did not confirm termination in {RosterTimeoutSec:0}s - casting anyway.");
                    }

                    _phase = Phase.Cast;
                    _phaseAt = _c._t;
                    return false;
                }

                case Phase.Cast:
                {
                    // THE FINAL BEAT (owner, 2026-10-07): 500ms more before the summon - the
                    // client can already see the last buff running while the server is still
                    // applying it, and a pet summoned in that gap resolves a tier low
                    // (107 instead of 131).
                    if (_c._t - _phaseAt < PreCastWaitSec)
                    {
                        return false;
                    }

                    Cast(me); // into WaitPet
                    return false;
                }

                case Phase.WaitPet:
                default:
                {
                    if (me.Pets.Any(p => p.Role == _line.Role))
                    {
                        return true; // the wire confirms: this line's pet is up
                    }

                    // The summon is RETRIED inside the buffed window (owner, 2026-10-07): a lost or
                    // refused cast must not hand the slot to the later cadence refill, which would
                    // summon at whatever stats are left after the next line's cancels (the 107
                    // after the heal line dropped the MC mocham). The pet brain's own 12s recast
                    // guard spaces the re-issues.
                    if (_c._t - _phaseAt > PetWaitSec * (_summonRetries + 1))
                    {
                        _summonRetries++;
                        if (_summonRetries > MaxSummonRetries)
                        {
                            _c._logger.LogWarning(
                                $"PETCYCLE: {_line.Label} pet did not appear after {MaxSummonRetries} summons in " +
                                $"{_c._t - _phaseAt:0}s - continuing (the session-end pass retries it).");
                            return true;
                        }

                        _c._logger.LogWarning(
                            $"PETCYCLE: {_line.Label} pet did not appear in {PetWaitSec:0}s - summoning again " +
                            $"(try {_summonRetries + 1} of {MaxSummonRetries + 1}).");
                        _c._pet.CastLineRequest(me, _line.Role);
                    }

                    return false;
                }
            }
        }

        public BuffBotController.GuidedTurn? NextTurn(LocalPlayer me)
        {
            var turn = _turn;
            _turn = null; // exactly once - after it is sent, WaitLand owns the waiting
            return turn;
        }

        private void Cast(LocalPlayer me)
        {
            // THE CAST-TIME PICTURE (owner, 2026-10-07): every buff running at the summon moment -
            // the ground truth when a pet comes out under-tier (a 107 that should have been a 131).
            var buffs = new List<string>();
            if (me.Buffs != null)
            {
                foreach (var b in me.Buffs)
                {
                    var left = b.Cooldown?.RemainingTime ?? 0;
                    buffs.Add($"'{b.NanoItem?.Name ?? NanoLibrary.NameOf(b.Id)}' {b.Id} " +
                              $"(strain {BuffCatalog.StrainOf(b.Id)}, {left / 60:0}m)");
                }
            }

            _c._logger.LogInformation(
                $"PETCYCLE: {_line.Label} pet cast - {buffs.Count} buff(s) running: {(buffs.Count > 0 ? string.Join(", ", buffs) : "NONE")}");

            _phase = Phase.WaitPet;
            _phaseAt = _c._t;
            _c._pet.CastLineRequest(me, _line.Role);

            // THE CAST SNAPSHOT (owner, 2026-10-07): which formula went out, and the caster's
            // gate stats AT this instant - the pet's own requirement per stat is the smaller of
            // the formula's gate and the cast-time stat, and the floor phase lifts 80% of THAT.
            var formula = _c._pet.LastSummonNanoFor(_line.Role);
            if (formula != null)
            {
                _c.RecordSnapshot(_line.Role, formula.Value, me);

                // THE SUMMON'S OWN WINDOW (owner, 2026-10-07): after the last pet, the floor's
                // casts must wait the summon nano's RECHARGE time + the CastDelayTime const
                // (plus, not minus) - the pet appearing on the wire already covers the attack
                // phase; the "Already executing" window that remains is the recharge.
                if (ItemData.Find(formula.Value, out NanoItem sni) && sni != null)
                {
                    _c.SetGate(sni.RechargeDelay + CastDelayTime);
                }
            }
        }

        private static bool Landed(LocalPlayer me, BuffCatalog.BuffEntry b)
        {
            return me.Buffs != null && b.LandIds.Count > 0 && me.Buffs.Any(r => b.LandIds.Contains(r.Id));
        }

        /// <summary>
        ///     Already covered, so the bot is not asked again: the exact nano is running, or a
        ///     same-strain buff at least as strong covers every stat the entry raises (a stronger
        ///     same-strain buff cannot be overwritten by a weaker cast anyway).
        /// </summary>
        private static bool Satisfied(LocalPlayer me, BuffCatalog.BuffEntry e, int statA, int statB)
        {
            var buffs = me.Buffs;
            if (buffs == null || e.LandIds.Count == 0)
            {
                return false;
            }

            var needA = BuffCatalog.GainFor(e, statA);
            var needB = BuffCatalog.GainFor(e, statB);

            foreach (var r in buffs)
            {
                if (e.LandIds.Contains(r.Id))
                {
                    return true; // the exact nano is running
                }

                if (BuffCatalog.StrainOf(r.Id) != e.Strain)
                {
                    continue; // different overwrite group
                }

                var covers = (needA == 0 || BuffCatalog.SkillContributionOf(r.Id, statA) >= needA) &&
                             (needB == 0 || BuffCatalog.SkillContributionOf(r.Id, statB) >= needB);
                if (covers)
                {
                    return true;
                }
            }

            return false;
        }

        private enum Phase
        {
            Plan,
            WaitLand,
            Settle, // the ask is fully in - breathe 500ms so the server's state settles before the summon
            Terminate, // THIS line's old pet dies here (the slot gate wants it free); the rest of the roster keeps fighting
            Cast,
            WaitPet,
        }
    }

    /// <summary>
    ///     Step 5, one per line (right after the line's summon): buff the pet itself, then the
    ///     recharge beat. Per line the pet accepts (PetBuffLines: the damage/initiative families
    ///     are ATTACK-only, the rest land on every manifestation) the TOP learned formula is the
    ///     target - and the step CALCULATES THE NEEDED EXTERNAL BUFFS for it (the caster-side
    ///     skill shortfalls of the top formulas, owner 2026-10-07): one lift ask to the bot
    ///     (PlanCasterLift, NCU-budgeted), wait for EVERY lift buff (no partial lift casts),
    ///     500ms settle - then per line the highest-StackingOrder learned formula that is
    ///     castable post-lift is cast ON the pet; a line the pet already carries at equal or
    ///     better stacking is skipped (owner, 2026-10-06). Between casts: under 50% nano or
    ///     health, sit for the rechargers through the HealController's demand - but only once
    ///     the cast in flight has REALLY finished (IsCasting clear), because sitting aborts a
    ///     nano; the HealController re-checks the same guard before its own sit.
    /// </summary>
    private sealed class PetBuffStep : IStep
    {
        private readonly PetFirstBuffCycle _c;
        private readonly LineSpec _line;
        private readonly List<int> _lines = new List<int>(); // the strain lines this pet accepts
        private readonly NcuStep _ncu;
        private readonly HashSet<int> _skippedLines = new HashSet<int>();
        private double _lastCastAt = double.NegativeInfinity;
        private List<BuffCatalog.BuffEntry> _lift = new List<BuffCatalog.BuffEntry>(); // the external buffs the top formulas need
        private bool _liftOut; // the lift tell was sent - wait for every buff to land
        private int _lineIdx; // cursor over _lines during the cast loop
        private double _phaseAt;
        private bool _planned;
        private bool _settling;
        private BuffBotController.GuidedTurn? _turn;
        private int _waitLogs;
        private bool _waitingCastEnd;
        private bool _waitingRest;

        public PetBuffStep(PetFirstBuffCycle c, NcuStep ncu, LineSpec line)
        {
            _c = c;
            _ncu = ncu;
            _line = line;
        }

        public string Describe => $"{_line.Label} pet buffs";

        public bool Update(LocalPlayer me, double dt)
        {
            if (!_planned)
            {
                _planned = true;
                Plan(me);
            }

            if (_lines.Count == 0)
            {
                return true; // nothing this pet accepts
            }

            // THE SHARED CAST WINDOW (unconditional - it must hold the FIRST cast of this step
            // too, e.g. the summon's recharge before the first pet buff): no new cast while a
            // previous nano's attack/recharge runs.
            if (_c._t < _c.GateUntil)
            {
                return false;
            }

            // HOLD WHILE A REST IS WANTED OR RUNNING (owner, 2026-10-07): seated casts are refused
            // ("You must be standing up") and a sit aborts a nano in flight - whoever sat (our
            // recharge demand or the HealController's own want), the buffs wait for it to end.
            if (_c._heal.RestActive)
            {
                return false;
            }

            // a refused cast never sets IsCasting - pace the retries so a failure cannot spam
            if (_c._t - _lastCastAt < CastRetrySec)
            {
                return false;
            }

            if (_waitingRest)
            {
                _waitingRest = false;
            }

            // THE LIFT: wait for every asked buff - a partial lift casts a lower tier for good
            // (the same rule as the summon path) - then the settle beat.
            if (_liftOut)
            {
                var missing = _lift.Where(b => !Landed(me, b)).ToList();
                if (missing.Count > 0)
                {
                    if (_c._t - _phaseAt > WaitLogEverySec * _waitLogs)
                    {
                        _waitLogs++;
                        _c._logger.LogInformation(
                            $"PETCYCLE: {_line.Label} pet buffs - still waiting for the lift " +
                            $"({string.Join(", ", missing.Select(b => b.Name))}).");
                    }

                    return false;
                }

                _liftOut = false;
                _settling = true;
                _phaseAt = _c._t;
                return false;
            }

            if (_settling)
            {
                if (_c._t - _phaseAt < AskSettleSec)
                {
                    return false;
                }

                _settling = false;
            }

            // the cast in flight must REALLY finish - IsCasting clear (the shared window above
            // covers the attack+decay clock) before anything re-casts
            if (_waitingCastEnd)
            {
                if (me.IsCasting || _c._t < _c.GateUntil)
                {
                    return false;
                }

                _waitingCastEnd = false;
            }

            // under 50% nano or health: the rechargers, before the next buff
            if (Low(me))
            {
                _c._logger.LogInformation(
                    $"PETCYCLE: {_line.Label} pet buffs - under 50% (nano {Pct(me, Stat.CurrentNano, Stat.MaxNanoEnergy):0}%, " +
                    $"health {Pct(me, Stat.Health, Stat.MaxHealth):0}%) - sitting for the rechargers.");
                _c._heal.DemandRecharge($"{_line.Label} pet buffs");
                _waitingRest = true;
                return false;
            }

            // THE CAST LOOP: the next line that still wants a buff.
            while (_lineIdx < _lines.Count)
            {
                var lineStrain = _lines[_lineIdx];
                var pick = PickCastable(me, lineStrain, out var anyLearned);
                if (pick == null)
                {
                    _lineIdx++;
                    if (anyLearned && _skippedLines.Add(lineStrain))
                    {
                        _c._logger.LogWarning(
                            $"PETCYCLE: {_line.Label} pet - pet-buff line {lineStrain}: formulas learned but " +
                            "still short of their requirements even after the lift - skipped.");
                    }

                    continue;
                }

                var pet = me.Pets.FirstOrDefault(p => p.Role == _line.Role);
                if (pet == null)
                {
                    _c._logger.LogWarning($"PETCYCLE: {_line.Label} pet is gone - dropping its remaining buffs.");
                    _lineIdx = _lines.Count;
                    break;
                }

                if (CarriedStacking(me, pet, lineStrain) >= pick.Value.order)
                {
                    _lineIdx++;
                    continue; // the pet already carries the line at equal or better stacking
                }

                if (!DynelManager.Find(pet.Identity, out SimpleChar petChar))
                {
                    return false; // the pet dynel is not streamed yet - the next tick retries
                }

                me.Cast(petChar, pick.Value.ni.Id);
                _lastCastAt = _c._t;
                // the nano's own window: attack + decay (seconds) + 0.5s
                var gate = pick.Value.ni.AttackDelay + pick.Value.ni.RechargeDelay + CastDelayTime;
                _c.SetGate(gate);
                _waitingCastEnd = true;
                _c._logger.LogInformation(
                    $"PETCYCLE: {_line.Label} pet - casting '{pick.Value.ni.Name}' ({pick.Value.ni.Id}, tier {pick.Value.order}, " +
                    $"gate {gate:0.0}s) on the pet.");
                return false; // the next line waits for this cast to really finish
            }

            return true; // every line handled
        }

        public BuffBotController.GuidedTurn? NextTurn(LocalPlayer me)
        {
            var turn = _turn;
            _turn = null; // exactly once - after it is sent, the lift wait owns the step
            return turn;
        }

        // PLAN: the lines this pet accepts, and the lift ask for the caster-side shortfalls of the
        // TOP learned formula per line (aggregated per stat - one tell covers every line's lift).
        private void Plan(LocalPlayer me)
        {
            if (!PetBuffLines.TryGetValue(_line.Role, out var lines) || lines.Length == 0)
            {
                return;
            }

            _lines.AddRange(lines);

            var needs = new Dictionary<int, int>();
            foreach (var lineStrain in lines)
            {
                var top = TopLearned(me, lineStrain);
                if (top == null)
                {
                    continue;
                }

                foreach (var kv in BuffCatalog.CasterSkillGaps(top.Id, me))
                {
                    needs[kv.Key] = Math.Max(needs.TryGetValue(kv.Key, out var had) ? had : 0, kv.Value);
                }
            }

            if (needs.Count == 0)
            {
                return; // everything castable as we stand - straight to the casts
            }

            // THE INTER-LINE FIX for the lift: singles raising none of the needed stats go
            // (previous lines' leftovers), the freed NCU priced into the lift's budget.
            var freed = _c.CancelForeignSingles(me, needs.Keys.ToList(), $"{_line.Label} pet-buff lift");
            var budget = BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me)) + freed;
            _lift = _c._catalog.PlanCasterLift(me, needs, budget, IsSlOrLe(me));
            var needText = string.Join(", ", needs.Select(n => $"{(Stat)n.Key} +{n.Value}"));
            if (_lift.Count == 0)
            {
                _c._logger.LogInformation(
                    $"PETCYCLE: {_line.Label} pet buffs - the top formulas need {needText} but nothing " +
                    $"affordable is askable (free NCU {budget}) - casting what is castable as we stand.");
                return;
            }

            var codes = string.Join(" ", _lift.Select(b => b.Tell));
            _turn = new BuffBotController.GuidedTurn
            {
                Action = new BuffCatalog.BuffAction
                {
                    Source = BuffCatalog.BuffSource.BuffBot,
                    Name = $"{_line.Label} pet-buff lift ({_lift.Count} buffs)",
                    BotName = _c._catalog.BotName,
                    Tell = codes,
                    NeedsTeam = _lift.Any(b => b.NeedsTeam),
                    LandIds = _lift.SelectMany(b => b.LandIds).Distinct().ToArray(),
                    RequireAll = true,
                },
            };
            _liftOut = true;
            _phaseAt = _c._t;
            _c._logger.LogInformation(
                $"PETCYCLE: {_line.Label} pet buffs - the top formulas need {needText} - asking " +
                $"'{_c._catalog.BotName} {codes}' ({_lift.Count} buffs of {budget} free NCU).");
        }

        // The highest-StackingOrder learned formula of the line whose caster-side requirements are
        // met NOW (post-lift) - the ladder falls down when the lift could not cover the top.
        private static (NanoItem ni, int order)? PickCastable(LocalPlayer me, int lineStrain, out bool anyLearned)
        {
            anyLearned = false;
            NanoItem? best = null;
            var bestOrder = -1;
            foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
            {
                var profile = NanoLibrary.Find(nanoId);
                if (profile == null || profile.Stat(75) != lineStrain || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
                {
                    continue;
                }

                anyLearned = true;
                if (BuffCatalog.CasterSkillGaps(nanoId, me).Count > 0)
                {
                    continue; // still short even after the lift
                }

                var order = profile.Stat((int)Stat.StackingOrder);
                if (best == null || order > bestOrder)
                {
                    best = ni;
                    bestOrder = order;
                }
            }

            return best == null ? null : (best, bestOrder);
        }

        private static NanoItem? TopLearned(LocalPlayer me, int lineStrain)
        {
            NanoItem? best = null;
            var bestOrder = -1;
            foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
            {
                var profile = NanoLibrary.Find(nanoId);
                if (profile == null || profile.Stat(75) != lineStrain || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
                {
                    continue;
                }

                var order = profile.Stat((int)Stat.StackingOrder);
                if (best == null || order > bestOrder)
                {
                    best = ni;
                    bestOrder = order;
                }
            }

            return best;
        }

        private static int CarriedStacking(LocalPlayer me, NpcChar pet, int lineStrain)
        {
            var best = 0;
            if (DynelManager.Find(pet.Identity, out SimpleChar petChar) && petChar.Buffs != null)
            {
                foreach (var b in petChar.Buffs)
                {
                    if (BuffCatalog.StrainOf(b.Id) != lineStrain)
                    {
                        continue;
                    }

                    best = Math.Max(best, NanoLibrary.Find(b.Id)?.Stat((int)Stat.StackingOrder) ?? 0);
                }
            }

            return best;
        }

        private static bool Landed(LocalPlayer me, BuffCatalog.BuffEntry b)
        {
            return me.Buffs != null && b.LandIds.Count > 0 && me.Buffs.Any(r => b.LandIds.Contains(r.Id));
        }

        private static bool Low(LocalPlayer me)
        {
            return Pct(me, Stat.CurrentNano, Stat.MaxNanoEnergy) < 50 || Pct(me, Stat.Health, Stat.MaxHealth) < 50;
        }

        private static double Pct(LocalPlayer me, Stat cur, Stat max)
        {
            return me.TryGetStat(max, out var m) && m > 0 && me.TryGetStat(cur, out var v) ? v * 100.0 / m : 100.0;
        }
    }

    /// <summary>
    ///     Step 6, once after all pets are out (owner, 2026-10-07): THE OBEDIENCE FLOOR + the
    ///     comfort fill. First every running buff EXCEPT the NCU buff is cleared - the floor set
    ///     must be the smallest that obeys. Then, from the cast snapshots, each line's pet
    ///     requirements (stat -> min(formula gate, cast-time stat)) are lifted to 80%: the
    ///     LEAST-NCU durable set via BuildControlPlan per line (floor math, lowest rungs first),
    ///     merged by strain, wrangles excluded - LONG-TERM buffs only. The actions walk one by
    ///     one (self-casts and bot tells, each awaited, cast gates respected). Last, the comfort
    ///     fill in the owner's order as ONE tell: Fixer long HoT, Doctor health + long HoT and
    ///     Fixer runspeed are MUSTS (biggest tier that fits, downgrades so a must is never
    ///     crowded out); Enforcer Essence, damage shields / Coruscating Screen and damage buffs
    ///     fill whatever NCU is left.
    /// </summary>
    private sealed class FloorStep : IStep
    {
        private readonly List<BuffCatalog.BuffAction> _actions = new List<BuffCatalog.BuffAction>();

        // THE OUTCOME LEDGER: each action's catalog entry, appended in lockstep with _actions -
        // when the action lands, the entry goes into the cycle's outcome (the persistent buffs).
        private readonly List<BuffCatalog.BuffEntry> _actionEntries = new List<BuffCatalog.BuffEntry>();

        private readonly PetFirstBuffCycle _c;
        private readonly NcuStep _ncu;
        private bool _clearOut;
        private List<BuffCatalog.BuffEntry> _comfort = new List<BuffCatalog.BuffEntry>();
        private bool _comfortPlanned; // the comfort fill is planned once, then it rides the walk
        private BuffCatalog.BuffAction? _current;
        private BuffCatalog.BuffEntry? _currentEntry; // the entry behind _current (ledgered on landing)
        private int _comfortFrom; // the _actions mark where the comfort fill begins (its walk re-checks the NCU)
        private int _idx;
        private double _lastCastAt = double.NegativeInfinity;
        private Phase _phase = Phase.Clear;
        private double _phaseAt;
        private BuffBotController.GuidedTurn? _turn;
        private int _waitLogs;
        private bool _waitingCastEnd;
        private bool _waitingRest;

        public FloorStep(PetFirstBuffCycle c, NcuStep ncu)
        {
            _c = c;
            _ncu = ncu;
        }

        public string Describe => "floor + comfort";

        public bool Update(LocalPlayer me, double dt)
        {
            // THE SHARED CAST WINDOW (unconditional - it must hold the FIRST cast of this step
            // too, e.g. the last summon's recharge before the first floor cast): no new cast
            // while a previous nano's attack/recharge runs.
            if (_c._t < _c.GateUntil)
            {
                return false;
            }

            // HOLD WHILE A REST IS WANTED OR RUNNING (as everywhere: seated casts are refused,
            // and a sit aborts a nano in flight).
            if (_c._heal.RestActive)
            {
                return false;
            }

            // the cast in flight must REALLY finish - IsCasting clear (the shared window above
            // covers the attack+decay clock)
            if (_waitingCastEnd)
            {
                if (me.IsCasting)
                {
                    return false;
                }

                _waitingCastEnd = false;
            }

            // a refused cast never sets IsCasting - pace the retries
            if (_c._t - _lastCastAt < CastRetrySec)
            {
                return false;
            }

            switch (_phase)
            {
                case Phase.Clear:
                    return Clear(me);

                case Phase.Walk:
                default:
                    return Walk(me);
            }
        }

        public BuffBotController.GuidedTurn? NextTurn(LocalPlayer me)
        {
            var turn = _turn;
            _turn = null;
            return turn;
        }

        private bool Clear(LocalPlayer me)
        {
            var exempt = _ncu.LandId ?? 0;
            var leftovers = me.Buffs == null
                ? new List<int>()
                : me.Buffs.Where(b => b.Id != exempt).Select(b => b.Id).Distinct().ToList();

            if (!_clearOut)
            {
                _clearOut = true;
                _phaseAt = _c._t;
                foreach (var id in leftovers)
                {
                    me.RemoveFriendlyNano(me.Identity, id);
                }

                _c._logger.LogInformation(
                    $"PETCYCLE: floor - stripping {leftovers.Count} buff(s) (the NCU buff stays) before the obedience floor.");
                return false;
            }

            if (leftovers.Count == 0 || _c._t - _phaseAt > StripTimeoutSec)
            {
                if (leftovers.Count > 0)
                {
                    _c._logger.LogWarning(
                        $"PETCYCLE: floor - {leftovers.Count} buff(s) survived the clear - building the floor set around them.");
                }

                BuildFloorActions(me);
                _phase = Phase.Walk;
                return false;
            }

            return false; // the strips are confirming
        }

        // THE FLOOR SET (owner, 2026-10-07): the aggregated 80% floors per stat, the BASE
        // shortfalls against them, then the LEAST-NCU cover - PlanCasterLift's greedy stops the
        // moment every shortfall is covered (no mochams where a teaching would do, no buffs
        // where the base is already above the floor). Wrangles excluded: long-term only.
        private void BuildFloorActions(LocalPlayer me)
        {
            var learned = new HashSet<int>(me.SpellList ?? Array.Empty<int>());

            // the aggregated floor per stat across all lines' snapshots (the snapshots ARE the
            // floors: 0.8 of the selected pet variant's thresholds)
            var floors = new Dictionary<int, int>();
            foreach (var (_, lineFloors) in _c._petFloorByRole)
            {
                foreach (var kv in lineFloors)
                {
                    floors[kv.Key] = Math.Max(floors.TryGetValue(kv.Key, out var had) ? had : 0, kv.Value);
                }
            }

            // the base shortfalls (post-clear stats vs the floors)
            var needs = new Dictionary<int, int>();
            foreach (var kv in floors)
            {
                me.TryGetStat((Stat)kv.Key, out var baseVal);
                var need = Math.Max(0, kv.Value - baseVal);
                if (need > 0)
                {
                    needs[kv.Key] = need;
                }
            }

            _c._logger.LogInformation(
                "PETCYCLE: floor - per stat (floor / base / still needed): " +
                string.Join(", ", floors.OrderBy(f => f.Key).Select(f =>
                {
                    me.TryGetStat((Stat)f.Key, out var baseVal);
                    return $"{(Stat)f.Key} {f.Value}/{baseVal}/-{needs.GetValueOrDefault(f.Key)}";
                })) + ".");

            if (needs.Count == 0)
            {
                _c._logger.LogInformation("PETCYCLE: floor - the base already meets every floor; no floor buffs needed.");
                return;
            }

            var free = BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me));
            var set = _c._catalog.PlanCasterLift(me, needs, free, IsSlOrLe(me), excludeWrangles: true);
            if (set.Count == 0)
            {
                _c._logger.LogWarning(
                    "PETCYCLE: floor - nothing affordable covers the shortfalls " +
                    $"({string.Join(", ", needs.Select(n => $"{(Stat)n.Key} +{n.Value}"))}, free NCU {free}) - the pets may disobey.");
                return;
            }

            foreach (var b in set)
            {
                var action = _c._catalog.RouteFor(b, "Metaphysicist", _c._catalog.BotName, learned.Contains);
                if (action.Source != BuffCatalog.BuffSource.Unavailable)
                {
                    _actions.Add(action);
                    _actionEntries.Add(b);
                }
            }

            if (_actions.Count > 0)
            {
                _c._logger.LogInformation(
                    $"PETCYCLE: floor - {_actions.Count} action(s), least NCU to the floors: " +
                    $"{string.Join(", ", _actions.Select(a => a.Describe))}.");
            }
        }

        private bool Walk(LocalPlayer me)
        {
            // the current action's landing (bot tells are awaited here too - the controller's own
            // wait runs in parallel and only closes its step)
            if (_current != null)
            {
                if (ActionLanded(me, _current))
                {
                    _c._logger.LogInformation($"PETCYCLE: floor - '{_current.Name}' landed.");
                    if (_currentEntry != null)
                    {
                        // THE OUTCOME LEDGER: a floor/comfort buff that landed is part of the
                        // persistent set the brain later compares against.
                        _c.RecordOutcome(_currentEntry.Name, _currentEntry.LandIds,
                            _currentEntry.Modifies, _currentEntry.Ncu, me);
                    }

                    _current = null;
                    _currentEntry = null;
                    _idx++;
                }
                else if (_c._t - _phaseAt > NcuWaitSec)
                {
                    _c._logger.LogWarning(
                        $"PETCYCLE: floor - '{_current.Name}' did not land in {NcuWaitSec:0}s - moving on.");
                    _current = null;
                    _currentEntry = null; // never landed - no outcome entry, no ghost to chase
                    _idx++;
                }
                else
                {
                    return false;
                }
            }

            if (Low(me))
            {
                _c._logger.LogInformation(
                    $"PETCYCLE: floor - under 50% (nano {Pct(me, Stat.CurrentNano, Stat.MaxNanoEnergy):0}%, " +
                    $"health {Pct(me, Stat.Health, Stat.MaxHealth):0}%) - sitting for the rechargers.");
                _c._heal.DemandRecharge("floor buffs");
                _waitingRest = true;
                return false;
            }

            while (_idx < _actions.Count)
            {
                var a = _actions[_idx];
                if (a.Source == BuffCatalog.BuffSource.Unavailable || ActionLanded(me, a))
                {
                    _idx++;
                    continue;
                }

                // THE COMFORT RE-CHECK (owner, 2026-10-08): comfort is optional - when the NCU that
                // was free at plan time is gone (an unknown running buff, a stat the wire still
                // under-reports), skip the pick instead of waiting 90s out a cast the server refuses.
                // Floor actions skip nothing: they MUST land, and the NcuWaitSec move-on already
                // tolerates a refusal.
                if (_idx >= _comfortFrom && a.Ncu > BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me)))
                {
                    _c._logger.LogInformation(
                        $"PETCYCLE: comfort - '{a.Name}' needs {a.Ncu} NCU, " +
                        $"{BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me))} free - skipped.");
                    _idx++;
                    continue;
                }

                if (a.Source == BuffCatalog.BuffSource.SelfCast)
                {
                    me.Cast(a.SelfCastNanoId);
                    _lastCastAt = _c._t;
                    if (ItemData.Find(a.SelfCastNanoId, out NanoItem ni) && ni != null)
                    {
                        _c.SetGate(ni.AttackDelay + ni.RechargeDelay + CastDelayTime);
                        _waitingCastEnd = true;
                    }

                    _current = a;
                    _currentEntry = _actionEntries[_idx];
                    _phaseAt = _c._t;
                    _c._logger.LogInformation($"PETCYCLE: floor - self-casting '{a.Name}' ({a.SelfCastNanoId}).");
                    return false;
                }

                _current = a;
                _currentEntry = _actionEntries[_idx];
                _phaseAt = _c._t;
                _turn = new BuffBotController.GuidedTurn { Action = a, };
                return false; // the controller sends the tell and watches the landing
            }

            // THE FLOOR IS UP: the comfort fill (planned ONCE) - routed like the floor set (a
            // learned self-cast becomes me.Cast, the rest are bot tells), appended to the walk.
            if (!_comfortPlanned)
            {
                _comfortPlanned = true;
                _comfortFrom = _actions.Count; // the floor actions end here - the walk re-checks NCU past this mark
                var free = BuffCatalog.FreeNcu(me, _c.RememberedMaxNcu(me));
                _comfort = _c._catalog.PlanComfort(me, free, IsSlOrLe(me), "Metaphysicist");
                var learned = new HashSet<int>(me.SpellList ?? Array.Empty<int>());
                foreach (var b in _comfort)
                {
                    var action = _c._catalog.RouteFor(b, "Metaphysicist", _c._catalog.BotName, learned.Contains);
                    if (action.Source != BuffCatalog.BuffSource.Unavailable)
                    {
                        _actions.Add(action);
                        _actionEntries.Add(b);
                    }
                }

                if (_comfort.Count == 0)
                {
                    _c._logger.LogInformation($"PETCYCLE: comfort - nothing affordable (free NCU {free}) - the cycle ends here.");
                }
                else
                {
                    _c._logger.LogInformation(
                        $"PETCYCLE: comfort - {_comfort.Count} pick(s), {_comfort.Sum(b => b.Ncu)} NCU of {free} free: " +
                        $"{string.Join(", ", _comfort.Select(b => b.Name))}.");
                }
            }

            if (_idx >= _actions.Count)
            {
                var roles = string.Join(", ", _c._petFloorByRole.Keys.Select(r => r.ToString()));
                _c._logger.LogInformation(
                    $"PETCYCLE: floor + comfort complete - pets out ({roles}), the floor set is in, comfort landed.");
                return true;
            }

            return false; // continue walking (the appended comfort actions ride the same walk)
        }

        private enum Phase
        {
            Clear, // strip everything but the NCU buff
            Walk, // the floor set, then the comfort fill - one action at a time
        }
    }
}