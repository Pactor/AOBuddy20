// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: OwnerAssist.cs
//
// Last modified: 2026-10-06
// Created:       2026-10-06
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOSharp.Clientless;
using AOSharp.Common.GameData;

namespace AOBuddy20.Controlling;

/// <summary>
///     THE ONE place the bot reads "who is my owner fighting right now". Ported from AOBuddy10's
///     CombatController.GetAssistTarget. The whole thing rests on one wire fact: the only broadcast
///     signal of who someone is fighting is their <c>FightingIdentity</c> (the SDK fills it from the
///     target's AttackMessage / combat full-update and clears it on StopFight). A bare selection
///     (LookAt) and /assist are NEVER relayed for another character - so owner-assist only works once
///     the owner (or the owner's pet) is actually in combat. See [[owner-target-assist]].
///
///     The target ladder, in priority order:
///       1. the owner's OWN target (owner.FightingIdentity) - the owner is swinging it himself;
///       2. the target the owner's PETS are on - the owner is a PET CLASS fighting through its pets
///          (scan the NPCs the SERVER says the owner owns, read THEIR FightingIdentity).
///     Alive-gated; NOT range-gated - each caller applies its own leash (the pet hunter leashes to its
///     hunt radius; the combat buddy to weapon range). <see cref="AssistSource" /> tells which branch hit
///     so a caller can decide to only send pets (owner fighting through pets) vs also swing itself.
/// </summary>
public sealed class OwnerAssist
{
    // Grace: the owner's FightingIdentity can blip null for a tick at a target change / a full-update
    // echo while he is still fighting the same LIVE mob. Hold the last target for this long so assist
    // does not drop (and the combat buddy does not fire a spurious StopAttack) over a one-tick gap. A
    // real stand-down / a kill (the held mob is gone or dead) clears inside the window regardless.
    private const long GraceMs = 2500;

    private readonly string _ownerName;

    private Identity? _lastMobId;
    private AssistSource _lastSource;
    private long _lastSeenMs;

    public OwnerAssist(AccountInfo config)
    {
        _ownerName = config.Owner ?? "";
    }

    /// <summary>The owner as a live dynel (by configured name), or null when unset / not in view.</summary>
    public PlayerChar? Owner()
    {
        return string.IsNullOrEmpty(_ownerName)
            ? null
            : DynelManager.Players.FirstOrDefault(p => string.Equals(p.Name, _ownerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The mob the owner is fighting right now (own target first, then what the owner's pets are on),
    ///     or null when the owner is not in combat / not in view. Alive-gated, not range-gated.
    /// </summary>
    public NpcChar? Target(LocalPlayer me, out AssistSource source)
    {
        source = AssistSource.None;
        var owner = Owner();
        if (owner == null)
        {
            return null;
        }

        // 1) The owner's OWN target.
        if (owner.FightingIdentity.HasValue
            && DynelManager.Find(owner.FightingIdentity.Value, out NpcChar mob) && IsAlive(mob))
        {
            return Remember(mob, AssistSource.OwnerTarget, out source);
        }

        // 2) What the owner's PETS are fighting (owner is a pet class). Ownership is the SERVER-stated
        //    NpcChar.Owner, never distance-guessed; our own pets are excluded because their Owner is us,
        //    not the owner. Take the first such pet's live target.
        foreach (var pet in DynelManager.Npcs)
        {
            if (!pet.Owner.HasValue || pet.Owner.Value != owner.Identity || !pet.FightingIdentity.HasValue)
            {
                continue;
            }

            if (DynelManager.Find(pet.FightingIdentity.Value, out NpcChar petMob) && IsAlive(petMob))
            {
                return Remember(petMob, AssistSource.OwnerPetTarget, out source);
            }
        }

        // Nothing right now: hold the last target briefly over a one-tick FightingIdentity blip, but only
        // while it is still a live dynel (a kill / despawn ends assist at once, as does the grace expiring).
        if (Environment.TickCount64 - _lastSeenMs < GraceMs && _lastMobId.HasValue
            && DynelManager.Find(_lastMobId.Value, out NpcChar held) && IsAlive(held))
        {
            source = _lastSource;
            return held;
        }

        _lastMobId = null;
        return null;
    }

    private NpcChar Remember(NpcChar mob, AssistSource found, out AssistSource source)
    {
        _lastMobId = mob.Identity;
        _lastSource = found;
        _lastSeenMs = Environment.TickCount64;
        source = found;
        return mob;
    }

    private static bool IsAlive(SimpleChar c)
    {
        return c != null && (!c.TryGetStat(Stat.Health, out var hp) || hp > 0);
    }
}

/// <summary>Which branch of the owner-assist ladder produced the target - lets a caller tell "the owner
/// is swinging it himself" (so the buddy may swing too) from "the owner is fighting through pets" (so the
/// buddy sends only its pets and holds fire).</summary>
public enum AssistSource
{
    None,
    OwnerTarget,    // the owner's own FightingIdentity
    OwnerPetTarget, // the target the owner's pets are on
}
