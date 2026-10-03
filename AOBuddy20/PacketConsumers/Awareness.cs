// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: Awareness.cs
//
// Last modified: 2026-10-02
// Created:       2026-09-29 23:09
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Enums;
using AOBuddy20.Interfaces;
using AOBuddy20.Nav;
using AOBuddy20.Network;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.PacketConsumers;

/// <summary>
///     AWARENESS (owner, 2026-09-27: "he should always know how many mobs are near him ... he needs
///     to be aware"): the MONSTER picture round the bot, rebuilt twice a second, that every decision
///     reads. Players are not in it; our own pets are not in it (an owned NpcChar is a pet, and
///     nothing else carries an Owner).
///     Near   - live monsters within NearRange of the bot;
///     OnBot  - the monsters attacking the BOT: their FightingIdentity points at him, or their last
///              blow (Attack/AttackInfo) named him. Kept with a linger, so the gap between swings
///              does not drop one, and dropped when the mob dies, despawns or sits out the linger;
///     OnPets - the monsters attacking OUR PETS, the same rules, tracked SEPARATELY (a mob that
///              turned from the pet to the bot stays on the pet list until its own linger runs out).
///     A fresh attacker logs AGGRO once; a change in the picture logs "AWARE: ..." (at most every
///     two seconds while nothing is on us, at once when something is).
///     Runs entirely on the update thread (packet pump and BotLoop tick are the same thread), so
///     the dictionaries need no locks.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class Awareness : IPacketConsumer
{
    private const float NearRange = 40f;
    private const double ScanInterval = 0.5; // the picture is rebuilt at this cadence (AOBuddy10's rate)
    private const double AttackerLinger = 10.0; // an attacker that swung at us this recently is still on us

    private readonly ILogger<Awareness> _logger;
    private readonly HashSet<Identity> _petIds = new(); // our pets this scan (summons come and go)
    private readonly Dictionary<Identity, double> _onBot = new(); // mob -> clock of its last blow/lock on the bot
    private readonly Dictionary<Identity, double> _onPets = new(); // mob -> clock of its last blow/lock on a pet
    private readonly HashSet<Identity> _firstSeen = new(); // the monsters sighted this stay (MobDanger's atlas book)

    private double _clock;
    private double _at = -1; // last scan
    private double _lastLogAt = -99;
    private int _pf = -1;
    private string _lastSummary = "";

    public Awareness(ILogger<Awareness> logger)
    {
        _logger = logger;
        _logger.LogInformation("Awareness initialized");
    }

    /// <summary>The monsters around the bot, nearest first.</summary>
    public IReadOnlyList<Seen> Near { get; private set; } = new List<Seen>();

    /// <summary>The monsters attacking the BOT, nearest first.</summary>
    public IReadOnlyList<Seen> OnBot { get; private set; } = new List<Seen>();

    /// <summary>The monsters attacking OUR PETS, nearest first.</summary>
    public IReadOnlyList<Seen> OnPets { get; private set; } = new List<Seen>();

    public int NearCount => Near.Count;
    public int OnBotCount => OnBot.Count;
    public int OnPetsCount => OnPets.Count;

    public string Summary()
    {
        return $"{OnBotCount} on bot, {OnPetsCount} on pets, {NearCount} near";
    }

    /// <summary>The nearest one on the bot, else the nearest one on a pet, or null.</summary>
    public SimpleChar? Chaser()
    {
        return OnBot.FirstOrDefault()?.Mob ?? OnPets.FirstOrDefault()?.Mob;
    }

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(ProcessAttackMessage, N3MessageType.Attack, (int)ControlPriority.Combat);
        router.Register(ProcessAttackInfoMessage, N3MessageType.AttackInfo, (int)ControlPriority.Combat);
    }

    /// <summary>The decision tick (BotLoop, update thread).</summary>
    public void Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            Clear();
            return;
        }

        var pf = (int)Playfield.ModelId;
        if (pf != _pf)
        {
            // A zone change empties the picture: nothing in it points at this playfield any more.
            _pf = pf;
            _onBot.Clear();
            _onPets.Clear();
            _firstSeen.Clear(); // re-entry sights the monsters again - a new atlas line per stay
            _lastSummary = "";
        }

        if (_clock - _at < ScanInterval)
        {
            return;
        }

        _at = _clock;

        // WHO IS FIGHTING WHOM: a monster locked on the bot or a pet (the SDK maintains
        // FightingIdentity from the wire's Attack/StopFight traffic).
        _petIds.Clear();
        foreach (var p in me.Pets)
        {
            _petIds.Add(p.Identity);
        }

        foreach (var n in DynelManager.Npcs)
        {
            if (n == null || n.Owner.HasValue)
            {
                continue; // pets and non-NPCs are not monsters of ours
            }

            var ft = n.FightingIdentity;
            if (!ft.HasValue)
            {
                continue;
            }

            if (ft.Value == me.Identity)
            {
                MarkAttacker(_onBot, n, "on bot");
            }
            else if (_petIds.Contains(ft.Value))
            {
                MarkAttacker(_onPets, n, "on a pet");
            }
        }

        Expire(_onBot);
        Expire(_onPets);

        // THE PICTURE: attackers first (they may stand past NearRange - a ranged mob still counts),
        // then the crowd within range.
        OnBot = AttackerList(_onBot, me);
        OnPets = AttackerList(_onPets, me);
        var near = new List<Seen>();
        foreach (var n in DynelManager.Npcs)
        {
            if (n == null || n.Owner.HasValue)
            {
                continue;
            }

            if (n.TryGetStat(Stat.Health, out var hp) && hp <= 0)
            {
                continue; // a corpse is not a threat
            }

            var d = me.DistanceFrom(n);
            if (d > NearRange)
            {
                continue;
            }

            n.TryGetStat(Stat.Level, out var lvl);
            near.Add(new Seen
            {
                Mob = n,
                Level = lvl,
                Dist = d,
                OnBot = _onBot.ContainsKey(n.Identity),
                OnPets = _onPets.ContainsKey(n.Identity),
            });
        }

        Near = near.OrderBy(s => s.Dist).ToList();

        // MOBDANGER'S ATLAS (mob spawn places, ported 2026-10-03): each monster's FIRST position - where
        // it stood when it came into view, before it could chase anything. Once per instance per stay;
        // one JSONL line each, binned by MobDanger when the grid prices its spots.
        me.TryGetStat(Stat.Level, out var myLvl);
        MobDanger.SetMyLevel(myLvl);
        foreach (var n in DynelManager.Npcs)
        {
            if (n == null || n.Owner.HasValue)
            {
                continue;
            }

            if (!_firstSeen.Add(n.Identity))
            {
                continue;
            }

            n.TryGetStat(Stat.Level, out var slvl);
            MobDanger.NoteSighting(pf, n.Identity.Instance.ToString(), n.Name, slvl, n.Transform.Position);
        }

        // ...and the live picture: hostile kinds in view that are NOT on us (the ones on us are past
        // routing) - the walk re-plans round these the moment the picture changes.
        MobDanger.SetLive(pf, Near
            .Where(s => s.Mob != null && !s.OnBot && !s.OnPets && MobDanger.IsHostile(s.Mob.Name))
            .Select(s => new MobDanger.LiveMob(s.Mob.Name, s.Level, s.Mob.Transform.Position))
            .ToList());

        var now = Summary();
        if (now != _lastSummary && (_clock - _lastLogAt > 2 || OnBotCount + OnPetsCount > 0))
        {
            _lastSummary = now;
            _lastLogAt = _clock;
            _logger.LogInformation($"AWARE: {now}.");
        }
    }

    // ---- The attacker books ----------------------------------------------------------------

    // A mob joins the book once (AGGRO logs here) and stays while it keeps it up; the timestamp is
    // refreshed on every blow and every FightingIdentity sighting.
    private void MarkAttacker(Dictionary<Identity, double> book, NpcChar mob, string what)
    {
        if (book.ContainsKey(mob.Identity))
        {
            book[mob.Identity] = _clock;
            return;
        }

        book[mob.Identity] = _clock;
        var d = DynelManager.LocalPlayer?.DistanceFrom(mob) ?? 0f;
        mob.TryGetStat(Stat.Level, out var lvl);
        _logger.LogInformation($"AGGRO: '{mob.Name}' lvl {lvl} ({d:0} m) {what}.");

        // MOBDANGER (ported 2026-10-03): a kind that turns on us UNPROVOKED outdoors is hostile from now
        // on (hostile_mobs.json, kept across restarts, with the spot we stood at). Provoked = one of OUR
        // PETS was already fighting it - the bot itself never opens a fight. Mission playfields are out:
        // everything in there attacks, that is the building's business.
        var me = DynelManager.LocalPlayer;
        var aggroPf = (int)Playfield.ModelId;
        if (me != null && aggroPf >= 0 && aggroPf < 100000 && !ProvokedByOurPets(me, mob))
        {
            MobDanger.NoteAggro(aggroPf, mob.Name, lvl, d, me.Transform.Position);
        }
    }

    // True when one of our pets has this monster locked: the mob answered the pet, it did not start it.
    private static bool ProvokedByOurPets(LocalPlayer me, NpcChar mob)
    {
        foreach (var p in me.Pets)
        {
            if (DynelManager.Find(p.Identity, out NpcChar pet) && pet.FightingIdentity == mob.Identity)
            {
                return true;
            }
        }

        return false;
    }

    // Out of the book when it sat out the linger, died, or despawned - silently; the counts speak.
    private void Expire(Dictionary<Identity, double> book)
    {
        foreach (var id in book.Keys.ToList())
        {
            if (_clock - book[id] > AttackerLinger)
            {
                book.Remove(id);
                continue;
            }

            if (!DynelManager.Find(id, out NpcChar mob) || DynelManager.Dead.Contains(id) ||
                (mob.TryGetStat(Stat.Health, out var hp) && hp <= 0))
            {
                book.Remove(id);
            }
        }
    }

    private List<Seen> AttackerList(Dictionary<Identity, double> book, LocalPlayer me)
    {
        var list = new List<Seen>();
        foreach (var id in book.Keys)
        {
            if (!DynelManager.Find(id, out NpcChar mob))
            {
                continue;
            }

            mob.TryGetStat(Stat.Level, out var lvl);
            list.Add(new Seen
            {
                Mob = mob,
                Level = lvl,
                Dist = me.DistanceFrom(mob),
                OnBot = book == _onBot,
                OnPets = book == _onPets,
            });
        }

        return list.OrderBy(s => s.Dist).ToList();
    }

    // ---- The wire: every blow names who struck whom ------------------------------------------

    private bool ProcessAttackMessage(AOMessage arg)
    {
        var m = (AttackMessage)arg.Body;
        NoteBlow(m.Identity, m.Target);
        return false;
    }

    private bool ProcessAttackInfoMessage(AOMessage arg)
    {
        var m = (AttackInfoMessage)arg.Body;
        NoteBlow(m.Identity, m.Target);
        return false;
    }

    // A blow enters the books only when a monster swung at the bot or one of OUR pets: players'
    // blows and pets' blows (ours included) are not the bot's fights.
    private void NoteBlow(Identity attacker, Identity target)
    {
        if (attacker.Instance == 0 || target.Instance == 0 || DynelManager.Dead.Contains(attacker))
        {
            return;
        }

        if (!DynelManager.Find(attacker, out NpcChar mob) || mob.Owner.HasValue)
        {
            return; // not a monster: a player, or a pet (ours swings too)
        }

        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            return;
        }

        if (target == me.Identity)
        {
            MarkAttacker(_onBot, mob, "on bot");
        }
        else if (_petIds.Contains(target))
        {
            MarkAttacker(_onPets, mob, "on a pet");
        }
    }

    private void Clear()
    {
        _onBot.Clear();
        _onPets.Clear();
        Near = new List<Seen>();
        OnBot = new List<Seen>();
        OnPets = new List<Seen>();
    }

    public sealed class Seen
    {
        public NpcChar? Mob;
        public int Level;
        public float Dist;

        /// <summary>True when this mob is in the OnBot book (fighting the bot).</summary>
        public bool OnBot;

        /// <summary>True when this mob is in the OnPets book (fighting one of our pets).</summary>
        public bool OnPets;
    }
}