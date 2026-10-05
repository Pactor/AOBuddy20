// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BrainKind.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

namespace AOBuddy20.Enums;

/// <summary>
///     The brain families a character carries one brain of each. The family decides the
///     ControlPriority the brain's episodes hold (ExternalBuffing 300, Selfbuffing 600, Pet 650,
///     Combat 700) and which base class a brain derives from.
///     Pet is the pet-class family (Engineer, MP, Bureaucrat, Trader, Adventurer): summon/charm,
///     keep, and command pets. See PETBRAIN-DESIGN.md.
/// </summary>
public enum BrainKind
{
    Combat,
    Selfbuffing,
    ExternalBuffing,
    Pet,
}