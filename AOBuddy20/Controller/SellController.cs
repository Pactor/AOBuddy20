// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: SellController.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02 (ported from AOBuddy10 MissionRun.cs ShopStep.Sell, new selection rules)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Components;
using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Nav;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     SELLING (owner, 2026-10-02): sell the contents of the inventory bags to a shop terminal.
///     The MAIN INVENTORY is never sold - only items that came out of a bag, staged through it.
///     NODROP items (ItemValues.IsNoDrop, the item data's Flags bit 26) are skipped and stay in
///     the bag; a bag inside a bag stays a bag.
///
///     No terminal in reach here: the run's ONE travel takes it to the shop playfield
///     (ResupplyShopPf, default 1187 "Neutral Supermarket Advanced" - the Fair Trade instance,
///     entered over proxy terminals), on the MovementController's travel machinery - the same trip
///     resupply makes. Outbound only; the way back belongs to whatever controller owns the body next.
///
///     The wire is AOBuddy10's proven sell (capture 20260924-074329): LookAt + Use the shop
///     terminal, wait for the window, Trade AddItem - up to five items per trade, each naming the
///     SELLER as Param1/2 and the item's inventory slot as Param3/4 - then Trade Accept with NO
///     target. The server pays (Cash) and closes the window, so every batch re-opens the terminal,
///     2.5 s per batch. Items can't be sold from inside a bag (owner): they are moved out with
///     ClientMoveItemToInventory, up to five per pass while free slots last, and only those staged
///     items are ever offered.
///
///     Refusals (AOBuddy10's rules): a whole batch refused once is the TERMINAL - blacklist it and
///     try the next nearest; items left over from a partly sold batch are refused by the vendor and
///     are never offered again.
///
///     Control scheme: while a run is active the controller holds the ControlArbiter at
///     <see cref="ControlPriority.Selling" /> and walks to terminals through a MovementController
///     goal at the same priority. The decision tick runs on the update thread (BotLoop).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class SellController
{
    private enum Phase { Idle, BagCheck, Approach, Selling, Transit }

    private const int SellBatch = 5; // items per trade (AOBuddy10's proven batch)
    private const int MaxRounds = 12; // batches per run, then give up
    private const int MaxMoves = 20; // bag->inventory move passes per vendor
    private const float VendorRange = 40f; // terminals within this of the bot are considered
    private const float VendorReach = 3f; // walk this close to a terminal before using it
    private const double BatchCadence = 2.5; // seconds between sell batches (AOBuddy10)
    private const double WindowBeat = 1.0; // the trade window needs a beat to open after the Use
    private const double SettleSeconds = 0.6; // stand still before using the terminal (travel legs' beat)
    private const double ApproachTimeout = 90.0; // a terminal the walk can never reach

    // The travel to the shop playfield is watched per-leg by the MovementController's machinery;
    // this only bounds the WHOLE trip so a run can never sit in transit forever.
    private const double TransitTimeout = 600.0;

    // CHECK FIRST (owner, 2026-10-02): open the bags, wait this long for the contents to arrive on
    // the wire, and only travel when there is something to sell. The first run traveled to Fair
    // Trade with the bags' contents not yet known and nothing to sell.
    private const double BagCheckSeconds = 2.0;

    private readonly ILogger<SellController> _logger;
    private readonly MovementController _movement;
    private readonly ControlArbiter _controlArbiter;
    private readonly AccountInfo _config;
    private uint _tellId; // the owner's chat id, proven once by a command tell

    private Phase _phase = Phase.Idle;
    private double _phaseTime;
    private double _lastActionAt = -99; // last batch/move/use, for the 2.5 s cadence
    private double _useAt = -1; // the terminal was used since (window beat starts here)
    private double _arrivedAt = -1; // standing on the terminal since (settle beat)
    private double _transitAt = -1; // in transit to the shop playfield since (trip watchdog starts here)
    private bool _travelPlanned; // the trip to the shop playfield: at most once per run
    private int _runPf = -1;
    private int _rounds;
    private int _moves;
    private int _stage; // 0: LookAt + Use, 1: window open, send the batch
    private int _cashAtStart;
    private int _wholeRefusals; // whole batches refused in a row: the terminal, not the items

    private Identity _vendor = Identity.None;
    private string _vendorName = "";
    private readonly List<Identity> _candidates = new();
    private readonly HashSet<Identity> _badVendors = new(); // took none of a whole batch, twice-backed
    private readonly HashSet<Identity> _refusedSlots = new(); // items a vendor declined: never offered again
    private readonly HashSet<Identity> _staged = new(); // items moved out of a bag: the ONLY ones sellable
    private List<Identity>? _lastBatchSlots; // the slots of the batch in flight (refusal bookkeeping)
    private Identity _lastVendor = Identity.None;
    private readonly Dictionary<Identity, Identity> _stagedFrom = new(); // staged item -> the bag it came from

    public bool Active => _phase != Phase.Idle;

    /// <summary>Items the predicate answers true for never sell, wherever they sit (the mission
    /// run's want list keeps what it rolled for, AOBuddy10's Bankable). Null sells everything.</summary>
    public Func<Item, bool> KeepFromSale;

    public SellController(ILogger<SellController> logger, MovementController movement,
        ControlArbiter controlArbiter, AccountInfo config)
    {
        _logger = logger;
        _movement = movement;
        _controlArbiter = controlArbiter;
        _config = config;
        _logger.LogInformation("Sell controller initialized.");
    }

    /// <summary>OwnerChat proves the owner's chat id once per tell; async replies use it.</summary>
    public void SetTellId(uint charId)
    {
        _tellId = charId;
    }

    // ---- Commands -----------------------------------------------------------

    /// <summary>The 'sell' command. parts is the raw command split.</summary>
    public void Command(string[] parts, Action<string> reply)
    {
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            reply("No character loaded.");
            return;
        }

        switch (arg)
        {
            case "stop":
                if (Active)
                {
                    Stop("owner");
                    reply("Selling stopped.");
                }
                else
                {
                    reply("Not selling.");
                }

                break;
            case "status":
                reply("Sell: " + Describe());
                break;
            default:
                Start(me, reply);
                break;
        }
    }

    private void Start(LocalPlayer me, Action<string> reply)
    {
        if (Active)
        {
            reply("Already selling — " + Describe());
            return;
        }

        if (Inventory.Containers.Count == 0)
        {
            reply("No bags in inventory to sell from.");
            return;
        }

        _runPf = (int)Playfield.ModelId;
        _staged.Clear();
        _stagedFrom.Clear();
        _refusedSlots.Clear();
        _badVendors.Clear();
        _lastBatchSlots = null;
        _lastVendor = Identity.None;
        _wholeRefusals = 0;
        _rounds = 0;
        _moves = 0;
        _stage = 0;
        _lastActionAt = -99;

        // CHECK FIRST: open the bags whose contents aren't cached, give the contents a beat to
        // arrive on the wire, and only travel when something is actually sellable (BagCheckTick
        // below).
        OpenUnknownBags(me);

        reply($"Opening {Inventory.Containers.Count} bag(s) and checking what's sellable - " +
              "NODROP items and the main inventory stay.");
        _controlArbiter.TakeControl(ControlPriority.Selling);
        SetPhase(Phase.BagCheck);
    }

    public void Stop(string why)
    {
        if (!Active)
        {
            return;
        }

        if (_phase == Phase.Selling && _vendor != Identity.None)
        {
            CloseShopWindow();
        }

        _logger.LogInformation($"SELL: stopped ({why}).");
        TearDown();
    }

    private string Describe()
    {
        return _phase switch
        {
            Phase.Idle =>
                $"idle. {_staged.Count} staged, {_refusedSlots.Count} refused slot(s), " +
                $"{_badVendors.Count} blacklisted terminal(s) this session.",
            Phase.Approach => $"walking to '{_vendorName}' ({_candidates.Count} left after it).",
            Phase.Selling =>
                $"selling at '{_vendorName}': {_rounds} batch(es), {_moves} move pass(es), " +
                $"{_staged.Count} staged, {_refusedSlots.Count} refused slot(s).",
            _ => _phase.ToString(),
        };
    }

    // ---- Frame ----------------------------------------------------------------

    /// <summary>The decision tick, every update while in play (BotLoop). Returns true while active.</summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        if (!Active)
        {
            return false;
        }

        if ((int)Playfield.ModelId != _runPf)
        {
            if (_phase != Phase.Transit)
            {
                _logger.LogInformation("SELL: zoned mid-run — stopping.");
                Tell("Zoned while selling — stopped.");
                Stop("zoned");
                return false;
            }

            // In transit to the shop playfield a zone IS the plan working; TransitTick below
            // watches for the arrival.
        }

        _phaseTime += dt;
        switch (_phase)
        {
            case Phase.BagCheck:
                BagCheckTick(me);
                break;
            case Phase.Approach:
                ApproachTick(me);
                break;
            case Phase.Selling:
                SellingTick(me);
                break;
            case Phase.Transit:
                TransitTick(me);
                break;
        }

        return true;
    }

    // The bags are open and the contents have had their beat: sell only if there is something to
    // sell - never travel for an empty bag.
    private void BagCheckTick(LocalPlayer me)
    {
        if (_phaseTime < BagCheckSeconds)
        {
            return;
        }

        var sellable = SellableInBags();
        if (sellable.Count == 0)
        {
            Tell("The bags hold nothing sellable (NODROP items and the bags themselves stay). Not traveling.");
            _logger.LogInformation("SELL: nothing sellable in the bags - not traveling.");
            TearDown();
            return;
        }

        me.TryGetStat(Stat.Cash, out _cashAtStart);
        _logger.LogInformation($"SELL: {sellable.Count} sellable item(s) in the bags.");
        NextVendor(me);
    }

    // En route to the shop playfield - the same trip resupply makes (ResupplyShopPf, default 1187
    // "Neutral Supermarket Advanced", entered over proxy terminals; the playfield MODEL reads 1187
    // even though in-game it is an instance). The legs are the MovementController's; this watches
    // for the arrival or the plan giving up.
    private void TransitTick(LocalPlayer me)
    {
        var shopPf = _config.ResupplyShopPf;
        if ((int)Playfield.ModelId == shopPf)
        {
            _runPf = shopPf; // arrival: the local-zone guard works again from here
            NextVendor(me);
            return;
        }

        if (_movement.TravelTargetPf != shopPf)
        {
            me.TryGetStat(Stat.Cash, out var cash);
            Finish(me, cash, "Travel to the shop playfield didn't happen - selling stopped.");
            return;
        }

        if (_transitAt < 0)
        {
            _transitAt = _phaseTime;
            return;
        }

        if (_phaseTime - _transitAt > TransitTimeout)
        {
            me.TryGetStat(Stat.Cash, out var cash);
            Finish(me, cash, $"Travel to the shop playfield ran over {TransitTimeout / 60:0} minutes - giving up.");
        }
    }

    private void ApproachTick(LocalPlayer me)
    {
        if (!DynelManager.Find(_vendor, out VendingMachine vm))
        {
            _logger.LogInformation($"SELL: terminal '{_vendorName}' is gone.");
            _movement.ClearDesiredGoal(ControlPriority.Selling);
            NextVendor(me);
            return;
        }

        if (_movement.IsGoalReached(ControlPriority.Selling))
        {
            if (_arrivedAt < 0)
            {
                _arrivedAt = _phaseTime;
            }

            if (_phaseTime - _arrivedAt >= SettleSeconds)
            {
                // At the terminal: make sure every bag's contents are known (only uncached ones
                // are opened), then start the sell cycle.
                OpenUnknownBags(me);

                SetPhase(Phase.Selling);
                _stage = 0;
                _lastActionAt = _phaseTime;
                _logger.LogInformation($"SELL: at '{_vendorName}' - bags opened, selling starts.");
            }

            return;
        }

        if (_phaseTime > ApproachTimeout)
        {
            _logger.LogInformation($"SELL: never got within {VendorReach:0}m of '{_vendorName}' in {ApproachTimeout:0}s — trying the next terminal.");
            _movement.ClearDesiredGoal(ControlPriority.Selling);
            NextVendor(me);
        }
    }

    private void SellingTick(LocalPlayer me)
    {
        // The 2.5 s cadence gates every action (AOBuddy10's _sellSentAt).
        if (_phaseTime - _lastActionAt < BatchCadence)
        {
            return;
        }

        // What the last batch left behind was refused. A whole batch refused is the TERMINAL, not
        // the items - blacklist it and try the next nearest; items left over from a partly sold
        // batch are refused and never offered again (AOBuddy10's rules, wire-proven).
        if (_lastBatchSlots != null)
        {
            var left = _lastBatchSlots.Where(slot => Inventory.Items.Any(i => i != null && i.Slot == slot)).ToList();
            if (left.Count == _lastBatchSlots.Count && ++_wholeRefusals < 2 && _badVendors.Add(_lastVendor))
            {
                _logger.LogInformation($"SELL: '{_vendorName}' took none of {left.Count} - trying another terminal.");
                _lastBatchSlots = null;
                NextVendor(me);
                return;
            }

            _wholeRefusals = 0;
            foreach (var slot in left)
            {
                _refusedSlots.Add(slot);
            }

            if (left.Count > 0)
            {
                _logger.LogInformation($"SELL: the shop refused {left.Count} item(s); leaving them.");
            }

            _lastBatchSlots = null;
        }

        // What is on offer: ONLY items staged out of the bags, sitting in the main inventory now.
        var sell = Inventory.Items
            .Where(i => i != null && i.Slot.Type == IdentityType.Inventory && _staged.Contains(i.UniqueIdentity) &&
                        !_refusedSlots.Contains(i.Slot))
            .ToList();
        if (sell.Count == 0)
        {
            // Nothing staged: bring the next few out of the bags (you can't sell from a backpack).
            var inBags = SellableInBags();
            var room = Inventory.NumFreeSlots - 1;
            if (inBags.Count > 0 && room > 0 && _moves < MaxMoves)
            {
                foreach (var it in inBags.Take(Math.Min(SellBatch, room)))
                {
                    Item.MoveItemToInventory(it.Slot, 0x6F);
                    _staged.Add(it.UniqueIdentity);
                    _logger.LogInformation($"SELL: '{it.Name}' out of a bag ({it.Slot}).");
                }

                _moves++;
                _lastActionAt = _phaseTime;
                return;
            }

            me.TryGetStat(Stat.Cash, out var cash);
            Finish(me, cash,
                inBags.Count > 0
                    ? $"Out of free slots or move budget - {inBags.Count} sellable item(s) still in the bags."
                    : $"Sold out. {_rounds} batch(es), {_refusedSlots.Count} refused item(s) left in the bags.");
            return;
        }

        if (_rounds >= MaxRounds)
        {
            me.TryGetStat(Stat.Cash, out var cash);
            Finish(me, cash, $"Batch budget spent ({MaxRounds}) - stopping with {sell.Count} staged item(s) unsold.");
            return;
        }

        if (!DynelManager.Find(_vendor, out VendingMachine vm))
        {
            _logger.LogInformation($"SELL: terminal '{_vendorName}' is gone - trying another.");
            NextVendor(me);
            return;
        }

        if (_stage == 0)
        {
            // Up to the terminal first, then LookAt + Use (the sales that worked were the close ones).
            if (Movement.Flat(me.Transform.Position, vm.Transform.Position) > VendorReach)
            {
                _movement.SetDesiredGoal(vm.Transform.Position, _runPf, ControlPriority.Selling, VendorReach);
                _lastActionAt = _phaseTime;
                SetPhase(Phase.Approach);
                return;
            }

            Client.Send(new LookAtMessage { Target = _vendor, ReturnInfo = 0 });
            GameCommands.UseObject(me, _vendor);
            _stage = 1;
            _useAt = _phaseTime;
            _lastActionAt = _phaseTime;
            return;
        }

        // Stage 1: the window is open (ShopUpdate + Trade open) - send the batch.
        if (_phaseTime - _useAt < WindowBeat)
        {
            return;
        }

        var batch = sell.Take(SellBatch).ToList();
        foreach (var it in batch)
        {
            Client.Send(new TradeMessage
            {
                Version = 2,
                Action = TradeAction.AddItem,
                Param1 = (int)me.Identity.Type,
                Param2 = me.Identity.Instance,
                Param3 = (int)it.Slot.Type,
                Param4 = it.Slot.Instance,
            });
        }

        Client.Send(new TradeMessage { Version = 2, Action = TradeAction.Accept }); // no target: the SELL accept
        _logger.LogInformation($"SELL: selling {string.Join(", ", batch.Select(b => b.Name))} to '{_vendorName}' (batch {_rounds + 1}).");
        _lastBatchSlots = batch.Select(b => b.Slot).ToList();
        _lastVendor = _vendor;
        _stage = 0;
        _rounds++;
        _lastActionAt = _phaseTime;
    }

    // ---- bags and vendors ------------------------------------------------------

    // Open only the bags whose contents the SDK doesn't have yet. The marker is the container's
    // Handle (IsOpen): the login read-through or any earlier open answers with one, and an OPENED
    // bag that is empty is known-empty - only the Handle-0 shell login plants needs the open.
    // Container.Items is the session cache: once delivered it stays for the whole session and
    // survives the login inventory rebuild; a relog starts empty.
    private void OpenUnknownBags(LocalPlayer me)
    {
        foreach (var b in Inventory.Items.Where(i =>
                     i != null && i.Slot.Type == IdentityType.Inventory &&
                     i.UniqueIdentity.Type == IdentityType.Container))
        {
            var ct = Inventory.Containers.FirstOrDefault(c => c.Identity == b.UniqueIdentity);
            if (ct is { IsOpen: true })
            {
                continue; // contents already delivered (or known-empty)
            }

            GameCommands.OpenContainer(me, b.Slot);
        }
    }

    // The contents of every bag: everything except NODROP items (they stay - the owner said so) and
    // bags themselves (a bag inside a bag stays a bag). Refused slots never come back. Remembers the
    // source bag of everything seen, so unsold items can go home at the end.
    private List<Item> SellableInBags()
    {
        var sell = new List<Item>();
        foreach (var c in Inventory.Containers)
        {
            foreach (var it in c.Items)
            {
                if (SellableItem(it))
                {
                    sell.Add(it);
                    _stagedFrom[it.UniqueIdentity] = c.Identity;
                }
            }
        }

        return sell;
    }

    private bool SellableItem(Item i)
    {
        if (i?.Name == null)
        {
            return false;
        }

        if (i.UniqueIdentity.Type == IdentityType.Container)
        {
            return false; // a bag inside a bag stays a bag
        }

        if (ItemValues.IsNoDrop(i.Id, i.HighId))
        {
            return false; // NODROP: never sellable, stays in the bag (owner, 2026-10-02)
        }

        if (KeepFromSale != null && KeepFromSale(i))
        {
            return false; // kept by another controller's rule (the want list): stays in the bag
        }

        if (_refusedSlots.Contains(i.Slot))
        {
            return false;
        }

        return true;
    }

    private void NextVendor(LocalPlayer me)
    {
        _candidates.Clear();
        _candidates.AddRange(DynelManager.VendingMachines
            .Where(v => !_badVendors.Contains(v.Identity) && Movement.Flat(me.Transform.Position, v.Transform.Position) < VendorRange)
            .OrderBy(v => Movement.Flat(me.Transform.Position, v.Transform.Position))
            .Select(v => v.Identity));

        if (_candidates.Count == 0)
        {
            // No terminal in reach here: the run's ONE travel - to the shop playfield (the Fair
            // Trade instance, ResupplyShopPf). The way BACK is not selling's business. At most once
            // per run, so an empty shop can't loop us into re-planning.
            var shopPf = _config.ResupplyShopPf;
            if (shopPf > 0 && !_travelPlanned && (int)Playfield.ModelId != shopPf)
            {
                var line = _movement.PlanTravel(shopPf, null);
                if (_movement.TravelTargetPf == shopPf)
                {
                    _travelPlanned = true;
                    SetPhase(Phase.Transit);
                    Tell($"Nothing sellable in reach here - heading for {Zoning.Name(shopPf)}. {line}");
                    return;
                }

                _logger.LogInformation($"SELL: travel to {Zoning.Name(shopPf)} refused - {line}");
            }

            me.TryGetStat(Stat.Cash, out var cash);
            Finish(me, cash, "No shop terminal in range to sell to.");
            return;
        }

        _vendor = _candidates[0];
        _candidates.RemoveAt(0);
        if (!DynelManager.Find(_vendor, out VendingMachine vm))
        {
            _badVendors.Add(_vendor); // vanished between listing and walking: don't retry it
            NextVendor(me); // bounded: each pass consumes a candidate or finishes
            return;
        }

        _vendorName = string.IsNullOrEmpty(vm.Name) ? _vendor.ToString() : vm.Name;
        _stage = 0;
        _moves = 0;
        _arrivedAt = -1;
        _lastBatchSlots = null;
        _movement.SetDesiredGoal(vm.Transform.Position, _runPf, ControlPriority.Selling, VendorReach);
        _logger.LogInformation($"SELL: next terminal '{_vendorName}' ({_vendor}).");
        SetPhase(Phase.Approach);
    }

    // ---- lifecycle ----------------------------------------------------------------

    private void SetPhase(Phase p)
    {
        _phase = p;
        _phaseTime = 0;
        _arrivedAt = -1;
        _useAt = -1;
        _transitAt = -1;
    }

    private void CloseShopWindow()
    {
        // A half-open trade window is declined naming the vendor; the Accept already closed its own.
        Client.Send(new TradeMessage
        { Version = 2, Action = TradeAction.Decline, Param1 = (int)_vendor.Type, Param2 = _vendor.Instance });
    }

    private void Finish(LocalPlayer me, int cashNow, string why)
    {
        // MAIN INVENTORY UNTOUNCHED, part two: everything staged but not sold (refused, out of
        // budget) goes back into the bag it came from, while the bags have room for it.
        var returned = 0;
        foreach (var it in Inventory.Items
                     .Where(i => i != null && i.Slot.Type == IdentityType.Inventory && _stagedFrom.ContainsKey(i.UniqueIdentity))
                     .ToList())
        {
            if (!_stagedFrom.TryGetValue(it.UniqueIdentity, out var bagId))
            {
                continue;
            }

            var bag = Inventory.Containers.FirstOrDefault(c => c.Identity == bagId);
            if (bag == null || bag.NumFreeSlots <= 0)
            {
                continue; // its bag is full or gone: the item stays in the main inventory
            }

            it.MoveToContainer(bagId);
            _stagedFrom.Remove(it.UniqueIdentity);
            returned++;
        }

        var earned = cashNow - _cashAtStart;
        var text = $"Selling done: {why} Cash {_cashAtStart} -> {cashNow} ({earned:+0;-0})." +
                   (returned > 0 ? $" {returned} unsold item(s) returned to their bags." : "");
        _logger.LogInformation("SELL: " + text);
        Tell(text);
        TearDown();
    }

    private void TearDown()
    {
        _controlArbiter.ReleaseControl();
        _movement.CancelTravel(); // a still-open trip to the shop playfield goes with the run
        _movement.ClearDesiredGoal(ControlPriority.Selling);
        _phase = Phase.Idle;
        _phaseTime = 0;
        _vendor = Identity.None;
        _vendorName = "";
        _candidates.Clear();
        _staged.Clear();
        _stagedFrom.Clear();
        _lastBatchSlots = null;
        _stage = 0;
        _rounds = 0;
        _moves = 0;
        _arrivedAt = -1;
        _useAt = -1;
        _transitAt = -1;
        _travelPlanned = false;
        _lastActionAt = -99;
        _runPf = -1;
    }

    // ---- owner link ----------------------------------------------------------------

    private int OwnerInstance()
    {
        if (string.IsNullOrEmpty(_config.Owner))
        {
            return 0;
        }

        var owner = DynelManager.Players.FirstOrDefault(pl =>
            string.Equals(pl.Name, _config.Owner, StringComparison.OrdinalIgnoreCase));
        return owner?.Identity.Instance ?? 0;
    }

    private void Tell(string text)
    {
        if (_tellId != 0)
        {
            Client.SendPrivateMessage(_tellId, text);
            return;
        }

        var owner = OwnerInstance();
        if (owner != 0)
        {
            Client.SendPrivateMessage((uint)owner, text);
        }
        else
        {
            _logger.LogInformation($"SELL (owner not reachable, tell not sent): {text}");
        }
    }
}