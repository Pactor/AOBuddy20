// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: ExternalBuffingBrain.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Utils;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     EXTERNAL BUFFING BRAIN BASE - being buffed from OUTSIDE the character, in two directions
///     (AOBuddy10's ChewyBuffs / CodedocBuffs / SupportController keep-up side):
///     (1) ASKING buff-bots and players: the staged tell ladder - the NCU buff first, then the
///     own profession's codes, then helper skill buffs (the cheapest set closing every skill
///     gap of a wanted nano), movement, regen, and the fill by score-per-NCU; team-cast buffs
///     come with a team-invite handshake. Refusals are learned ("your level is too low" until
///     he levels; self-only auras that can never land on us), and every ask is NCU-aware.
///     NOTE for the family implementation: the invite/team packets are not wired yet - when
///     they are, they register through a neutral singleton (the Awareness pattern) or the
///     BrainBank as an IPacketConsumer, NOT from a brain instance (brain construction happens
///     after the session starts, too late for RegisterPackets).
///     (2) BEING buffed by the owner/others: verify a landed buff ~20 s later (BuffStatus
///     .BuffChanged / Client.NanoSeen), and never re-ask for what cannot land.
///     The stage machine below is the ladder's skeleton; EnterStage is the only legal way to
///     move, so the log carries every transition.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public abstract class ExternalBuffingBrain
{
    protected ExternalBuffingBrain(ILogger logger, ControlArbiter controlArbiter)
    {
        _logger = logger;
        _controlArbiter = controlArbiter;
    }

    protected readonly ILogger _logger;
    protected readonly ControlArbiter _controlArbiter;

    protected double _clock; // the brain's own clock, accumulated from dt

    /// <summary>The ask ladder, in the order the buff-bots expect it.</summary>
    protected enum Stage
    {
        Idle,
        NcuBuff,
        OwnProfession,
        HelperSkills,
        Movement,
        Regen,
        Fill,
        Verify, // being buffed: confirm what was cast on us actually landed
    }

    protected Stage _stage = Stage.Idle;

    /// <summary>True while an external-buffing episode is open (BotLoop claims Tasks.ExternalBuff).</summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            return false;
        }

        return PolicyTick(me, dt);
    }

    /// <summary>
    ///     POLICY: this tick's decisions, in <see cref="_stage" />. Return true only while an
    ///     episode holds the arbiter at ControlPriority.ExternalBuffing. The general brain
    ///     returns false (dormant) - nothing ever leaves Stage.Idle.
    /// </summary>
    protected virtual bool PolicyTick(LocalPlayer me, double dt)
    {
        return false;
    }

    /// <summary>
    ///     CROSS-BRAIN: whether an external-buffing episode is in flight (a dance, a renewal, a
    ///     session). The chain gates SELF-BUFFING on this - the buff-up owns the cast window,
    ///     and a selfbuff cast would collide with the pet summons the dance drives (one cast at
    ///     a time on the wire). Base answer: never busy (the general brain is dormant).
    /// </summary>
    public virtual bool BuffingInProgress => false;

    /// <summary>ENGINE: the only legal way to change stage - every transition is logged.</summary>
    protected void EnterStage(Stage next, string why)
    {
        if (_stage == next)
        {
            return;
        }

        _logger.LogInformation($"EXTBUFF: stage {_stage} -> {next} ({why}).");
        _stage = next;
    }

    /// <summary>
    ///     SEAM: send a tell. Every ask goes through here, so the log carries what we asked of
    ///     whom; no chat client, no ask.
    /// </summary>
    protected void Tell(uint charId, string message)
    {
        if (Client.Chat == null)
        {
            _logger.LogInformation($"EXTBUFF: no chat client - tell to {charId} not sent: '{message}'.");
            return;
        }

        _logger.LogInformation($"EXTBUFF: tell to {charId}: '{message}'.");
        Client.SendPrivateMessage(charId, message);
    }
}