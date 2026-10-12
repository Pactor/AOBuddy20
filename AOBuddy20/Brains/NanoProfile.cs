// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: NanoProfile.cs
//
// Last modified: 2026-10-04
// Created:       2026-10-04
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

namespace AOBuddy20.Brains;

/// <summary>
///     One nano formula from OmniCell's nanos.ocp (see <see cref="NanoLibrary" />): the raw
///     decision material, uninterpreted. The stat dictionary carries EVERYTHING the client
///     record holds - NanoStrain (75, the stacking group), StackingOrder (551, who supersedes
///     whom within a strain), the OnUse effect stats (the heal amount lives here - 643 on a
///     complete heal), NCU, cast times - as raw stat-id/value pairs. Actions carry the cast
///     actions with their requirement triples (profession gates, skill minimums), still raw.
///     Reading any of it is the brains' business; the library only carries it. Names are NOT
///     in the pack - <see cref="NanoLibrary.NameOf" /> resolves them from ItemData.bin.
/// </summary>
public sealed class NanoProfile
{
    public int NanoId;
    public IReadOnlyDictionary<int, int> Stats = new Dictionary<int, int>();
    public IReadOnlyList<NanoAction> Actions = Array.Empty<NanoAction>();

    /// <summary>
    ///     The flat stat modifiers this formula APPLIES when it lands (event function 53045
    ///     ModifyStat, arg0 = stat, arg1 = amount), keyed stat -> amount. The buff's actual
    ///     numbers: +MC/+TS (130/131), +Max NCU (181), +attributes (16..21), heal amounts, etc.
    ///     - read straight from the pack so the buff catalog never parses an effect string.
    ///     Only integer-argument modifies are kept (the flat buffs); percent/float modifies
    ///     are not (they are not what the pet-buff math needs). Several functions touching the
    ///     same stat are summed.
    /// </summary>
    public IReadOnlyDictionary<int, int> Modifies = new Dictionary<int, int>();

    /// <summary>Per-modified-stat effect target: User(1)/Wearer(2)=self, Target(3)=others. The accurate
    /// self/other signal for a specific skill, unlike the formula-wide TargetsOthers.</summary>
    public IReadOnlyDictionary<int, int> ModifyTargets = new Dictionary<int, int>();

    /// <summary>True when the buff to <paramref name="statId"/> lands on the cast TARGET (others); false = self.</summary>
    public bool ModifyTargetsOthers(int statId) => ModifyTargets.TryGetValue(statId, out var t) && t == 3;

    /// <summary>
    ///     The nanos this formula CASTS, as (FunctionType, TargetNanoId) pairs: CastNano (53051, on SELF),
    ///     TeamCastNano (53066, on the whole TEAM) and AreaCastNano (53087, on others in an area). A Fixer
    ///     NCU wrapper (e.g. 275043) casts the landed buff through one of these - the function type tells us
    ///     whether the landed nano reaches OTHERS (team/area) or only the caster (self). This is the
    ///     self-vs-others delivery signal (AOBuddy10's method: a self-cast buff cannot be requested from a
    ///     buff bot, however rich the landed nano's own effects are).
    /// </summary>
    public IReadOnlyList<(int Func, int NanoId)> Casts = Array.Empty<(int, int)>();

    /// <summary>
    ///     The formula's SummonPet calls (function 53167), one per pet VARIANT the formula can
    ///     produce: the template is in Args[1] and each call carries its own requirement leaves -
    ///     the TS/MC THRESHOLDS that select the variant (owner, 2026-10-07: "if you have >=692
    ///     TS/MC the highest one"). The highest variant whose thresholds the caster's stats meet
    ///     at cast time is the pet they get - and 80% of ITS thresholds is the obedience floor.
    ///     Calls without skill-gate leaves (op 93 chains) are template-fixed fallbacks.
    /// </summary>
    public IReadOnlyList<NanoSummonPet> SummonPets = Array.Empty<NanoSummonPet>();

    /// <summary>
    ///     True when this formula has a direct effect function targeting the cast TARGET (Target == 3), i.e.
    ///     a STANDALONE buff a bot can land on another character (single-target-on-others). Self-only
    ///     standalone buffs carry only User(1)/Wearer(2) effect targets. Not set by the cast functions above
    ///     (those deliver OTHER nanos) - only by the formula's own landing effects.
    /// </summary>
    public bool TargetsOthers;

    /// <summary>
    ///     EVERY effect function this formula runs when it lands, in record order - the complete behaviour,
    ///     not just the digested views above. The convenience fields (<see cref="Modifies" />,
    ///     <see cref="Casts" />, <see cref="SummonPets" />, <see cref="TargetsOthers" />) are derived from
    ///     this same list; read it directly when you need what they drop:
    ///     <list type="bullet">
    ///       <item>NON-stat effects - heals (53002/53012...), damage shields/absorbs/reflects, teleports,
    ///       drains, nukes, stuns: everything that is not ModifyStat (53045). This is the raw material for
    ///       ranking SURVIVAL buffs (best max-health / absorb / reflect for a level).</item>
    ///       <item>DURATION - <see cref="NanoEffect.TickCount" /> x <see cref="NanoEffect.TickInterval" />
    ///       per effect (see <see cref="NanoEffect.DurationRaw" />): how long the effect lasts, the input to
    ///       "when does our buff expire / need refreshing". UNIT not yet calibrated - see that field.</item>
    ///       <item>PER-EFFECT target and gate - <see cref="NanoEffect.Target" /> (self vs the cast target,
    ///       which may be you, your pet, or another player) and <see cref="NanoEffect.Requirements" /> (the
    ///       effect's own leaves): the raw signal for pet-only vs pet+you vs pet+you+others.</item>
    ///     </list>
    /// </summary>
    public IReadOnlyList<NanoEffect> Effects = Array.Empty<NanoEffect>();

    /// <summary>A stat's raw value, 0 when the record does not carry it (most stats are sparse).</summary>
    public int Stat(int statId) => Stats.TryGetValue(statId, out var v) ? v : 0;

    /// <summary>How much this formula modifies a stat when it lands, 0 if it does not touch it.</summary>
    public int Modify(int statId) => Modifies.TryGetValue(statId, out var v) ? v : 0;

    /// <summary>
    ///     The longest-lived effect's raw lifetime (max TickCount x TickInterval over the effects). For a
    ///     buff this is its own timer - the number to compare against elapsed time when deciding a refresh.
    ///     UNIT IS NOT YET CALIBRATED: the cast-time convention elsewhere is centiseconds
    ///     (HealController reads Stat(210)/100 = seconds), so this is PROBABLY centiseconds, but that must be
    ///     confirmed against one observed in-game buff timer (a sniff / the buff icon countdown) before any
    ///     refresh math trusts it. 0 = instant / no timed effect.
    /// </summary>
    public int DurationRaw => Effects.Count == 0
        ? 0
        : Effects.Select(e => e.DurationRaw).DefaultIfEmpty(0).Max();
}

/// <summary>How a function argument was tagged in the pack: 1 = int32, 2 = float, 3 = string.</summary>
public enum NanoArgKind { Int = 1, Float = 2, String = 3 }

/// <summary>One effect-function argument, keeping its original tag and order (ModifyStat is
/// [Int stat, Int amount]; a percent heal/absorb carries a Float; some carry a String).</summary>
public readonly struct NanoArg
{
    public readonly NanoArgKind Kind;
    public readonly int Int;
    public readonly double Float;
    public readonly string? Str;

    private NanoArg(NanoArgKind kind, int i, double f, string? s)
    {
        Kind = kind;
        Int = i;
        Float = f;
        Str = s;
    }

    public static NanoArg OfInt(int i) => new(NanoArgKind.Int, i, 0, null);
    public static NanoArg OfFloat(double f) => new(NanoArgKind.Float, 0, f, null);
    public static NanoArg OfString(string s) => new(NanoArgKind.String, 0, 0, s);
}

/// <summary>
///     One effect function of a formula (the client's spell-function record), captured in full. The
///     function TYPE is the mechanic (53045 ModifyStat, 53051/53066/53087 cast-nano self/team/area,
///     53167 SummonPet, and every other: heals, absorbs, reflects, teleports, drains, nukes, stuns -
///     uninterpreted here, exposed raw). <see cref="Target" /> is who it lands on; <see cref="Args" />
///     the parameters; <see cref="Requirements" /> the effect's own gate leaves.
/// </summary>
public sealed class NanoEffect
{
    public int FunctionType;

    /// <summary>User(1)/Wearer(2) = the caster (self); Target(3) = the cast target - which may be you,
    /// your pet, or another player depending on how the nano is cast and its receiver gates.</summary>
    public int Target;

    /// <summary>Number of times the effect applies. 1 = a single application (most stat buffs).</summary>
    public int TickCount;

    /// <summary>Raw time between ticks, in the client's own unit (see <see cref="DurationRaw" />).</summary>
    public int TickInterval;

    public bool DoLocalStats;

    /// <summary>The effect's own requirement leaves (gates on THIS effect, e.g. target-type checks),
    /// distinct from the formula's cast actions in <see cref="NanoProfile.Actions" />.</summary>
    public IReadOnlyList<NanoRequirement> Requirements = Array.Empty<NanoRequirement>();

    /// <summary>Every argument, original tag and order preserved. ModifyStat = [Int stat, Int amount].</summary>
    public IReadOnlyList<NanoArg> Args = Array.Empty<NanoArg>();

    /// <summary>Lands on the caster (self).</summary>
    public bool OnSelf => Target is 1 or 2;

    /// <summary>Lands on the cast target (you/pet/other), not the caster.</summary>
    public bool OnTarget => Target == 3;

    /// <summary>This effect's raw lifetime: TickCount x TickInterval, in the client's time unit. See
    /// <see cref="NanoProfile.DurationRaw" /> for the unit caveat (not yet calibrated).</summary>
    public int DurationRaw => TickCount * TickInterval;

    /// <summary>The i-th argument as an int, or null when absent / not int-tagged.</summary>
    public int? IntArg(int i) => i >= 0 && i < Args.Count && Args[i].Kind == NanoArgKind.Int ? Args[i].Int : null;
}

/// <summary>One cast action of a formula (ToUse/...), with its requirement list.</summary>
public sealed class NanoAction
{
    public int ActionType;
    public IReadOnlyList<NanoRequirement> Requirements = Array.Empty<NanoRequirement>();
}

/// <summary>One requirement triple, raw: stat number, operator, target and value.</summary>
public sealed class NanoRequirement
{
    public int ChildOperator;
    public int Operator;
    public int Stat;
    public int Target;
    public int Value;
}

/// <summary>One SummonPet call of a formula: the template arguments and the requirement leaves
/// that select this variant (the TS/MC thresholds).</summary>
public sealed class NanoSummonPet
{
    public IReadOnlyList<int> Args = Array.Empty<int>();
    public IReadOnlyList<NanoRequirement> Requirements = Array.Empty<NanoRequirement>();
}
