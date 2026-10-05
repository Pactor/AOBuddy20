// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: GeneralPetBrain.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The GENERAL pet brain: the fallback when no pet brain for the character's profession is
///     registered (and the base profession pet brains inherit from). DORMANT for now - it keeps
///     no roster, summons nothing and commands nothing, logging once that it is dormant, until
///     the pet families are implemented. A non-pet profession resolves to this and does nothing,
///     which is correct: it has no pets.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Pet)]
public class GeneralPetBrain : PetBrain
{
    private bool _loggedDormant;

    public GeneralPetBrain(ILogger<GeneralPetBrain> logger, ControlArbiter controlArbiter)
        : base(logger, controlArbiter)
    {
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        if (!_loggedDormant)
        {
            _loggedDormant = true;
            _logger.LogInformation("PET: general brain is dormant - no pet actions this session.");
        }

        return false;
    }
}
