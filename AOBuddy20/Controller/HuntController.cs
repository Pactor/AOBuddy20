// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: HuntController.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Components;
using AOBuddy20.Configuration;
using AOBuddy20.Nav;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Controlling;

/// <summary>
///     HUNT - the pets do the fighting, the bot stays put and passive (owner, 2026-10-05: "hide in a
///     safe room, send pets, only attack if they aggro back to him"). Ported/extended from AOBuddy10
///     HuntController. Produces only the PET's target each tick; moves nothing.
///     Target priority (highest first):
///     1. OWNER-ASSIST - if the player we follow is attacking a mob, the pets attack it too, no matter
///        any ignore/blacklist (owner, 2026-10-05). Works even with hunt OFF: helping the owner's own
///        deliberate attack is baseline, and he only attacks when he means to.
///     2. DEFEND - a mob attacking ANY of us (the bot, a teammate, or a pet of any of them) beats a
///        hunt mob that is not; a temp set-aside is cleared when the mob attacks us.
///     3. HUNT (only while Active) - the nearest huntable mob in <see cref="Radius" />.
///     Safety rails on the PROACTIVE hunt (owner-assist and defend bypass all of them):
///     - PERMA-BLACKLIST (persisted by name): mini-bosses we would only die to until we level. Never
///       hunted, and NOT cleared by aggro - only owner-assist overrides it.
///     - LEVEL CEILING scaled off the PET, not the player (a L40 owner can field a L100 pet): a mob is
///       too high if its level exceeds (best attack-pet level + <see cref="LevelMargin" />).
///     - FACTION-SAFE (Shadowlands): we cannot tell a mob's faction from local data, but a same-faction
///       mob never aggros us while the opposing one does - so in faction-safe mode we drop the blanket
///       Monster-side hunt and take only learned-hostile/aggroing mobs, which can never be our own
///       faction. Auto = on whenever our Redeemed/Unredeemed standing is set.
///     Runs on the update thread (the pet brain calls Tick); no locks.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class HuntController
{
    private const float DefaultRadius = 40f;
    private const float LeashSlack = 15f;
    private const double EngageTimeoutSec = 25.0;
    private const double SetAsideSec = 30.0;
    private const double LeashSetAsideSec = 15.0;
    private const double IdleLogSec = 30.0;

    public enum FactionMode
    {
        Auto, // faction-safe whenever our Redeemed/Unredeemed standing is set
        On,   // always faction-safe (learned-hostile/aggro only, no blanket Monster-side)
        Off,  // never (blanket Monster-side hunt) - the owner's call on RubiKa
    }

    private readonly ILogger<HuntController> _logger;
    private readonly AccountInfo _config;
    private readonly OwnerAssist _ownerAssist;
    private readonly string _ownerName;
    private readonly HashSet<string> _perma = new(StringComparer.OrdinalIgnoreCase); // mob NAMES

    public bool Active { get; private set; }
    public float Radius { get; private set; } = DefaultRadius;
    public int LevelMargin { get; private set; } = 10;   // a mob may be this far above the pet
    public FactionMode Faction { get; private set; } = FactionMode.Auto;

    /// <summary>The mob the attack pets should be on this tick, or null.</summary>
    public SimpleChar? Target { get; private set; }

    private double _clock;
    private Identity? _manual; // the 'pet attack' sticky target - overrides everything until cleared/dead
    private Identity? _current;
    private double _sentAt;
    private int _hpAtSend;
    private int _kills;
    private double _idleLog = -99;
    private bool _noPetLogged;
    private readonly Dictionary<Identity, double> _setAside = new();

    public HuntController(AccountInfo config, OwnerAssist ownerAssist, ILogger<HuntController> logger)
    {
        _logger = logger;
        _config = config;
        _ownerAssist = ownerAssist;
        _ownerName = config.Owner ?? "";

        // All defaults come from the one conf file; a 'hunt' command updates them and saves it back.
        Radius = Math.Clamp(config.HuntRadius, 5f, 100f);
        LevelMargin = Math.Clamp(config.HuntMaxLevelMargin, 0, 500);
        if (Enum.TryParse<FactionMode>(config.HuntFactionMode, true, out var fm))
        {
            Faction = fm;
        }

        foreach (var name in config.HuntBlacklist ?? new List<string>())
        {
            _perma.Add(name);
        }

        if (_perma.Count > 0)
        {
            _logger.LogInformation($"HUNT: {_perma.Count} perma-blacklisted mob name(s) from the conf.");
        }
    }

    // ---- The 'hunt' owner command ----------------------------------------------------------

    public void Command(string[] parts, Action<string> reply)
    {
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        switch (arg)
        {
            case "on":
                Active = true;
                reply($"Hunt ON (radius {Radius:0} m, faction {Faction}, +{LevelMargin} over pet) - pets attack; the bot stays put.");
                break;
            case "off":
                Active = false;
                _current = null;
                Target = null;
                reply($"Hunt OFF ({_kills} down). Owner-assist still helps the player's own fight.");
                break;
            case "radius":
                if (parts.Length > 2 && float.TryParse(parts[2], out var r))
                {
                    Radius = Math.Clamp(r, 5f, 100f);
                    _config.HuntRadius = Radius;
                    _config.Save();
                }

                reply($"Hunt radius {Radius:0} m.");
                break;
            case "maxlevel":
                if (parts.Length > 2 && int.TryParse(parts[2], out var m))
                {
                    LevelMargin = Math.Clamp(m, 0, 500);
                    _config.HuntMaxLevelMargin = LevelMargin;
                    _config.Save();
                }

                reply($"Hunt level ceiling: pet level + {LevelMargin}.");
                break;
            case "faction":
                if (parts.Length > 2 && Enum.TryParse<FactionMode>(parts[2], true, out var fm))
                {
                    Faction = fm;
                    _config.HuntFactionMode = Faction.ToString();
                    _config.Save();
                }

                reply($"Hunt faction mode: {Faction}.");
                break;
            case "blacklist":
                BlacklistCommand(parts, reply);
                break;
            default:
                reply($"Hunt {(Active ? "ON" : "OFF")} - radius {Radius:0} m, faction {Faction}, ceiling pet+{LevelMargin}, " +
                      $"{_perma.Count} blacklisted, {_kills} down. Usage: hunt on|off|radius N|maxlevel N|faction auto|on|off|blacklist ...");
                break;
        }
    }

    private void BlacklistCommand(string[] parts, Action<string> reply)
    {
        var sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            reply(_perma.Count == 0 ? "Hunt blacklist is empty." : $"Hunt blacklist ({_perma.Count}): {string.Join(", ", _perma)}.");
            return;
        }

        // The name is everything after 'blacklist add/remove' (names have spaces).
        var name = string.Join(' ', parts.Skip(3)).Trim();
        if (name.Length == 0)
        {
            reply("Usage: hunt blacklist add <mob name> | remove <mob name> | list.");
            return;
        }

        if (sub == "add")
        {
            _perma.Add(name);
            PersistBlacklist();
            reply($"Blacklisted '{name}' - the pets will never hunt it (owner-assist still can).");
        }
        else if (sub == "remove")
        {
            var removed = _perma.Remove(name);
            PersistBlacklist();
            reply(removed ? $"Removed '{name}' from the blacklist." : $"'{name}' was not blacklisted.");
        }
        else
        {
            reply("Usage: hunt blacklist add <mob name> | remove <mob name> | list.");
        }
    }

    private void PersistBlacklist()
    {
        _config.HuntBlacklist = _perma.ToList();
        _config.Save();
    }

    // ---- The decision ----------------------------------------------------------------------

    public SimpleChar? Tick(LocalPlayer me, Awareness awareness, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            _current = null;
            Target = null;
            return null;
        }

        if (!me.Pets.Any(p => p.Role == PetType.Attack))
        {
            if (Active && !_noPetLogged)
            {
                _noPetLogged = true;
                _logger.LogInformation("HUNT: on, but no attack pet up - waiting for one.");
            }

            Target = null;
            return null;
        }

        _noPetLogged = false;
        var centre = me.Transform.Position;
        var guard = BuildGuard(me);

        // 0) MANUAL 'pet attack' - the owner pointed the pets at a mob; hold them on it (overrides
        // hunt and owner-assist) until it dies/despawns or the owner clears it ('pet follow').
        if (_manual.HasValue)
        {
            if (DynelManager.Find(_manual.Value, out NpcChar manual) && IsAlive(manual))
            {
                Target = manual;
                return manual;
            }

            _manual = null; // the manual target is gone - fall back to auto
        }

        // 1) OWNER-ASSIST - overrides every list, works even with hunt off.
        var ownerMob = OwnerFightTarget(me);
        if (ownerMob != null)
        {
            _current = null; // owner-assist is separate from the hunt's own tracking
            Target = ownerMob;
            return ownerMob;
        }

        if (!Active)
        {
            _current = null;
            Target = null;
            return null;
        }

        var factionSafe = IsFactionSafe(me);
        var ceiling = PetCeiling(me);
        var mob = _current.HasValue && DynelManager.Find(_current.Value, out NpcChar cur) ? cur : null;

        if (_current.HasValue && (mob == null || !IsAlive(mob)))
        {
            _logger.LogInformation($"HUNT: '{Target?.Name}' done ({++_kills} down).");
            _current = null;
            mob = null;
        }
        else if (mob != null && Movement.Flat(mob.Transform.Position, centre) > Radius + LeashSlack)
        {
            _logger.LogInformation($"HUNT: '{mob.Name}' went past the {Radius + LeashSlack:0} m leash - leaving it.");
            SetAside(mob.Identity, LeashSetAsideSec);
            _current = null;
            mob = null;
        }
        else if (mob != null && _clock - _sentAt > EngageTimeoutSec && !Losing(mob) && !OnUs(mob, awareness, guard))
        {
            // Spawned in a wall / unreachable (owner, 2026-10-05) - leave it be unless it is on us.
            _logger.LogInformation($"HUNT: the pets never got onto '{mob.Name}' in {EngageTimeoutSec:0} s - set aside {SetAsideSec:0} s.");
            SetAside(mob.Identity, SetAsideSec);
            _current = null;
            mob = null;
        }

        // DEFEND: something on us beats a hunt mob that is not - drop the current pick.
        if (mob != null && !OnUs(mob, awareness, guard)
            && DynelManager.Npcs.Any(n => n != null && n.Identity != mob.Identity
                                          && me.DistanceFrom(n) <= Radius && OnUs(n, awareness, guard)))
        {
            _logger.LogInformation($"HUNT: switching off '{mob.Name}' - something is attacking us.");
            _current = null;
            mob = null;
        }

        if (mob == null)
        {
            mob = DynelManager.Npcs
                .Where(n => n != null && !n.Owner.HasValue && IsAlive(n)
                            && me.DistanceFrom(n) <= Radius
                            // perma-blacklist: never (owner-assist above is the only override)
                            && !_perma.Contains(n.Name ?? "")
                            // temp set-aside: skipped unless it is attacking us (clears the blacklist)
                            && (!IsSetAside(n.Identity) || OnUs(n, awareness, guard))
                            // on us -> always; else a huntable KIND within the pet-scaled level ceiling
                            && (OnUs(n, awareness, guard)
                                || (IsHuntableKind(n, factionSafe) && WithinLevel(n, ceiling))))
                .OrderBy(n => OnUs(n, awareness, guard) ? 0 : 1) // anything on us first, then nearest
                .ThenBy(n => me.DistanceFrom(n))
                .FirstOrDefault();

            if (mob != null && OnUs(mob, awareness, guard))
            {
                _setAside.Remove(mob.Identity); // it attacked us: no longer temp-blacklisted
            }

            if (mob == null)
            {
                Target = null;
                if (_clock - _idleLog > IdleLogSec)
                {
                    _idleLog = _clock;
                    _logger.LogInformation($"HUNT: nothing huntable within {Radius:0} m.");
                }

                return null;
            }

            _current = mob.Identity;
            _sentAt = _clock;
            _hpAtSend = mob.TryGetStat(Stat.Health, out var hp) ? hp : -1;
            mob.TryGetStat(Stat.Level, out var lvl);
            _logger.LogInformation($"HUNT: pets -> '{mob.Name}' L{lvl} ({me.DistanceFrom(mob):0} m).");
        }

        Target = mob;
        return mob;
    }

    // ---- Owner-assist ----------------------------------------------------------------------

    private PlayerChar? ResolveOwner()
    {
        return _ownerAssist.Owner();
    }

    // The mob the attack pets should assist on: the owner's current target from the shared resolver
    // (the owner's own target, else what the owner's pets are on when the owner is a pet class), leashed
    // to our hunt radius so a pet is not dragged across the zone.
    private SimpleChar? OwnerFightTarget(LocalPlayer me)
    {
        var mob = _ownerAssist.Target(me, out _);
        if (mob == null)
        {
            return null;
        }

        return me.DistanceFrom(mob) <= Radius + LeashSlack ? mob : null;
    }

    /// <summary>
    ///     The 'pet attack' / 'pet follow' owner command: point the pets at the owner's current
    ///     target and HOLD them on it (overrides hunt/owner-assist until it dies or 'pet follow'
    ///     clears it), or stand them down. The owner's target is read from owner.FightingIdentity,
    ///     which the SDK fills from combat - so 'pet attack' needs the owner to have an actual
    ///     target (it may require being in combat; see [[owner-target-assist]]).
    /// </summary>
    public void CommandPet(string[] parts, Action<string> reply)
    {
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
        var me = DynelManager.LocalPlayer;
        switch (sub)
        {
            case "attack":
            case "kill":
                if (me == null)
                {
                    reply("Not in play.");
                    return;
                }

                if (!me.Pets.Any(p => p.Role == PetType.Attack))
                {
                    reply("No attack pet up.");
                    return;
                }

                var owner = ResolveOwner();
                if (owner == null)
                {
                    reply(string.IsNullOrEmpty(_ownerName) ? "No owner configured." : $"Can't see '{_ownerName}' nearby.");
                    return;
                }

                if (!owner.FightingIdentity.HasValue
                    || !DynelManager.Find(owner.FightingIdentity.Value, out NpcChar mob) || !IsAlive(mob))
                {
                    reply($"'{_ownerName}' has no target - select or attack a mob first (may need to be in combat).");
                    return;
                }

                _manual = mob.Identity;
                _logger.LogInformation($"PET: manual attack -> '{mob.Name}' (owner's target).");
                reply($"Pets -> '{mob.Name}'.");
                break;

            case "follow":
            case "stop":
                _manual = null;
                _current = null;
                reply("Pets on Follow (manual target cleared).");
                break;

            default:
                reply(_manual.HasValue
                    ? "Pets holding a manual target ('pet follow' to release). Usage: pet attack|follow."
                    : "Pets on auto (hunt / owner-assist). Usage: pet attack|follow.");
                break;
        }
    }

    // ---- Huntable / faction / level --------------------------------------------------------

    private static bool IsHuntableKind(NpcChar n, bool factionSafe)
    {
        if (MobDanger.IsHostile(n.Name))
        {
            return true;
        }

        // Faction-safe (Shadowlands): no blanket Monster-side, or we might kill our own faction.
        return !factionSafe && n.Side == Side.Monster;
    }

    private bool IsFactionSafe(LocalPlayer me)
    {
        return Faction switch
        {
            FactionMode.On => true,
            FactionMode.Off => false,
            // Auto: on once our Redeemed/Unredeemed standing is set (we are in the SL faction game).
            _ => (me.TryGetStat(Stat.ClanRedeemed, out var red) && red != 0)
                 || (me.TryGetStat(Stat.OTUnredeemed, out var unred) && unred != 0),
        };
    }

    // The ceiling scales off the PET (a L40 owner can field a L100 pet): best attack-pet level +
    // the margin. No readable pet level -> no ceiling (allow all).
    private int PetCeiling(LocalPlayer me)
    {
        var best = -1;
        foreach (var p in me.Pets.Where(p => p.Role == PetType.Attack))
        {
            if (p.TryGetStat(Stat.Level, out var lvl) && lvl > best)
            {
                best = lvl;
            }
        }

        return best < 0 ? int.MaxValue : best + LevelMargin;
    }

    private static bool WithinLevel(NpcChar n, int ceiling)
    {
        return ceiling == int.MaxValue || !n.TryGetStat(Stat.Level, out var lvl) || lvl <= 0 || lvl <= ceiling;
    }

    // ---- "Us" ------------------------------------------------------------------------------

    private static bool OnUs(NpcChar n, Awareness awareness, HashSet<Identity> guard)
    {
        if (awareness.OnBot.Any(s => s.Mob != null && s.Mob.Identity == n.Identity)
            || awareness.OnPets.Any(s => s.Mob != null && s.Mob.Identity == n.Identity))
        {
            return true;
        }

        return AttackingGuard(n, guard);
    }

    private static HashSet<Identity> BuildGuard(LocalPlayer me)
    {
        var g = new HashSet<Identity> { me.Identity };
        foreach (var m in Team.Members)
        {
            g.Add(m.Identity);
        }

        foreach (var n in DynelManager.Npcs)
        {
            if (n != null && n.Owner.HasValue && g.Contains(n.Owner.Value))
            {
                g.Add(n.Identity);
            }
        }

        return g;
    }

    private static bool AttackingGuard(NpcChar n, HashSet<Identity> guard)
    {
        if (n == null || guard.Contains(n.Identity) || !n.FightingIdentity.HasValue
            || !guard.Contains(n.FightingIdentity.Value))
        {
            return false;
        }

        return DynelManager.Find(n.FightingIdentity.Value, out SimpleChar victim)
               && Movement.Flat(victim.Transform.Position, n.Transform.Position) <= 10f;
    }

    private bool IsSetAside(Identity id)
    {
        return _setAside.TryGetValue(id, out var until) && until > _clock;
    }

    private void SetAside(Identity id, double seconds)
    {
        _setAside[id] = _clock + seconds;
    }

    private bool Losing(SimpleChar n)
    {
        return _hpAtSend > 0 && n.TryGetStat(Stat.Health, out var hp) && hp < _hpAtSend;
    }

    private static bool IsAlive(SimpleChar c)
    {
        return !c.TryGetStat(Stat.Health, out var hp) || hp > 0;
    }

}
