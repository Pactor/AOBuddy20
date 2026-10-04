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
///     One castable nano from the profession's profile (GameData/Profiles/&lt;profession&gt;-nanos.json,
///     tools/mp-nano-extractor over nanos.ocp): the raw decision material, UNINTERPRETED - the cast
///     requirements and the effect summary in the extractor's own text form
///     ("PsychologicalModification &gt; 703", "Modify[54 +/-100]", "SummonPet 20x20..."), the AOSharp
///     NanoLine enum name for the nano's strain, and the profile category the file filed it under
///     ("Heals - Single Target", "Nukes / Damage", ...). Reading any of it is the brains' business;
///     the library only carries it.
/// </summary>
public sealed class NanoProfile
{
    public int NanoId;
    public string Name = "";
    public string Category = "";
    public int NanoStrain;
    public string NanoLine = "";
    public int MinLevel; // the ToUse action's character-level requirement; 0 = none stated
    public bool PetTargeted; // the cast wants a pet as its target
    public IReadOnlyList<string> CastReqs = Array.Empty<string>();
    public IReadOnlyList<string> Effects = Array.Empty<string>();
}
