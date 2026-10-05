// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: GeneralCombatBrain.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Enums;
using AOBuddy20.Network;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The GENERAL combat brain: the fallback when no brain for the character's profession is
///     registered (and the base profession brains inherit from). DORMANT for now - it selects
///     no target and takes no action, logging once that it is dormant, until the combat family
///     is implemented.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Combat)]
public class GeneralCombatBrain : CombatBrain
{
    protected readonly PacketRouter _packetRouter;
    private bool _loggedDormant;

    public GeneralCombatBrain(ILogger<GeneralCombatBrain> logger, ControlArbiter controlArbiter, PacketRouter packetRouter)
        : base(logger, controlArbiter, packetRouter)
    {
        _packetRouter = packetRouter;
    }

    protected override SimpleChar? SelectTarget(LocalPlayer me)
    {
        if (!_loggedDormant)
        {
            _loggedDormant = true;
            _logger.LogInformation("COMBAT: general brain is dormant - no combat actions this session.");
        }

        return null;
    }
}