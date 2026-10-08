// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MetaphysicistCombatBrain.cs
//
// Last modified: 2026-10-08
// Created:       2026-10-08
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Network;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The METAPHYSICIST combat brain: the pets fight, the body stays in the background
///     (owner's rule 2026-10-08). The engine runs unchanged - attack-once, arbiter hold,
///     null-blink - but this profession never swings:
///       - <see cref="Engage" /> is a no-op: no body attack is ever sent, nothing to stand down.
///       - <see cref="OnEngage" /> hands the mob to the PET brain every tick
///         (<see cref="PetBrain.DriveCombatPet" />): the attack pet is driven onto it, re-driven
///         when the target changes or a pet misses the order - the pet family's own engine.
///       - <see cref="OnDisengage" /> stands the pets down to Follow, once.
///     The target ladder (the same one the general brain's doc describes, minus the melee step):
///       1. the owner-assist target - the owner is swinging it himself, or his pets are on it;
///       2. anything attacking us or OUR pets (we hold aggro while the owner stands down).
///     The body's positioning stays with the movement layer (combat is a pure overlay here too:
///     no retreat, no kiting - the pets do the dying).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Combat, Profession.Metaphysicist)]
public class MetaphysicistCombatBrain : GeneralCombatBrain
{
    public MetaphysicistCombatBrain(ILogger<MetaphysicistCombatBrain> logger, ControlArbiter controlArbiter,
        PacketRouter packetRouter, OwnerAssist ownerAssist, BrainBank brains)
        : base(logger, controlArbiter, packetRouter, ownerAssist, brains)
    {
    }

    protected override SimpleChar? SelectTarget(LocalPlayer me)
    {
        // 1. the owner's fight (himself, or the pets he is fighting through)
        var mob = _ownerAssist.Target(me, out _);
        if (mob != null)
        {
            return mob;
        }

        // 2. anything that has turned on us or our pets - the meta does not get to pick fights,
        //    but it does not get to be lunch either: the pets answer aggro.
        foreach (var npc in DynelManager.Npcs)
        {
            if (!npc.FightingIdentity.HasValue || !IsAlive(npc))
            {
                continue;
            }

            var attacker = npc.FightingIdentity.Value;
            if (attacker == me.Identity || me.Pets.Any(p => p.Identity == attacker))
            {
                return npc;
            }
        }

        return null;
    }

    /// <summary>The body never swings - the pets are the weapon.</summary>
    protected override void Engage(LocalPlayer me, SimpleChar target)
    {
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

    private static bool IsAlive(SimpleChar c)
    {
        return !c.TryGetStat(Stat.Health, out var hp) || hp > 0;
    }
}
