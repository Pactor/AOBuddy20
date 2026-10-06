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
///     whom within a strain), TimeExist (8, the effect's run time in 100th-seconds - the 4-hour
///     infuses read 1,440,000, the 3-minute wrangles 18,000), the OnUse effect stats (the heal
///     amount lives here - 643 on a complete heal), NCU, cast times - as raw stat-id/value
///     pairs. Actions carry the cast actions with their requirement triples (profession gates,
///     skill minimums), still raw. Summons carry the pet-creation branches (see
///     <see cref="SummonBranch" />). Reading any of it is the brains' business; the library
///     only carries it. Names are NOT in the pack - <see cref="NanoLibrary.NameOf" /> resolves
///     them from ItemData.bin.
/// </summary>
public sealed class NanoProfile
{
    public int NanoId;
    public IReadOnlyDictionary<int, int> Stats = new Dictionary<int, int>();
    public IReadOnlyList<NanoAction> Actions = Array.Empty<NanoAction>();
    public IReadOnlyList<SummonBranch> Summons = Array.Empty<SummonBranch>();

    /// <summary>
    ///     The child formula a TEAMCAST function puts on the team (omnicell's FunctionType
    ///     TeamCastNano = 53066, one int arg: the child nano id), 0 when the formula has none.
    ///     A team-cast formula is a WRAPPER that never lands as a buff itself - the "Team Skill
    ///     Wrangler" carries no NanoStrain of its own; the nano that lands on the team is this
    ///     child, and ITS stat 75 is the stacking group the receiver actually gets (owner,
    ///     2026-10-06: "team wranglers cast 2 different nanos - 1 on the caster and one on the
    ///     team"; the caster-side CastNano child is deliberately not followed).
    /// </summary>
    public int TeamCastChild;

    /// <summary>A stat's raw value, 0 when the record does not carry it (most stats are sparse).</summary>
    public int Stat(int statId) => Stats.TryGetValue(statId, out var v) ? v : 0;

    /// <summary>
    ///     The highest template level any summon branch of this formula can yield - the formula's
    ///     true strength order. THIS ranks summon nanos, never the crystal QL: the heal line's
    ///     ids interleave against power (Medinos 125738 is the weakest, Salvinous 125745 the
    ///     second), so QL ordering picks exactly the wrong pets.
    /// </summary>
    public int TopTemplateLevel => Summons.Count == 0 ? 0 : Summons.Max(b => b.TemplateLevel);

    /// <summary>
    ///     TimeExist (stat 8): how long the formula's effect runs. The record stores it in
    ///     100th-seconds of game time - Composite Mochams read 360,000 per hour, the nano-skill
    ///     infuses 1,440,000 (4 hours), the Trader wrangles 18,000 (3 minutes).
    /// </summary>
    public int TimeExistSeconds => Stat(8) / 100;
}

/// <summary>
///     One pet-creation branch of a formula (SummonPet 53167 / SpawnItem 53064, captured out of
///     the record's effect events). The multi-tier self-scaling summons carry ONE function per
///     template they can yield, each with its own requirement list - Summon Anger Manifestation
///     runs seven (templates 10..1, the top one gated at Time&amp;Space/Matter Creation 63, the
///     bottom free), Summon Biazu five (180..160). THE PET ITSELF NEVER TELLS US ITS
///     REQUIREMENTS (owner, 2026-10-05, marked IMPORTANT) - which tier we GOT is answered by
///     evaluating these lists against the stats held at the cast instant; the highest branch
///     whose skill requirements the snapshot satisfies is the pet the server hands out.
///     Non-skill requirements on a branch (op 93/42 on stat 0 - the heal pets' repeated
///     healing-action calls) do not gate the tier and are ignored by that evaluation.
/// </summary>
public sealed class SummonBranch
{
    /// <summary><see cref="AOSharp.Common.GameData.SpellFunction" /> SummonPet or SpawnItem.</summary>
    public int FunctionType;

    /// <summary>The pet level this branch yields - the first template argument (180 for Biazu's
    /// top branch, 10 for Anger's). 0 when the record carries no template argument.</summary>
    public int TemplateLevel;

    /// <summary>The branch's own gate: the skill minimums that select this template.</summary>
    public IReadOnlyList<NanoRequirement> Requirements = Array.Empty<NanoRequirement>();

    /// <summary>The raw int arguments of the function, uninterpreted.</summary>
    public IReadOnlyList<int> Args = Array.Empty<int>();
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
