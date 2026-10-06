// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: CombatBrain.cs
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
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     COMBAT BRAIN BASE - the engine every combat brain runs on, with the policy seams a
///     profession overrides. The charter (AOBuddy10 CombatController, wire-proven):
///     Combat is a pure OVERLAY - it never moves the body (the server enforces weapon reach;
///     movement stays with the MovementController goals). Targets come from the ladder: the
///     owner's fight first (no distance cap), then anything attacking us or our pets, then
///     (solo) the nearest attacker. ATTACK IS SENT ONCE PER TARGET, gated on the target
///     CHANGING - never re-issued because IsAttacking looked false; a re-issue restarts the
///     weapon swing timer. No StopAttack when a fight ends - the server auto-stops, and our
///     own engagement closing is not a stop order. The owner's target flickers null mid-fight
///     (null-blink): the last target is HELD while the server still has us swinging, so a
///     policy that loses its pick for a tick cannot make us re-attack.
///     Weapon specials are DATA-DRIVEN (rules.md #4): availability comes from the wire
///     (KnownSpecials, learned from SpecialUsed/SpecialAvailable), not from profession tables;
///     one special per server round-trip; openers (SneakAttack, AimedShot) belong to a target
///     that is not fighting yet - that refinement is the family implementation's.
///     Unfightable targets (blows that never land) go to SetAside for a while instead of
///     re-attack spam.
///     While an engagement is open the brain holds the ControlArbiter at
///     <see cref="ControlPriority.Combat" />; the tick is called from BotLoop on the update
///     thread, the same one every packet handler runs on.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public abstract class CombatBrain
{
    // A target our blows cannot touch sits out this long before we try it again (AOBuddy10's
    // SetAside: feedback-110-immune find-person NPCs).
    private const double SetAsideForgetSeconds = 30.0;

    protected CombatBrain(ILogger logger, ControlArbiter controlArbiter, PacketRouter packetRouter)
    {
        _logger = logger;
        _controlArbiter = controlArbiter;
        _packetRouter = packetRouter;
    }

    protected readonly ILogger _logger;
    protected readonly ControlArbiter _controlArbiter;
    protected readonly PacketRouter _packetRouter;

    protected double _clock; // the brain's own clock, accumulated from dt

    // What Attack was last sent for, THIS engagement: the attack-once gate reads it, and the
    // null-blink hold keeps it while the server still has us fighting.
    private Identity _attackedTarget;

    private bool _holding; // an engagement is open: arbiter held at Combat
    private readonly Dictionary<Identity, double> _setAside = new(); // mob -> clock it may be tried again

    /// <summary>True while an engagement is open (BotLoop claims Tasks.Combat).</summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            HardEnd(); // zone/login loss: forget everything
            return false;
        }

        var target = SelectTarget(me);
        if (target == null || IsSetAside(target.Identity))
        {
            // No target to hold: close the engagement - but the attack-once note only when the
            // server has stopped us fighting (null-blink hold, see class doc).
            if (!me.IsAttacking)
            {
                _attackedTarget = default;
            }

            EndEngagement();
            return false;
        }

        Engage(me, target);
        SpecialsTick(me, target, dt);

        if (!_holding)
        {
            _holding = true;
            _controlArbiter.TakeControl(ControlPriority.Combat);
            _logger.LogInformation($"COMBAT: engaging '{target.Name}'.");
        }

        return true;
    }

    /// <summary>
    ///     POLICY: pick this tick's target from the ladder, or null to stand down. The general
    ///     brain returns null (dormant); profession brains override this - the ENGINE below does
    ///     not change.
    /// </summary>
    protected virtual SimpleChar? SelectTarget(LocalPlayer me)
    {
        return null;
    }

    /// <summary>
    ///     ENGINE: the wire-proven invariant - Attack ONCE per target, re-sent only when the
    ///     target CHANGES. Never gate on IsAttacking: a re-issue restarts the swing timer.
    ///     The server's auto-attack follows our SELECTION, and a heal or a pet command this tick moves
    ///     it (every Cast / CommandPets sets the target first, and BotLoop's heal preempts the combat
    ///     tick while it casts on us). So re-assert the selection on the mob EVERY tick - SetTarget only,
    ///     which does NOT reset the swing timer (AOBuddy10's "restore target, never re-Attack"). Without
    ///     this a buddy stops swinging after a self-heal: the weapon is left selecting us.
    /// </summary>
    protected void Engage(LocalPlayer me, SimpleChar target)
    {
        Targeting.SetTarget(target.Identity);

        if (_attackedTarget == target.Identity)
        {
            return; // already on it - the server keeps swinging; the SetTarget above just restores selection
        }

        me.Attack(target.Identity);
        _attackedTarget = target.Identity;
        _logger.LogInformation($"COMBAT: attack -> '{target.Name}'.");
    }

    /// <summary>
    ///     ENGINE HOOK: weapon specials, once per tick while fighting. Base no-op; a family
    ///     implementation overrides this (one special per server round-trip, openers first).
    /// </summary>
    protected virtual void SpecialsTick(LocalPlayer me, SimpleChar target, double dt)
    {
    }

    /// <summary>
    ///     ENGINE helper: fire one special if the server says we know it and it is off cooldown
    ///     (the cooldowns are wire-maintained - KnownSpecials / Cooldowns). Returns false when
    ///     the special is unknown or still recharging.
    /// </summary>
    protected bool FireSpecial(LocalPlayer me, SimpleChar target, Stat special)
    {
        if (!me.KnownSpecials.Contains(special) || !me.IsSpecialReady(special))
        {
            return false;
        }

        me.PerformSpecialAttack(target.Identity, special);
        _logger.LogInformation($"COMBAT: special {special} -> '{target.Name}'.");
        return true;
    }

    /// <summary>Set a target aside: SelectTarget results matching it are refused for a while.</summary>
    protected void SetAside(Identity mob, double seconds = SetAsideForgetSeconds)
    {
        _setAside[mob] = _clock + seconds;
    }

    protected bool IsSetAside(Identity mob)
    {
        return _setAside.TryGetValue(mob, out var until) && _clock < until;
    }

    private void EndEngagement()
    {
        if (_holding)
        {
            _holding = false;
            _controlArbiter.ReleaseControl();
            _logger.LogInformation("COMBAT: engagement closed.");
        }
    }

    private void HardEnd()
    {
        _attackedTarget = default;
        EndEngagement();
    }
}