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

    /// <summary>A stat's raw value, 0 when the record does not carry it (most stats are sparse).</summary>
    public int Stat(int statId) => Stats.TryGetValue(statId, out var v) ? v : 0;
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
