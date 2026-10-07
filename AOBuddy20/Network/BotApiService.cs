// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BotApiService.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Chat;
using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Nav;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Storage;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace AOBuddy20.Network;

/// <summary>
///     The local control API's FEED (AOBuddy10 Main.cs roles, minus its thread violations): owns
///     the <see cref="BotApi" /> listener and, on the update thread (review.md #9 - the only
///     thread allowed to touch SDK state; AOBuddy10 built its /status on the API thread), does
///     everything the API must not:
///       - drains queued /command texts into OwnerChat.RunCommand (one command path, tell or API);
///       - rebuilds the /status and /nav JSON every second and /inventory every 5 s into cached
///         strings the listener thread serves verbatim;
///       - the bag read-through (AOBuddy10 BagReadTick): a few seconds after the login inventory
///         lands, each worn bag is opened once (one per 1.5 s) so its contents are known - that
///         is what turns /inventory's "known" flags true and lets lootbag list name the bags.
///         Re-runs after every zone (the playfield change stands in for AOBuddy10's
///         ContainerGeneration, which this SDK does not carry).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BotApiService
{
    // The read-through starts this long after the inventory model first shows items (let the
    // login dump land), and opens at most one bag per this many seconds (each container update
    // needs room on the wire).
    private const double BagReadDelay = 2.0;
    private const double BagOpenInterval = 1.5;

    private readonly BotApi _botApi;
    private readonly OwnerChat _ownerChat;
    private readonly Awareness _awareness;
    private readonly MovementController _movement;
    private readonly MissionController _mission;
    private readonly LootBagStore _lootBags;
    private readonly BotLoop _botLoop;
    private readonly ILogger<BotApiService> _logger;

    private double _clock;
    private double _statusAt = -1, _invAt = -1;
    private volatile string _statusJson = "{}";
    private volatile string _navJson = "{}";
    private volatile string _inventoryJson = "{}";

    // The bag read-through's own state.
    private int _pf = -1;
    private bool _bagReadDone;
    private double _bagReadAt = -1;
    private double _nextOpenAt;
    private int _opened;

    public BotApiService(AccountInfo config, OwnerChat ownerChat, Awareness awareness, MovementController movement,
        MissionController mission, LootBagStore lootBags, BotLoop botLoop, ApiLogRing logRing, ILogger<BotApiService> logger)
    {
        _ownerChat = ownerChat;
        _awareness = awareness;
        _movement = movement;
        _mission = mission;
        _lootBags = lootBags;
        _botLoop = botLoop;
        _logger = logger;
        _botApi = new BotApi(config.BotApiPort,
            s => logger.LogInformation(s),
            () => _statusJson,
            () => _navJson,
            () => _inventoryJson,
            after => logRing.Json(after));
    }

    public void Start()
    {
        _botApi.Start();
        Client.OnUpdate += OnUpdate;
    }

    public void Stop()
    {
        Client.OnUpdate -= OnUpdate;
        _botApi.Stop();
    }

    // Client.Update invokes OnUpdate only while in play; guarded end to end like every tick.
    private void OnUpdate(object? sender, double deltaTime)
    {
        try
        {
            _clock += deltaTime;
            _botApi.Drain(_ownerChat.RunCommand);

            var me = DynelManager.LocalPlayer;
            BagReadTick(me);

            if (_clock - _statusAt >= 1.0)
            {
                _statusAt = _clock;
                _statusJson = BuildStatus(me).ToString(Formatting.Indented);
                _navJson = BuildNav(me).ToString(Formatting.Indented);
            }

            if (_clock - _invAt >= 5.0)
            {
                _invAt = _clock;
                _inventoryJson = BuildInventory().ToString(Formatting.Indented);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotApi tick failed.");
        }
    }

    // ---- /status -----------------------------------------------------------------------------

    private JObject BuildStatus(LocalPlayer? me)
    {
        var o = new JObject();
        try
        {
            o["playfield"] = (int)Playfield.ModelId;
            o["zone"] = "" + Playfield.Name;
            o["freeSlots"] = Inventory.NumFreeSlots;
            o["inMission"] = false;

            var taskKind = _botLoop.CurrentTask.ToString().ToLowerInvariant();
            var taskText = _movement.DescribeState();
            o["task"] = new JObject { ["kind"] = taskKind, ["text"] = taskText };
            o["behavior"] = taskKind;
            o["aware"] = _awareness.Summary();

            if (me == null)
            {
                o["heartbeat"] = "Not in play.";
                return o;
            }

            var hpPct = Pct(me, Stat.Health, Stat.MaxHealth);
            var nanoPct = Pct(me, Stat.CurrentNano, Stat.MaxNanoEnergy);
            me.TryGetStat(Stat.Level, out var level);
            o["heartbeat"] = $"Lvl {level}. HP {PctText(hpPct)}. Nano {PctText(nanoPct)}. " +
                             $"{taskKind} - {taskText}. {_awareness.Summary()}";
            o["hp"] = StatsJson(me, Stat.Health, Stat.MaxHealth, hpPct);
            o["nano"] = StatsJson(me, Stat.CurrentNano, Stat.MaxNanoEnergy, nanoPct);
            o["credits"] = me.TryGetStat(Stat.Cash, out var cash) ? cash : -1L;
            o["dead"] = me.TryGetStat(Stat.Health, out var hp) && hp <= 0;
            o["resting"] = false;
            o["casting"] = me.IsCasting;
            o["inCombat"] = me.IsAttacking || _awareness.OnBotCount > 0;

            // Stat.XP is the TOTAL XP: the table's cumulative sums turn it into the way into the
            // next level ("total" rides along so the monitor can rate xp/s across dings)
            var xpTotal = me.TryGetStat(Stat.XP, out var xp) ? xp : -1L;
            o["xp"] = new JObject
            {
                ["level"] = level,
                ["total"] = xpTotal,
                ["into"] = XpTable.IntoLevel(level, xpTotal),
                ["pctNext"] = XpTable.PercentToNext(level, xpTotal),
            };

            var pets = new JArray();
            foreach (var p in me.Pets)
            {
                pets.Add(new JObject
                {
                    ["name"] = "" + p.Name,
                    ["role"] = p.Role.ToString(),
                    ["level"] = p.TryGetStat(Stat.Level, out var pl) ? pl : -1,
                    ["hpPct"] = Pct(p, Stat.Health, Stat.MaxHealth),
                    ["dist"] = Round(me.DistanceFrom(p)),
                });
            }

            o["pets"] = pets;

            var target = me.FightingTarget;
            if (target != null)
            {
                o["target"] = new JObject
                {
                    ["name"] = "" + target.Name,
                    ["hpPct"] = Pct(target, Stat.Health, Stat.MaxHealth),
                    ["nanoPct"] = Pct(target, Stat.CurrentNano, Stat.MaxNanoEnergy),
                    ["dist"] = Round(me.DistanceFrom(target)),
                };
            }

            PosInto(o, me);
        }
        catch (Exception ex)
        {
            o["error"] = ex.Message;
        }

        return o;
    }

    // ---- /nav -----------------------------------------------------------------------------

    private JObject BuildNav(LocalPlayer? me)
    {
        var o = new JObject
        {
            ["time"] = DateTime.Now.ToString("HH:mm:ss"),
        };
        try
        {
            var pf = (int)Playfield.ModelId;
            o["pf"] = pf;
            o["zone"] = "" + Playfield.Name;
            o["inMission"] = _mission.NavInMission;

            var travelPf = _movement.TravelTargetPf;
            o["active"] = travelPf != 0;
            o["phase"] = travelPf != 0 ? $"travel to {Zoning.Name(travelPf)}" : _movement.DescribeState();

            // The mission map's whole feed (AOBuddy10 MissionRun.NavJson's mission object): the
            // building's zone-in placement, which the monitor recomposes from its own pool files
            // (NavData.ComposeMission), the server's doors and the body's floor. Without it the
            // monitor drew the plain coordinate grid over a mission (owner, 2026-10-04).
            var mj = _mission.NavMissionJson(me?.Transform.Position);
            if (mj != null)
            {
                o["mission"] = mj;
            }

            if (me == null)
            {
                return o;
            }

            if (travelPf != 0)
            {
                o["hike"] = new JObject
                {
                    ["fromPf"] = pf,
                    ["targetPf"] = travelPf,
                };
            }

            // The monsters round him, for the map (AOBuddy10 /nav's npcs - nearest 40 within 120 m).
            var npcs = new JArray();
            foreach (var n in DynelManager.Npcs
                         .Where(n => n != null && me.DistanceFrom(n) <= 120f)
                         .OrderBy(n => me.DistanceFrom(n))
                         .Take(40))
            {
                npcs.Add(new JObject
                {
                    ["name"] = "" + n.Name,
                    ["p"] = Vec(n.Transform.Position),
                    ["hpPct"] = Pct(n, Stat.Health, Stat.MaxHealth),
                    ["fighting"] = n.FightingIdentity == me.Identity,
                    ["pet"] = n.Owner.HasValue,
                    ["role"] = n.Owner.HasValue ? n.Role.ToString() : null,
                    ["level"] = n.TryGetStat(Stat.Level, out var lvl) ? lvl : -1,
                    ["dist"] = Round(me.DistanceFrom(n)),
                });
            }

            o["npcs"] = npcs;
            PosInto(o, me);
        }
        catch (Exception ex)
        {
            o["error"] = ex.Message;
        }

        return o;
    }

    // ---- /inventory -----------------------------------------------------------------------

    private JObject BuildInventory()
    {
        var o = new JObject();
        try
        {
            o["freeSlots"] = Inventory.NumFreeSlots;
            var items = new JArray();
            foreach (var it in (Inventory.Items ?? new List<Item>())
                     .Where(x => x != null && x.Slot.Type == IdentityType.Inventory)
                     .OrderBy(x => x.Slot.Instance))
            {
                items.Add(ItemJson(it));
            }

            o["items"] = items;

            // The bags' lootbag-list numbers: the same filter and order the 'lootbag' command numbers
            // by (OwnerChat.LootBagCommand), so the monitor's set/reset names the exact bag.
            var numbered = (Inventory.Items ?? new List<Item>())
                .Where(x => x != null && x.Slot.Type == IdentityType.Inventory && x.UniqueIdentity.Type == IdentityType.Container)
                .OrderBy(x => x.Slot.Instance)
                .ToList();

            var bags = new JArray();
            foreach (var c in Inventory.Containers ?? new List<Container>())
            {
                // KNOWN vs UNKNOWN: a bag's contents arrive only when it is OPENED (the login
                // read-through, or the sell flow). The marker is the container's Handle (0 = the
                // shell login plants, no answer yet) - NOT the item count: an OPENED bag that is
                // empty is known-empty (0 items, all free), not unknown.
                var known = c.IsOpen;
                var n = numbered.FindIndex(b => b.UniqueIdentity == c.Identity) + 1;
                var bag = new JObject
                {
                    ["name"] = c.Item?.Name ?? "backpack",
                    ["known"] = known,
                    ["free"] = known ? c.NumFreeSlots : (int?)null,
                    ["loot"] = _lootBags.IsLootBag(c.Identity), // the lootbag designation
                    ["items"] = known
                        ? new JArray(c.Items.OrderBy(x => x.Slot.Instance).Select(ItemJson))
                        : new JArray(),
                };
                if (n > 0)
                {
                    bag["n"] = n; // 'lootbag add <n>' / 'remove <n>' name this bag
                }

                bags.Add(bag);
            }

            o["bags"] = bags;
        }
        catch (Exception ex)
        {
            o["error"] = ex.Message;
        }

        return o;
    }

    private static JObject ItemJson(Item it)
    {
        return new JObject
        {
            ["slot"] = it.Slot.Instance & 0xFF,
            ["name"] = "" + it.Name,
            ["ql"] = it.Ql,
            ["count"] = Math.Max(1, it.Count),
        };
    }

    // ---- The bag read-through (AOBuddy10 BagReadTick) ----------------------------------------

    private void BagReadTick(LocalPlayer? me)
    {
        var pf = (int)Playfield.ModelId;
        if (pf != _pf)
        {
            // A zone change invalidates the container cache (this SDK has no ContainerGeneration).
            _pf = pf;
            _bagReadDone = false;
            _bagReadAt = -1;
        }

        if (_bagReadDone || me == null)
        {
            return;
        }

        var items = Inventory.Items;
        if (items == null || items.Count == 0)
        {
            return; // the inventory model has not landed yet
        }

        if (_bagReadAt < 0)
        {
            _bagReadAt = _clock;
        }

        if (_clock - _bagReadAt < BagReadDelay || _clock < _nextOpenAt)
        {
            return;
        }

        foreach (var b in items)
        {
            if (b == null || b.Slot.Type != IdentityType.Inventory || b.UniqueIdentity.Type != IdentityType.Container)
            {
                continue;
            }

            var ct = Inventory.Containers?.FirstOrDefault(c => c.Identity == b.UniqueIdentity);
            if (ct is { IsOpen: true })
            {
                continue; // contents delivered (or known-empty): the open's Handle marks them read
            }

            GameCommands.OpenContainer(me, b.Slot);
            _nextOpenAt = _clock + BagOpenInterval;
            _opened++;
            return; // one per beat
        }

        _bagReadDone = true;
        _logger.LogInformation($"BAGS: read-through done (login or zone) - opened {_opened} bag(s).");
        _opened = 0;
    }

    // ---- JSON helpers --------------------------------------------------------------------------

    private static void PosInto(JObject o, LocalPlayer me)
    {
        o["pos"] = Vec(me.Transform.Position);
        var fwd = me.MovementComponent.Heading.Forward;
        o["hdg"] = new JArray { Round(fwd.X), Round(fwd.Z) };
    }

    private static JArray Vec(Vector3 v)
    {
        return new JArray { Round(v.X), Round(v.Y), Round(v.Z) };
    }

    private static JObject StatsJson(SimpleChar c, Stat cur, Stat max, int pct)
    {
        c.TryGetStat(cur, out var cv);
        c.TryGetStat(max, out var mv);
        return new JObject { ["pct"] = pct, ["cur"] = cv, ["max"] = mv };
    }

    private static int Pct(SimpleChar c, Stat cur, Stat max)
    {
        if (!c.TryGetStat(cur, out var v) || !c.TryGetStat(max, out var m) || m <= 0)
        {
            return -1;
        }

        return Math.Max(0, Math.Min(100, v * 100 / m));
    }

    private static string PctText(int pct)
    {
        return pct < 0 ? "?" : $"{pct}%";
    }

    private static double Round(float v)
    {
        return Math.Round(v, 1);
    }
}