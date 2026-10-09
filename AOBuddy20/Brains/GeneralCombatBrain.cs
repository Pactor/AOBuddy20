// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: GeneralCombatBrain.cs
//
// Last modified: 2026-10-06
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Network;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The GENERAL combat brain, now the OWNER-ASSIST buddy (AOBuddy10 CombatController, the swing
///     side). Every profession resolves to this today, so any profession buddy assists. It is the
///     fallback and the base profession brains inherit from it; the ENGINE (attack-once, arbiter hold,
///     null-blink) lives in <see cref="CombatBrain" /> - this supplies only the target policy.
///
///     Behaviour (wire fact: the only broadcast of who someone fights is FightingIdentity - see
///     <see cref="OwnerAssist" />):
///       - SWING what the owner is swinging: target = the owner's own FightingIdentity. No distance cap
///         (the server enforces weapon reach; follow keeps us at his side).
///       - The owner is a PET CLASS fighting THROUGH its pets (AssistSource.OwnerPetTarget): HOLD FIRE -
///         our own pets take that target (the pet brain reads the same owner-assist target), AOBuddy10's
///         pets-only / "hiding" case. We do not melee it.
///       - ENTER when the owner enters (a target appears), EXIT when he exits (target goes null - the
///         engine releases ControlPriority.Combat). When the owner STANDS DOWN while we are still
///         swinging his mob, send exactly ONE StopAttack to mirror him out - the one sanctioned
///         StopAttack send (the engine otherwise never sends one; the server auto-stops at a kill).
///     With no owner configured / in view, the owner-assist target is null and this stays dormant.
///
///     MISSION CLEAR MODE (AOBuddy10, owner 2026-09-25): while the mission run is clearing the
///     building, every fight is the job - the ladder gains a rung ahead of everything else:
///     anything attacking us or our pets (the Awareness books), else the run's pull pick (the mob
///     the walk is closing on) - engaged only once in reach, so the walk is never stranded. A pet
///     class fights the same foes THROUGH ITS PETS: its body never swings (<see cref="Engage" />
///     stays a no-op for it) and the attack pet is driven onto the foe (AOBuddy10's pets-only case,
///     aimed by the run instead of the owner).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Combat)]
public class GeneralCombatBrain : CombatBrain
{
    protected readonly PacketRouter _packetRouter;
    protected readonly OwnerAssist _ownerAssist;
    protected readonly BrainBank _brains;
    protected readonly MissionController _mission;
    protected readonly Awareness _awareness;

    private bool _hadOwnerTarget; // we were swinging the owner's target, for the stand-down StopAttack edge
    private bool _stoodDown; // the one StopAttack has been sent for this stand-down (dedupe)

    public GeneralCombatBrain(ILogger<GeneralCombatBrain> logger, ControlArbiter controlArbiter,
        PacketRouter packetRouter, OwnerAssist ownerAssist, BrainBank brains,
        MissionController mission, Awareness awareness)
        : base(logger, controlArbiter, packetRouter)
    {
        _packetRouter = packetRouter;
        _ownerAssist = ownerAssist;
        _brains = brains;
        _mission = mission;
        _awareness = awareness;
    }

    protected override SimpleChar? SelectTarget(LocalPlayer me)
    {
        // CLEAR MODE first: every fight is the job, pets too - the clear rung sits ahead of the
        // pet-class exit, and a pet class resolves it without swinging (see Engage).
        if (_mission.Clearing)
        {
            var foe = ClearTarget(me);
            if (foe != null)
            {
                return foe;
            }
        }

        // A PET CLASS stays back and lets its pet fight (owner's rule 2026-10-06): no melee-assist at all,
        // the pet brain already drives the pet onto the owner's target. Only non-pet professions swing.
        if (_brains.IsPetClass)
        {
            return null;
        }

        var mob = _ownerAssist.Target(me, out var source);

        // Swing only when the OWNER is swinging it himself.
        if (mob != null && source == AssistSource.OwnerTarget)
        {
            _hadOwnerTarget = true;
            _stoodDown = false;
            return mob;
        }

        // Owner stood down (or switched to pets-only) while we were still swinging his mob: mirror him
        // OUT with exactly one StopAttack. (A natural kill ends the same way, which is also what we want
        // for a pure assist buddy - disengage when the owner disengages.)
        if (!_stoodDown && _hadOwnerTarget && me.IsAttacking)
        {
            me.StopAttack();
            _stoodDown = true;
            _logger.LogInformation("COMBAT: owner stood down - stopping attack to mirror him.");
        }

        _hadOwnerTarget = false;
        return null; // engine closes the engagement (exit combat); pets-only target is left to the pet brain
    }

    /// <summary>The clear-mode foe, in reach: what is on us or our pets, else the run's pull pick.
    /// The range gates keep the engagement inside weapon reach - the walk is the mission's, and an
    /// engagement opened at 20 m would strand it (the mission tick stops running while combat
    /// holds the chain).</summary>
    protected SimpleChar? ClearTarget(LocalPlayer me)
    {
        var forbidden = _mission.ClearFoeForbidden;

        // Anything attacking the bot or our pets (nearest first), inside the fight-walk gate +5 m.
        foreach (var s in _awareness.OnBot.Concat(_awareness.OnPets))
        {
            var n = s.Mob;
            if (n == null || n.Identity == forbidden ||
                DynelManager.Dead.Contains(n.Identity) ||
                (n.TryGetStat(Stat.Health, out var hp) && hp <= 0))
            {
                continue;
            }

            if (me.DistanceFrom(n) <= 8f)
            {
                return n;
            }
        }

        // The run's pull pick, arrived in reach.
        var pick = _mission.PullPick;
        if (pick != null && pick.Identity != forbidden &&
            !DynelManager.Dead.Contains(pick.Identity) &&
            (!pick.TryGetStat(Stat.Health, out var php) || php > 0) &&
            me.DistanceFrom(pick) <= 6f)
        {
            return pick;
        }

        return null;
    }

    /// <summary>A pet class never swings its body - the pets are the weapon (same rule as assist).</summary>
    protected override void Engage(LocalPlayer me, SimpleChar target)
    {
        if (_brains.IsPetClass)
        {
            return;
        }

        base.Engage(me, target);
    }

    protected override void OnEngage(LocalPlayer me, SimpleChar target)
    {
        (_brains.Pet as PetBrain)?.DriveCombatPet(me, target);
    }

    protected override void OnDisengage(LocalPlayer? me)
    {
        if (me != null)
        {
            (_brains.Pet as PetBrain)?.DriveCombatPet(me, null);
        }
    }
}
