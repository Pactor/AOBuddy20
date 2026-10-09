// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: ResupplyController.cs
//
// Last modified: 2026-10-01
// Created:       2026-10-01 (ported from AOBuddy10 ResupplyController.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Interfaces;
using AOBuddy20.Nav;
using AOBuddy20.Network;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     RESUPPLY (ported from AOBuddy10): stand in a shop (Fair Trade, Omni/Clan store), walk to the
///     terminal that sells stims and rechargers, buy the best ones the character can actually use
///     (First Aid / Treatment decide, not price), and ask the owner for credits when it can't pay.
///     Nothing in reach in this zone: the run's ONE travel takes it to the shop playfield
///     (ResupplyShopPf, default 1187 "Neutral Supermarket Advanced", entered over proxy terminals)
///     on the MovementController's travel machinery - outbound only; the way back belongs to
///     whatever controller owns the body next.
///
///     Control scheme: while a run is active the controller takes the ControlArbiter at
///     <see cref="ControlPriority.Resupply" /> (above Mission - a run must not starve mid-mission -
///     below Combat) and walks to terminals through a MovementController goal at the same priority
///     - so lower systems yield and the body stays with the shopping, while anything higher
///     (combat) still preempts it by the usual rules. The decision tick itself runs on the update
///     thread (BotLoop): the same thread the packet handlers below fire on, so the state machine
///     needs no locks.
///
///     The shop protocol, as the live server speaks it (AO Item Assistant's logs of real sessions,
///     and OmniCell's vendor handler for the slot numbering):
///       Use (GenericCmd) on the VendingMachine  -> server sends ShopUpdate: the machine's stock, and a
///                                                  Trade Open with the machine as the partner
///       Trade AddItem(machine, 0x6F, slot)      -> one of the stock line's items onto the buy side; slot
///                                                  is the 0-based index into ShopUpdate's list, and the
///                                                  same line can be added again for another copy
///       Trade Accept(machine)                   -> server commits (Trade op 4 from the machine), sends each
///                                                  item as an AddTemplate and takes the credits
///       Trade Decline(machine)                  -> closes the window, buys nothing
///     A line costs Value x the terminal's SellModifier/100 x the buyer's Computer Literacy discount
///     (ItemValues.ShopPrice). The modifier is not in the terminal's spawn packet; it's on the terminal's
///     template (StaticInstance), which ItemValues carries - at ICC Fair Trade the Basic/Advanced/Superior
///     pharmacies are 105/505/1005. After each purchase, paid / computed is saved per terminal to
///     resupply.json as a correction (expected ~1.0; it's there in case another shop disagrees). A terminal
///     whose template has no modifier is priced at bare Value and the server is the judge: a buy that doesn't
///     go through is retried at half the count, and one that fails at a single item means we can't afford it.
///
///     One item kind per purchase, so each cash drop belongs to one price.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class ResupplyController : IPacketConsumer
{
    private enum Phase { Idle, Approach, Opening, Adding, Settling, WaitMoney, Transit }

    private enum Supply { Stim, Recharger, Container, Lockpick }

    private sealed class Offer
    {
        public Offer(int slot, Item item)
        {
            Slot = slot;
            Item = item;
        }

        public int Slot;
        public Item Item;
    }

    private sealed class Memory
    {
        // "pf:instance" -> paid / computed price
        public Dictionary<string, double> Corrections = new();

        // "pf:instance" -> "Stim,Recharger" / ""
        public Dictionary<string, string> Machines = new();
    }

    // AOBuddy10's proven timings (ResupplyController.cs:98-105) and the walk settle beat the
    // travel legs use (MovementController.SettleSeconds): stand still a moment so the server has
    // our stop before we use the terminal from where it has us.
    private const double SettleSeconds = 0.6;
    private const double OpenTimeout = 5.0; // no ShopUpdate in this long = not a shop / too far
    private const double FirstAddDelay = 0.5; // let the shop window open before adding to it
    private const double AddInterval = 0.2; // one AddItem per this, like a player clicking
    private const double SettleTimeout = 8.0; // no commit in this long = the server refused the buy
    private const double CashSettle = 1.5; // after the commit, wait this long for the Cash stat
    private const int MaxOpens = 16;

    // The approach rides the MovementController's routed walk; this only guards a terminal the
    // walk can never reach (walled off, indoors): give up on it and try the next.
    private const double ApproachTimeout = 90.0;

    // The travel to the shop playfield is watched per-leg by the MovementController's own
    // machinery; this only bounds the WHOLE trip (multi-zone routes walk for minutes) so a run
    // can never sit in transit forever.
    private const double TransitTimeout = 600.0;

    // After his accept (an accept right on his change is dropped) / after our accept.
    private const double OwnerAcceptDelay = 1.0;
    private const double OwnerConfirmDelay = 1.5;

    private readonly ILogger<ResupplyController> _logger;
    private readonly MovementController _movement;
    private readonly ControlArbiter _controlArbiter;
    private readonly AccountInfo _config;
    private readonly string _file;
    private Memory? _mem;

    private Phase _phase = Phase.Idle;
    private double _phaseTime;
    private readonly List<Identity> _candidates = new();
    private Identity _machine = Identity.None;
    private string _machineName = "";
    private int _runPf = -1; // the playfield the run started in; a zone ends it (travel legs excepted)
    private double _arrivedAt = -1; // standing on the terminal since (settle beat starts here)
    private double _transitAt = -1; // in transit to the shop playfield since (trip watchdog starts here)
    private bool _travelPlanned; // the trip to the shop playfield: at most once per run
    private int _opens; // machine opens this run (runaway guard)

    // What the current machine answered with.
    private VendingMachineSlot[]? _stock;
    private bool _tradeDone;
    private bool _tradeDeclined;
    private string? _lastFeedback;

    // The purchase in flight.
    private Supply _kind;
    private Offer? _offer;
    private int _count;
    private int _added;
    private int _retryCount; // unknown price: the halved count to try on the reopen
    private int _cashBefore;
    private double _doneAt = -1;

    // What this run set out to do, so a purchase the inventory model never registers can't loop us.
    private readonly Dictionary<Supply, int> _haveAtStart = new();
    private readonly Dictionary<Supply, int> _bought = new();

    // Waiting on the owner for credits.
    private int _cashAtWait;
    private int _waitMissing;
    private int _waitCount;
    private int _waitPrice;
    private bool _waitEstimated; // the price is Value-based, not one we've paid
    private double _nagAccum;

    // A CONTAINER run ('resupply bags n' / the mission run's 'buybags'): buy n more bags than we
    // carry now. 0 = the run is about stims/rechargers only.
    private int _containerBuy;

    // The owner handing us credits in a player trade. Our answer to his accept, wire-proven:
    // Accept(owner) then Confirm(owner), each naming the owner with p34 empty (as the retail client
    // sends them). One Confirm closes it; a second opens a second confirm window.
    private enum OwnerStep { None, Accept, Confirm, Done }

    private bool _ownerTrade;
    private OwnerStep _ownerStep;
    private double _ownerWait;
    private uint _tellId; // the owner's chat id, proven once by a command tell; async tells go here

    public bool Active => _phase != Phase.Idle;

    public ResupplyController(ILogger<ResupplyController> logger, MovementController movement,
        ControlArbiter controlArbiter, AccountInfo config)
    {
        _logger = logger;
        _movement = movement;
        _controlArbiter = controlArbiter;
        _config = config;
        _file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resupply.json");
        // Player trades: the owner handing us credits when we asked for them. Fires on the update
        // thread (the packet pump), the same thread Tick runs on.
        Trade.TradeStatusChanged += OnTradeStatusChanged;
        _logger.LogInformation("Resupply controller initialized.");
    }

    /// <summary>
    ///     Below the warning floor on either supply - what solo mode will check before deciding to
    ///     go shopping. The lockpick rides along: locked mission doors want one in the packs
    ///     (owner, 2026-10-09), and it hangs in a DIFFERENT machine than the pharmacy's - the
    ///     machine memory learns which one sells it on the first trip.
    /// </summary>
    public bool NeedsResupply()
    {
        return Have(Supply.Stim) <= _config.LowStimCount ||
               Have(Supply.Recharger) <= _config.LowRechargerCount ||
               Have(Supply.Lockpick) < _config.ResupplyLockpickTarget;
    }

    // A container run prefers terminals whose name says containers/backpacks over the supply keywords.
    private bool NameSuggestsSupplies(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (_containerBuy > 0)
        {
            return name.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Backpack", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return _config.ResupplyMachineKeywords != null &&
               _config.ResupplyMachineKeywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>OwnerChat proves the owner's chat id once per tell; async replies (buys, nags) use it.</summary>
    public void SetTellId(uint charId)
    {
        _tellId = charId;
    }

    // ---- Commands -----------------------------------------------------------

    /// <summary>
    ///     The 'resupply' command. parts is the raw command split; sub-words and replies are the
    ///     AOBuddy10 ones.
    /// </summary>
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
                    reply("Resupply stopped.");
                }
                else
                {
                    reply("Not resupplying.");
                }

                break;
            case "status":
                reply("Resupply: " + Describe());
                break;
            case "forget":
                Forget();
                reply("Forgot which terminals sell what; the next resupply checks them all again.");
                break;
            case "machines":
            {
                var ml = DescribeMachines(me);
                if (ml.Count == 0)
                {
                    reply($"No terminals within {_config.ResupplySearchRadius:0}m.");
                    break;
                }

                foreach (var l in ml.Take(15))
                {
                    reply(Truncate(l, 440));
                }

                if (ml.Count > 15)
                {
                    reply($"…(+{ml.Count - 15} more, all in the log)");
                }

                break;
            }
            case "bags":
            {
                if (parts.Length < 3 || !int.TryParse(parts[2], out var bags) || bags < 1 || bags > 20)
                {
                    reply("Usage: resupply bags <1-20>");
                    return;
                }

                StartContainers(me, bags, reply);
                break;
            }
            default:
                Start(me, reply);
                break;
        }
    }

    public void Start(LocalPlayer me, Action<string> reply)
    {
        if (Active)
        {
            reply("Already resupplying — " + Describe());
            return;
        }

        Mem(); // the terminal memory in, before the run reads it

        _haveAtStart.Clear();
        _bought.Clear();
        _containerBuy = 0;
        foreach (Supply s in new[] { Supply.Stim, Supply.Recharger, Supply.Lockpick })
        {
            _haveAtStart[s] = Have(s);
            _bought[s] = 0;
        }

        var needs = Needs();
        if (needs.Count == 0)
        {
            reply($"Stocked up: {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers. Nothing to buy.");
            return;
        }

        var candidates = BuildCandidates(me, needs);
        var travel = ShopTravel.Local;
        var travelLine = "";
        if (candidates.Count == 0)
        {
            travel = PlanShopTravel(out travelLine);
            if (travel == ShopTravel.Failed)
            {
                reply($"Nothing in reach within {_config.ResupplySearchRadius:0}m. {travelLine}");
                return;
            }

            if (travel == ShopTravel.Local)
            {
                reply($"No shop terminals within {_config.ResupplySearchRadius:0}m that could sell " +
                      $"{string.Join(" or ", needs.Select(Plural))}.");
                return;
            }
        }

        _runPf = (int)Playfield.ModelId;
        _opens = 0;
        _retryCount = 0;
        me.TryGetStat(Stat.Cash, out var cash);
        var where = candidates.Count > 0
            ? $"{candidates.Count} terminal(s) to check"
            : $"traveling to {Zoning.Name(_config.ResupplyShopPf)}";
        reply($"Resupplying: {string.Join(", ", needs.Select(s => $"{Remaining(s)} {Plural(s)}"))}. " +
              $"{cash} credits, {where}.{(travel == ShopTravel.Travelled ? $" {travelLine}" : "")}");
        _logger.LogInformation($"RESUPPLY: start — needs " +
                               $"{string.Join(", ", needs.Select(s => $"{s} have {Have(s)} want {Want(s)}"))}, cash {cash}, " +
                               $"{where}.");

        // While the run is active this controller owns the body at its priority: lower systems
        // yield the arbiter, and the walk to a terminal is a goal no lower priority can take.
        _controlArbiter.TakeControl(ControlPriority.Resupply);
        if (candidates.Count > 0)
        {
            _candidates.AddRange(candidates);
            NextMachine(me);
        }

        // else PlanShopTravel already set the Transit phase; the trip walks on the
        // MovementController's travel goals
    }

    /// <summary>
    ///     The CONTAINER run ('resupply bags n', and the mission run's 'mission buybags n'): buy n
    ///     more bags than we carry now - the loot bags for the mission run's rewards. The same shop
    ///     protocol as the supplies, the same one-travel rule; the bought bag is the cheapest line
    ///     of <see cref="AccountInfo.ResupplyContainerName" /> the terminal stocks.
    /// </summary>
    public void StartContainers(LocalPlayer me, int n, Action<string> reply)
    {
        if (Active)
        {
            reply("Already resupplying — " + Describe());
            return;
        }

        Mem();

        _haveAtStart.Clear();
        _bought.Clear();
        _containerBuy = n;
        _haveAtStart[Supply.Container] = Have(Supply.Container);
        _bought[Supply.Container] = 0;

        var candidates = BuildCandidates(me, Needs());
        var travel = ShopTravel.Local;
        var travelLine = "";
        if (candidates.Count == 0)
        {
            travel = PlanShopTravel(out travelLine);
            if (travel == ShopTravel.Failed)
            {
                reply($"Nothing in reach within {_config.ResupplySearchRadius:0}m. {travelLine}");
                return;
            }

            if (travel == ShopTravel.Local)
            {
                reply($"No shop terminals within {_config.ResupplySearchRadius:0}m that could sell bags.");
                return;
            }
        }

        _runPf = (int)Playfield.ModelId;
        _opens = 0;
        _retryCount = 0;
        me.TryGetStat(Stat.Cash, out var cash);
        var where = candidates.Count > 0
            ? $"{candidates.Count} terminal(s) to check"
            : $"traveling to {Zoning.Name(_config.ResupplyShopPf)}";
        reply($"Buying {n} bag(s): I carry {_haveAtStart[Supply.Container]}, want " +
              $"{_haveAtStart[Supply.Container] + n}. {cash} credits, {where}.");
        _logger.LogInformation($"RESUPPLY: container run - carry {_haveAtStart[Supply.Container]}, " +
                               $"buy {n}, cash {cash}, {where}.");

        _controlArbiter.TakeControl(ControlPriority.Resupply);
        if (candidates.Count > 0)
        {
            _candidates.AddRange(candidates);
            NextMachine(me);
        }
    }

    public void Stop(string why)
    {
        if (!Active)
        {
            return;
        }

        if (_phase is Phase.Opening or Phase.Adding or Phase.Settling)
        {
            CloseShop();
        }

        _logger.LogInformation($"RESUPPLY: stopped ({why}).");
        TearDown();
    }

    // What became of the try to plan the trip to the shop playfield.
    private enum ShopTravel { Travelled, Local, Failed }

    // No terminal in reach here: the one travel a resupply run plans - to the shop playfield
    // (ResupplyShopPf, default 1187 "Neutral Supermarket Advanced", the Fair Trade instance:
    // entered over proxy terminals, in-game an instance whose playfield MODEL is 1187). The way
    // BACK is not resupply's business - when the shopping ends, follow/mission own the body again.
    // The trip itself walks on the MovementController's travel machinery (zone-line route, legs,
    // re-planning after every zone); this only books it and watches for arrival. At most once per
    // run, so an empty shop can't bounce us into planning the same trip again.
    private ShopTravel PlanShopTravel(out string line)
    {
        line = "";
        var shopPf = _config.ResupplyShopPf;
        if (shopPf <= 0 || _travelPlanned || (int)Playfield.ModelId == shopPf)
        {
            return ShopTravel.Local;
        }

        line = _movement.PlanTravel(shopPf, null);
        if (_movement.TravelTargetPf != shopPf)
        {
            _logger.LogInformation($"RESUPPLY: travel to {Zoning.Name(shopPf)} refused - {line}");
            return ShopTravel.Failed;
        }

        _travelPlanned = true;
        SetPhase(Phase.Transit);
        _logger.LogInformation($"RESUPPLY: nothing in reach here - traveling to {Zoning.Name(shopPf)}.");
        return ShopTravel.Travelled;
    }

    public void Forget()
    {
        var mem = Mem();
        mem.Machines.Clear();
        Save(mem);
    }

    public string Describe()
    {
        var what = _offer != null ? $" {_added}/{_count}x {_offer.Item.Name} QL{_offer.Item.Ql}" : "";
        return _phase switch
        {
            Phase.Idle =>
                $"idle. {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers; " +
                $"{Mem().Corrections.Count} terminal price correction(s) learned.",
            Phase.WaitMoney => $"waiting for credits at '{_machineName}' to buy{what}.",
            _ => $"{_phase} '{_machineName}'{what} ({_candidates.Count} terminal(s) left).",
        };
    }

    // ---- Wire -----------------------------------------------------------------

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(ShopUpdateHandler, N3MessageType.ShopUpdate, 0);
        router.Register(TradeHandler, N3MessageType.Trade, 0);
        router.Register(AddTemplateHandler, N3MessageType.AddTemplate, 0);
        router.Register(FormatFeedbackHandler, N3MessageType.FormatFeedback, 0);
        router.Register(FeedbackHandler, N3MessageType.Feedback, 0);
    }

    // The stock of the machine we're shopping at. Logged whenever it arrives: this is the record
    // the price corrections and the sells-memory are checked against.
    private bool ShopUpdateHandler(AOMessage arg)
    {
        if (arg.Body is not ShopUpdateMessage shop)
        {
            return false;
        }

        if (shop.Identity == _machine)
        {
            _stock = shop.VendingMachineSlots ?? Array.Empty<VendingMachineSlot>();
        }

        LogShopUpdate(shop);
        return false;
    }

    private bool TradeHandler(AOMessage arg)
    {
        if (arg.Body is not TradeMessage trade)
        {
            return false;
        }

        var partner = new Identity((IdentityType)trade.Param1, trade.Param2);
        if (Active || _ownerTrade)
        {
            _logger.LogInformation($"RESUPPLY: Trade rx {trade.Action} partner={partner} p3={trade.Param3:X} p4={trade.Param4}");
        }

        // He changed the offer: his accept is void; we answer his next one afresh.
        if (_ownerTrade &&
            trade.Action is TradeAction.UpdateCredits or TradeAction.AddItem
                or TradeAction.RemoveItem or TradeAction.OtherPlayerAddItem)
        {
            _ownerStep = OwnerStep.None;
        }

        // The server's Decline names no partner (None:0) - it closes whatever window is open.
        if (trade.Action == TradeAction.Decline && (partner == _machine || partner == Identity.None))
        {
            _tradeDeclined = true;
        }
        else if (trade.Action == TradeAction.Complete && partner == _machine)
        {
            _tradeDone = true;
        }

        return false;
    }

    private bool AddTemplateHandler(AOMessage arg)
    {
        if (Active && arg.Body is AddTemplateMessage add)
        {
            _logger.LogInformation($"RESUPPLY: AddTemplate low={add.LowId} high={add.HighId} QL{add.Quality} count={add.Count}");
        }

        return false;
    }

    private bool FormatFeedbackHandler(AOMessage arg)
    {
        if (arg.Body is FormatFeedbackMessage ff)
        {
            _lastFeedback = ff.FormattedMessage;
            if (Active)
            {
                _logger.LogInformation($"RESUPPLY: feedback '{_lastFeedback}'");
            }
        }

        return false;
    }

    private bool FeedbackHandler(AOMessage arg)
    {
        if (arg.Body is FeedbackMessage fb)
        {
            _lastFeedback = $"feedback {fb.CategoryId}/{fb.MessageId}";
            if (Active)
            {
                _logger.LogInformation($"RESUPPLY: {_lastFeedback}");
            }
        }

        return false;
    }

    // A player trade opened on us: the owner's only when the target is him (opening a VENDING
    // machine trades with the machine - that one is ours to run, never to auto-accept).
    private void OnTradeStatusChanged(Identity who, TradeStatus status)
    {
        if (status == TradeStatus.Opened)
        {
            var owner = OwnerInstance();
            _ownerTrade = owner != 0 && who.Instance == owner;
            _ownerStep = OwnerStep.None;
            if (_ownerTrade)
            {
                _logger.LogInformation("RESUPPLY: owner opened a trade.");
            }

            return;
        }

        if (!_ownerTrade)
        {
            return;
        }

        switch (status)
        {
            case TradeStatus.Accept:
                if (_ownerStep != OwnerStep.None)
                {
                    break;
                }

                _ownerStep = OwnerStep.Accept; // his accept: answer it after the delay, in OwnerTick
                _ownerWait = 0;
                _logger.LogInformation("RESUPPLY: owner accepted the trade " +
                                       $"(offering {Trade.TargetWindowCache.Credits} credits, " +
                                       $"{Trade.TargetWindowCache.Items.Count} item(s)) — accepting.");
                break;
            case TradeStatus.Finished:
            case TradeStatus.Declined:
                _logger.LogInformation($"RESUPPLY: owner trade {status}.");
                _ownerTrade = false;
                _ownerStep = OwnerStep.None;
                break;
        }
    }

    // ---- Frame ----------------------------------------------------------------

    /// <summary>
    ///     The decision tick, every update while in play (BotLoop). Runs the owner-trade answers
    ///     even when no run is active (he can hand credits anytime); a run owns the body through its
    ///     MovementController goal. Returns true while a run is active.
    /// </summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        OwnerTick(dt);
        if (!Active)
        {
            return false;
        }

        if ((int)Playfield.ModelId != _runPf)
        {
            if (_phase != Phase.Transit)
            {
                // The zone-in interrupted us (an approach that grazed a line): the candidates and
                // the open shop belong to the old playfield. AOBuddy10 stopped here too; the next
                // 'resupply' (or solo mode) starts fresh from wherever we are now.
                _logger.LogInformation("RESUPPLY: zoned mid-run — stopping.");
                Tell("Zoned while resupplying — stopped.");
                Stop("zoned");
                return false;
            }

            // In transit to the shop playfield a zone IS the plan working; TransitTick below
            // watches for the arrival.
        }

        _phaseTime += dt;
        switch (_phase)
        {
            case Phase.Approach:
                ApproachTick(me);
                break;
            case Phase.Opening:
                OpeningTick(me);
                break;
            case Phase.Adding:
                AddingTick();
                break;
            case Phase.Settling:
                SettlingTick(me);
                break;
            case Phase.WaitMoney:
                WaitMoneyTick(me, dt);
                break;
            case Phase.Transit:
                TransitTick(me);
                break;
        }

        return true;
    }

    // En route to the shop playfield. The legs are the MovementController's (its own goals, leg
    // watchdogs and re-planning after every zone); this watches for the arrival - the playfield
    // MODEL reads 1187 even though in-game it is an instance - or the plan giving up.
    private void TransitTick(LocalPlayer me)
    {
        var shopPf = _config.ResupplyShopPf;
        if ((int)Playfield.ModelId == shopPf)
        {
            _runPf = shopPf; // arrival: the local-zone guard works again from here
            RebuildCandidates(me);
            if (_candidates.Count == 0)
            {
                Finish(me, $"No terminal in reach at {Zoning.Name(shopPf)} either. " +
                           $"Have {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers.");
                return;
            }

            _logger.LogInformation($"RESUPPLY: arrived in {Zoning.Name(shopPf)} - " +
                                   $"{_candidates.Count} terminal(s) in reach.");
            NextMachine(me);
            return;
        }

        if (_movement.TravelTargetPf != shopPf)
        {
            // The plan gave up (no route / an exit blacklisted out) or a manual order took the
            // body from it.
            Finish(me, $"Travel to {Zoning.Name(shopPf)} didn't happen - resupply stopped here. " +
                       $"Have {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers.");
            return;
        }

        if (_transitAt < 0)
        {
            _transitAt = _phaseTime;
            return;
        }

        if (_phaseTime - _transitAt > TransitTimeout)
        {
            Finish(me, $"Travel to {Zoning.Name(shopPf)} ran over {TransitTimeout / 60:0} minutes - giving up. " +
                       $"Have {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers.");
        }
    }

    // Walk to the machine through the MovementController (a goal at ControlPriority.Resupply, so
    // the routed grid walk and every higher-priority preemption rule apply), then settle and use.
    private void ApproachTick(LocalPlayer me)
    {
        if (!DynelManager.Find(_machine, out VendingMachine vm))
        {
            _logger.LogInformation($"RESUPPLY: '{_machineName}' is gone.");
            _movement.ClearDesiredGoal(ControlPriority.Resupply);
            NextMachine(me);
            return;
        }

        if (_movement.IsGoalReached(ControlPriority.Resupply))
        {
            if (_arrivedAt < 0)
            {
                _arrivedAt = _phaseTime;
            }

            if (_phaseTime - _arrivedAt >= SettleSeconds)
            {
                Open(me); // the walk holds on the reached goal; the body stays put while we shop
            }

            return;
        }

        if (_phaseTime > ApproachTimeout)
        {
            _logger.LogInformation($"RESUPPLY: never got within {_config.ResupplyUseRange:0}m of " +
                                   $"'{_machineName}' in {ApproachTimeout:0}s — skipping it.");
            _movement.ClearDesiredGoal(ControlPriority.Resupply);
            NextMachine(me);
        }
    }

    private void OpeningTick(LocalPlayer me)
    {
        if (_stock == null)
        {
            if (_phaseTime > OpenTimeout)
            {
                _logger.LogInformation($"RESUPPLY: '{_machineName}' didn't open a shop in {OpenTimeout:0}s" +
                                       $"{(_lastFeedback != null ? $" ('{_lastFeedback}')" : "")} — skipping it.");
                NextMachine(me);
            }

            return;
        }

        if (_phaseTime < FirstAddDelay)
        {
            return;
        }

        // What does it sell? Remember it for this playfield, so the next run walks straight to the
        // right one.
        var offers = _stock.Select((s, i) => (slot: i, item: StockItem(s)))
            .Where(x => x.item != null && !string.IsNullOrEmpty(x.item.Name))
            .Select(x => new Offer(x.slot, x.item!)).ToList();
        var sells = new List<Supply>();
        foreach (Supply s in new[] { Supply.Stim, Supply.Recharger, Supply.Lockpick })
        {
            if (offers.Any(o => Is(o.Item, s)))
            {
                sells.Add(s);
            }
        }

        Remember(_machine, sells);
        _logger.LogInformation($"RESUPPLY: '{_machineName}' sells: " +
                               (sells.Count == 0
                                   ? "none"
                                   : string.Join(", ", offers.Where(o => sells.Any(s => Is(o.Item, s)))
                                       .Select(o => $"[{o.Slot}] {o.Item.Name} QL{o.Item.Ql}"))));

        foreach (var need in Needs())
        {
            // Fitting = the highest QL whose First Aid / Treatment requirement he meets right now.
            // Bags take no skill check: the cheapest line of the wanted name is the one to buy.
            // The lockpick gates on its own use req (break/entry), not the heal kit's.
            var fitting = need == Supply.Container
                ? offers.Where(o => Is(o.Item, need)).OrderBy(o => o.Item.Ql)
                : need == Supply.Lockpick
                    ? offers.Where(o => Is(o.Item, need) && o.Item.MeetsUseReqs(me, false))
                        .OrderBy(o => o.Item.Ql) // any pick he can use opens the door; the cheap one
                    : offers.Where(o => Is(o.Item, need) && HealItems.MeetsHealReqs(o.Item, me))
                        .OrderByDescending(o => o.Item.Ql);
            var best = fitting.FirstOrDefault();
            if (best == null)
            {
                if (sells.Contains(need))
                {
                    _logger.LogInformation(need == Supply.Container
                        ? $"RESUPPLY: '{_machineName}' has no '{_config.ResupplyContainerName}'."
                        : $"RESUPPLY: '{_machineName}' has {Plural(need)} but none I have the skill for.");
                }

                continue;
            }

            BeginPurchase(me, need, best);
            return;
        }

        CloseShop();
        NextMachine(me);
    }

    private void BeginPurchase(LocalPlayer me, Supply kind, Offer offer)
    {
        _kind = kind;
        _offer = offer;
        // A purchase lands as ONE stack (seen: 7 stims bought = one slot used), so it needs one
        // free slot beyond the reserve, not one per item.
        if (Inventory.NumFreeSlots <= _config.ResupplyKeepFreeSlots)
        {
            CloseShop();
            Finish(me, $"My inventory is full — can't buy {Plural(kind)}.");
            return;
        }

        var count = Remaining(kind);
        if (_retryCount > 0)
        {
            count = Math.Min(count, _retryCount);
        }

        me.TryGetStat(Stat.Cash, out var cash);
        var price = PriceEach(offer.Item, out var priceFrom);
        if (price > 0)
        {
            var affordable = (cash - _config.ResupplyCashReserve) / price;
            if (affordable <= 0)
            {
                CloseShop();
                _waitEstimated = priceFrom == BareValue;
                WaitForMoney(me, cash, price * count - (cash - _config.ResupplyCashReserve), count, price);
                return;
            }

            if (affordable < count)
            {
                _logger.LogInformation($"RESUPPLY: can only afford {affordable} of {count} at {price} each.");
            }

            count = Math.Min(count, affordable);
        }

        _count = count;
        _added = 0;
        _cashBefore = cash;
        _tradeDone = false;
        _tradeDeclined = false;
        _doneAt = -1;
        _lastFeedback = null;
        _logger.LogInformation($"RESUPPLY: buying {count}x [{offer.Slot}] {offer.Item.Name} QL{offer.Item.Ql} " +
                               $"({(price > 0 ? $"{price} each, {priceFrom}" : "no price known")}), cash {cash}.");
        SetPhase(Phase.Adding);
    }

    private void AddingTick()
    {
        if (_tradeDeclined)
        {
            _logger.LogInformation("RESUPPLY: the shop closed while adding.");
            Failed(DynelManager.LocalPlayer);
            return;
        }

        if (_added < _count)
        {
            if (_phaseTime < (_added + 1) * AddInterval)
            {
                return;
            }

            SendTrade(TradeAction.AddItem, 0x6F, _offer!.Slot); // set by BeginPurchase before this phase
            _added++;
            return;
        }

        SendTrade(TradeAction.Accept, 0, 0);
        _logger.LogInformation($"RESUPPLY: added {_count}, accepting.");
        SetPhase(Phase.Settling);
    }

    private void SettlingTick(LocalPlayer me)
    {
        me.TryGetStat(Stat.Cash, out var cash);
        if (_tradeDone)
        {
            if (_doneAt < 0)
            {
                _doneAt = _phaseTime;
            }

            // The Cash stat can land a beat after the commit; wait for it (or a short while) to price the buy.
            if (cash == _cashBefore && _phaseTime - _doneAt < CashSettle)
            {
                return;
            }

            Bought(me, cash);
            return;
        }

        if (_tradeDeclined || _phaseTime > SettleTimeout)
        {
            // A commit we didn't see, but the money went: it went through.
            if (cash < _cashBefore)
            {
                Bought(me, cash);
                return;
            }

            _logger.LogInformation($"RESUPPLY: purchase not accepted ({(_tradeDeclined ? "declined" : "no answer")})" +
                                   $"{(_lastFeedback != null ? $" — '{_lastFeedback}'" : "")}.");
            Failed(me);
        }
    }

    private void Bought(LocalPlayer me, int cashNow)
    {
        var offer = _offer;
        if (offer == null)
        {
            return; // cannot happen while a purchase is in flight; keeps the null flow honest
        }

        var spent = _cashBefore - cashNow;
        _bought[_kind] += _count;
        _retryCount = 0;
        var priceNote = "";
        if (spent > 0)
        {
            var each = spent / (double)_count;
            priceNote = $" for {spent} credits ({each:0} each)";
            if (ItemValues.TryGet(offer.Item.Id, offer.Item.HighId, offer.Item.Ql, out var value) && value > 0)
            {
                var computed = Price(value, _machine, out var from, corrected: false);
                Mem().Corrections[MachineKey(_machine)] = each / computed;
                Save(Mem());
                _logger.LogInformation($"RESUPPLY: '{_machineName}' paid {each:0} each, computed {computed} " +
                                       $"({from}, Value {value}, CL {Cl()}) — correction x{each / computed:0.#####}.");
            }
        }

        _logger.LogInformation($"RESUPPLY: bought {_count}x {offer.Item.Name} QL{offer.Item.Ql}{priceNote}; cash {cashNow}. " +
                               $"Now {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers.");
        Tell($"Bought {_count}x {offer.Item.Name} QL{offer.Item.Ql}{priceNote}.");
        _offer = null;

        if (Needs().Count == 0)
        {
            Finish(me, _containerBuy > 0
                ? $"Bags bought: carrying {Have(Supply.Container)} now, {cashNow} credits left."
                : $"Resupplied: {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers, {cashNow} credits left.");
            return;
        }

        // Something else is still short — this machine may sell it too (it's first in line if it does).
        RebuildCandidates(me);
        NextMachine(me);
    }

    // The server didn't take the buy. With the terminal's markup known the price was right and we
    // checked the money — so it's not money, give up on this machine. Priced at bare Value, it most
    // likely is: try again at half the count, down to one.
    private void Failed(LocalPlayer me)
    {
        CloseShop();
        var offer = _offer;
        if (offer == null)
        {
            return; // cannot happen while a purchase is in flight; keeps the null flow honest
        }

        var priced = PriceEach(offer.Item, out var from) > 0 && from != BareValue;
        me.TryGetStat(Stat.Cash, out var cash);
        if (!priced && _count > 1)
        {
            _retryCount = _count / 2;
            _logger.LogInformation($"RESUPPLY: retrying with {_retryCount}.");
            _candidates.Insert(0, _machine);
            NextMachine(me);
            return;
        }

        if (!priced)
        {
            _waitEstimated = false;
            WaitForMoney(me, cash, 0, 1, 0);
            return;
        }

        _logger.LogInformation($"RESUPPLY: '{_machineName}' refused the buy with the credits there — skipping it.");
        _retryCount = 0;
        NextMachine(me);
    }

    // Not enough credits. Tell the owner what we need, remind him every so often, and carry on by
    // ourselves the moment money arrives (his trade, a loot sale, anything that raises Cash).
    private void WaitForMoney(LocalPlayer me, int cash, int missing, int count, int price)
    {
        _cashAtWait = cash;
        _nagAccum = 0;
        _waitMissing = missing;
        _waitCount = count;
        _waitPrice = price;
        SetPhase(Phase.WaitMoney);
        Nag(cash);
    }

    private void Nag(int cash)
    {
        var item = $"{_offer!.Item.Name} QL{_offer.Item.Ql}"; // set by BeginPurchase before the wait
        _logger.LogInformation($"RESUPPLY: short of credits for {item} (cash {cash}, price {_waitPrice}) — asking the owner.");
        Tell(_waitPrice > 0
            ? $"I need {(_waitEstimated ? "about " : "")}{_waitMissing} more credits to buy {_waitCount}x {item} " +
              $"({(_waitEstimated ? "~" : "")}{_waitPrice} each, I have {cash}). Trade me some and I'll carry on — or send 'resupply stop'."
            : $"I can't afford a single {item} (I have {cash} credits" +
              $"{(_lastFeedback != null ? $"; shop said: {_lastFeedback}" : "")}). Trade me some credits and I'll carry on — or send 'resupply stop'.");
    }

    private void WaitMoneyTick(LocalPlayer me, double dt)
    {
        me.TryGetStat(Stat.Cash, out var cash);
        if (cash > _cashAtWait)
        {
            _logger.LogInformation($"RESUPPLY: cash {_cashAtWait} -> {cash}, trying again.");
            Tell($"Got it, {cash} credits now — buying.");
            _retryCount = 0;
            _candidates.Insert(0, _machine);
            NextMachine(me);
            return;
        }

        if (cash < _cashAtWait)
        {
            _cashAtWait = cash;
        }

        _nagAccum += dt;
        if (_nagAccum >= _config.ResupplyNagSeconds)
        {
            _nagAccum = 0;
            Nag(cash);
        }
    }

    // The owner-trade answers, on every tick whether a run is active or not: his accept is answered
    // a moment later with our Accept, our Confirm follows a moment after that.
    private void OwnerTick(double dt)
    {
        if (!_ownerTrade || _ownerStep is not (OwnerStep.Accept or OwnerStep.Confirm))
        {
            return;
        }

        _ownerWait += dt;
        if (_ownerWait < (_ownerStep == OwnerStep.Accept ? OwnerAcceptDelay : OwnerConfirmDelay))
        {
            return;
        }

        _ownerWait = 0;
        var owner = Trade.CurrentTarget;
        var action = _ownerStep == OwnerStep.Accept ? TradeAction.Accept : TradeAction.Confirm;
        _logger.LogInformation($"RESUPPLY: owner trade — {action}.");
        Client.Send(new TradeMessage
        { Version = 2, Action = action, Param1 = (int)owner.Type, Param2 = owner.Instance });
        // After Confirm we're done (until he changes the offer); the server finishes once he confirms too.
        _ownerStep = _ownerStep == OwnerStep.Accept ? OwnerStep.Confirm : OwnerStep.Done;
    }

    // ---- Machines -----------------------------------------------------------

    // Terminals in reach, best first: ones we've seen sell what we need, then ones whose name
    // suggests medical supplies, then the rest by distance. Ones we've seen sell neither are left
    // out. Returns the ordered identities.
    private List<Identity> BuildCandidates(LocalPlayer me, List<Supply> needs)
    {
        var at = me.Transform.Position;
        var ranked = new List<(Identity id, int rank, float dist)>();
        foreach (var vm in DynelManager.VendingMachines)
        {
            var d = Vector3.Distance(at, vm.Transform.Position);
            if (d > _config.ResupplySearchRadius)
            {
                continue;
            }

            int rank;
            var known = Mem().Machines.TryGetValue(MachineKey(vm.Identity), out var sold);
            if (known)
            {
                if (!needs.Any(n => sold!.Split(',').Contains(n.ToString())))
                {
                    continue; // remembered as selling none of what we need
                }

                rank = 0;
            }
            else
            {
                rank = NameSuggestsSupplies(vm.Name) ? 1 : 2;
            }

            ranked.Add((vm.Identity, rank, d));
        }

        return ranked.OrderBy(r => r.rank).ThenBy(r => r.dist).Select(r => r.id).ToList();
    }

    private void RebuildCandidates(LocalPlayer me)
    {
        _candidates.Clear();
        _candidates.AddRange(BuildCandidates(me, Needs()));
    }

    private void NextMachine(LocalPlayer me)
    {
        _stock = null;
        _offer = null;
        _arrivedAt = -1;
        if (_candidates.Count == 0)
        {
            var left = Needs();
            if (left.Count > 0)
            {
                var travel = PlanShopTravel(out var line);
                if (travel == ShopTravel.Travelled)
                {
                    Tell($"Nothing more in reach here - heading for {Zoning.Name(_config.ResupplyShopPf)}. {line}");
                    return; // the Transit phase owns the body from here
                }
            }

            Finish(me, left.Count == 0
                ? "Resupplied."
                : $"Couldn't find a terminal here selling {string.Join(" or ", left.Select(Plural))} I can use." +
                  (_containerBuy > 0
                      ? ""
                      : $" Have {Have(Supply.Stim)} stims, {Have(Supply.Recharger)} rechargers."));
            return;
        }

        if (++_opens > MaxOpens)
        {
            Finish(me, $"Gave up after {MaxOpens} terminal visits.");
            return;
        }

        _machine = _candidates[0];
        _candidates.RemoveAt(0);
        if (!DynelManager.Find(_machine, out VendingMachine vm))
        {
            _logger.LogInformation($"RESUPPLY: terminal {_machine} vanished before the walk — skipping it.");
            NextMachine(me); // _opens still ticks, so this cannot recurse forever
            return;
        }

        _machineName = string.IsNullOrEmpty(vm.Name) ? _machine.ToString() : vm.Name;
        // The approach is a movement goal at this controller's priority: the routed walk takes the
        // body there (doors and zone lines avoided), higher-priority goals still preempt it, and
        // the reached flag is ours to poll.
        _movement.SetDesiredGoal(vm.Transform.Position, _runPf, ControlPriority.Resupply, _config.ResupplyUseRange);
        _logger.LogInformation($"RESUPPLY: next terminal '{_machineName}' ({_machine}), " +
                               $"{_candidates.Count} left after it.");
        SetPhase(Phase.Approach);
    }

    private void Open(LocalPlayer me)
    {
        _stock = null;
        _lastFeedback = null;
        _tradeDone = false;
        _tradeDeclined = false;
        GameCommands.UseObject(me, _machine);
        _logger.LogInformation($"RESUPPLY: opening '{_machineName}' ({_machine}).");
        SetPhase(Phase.Opening);
    }

    private void CloseShop()
    {
        SendTrade(TradeAction.Decline, 0, 0);
    }

    private void SendTrade(TradeAction action, int p3, int p4)
    {
        Client.Send(new TradeMessage
        {
            Version = 2,
            Action = action,
            Param1 = (int)_machine.Type,
            Param2 = _machine.Instance,
            Param3 = p3,
            Param4 = p4,
        });
    }

    private void Finish(LocalPlayer me, string text)
    {
        _logger.LogInformation("RESUPPLY: " + text);
        Tell(text);
        TearDown();
    }

    // Hand the body and the arbiter back: the run's goal goes (the walk holds, follow may resume),
    // a still-open trip to the shop playfield is cancelled with it ('resupply stop' must not leave
    // the walk running; the owner's 'stop' clears everything anyway), and the arbiter returns to
    // whoever is next.
    private void TearDown()
    {
        _controlArbiter.ReleaseControl();
        _movement.CancelTravel();
        _movement.ClearDesiredGoal(ControlPriority.Resupply);
        Reset();
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        _phaseTime = 0;
        _candidates.Clear();
        _machine = Identity.None;
        _machineName = "";
        _stock = null;
        _offer = null;
        _retryCount = 0;
        _arrivedAt = -1;
        _transitAt = -1;
        _travelPlanned = false;
        _runPf = -1;
        _containerBuy = 0;
    }

    private void SetPhase(Phase p)
    {
        _phase = p;
        _phaseTime = 0;
        _arrivedAt = -1;
        _transitAt = -1;
    }

    // For 'resupply machines': every terminal within the search radius, nearest first.
    private List<string> DescribeMachines(LocalPlayer me)
    {
        var mem = Mem();
        var at = me.Transform.Position;
        var lines = new List<string>();
        foreach (var vm in DynelManager.VendingMachines.OrderBy(x => Vector3.Distance(at, x.Transform.Position)))
        {
            var d = Vector3.Distance(at, vm.Transform.Position);
            if (d > _config.ResupplySearchRadius)
            {
                break;
            }

            var key = MachineKey(vm.Identity);
            var sells = mem.Machines.TryGetValue(key, out var s) ? (s.Length == 0 ? "sells neither" : "sells " + s) : "unchecked";
            var mult = mem.Corrections.TryGetValue(key, out var m) ? $", price correction x{m:0.#####}" : "";
            var line = $"'{vm.Name ?? "?"}' {d:0}m {Modifiers(vm.Stats)} ({sells}{mult})";
            lines.Add(line);
            _logger.LogInformation($"RESUPPLY MACHINES {vm.Identity} {line} | stats: " +
                                   string.Join(" ", (vm.Stats ?? new Dictionary<Stat, int>()).Select(kv => $"{kv.Key}={kv.Value}")));
        }

        return lines;
    }

    // A stock line as an item (name, QL-interpolated requirements). A line whose low/high templates
    // don't line up in the item data throws in the SDK's interpolation; it can't be a stim we'd buy.
    private static Item? StockItem(VendingMachineSlot s)
    {
        try
        {
            return new Item(s.ItemLowId, s.ItemHighId, s.Quality);
        }
        catch
        {
            return null;
        }
    }

    // The stock the shop window opened with, one line per entry with its price where the data
    // knows one - the record the sells-memory and the price corrections are checked against.
    private void LogShopUpdate(ShopUpdateMessage shop)
    {
        VendingMachineSlot[] lines = shop.VendingMachineSlots ?? Array.Empty<VendingMachineSlot>();
        var name = DynelManager.Find(shop.Identity, out VendingMachine vm) && !string.IsNullOrEmpty(vm.Name)
            ? vm.Name
            : "?";
        _logger.LogInformation($"RESUPPLY SHOPUPDATE {shop.Identity} '{name}' " +
                               $"{(vm?.Stats != null ? Modifiers(vm.Stats) : "")} CL={Cl()} — {lines.Length} line(s):");
        for (var i = 0; i < lines.Length; i++)
        {
            var s = lines[i];
            var it = StockItem(s);
            var valued = ItemValues.TryGet(s.ItemLowId, s.ItemHighId, s.Quality, out var v);
            var price = valued ? Price(v, shop.Identity, out _) : 0;
            _logger.LogInformation($"RESUPPLY SHOPUPDATE   [{i}] low={s.ItemLowId} high={s.ItemHighId} QL{s.Quality} " +
                                   $"'{it?.Name ?? "?"}' value={(valued ? v.ToString() : "?")}" +
                                   (price > 0 ? $" price={price}" : ""));
        }
    }

    // The terminal's markups, from its template (StaticInstance) - its own stats don't carry them.
    private static string Modifiers(Dictionary<Stat, int>? stats)
    {
        var tmpl = 0;
        if (stats != null && stats.TryGetValue(Stat.StaticInstance, out var t))
        {
            tmpl = t;
        }

        return tmpl != 0 && ItemValues.TryGetShopModifiers(tmpl, out var sm, out var bm)
            ? $"template {tmpl} SellModifier={sm} BuyModifier={bm}"
            : $"template {(tmpl != 0 ? tmpl.ToString() : "?")} SellModifier=- BuyModifier=-";
    }

    // ---- Supplies -----------------------------------------------------------

    // By EXACT name: a keyword like "Stim" also matches Boosted Stim, Burst of Speed Stim, Swim
    // Stim... — the pharmacy sells all of them, and AOBuddy10's first run bought 25 Boosted Stims
    // as "stims".
    private bool Is(Item it, Supply s)
    {
        return it?.Name != null &&
               string.Equals(it.Name, s switch
               {
                   Supply.Stim => _config.ResupplyStimName,
                   Supply.Recharger => _config.ResupplyRechargerName,
                   Supply.Lockpick => _config.ResupplyLockpickName,
                   _ => _config.ResupplyContainerName,
               }, StringComparison.OrdinalIgnoreCase);
    }

    // How many we carry that we can use: every stack of the item (main inventory and open bags)
    // whose First Aid / Treatment requirement we meet, by stack count. Bags are counted as what
    // they are - every container in the main inventory - whatever it is named.
    private int Have(Supply s)
    {
        if (s == Supply.Container)
        {
            return Inventory.Items?.Count(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                                               i.UniqueIdentity.Type == IdentityType.Container) ?? 0;
        }

        var me = DynelManager.LocalPlayer;
        var items = HealItems.AllInvItems();
        // the lockpick's gate is its own use req (break/entry), not the heal kit's First Aid
        return items.Where(it => Is(it, s) &&
                                 (s == Supply.Lockpick ? it.MeetsUseReqs(me, false) : HealItems.MeetsHealReqs(it, me)))
            .Sum(it => Math.Max(1, it.Count));
    }

    private int Want(Supply s)
    {
        if (s == Supply.Container)
        {
            return _haveAtStart.TryGetValue(Supply.Container, out var had) ? had + _containerBuy : _containerBuy;
        }

        return s switch
        {
            Supply.Stim => _config.ResupplyStimTarget,
            Supply.Recharger => _config.ResupplyRechargerTarget,
            Supply.Lockpick => _config.ResupplyLockpickTarget,
            _ => 0,
        };
    }

    // Still to buy this run: what the inventory says is missing, but never more than the run set
    // out to buy — so a purchase the inventory model misses can't make us buy the same stims over
    // and over.
    private int Remaining(Supply s)
    {
        var byInventory = Want(s) - Have(s);
        if (!_haveAtStart.TryGetValue(s, out var start))
        {
            return Math.Max(0, byInventory);
        }

        return Math.Max(0, Math.Min(byInventory, Want(s) - start - _bought[s]));
    }

    private List<Supply> Needs()
    {
        return new[] { Supply.Stim, Supply.Recharger, Supply.Container, Supply.Lockpick }.Where(s => Remaining(s) > 0).ToList();
    }

    private static string Plural(Supply s)
    {
        return s switch
        {
            Supply.Stim => "stims",
            Supply.Recharger => "rechargers",
            Supply.Lockpick => "lockpicks",
            _ => "bags",
        };
    }

    // ---- Prices and memory ------------------------------------------------------

    private static string MachineKey(Identity id)
    {
        return $"{(int)Playfield.ModelId}:{id.Instance}";
    }

    private const string BareValue = "bare Value, terminal markup unknown";

    // What one of these costs at the current terminal. 0 = no Value for it.
    private int PriceEach(Item it, out string from)
    {
        from = "";
        if (!ItemValues.TryGet(it.Id, it.HighId, it.Ql, out var value) || value <= 0)
        {
            return 0;
        }

        return Price(value, _machine, out from);
    }

    // Value x the terminal template's SellModifier x our CL discount (ItemValues.ShopPrice), times
    // the correction learned from an earlier purchase at this terminal. `from` says what went into it.
    private int Price(int value, Identity machine, out string from, bool corrected = true)
    {
        int price;
        if (DynelManager.Find(machine, out VendingMachine vm) && vm.Stats != null &&
            vm.Stats.TryGetValue(Stat.StaticInstance, out var tmpl) &&
            ItemValues.TryGetShopModifiers(tmpl, out var sell, out _) && sell > 0)
        {
            price = ItemValues.ShopPrice(value, sell, Cl());
            from = $"SellModifier {sell}, CL discount {ItemValues.ClDiscount(Cl()):0.##}";
        }
        else
        {
            price = value;
            from = BareValue;
        }

        if (corrected && Mem().Corrections.TryGetValue(MachineKey(machine), out var c) && c > 0 && Math.Abs(c - 1.0) > 0.001)
        {
            price = (int)Math.Ceiling(price * c);
            from += $", corrected x{c:0.####}";
        }

        return price;
    }

    private static int Cl()
    {
        return DynelManager.LocalPlayer != null && DynelManager.LocalPlayer.TryGetStat(Stat.ComputerLiteracy, out var cl) ? cl : 0;
    }

    private void Remember(Identity machine, List<Supply> sells)
    {
        var mem = Mem();
        mem.Machines[MachineKey(machine)] = string.Join(",", sells);
        Save(mem);
    }

    // The terminal memory, loaded from resupply.json on first use (JsonStore: a corrupt file is a
    // clean start, logged once).
    private Memory Mem()
    {
        if (_mem == null)
        {
            _mem = JsonStore.Load<Memory>(_file, s => _logger.LogInformation(s)) ?? new Memory();
        }

        return _mem;
    }

    private void Save(Memory mem)
    {
        JsonStore.Save(_file, Newtonsoft.Json.JsonConvert.SerializeObject(mem, Newtonsoft.Json.Formatting.Indented),
            s => _logger.LogInformation(s));
    }

    // The owner's dynel instance while he is in this playfield (he must be, to have opened a trade);
    // 0 when he isn't visible.
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
            _logger.LogInformation($"RESUPPLY (owner not reachable, tell not sent): {text}");
        }
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max];
    }
}