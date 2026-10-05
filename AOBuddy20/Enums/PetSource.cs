// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PetSource.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

namespace AOBuddy20.Enums;

/// <summary>
///     How a pet came to be ours - "the pet knows if it is a summon or a charm" (owner,
///     2026-10-05). It is not on the wire per pet; the brain records it at acquisition, because
///     re-acquiring differs: a <see cref="Summoned" /> slot re-casts its summon nano, a
///     <see cref="Charmed" /> slot re-charms a fresh target, and the two gate on different skills.
///     Charm is used by Bureaucrat, Trader and Adventurer (three different charm systems, one
///     source). See PETBRAIN-DESIGN.md.
/// </summary>
public enum PetSource
{
    Summoned,
    Charmed,
}
