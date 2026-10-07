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

    /// <summary>A stat's raw value, 0 when the record does not carry it (most stats are sparse).</summary>
    public int Stat(int statId) => Stats.TryGetValue(statId, out var v) ? v : 0;

    /// <summary>How much this formula modifies a stat when it lands, 0 if it does not touch it.</summary>
    public int Modify(int statId) => Modifies.TryGetValue(statId, out var v) ? v : 0;
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
