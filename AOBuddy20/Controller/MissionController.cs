// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MissionController.cs
//
// Last modified: 2026-10-09
// Created:       2026-09-30 00:09
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Components;
using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Interfaces;
using AOBuddy20.Nav;
using AOBuddy20.Network;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Storage;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     THE MISSION RUN - blitz mode, no combat (ported from AOBuddy10 MissionRoll/MissionRun/
///     MissionController): roll at a mission terminal, take a find-item or find-person mission,
///     travel to its door, walk in, select the target (the server completes the mission), pocket
///     the reward in a loot bag, walk out, and loop - until the owner stops it.
///
///     The wire, as the captures settle it (AOBuddy10 MISSION-MODE-PLAN.md):
///       - the terminal is Used once per session, then every roll is a QuestAlternative carrying
///         difficulty and the six sliders; the server answers with the same type holding 5 missions
///         (MissionIcon: 0x2C47 find person, 0x2C49 find item, ...), destination playfield and door
///         position, credits, XP and reward items;
///       - accepting is CreateQuest(mission identity); the server charges credits, puts the mission
///         key in the bags and sends QuestFullUpdate with a NEW quest identity;
///       - entering the building is purely positional: stand ON the door's exact coordinates and the
///         server moves you in - no packet, no key use (the key only rides along in the packs);
///       - the zone-in (PlayfieldAnarchyF) carries BuildingGeneratorData: NavData composes the
///         instance's rooms out of it and the movement walks the composed grid like any dungeon;
///       - the RAW QuestFullUpdate names the target; the typed one reads the quest LOG, which is
///         where held missions are deleted from;
///       - find item completes on one LookAt (ReturnInfo 0) on the floor item; find person on
///         InfoRequest + LookAt (ReturnInfo 1) on the NPC; completion arrives as CharacterAction
///         0x3B (MissionChanged) to the holder, or the quest's removal from the log;
///       - the reward is granted automatically: TemplateAction 87 puts it in the overflow window and
///         the SDK moves it to the next free inventory slot - the run only moves it on into a loot
///         bag. Leaving is walking: onto the building's exit doorway, stand, and the server moves
///         you out.
///
///     Control scheme: while the run is active it holds the ControlArbiter at
///     <see cref="ControlPriority.Mission" /> (below Resupply, above external buffing) and walks
///     through MovementController goals at the same priority - the routed grid walk, doors, zone
///     travel and every higher-priority preemption rule apply unchanged. Inside a building combat
///     is suppressed (blitz): BotLoop skips the combat brains while <see cref="SuppressCombat" />
///     holds; the heal keeps working. While the run waits on the SELL step (full bags) its Tick
///     answers false, so the decision chain falls through to the SellController.
///     The decision tick runs on the update thread (BotLoop); the same thread every packet handler
///     below fires on (the PacketRouter dispatches on Client.Update), so the state machine needs no
///     locks and no foreign-thread inputs.
///
///     THE WANT RUN (AOBuddy10, owner 2026-09-25, ported 2026-10-08): 'mission want' limits the
///     rolls to specific items - an exact name, or a query (kind, optionally a profession for
///     nanos, optionally a QL band, optionally a nano line). A want run accepts only missions
///     whose rewards are wanted, steers the roll difficulty toward the QL band it wants (the
///     mission QL follows level and difficulty; qlmap-&lt;character&gt;.json records the
///     mapping), and takes an ordinary mission once every WantRollCap rolls came up without one.
///     Every roll's rewards are counted in offered-&lt;character&gt;.json, so a wanted nano never
///     offered near its QL is dropped from the list as not a mission reward. Wanted items (and
///     nano crystals) never sell - the sell step keeps them (AOBuddy10's Bankable).
///
///     CLEAR MODE (AOBuddy10, owner 2026-09-25, ported 2026-10-09): 'mission run clear on' - kill
///     every mob in the building BEFORE the objective (the XP, and for Omni and Clan a side token
///     with the reward). The walk is this controller's: fight what is on us, close on the nearest
///     pull (one at a time), walk every room on the floor (rooms count on arrival, the person's
///     rooms last), ride buttons to floors with rooms left - and keep walking until the server's
///     share of dead mobs passes 90% (owner, 2026-10-09: the last run finished after 2-3 kills of
///     many). The swing is the combat brains': while <see cref="Clearing" /> holds,
///     <see cref="SuppressCombat" /> answers false and the brain engages what the run walks to
///     (attackers, then the pull pick); a pet class fights through its attack pet. The server
///     counts the clear for us - FormatFeedback 110/79979934, one float, the share of the
///     building's mobs dead (AOBuddy10 capture 20260925-110325) - and the run keeps its own count
///     of mobs seen/dead for sessions it never sends the line to. The person a find-person
///     mission sent us to is never an enemy - abstain from him even if he attacks while clearing.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class MissionController : IPacketConsumer
{
    private enum Phase { Idle, ToTerminal, Rolling, AwaitList, Accepting, ToDoor, EnterDoor, Blitz, PickDoor, RewardBag, Leaving, WaitingSell }

    // Mission types (the terminal list's MissionIcon codes).
    public const int TypeFindPerson = 0x2C47;
    public const int TypeFindItem = 0x2C49;
    public const int TypeReturnItem = 0x2C41;
    public const int TypeKillPerson = 0x2C42;
    public const int TypeRepair = 0x2C4E;

    // The elevator buttons of a mission building (client item table templates, AOBuddy10).
    private const int ButtonLowest = 159862;
    private const int ButtonHighest = 159869;

    // CharacterAction 0x3B: the MissionChanged the holder gets on completion (not in the SDK enum).
    private const int MissionChangedAction = 0x3B;

    // AOBuddy10's proven timings.
    private const double ListTimeout = 6.0; // no list in this long: roll again
    private const double AcceptSettle = 2.0; // the server charges credits; give it a beat
    private const double EnterApproach = 5f; // stand this far out on a door side first
    private const double EnterSideTimeout = 30.0; // an approach that never gets there
    private const double DoorStandTimeout = 8.0; // on the door this long without a zone: next side
    private const double DoorSettle = 0.6; // stand still before judging the door
    private const double DoorUseEverySec = 3.0; // the entrance Use repeats this often while standing
    private const float DoorUseRadius = 5f; // the IdentityType.Door dynel this close to the spot is the entrance

    // LOCKED DOORS (owner, 2026-10-09): shut doors want the lockpick from the packs, used ON the
    // door - with the skill for it one of the first tries lands, but it is a bit of random, so
    // twenty tries before the door is called a lost cause.
    private const int DoorPickTries = 20;
    private const double DoorPickEverySec = 2.0; // one pick attempt per this (the use takes a beat server-side)
    private const double DoorPickApproachTimeout = 25.0; // the walk to the shut door that never got there
    private const float DoorPickStandoff = 2.0f; // stop this far short of the door (its leaf bounces the body)
    private const double SearchHopTimeout = 25.0; // a room walk this long without arriving: next room
    private const int DoorSides = 8; // 45° ladder, this many sides per round
    private const int DoorRounds = 2; // and this many rounds, then the door is given up
    private const double BlitzTimeout = 1200.0; // 20 minutes inside, then the mission is dropped
    private const double RecordWait = 10.0; // the quest record after the zone-in
    private const double ActTimeout = 5.0; // no completion after selecting: select again
    private const int MaxActs = 3;
    private const double RewardSettle = 1.5; // wait for the reward burst before touching the bags
    private const double BagRefuseSeconds = 2.0; // the item still in the inventory this long: the bag is full
    private const double BagOpenBeat = 1.0; // after opening a bag, this long for its contents to land
    private const double RewardTimeout = 60.0; // stashing that won't finish: leave and keep the reward
    private const double ExitDoorStand = 3.0; // standing on the exit door before pushing a metre out
    private const double ExitStageTimeout = 16.0; // one leaving stage that never lands
    private const int MaxExitStands = 3;
    private const double TerminalTimeout = 600.0; // the whole trip back to the terminal
    private const double SellTimeout = 660.0; // waiting on the sell run (it may travel to the shop)
    private const int MaxRollsWarn = 20; // unproductive rolls in a row before the owner is warned
    private const float SearchReach = 3f; // room-centre goals
    private const float TargetReach = 2f; // the last metres to the target
    private const float ButtonReach = 1.5f;
    private const float DoorThroughMetres = 3.5f; // the door-check hop: this far past the door
    private const float DoorCheckReach = 1.5f;
    private const float TerminalReach = 0.5f; // the terminal walk: 4 m approach offset + this must stay inside MissionTerminalRadius
    private const int MaxPresses = 12; // button presses per building
    private const double RideWait = 4.0; // a button ride moves us more than RideMetres, in this long
    private const float RideMetres = 10f;
    private const float FloorBand = 6f; // a seen object within this Y of us is on our floor

    private readonly ILogger<MissionController> _logger;
    private readonly MovementController _movement;
    private readonly ControlArbiter _controlArbiter;
    private readonly ResupplyController _resupply;
    private readonly SellController _sell;
    private readonly LootBagStore _lootBags;
    private readonly AccountInfo _config;
    private readonly Awareness _awareness; // the monster picture the clear mode fights by

    private Phase _phase = Phase.Idle;
    private double _phaseTime;
    private uint _tellId; // the owner's chat id, proven once by a command tell

    // The terminal (missionterminal.json beside the executable, AOBuddy10's shape).
    private SavedTerminal _savedTerminal;
    private readonly HashSet<Identity> _usedTerminals = new();
    private Identity _terminal = Identity.None;
    private string _terminalName = "";

    // The list and the mission in hand.
    private readonly List<MissionInfo> _offered = new();
    private MissionInfo _current;
    private readonly List<(int low, int high)> _rewardIds = new();
    private int _emptyRolls;

    // Held terminal missions, from the quest log (QuestFullUpdate): the resume source and the
    // deletion list. The login log carries the whole mission back - the typed Quest has identity,
    // type code and reward items, the raw log has the door position - and the mission key in the
    // packs names the building (AOBuddy10's resume: capture 20260923-201746).
    private readonly Dictionary<Identity, Quest> _heldQuests = new();

    // The raw bytes of the two packets the typed model can't answer (the zone-in carries the
    // building layout and the return playfield; the raw QuestFullUpdate carries the target).
    // Captured through the PacketRouter (every AOMessage carries its RawPacket), consumed on the
    // same update thread - plain fields, no locks.
    private byte[] _zoneInRaw;
    private byte[] _questRaw;

    // The server's doors (DoorFullUpdate): exact world positions and the two rooms each connects
    // (Room indexes the packet's room table, 1-based; -1 = the door to the outside - the EXIT).
    // Everything the template decode approximates, these state outright - and they ALL land in
    // the zone-in burst, collected by playfield from the first packet on: a guard that waited
    // for the composed layout dropped the whole burst in the Tick's blind window (owner,
    // 2026-10-03: AOBuddy10 had the whole playfield at the entrance - no magic, we were
    // throwing the doors away).
    private readonly List<(short room, short adjoining, Vector3 pos, int pf, uint flags)> _serverDoors = new();
    private readonly HashSet<long> _doorKeys = new(); // streamed once each: the same door re-streams on every approach
    private int _doorKeysPf = -1; // the building _doorKeys was built for
    private readonly Dictionary<int, Vector3> _serverExitByPf = new();
    private Vector3? _serverExit => _serverExitByPf.TryGetValue(_missionPf, out var v) ? v : null;
    private int _doorsApplied = -1;
    private bool _navHandedOver;
    private double _holdAccum;
    private double _lastDoorApply;

    // Inside the building.
    private int _missionPf = -1;
    private AOBuddyNav _nav;
    private MissionRecord _record;
    private readonly Dictionary<Identity, SeenItem> _items = new();
    private readonly HashSet<(int floor, int room)> _searchedRooms = new();
    private int _presses;
    private bool _completed;

    // The room whose doors are being checked, and the doors already crossed for it. A template's
    // inner sections (its own doors - rooms.json 'doors' entries whose link is the room itself,
    // e.g. Mine_Lesser4_2 carries two) only stream their dynels once the body stands behind the
    // door, so the search walks every door of the room before it moves on (owner, 2026-10-03:
    // the item sat ~9 m from the centre, behind the inner door; the room was marked searched on
    // setting out, the target never streamed, and the run dropped "nothing left to search").
    private int _lastSearchRoom = -1;
    private readonly HashSet<long> _checkedDoors = new();

    // Goal bookkeeping (one goal at a time, ControlPriority.Mission).
    private bool _goalSet;
    private double _goalAt = -1;
    private string _goalWhat = "";
    private Vector3 _goalPos; // where _goalWhat points: a wandering pull moves, its label stays

    // The door approach (entering) and the exit push (leaving).
    private Vector3 _baseSide;
    private int _sideTry;
    private int _doorStage;
    private double _doorUseAt = double.NegativeInfinity; // the last entrance Use (the stand beat repeats it)
    private int _exitStands;

    // The lockpick beat (Phase.PickDoor): the shut door we stand at, the tries spent on it, and
    // the phase to resume when it opens (or honestly does not). _pickLost holds the doors the
    // beat already failed on (a re-pick gets three minutes' rest).
    private Phase _phaseBeforePick;
    private Vector3 _pickDoorPos;
    private long _pickDoorKey;
    private string _pickDoorLabel = "";
    private int _pickTries;
    private double _pickAt = double.NegativeInfinity;
    private readonly Dictionary<long, double> _pickLost = new();

    // The objective in progress.
    private int _acts;
    private Identity? _actTarget;
    private double _actedAt = -1;

    // The reward in flight to a bag.
    private Item _stashing;
    private double _stashAt;
    private Identity _stashBag;
    private Identity _bagJustOpened;
    private double _bagOpenedAt = -1;
    private readonly HashSet<Identity> _fullBags = new();

    // The button ride in flight.
    private Identity _pendingButton;
    private double _pressedAt = -1;
    private Vector3 _pressedFrom;

    // The sell step between runs.
    private double _sellWaitAt;

    // ---- WANT RUN (AOBuddy10, owner 2026-09-25) --------------------------------------------
    private WantList _wants;
    private bool _wantRun;
    private int _wantRolls;
    private const int WantRollCap = 300; // rolls in a row with nothing wanted before an ordinary mission is taken
    private const int DiffMin = 1, DiffMax = 11, NanoWindow = 8;
    private int? _wantDifficulty; // the difficulty the want run steered to; null = the owner's setting
    private int _lastDifficulty = -1; // what the last roll actually sent (the QL map's key half)
    private readonly HashSet<WantList.Entry> _unreachable = new();
    private Dictionary<string, int> _qlMapStore; // "level:difficulty" -> mission QL, qlmap-<character>.json
    private Dictionary<int, int> _offerTally; // template -> times offered, offered-<character>.json
    private Dictionary<int, int> _rollsAtQl; // mission QL -> rolls rolled at it
    private HashSet<int> _notRollable; // nano programs judged no mission reward (never offered near their QL)
    private int _offeredDirty;

    public bool Active => _phase != Phase.Idle;

    /// <summary>Inside a mission building: no combat - EXCEPT while clearing, where every fight
    /// is the job (the brains swing, the heal keeps preempting both). BotLoop skips the combat
    /// brains while this holds; the heal (stims, and the recharger rest between rooms) keeps
    /// working either way.</summary>
    public bool SuppressCombat =>
        _phase is Phase.RewardBag or Phase.Leaving || (_phase == Phase.Blitz && !Clearing);

    public MissionController(ILogger<MissionController> logger, MovementController movement,
        ControlArbiter controlArbiter, ResupplyController resupply, SellController sell,
        LootBagStore lootBags, AccountInfo config, Awareness awareness)
    {
        _logger = logger;
        _movement = movement;
        _controlArbiter = controlArbiter;
        _resupply = resupply;
        _sell = sell;
        _lootBags = lootBags;
        _config = config;
        _awareness = awareness;
        // Wanted items never sell: the reward lands in a loot bag and the sell step empties the
        // bags - without this the run would sell back what it just rolled for (AOBuddy10 kept
        // wanted items and nano crystals out of the sale with its Bankable rule).
        _sell.KeepFromSale = KeptFromSale;
        _savedTerminal = JsonStore.Load<SavedTerminal>(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "missionterminal.json"), s => _logger.LogInformation(s));
        if (_savedTerminal != null)
        {
            _logger.LogInformation($"MISSION: saved terminal pf {_savedTerminal.pf} {_savedTerminal.type}:{_savedTerminal.id}.");
        }

        _logger.LogInformation("Mission controller initialized.");
    }

    /// <summary>OwnerChat proves the owner's chat id once per tell; async replies use it.</summary>
    public void SetTellId(uint charId)
    {
        _tellId = charId;
    }

    // ---- Commands -----------------------------------------------------------

    /// <summary>The 'mission' command: run, stop, skip, status, roll, list, accept n, buybags n.</summary>
    public void Command(string[] parts, Action<string> reply)
    {
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            reply("No character loaded.");
            return;
        }

        switch (sub)
        {
            case "run":
                // 'mission run clear on|off' (AOBuddy10's flag): toggles clear mode and says so -
                // the run itself is a bare 'mission run', which keeps the mode on.
                if (parts.Length > 2 && parts[2].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    ToggleClearMode(parts.Length > 3 ? parts[3].ToLowerInvariant() : "", reply);
                    break;
                }

                // A bare 'mission run' is the ordinary loop again: the want run switches off and
                // the difficulty goes back to the owner's setting (AOBuddy10's 'mission run').
                _wantRun = false;
                _wantDifficulty = null;
                _unreachable.Clear();
                Start(me, reply);
                break;
            case "stop":
                if (Active)
                {
                    Stop("owner");
                    reply("Mission run stopped.");
                }
                else
                {
                    reply("Not on a mission run.");
                }

                break;
            case "status":
                reply("Mission: " + Describe());
                break;
            case "clear":
                // 'mission clear on|off' (owner 2026-10-09: he typed the natural spelling and the
                // run blitzed on - this used to fall into the abandon below): the CLEAR MODE
                // toggle, same as 'mission run clear on|off'.
                if (parts.Length > 2 && (parts[2].Equals("on", StringComparison.OrdinalIgnoreCase) ||
                                         parts[2].Equals("off", StringComparison.OrdinalIgnoreCase)))
                {
                    ToggleClearMode(parts[2].ToLowerInvariant(), reply);
                    break;
                }

                // Bare 'mission clear': the run stops AND the mission is deleted - abandoned, no
                // re-roll (that is 'skip'), no resume (that is 'stop').
                if (_heldQuests.Count == 0 && !Active)
                {
                    reply("No mission to clear. ('mission clear on|off' toggles clear mode.)");
                    break;
                }

                if (Active)
                {
                    Stop("owner clear");
                }

                if (_heldQuests.Count > 0)
                {
                    DeleteHeld("owner clear");
                    reply("Mission cancelled and deleted.");
                }
                else
                {
                    reply("Mission run stopped - nothing held to delete.");
                }

                break;
            case "skip":
                // AOBuddy10's 'mission run skip': the held mission is deleted and a fresh one is rolled.
                if (_heldQuests.Count == 0)
                {
                    reply("No held mission to skip.");
                    break;
                }

                if (Active)
                {
                    DropMission("owner skip");
                }
                else
                {
                    DeleteHeld("owner skip");
                    reply("Held mission deleted.");
                }

                break;
            case "roll":
                RollHere(me, reply);
                break;
            case "want":
                WantCommand(parts, me, reply);
                break;
            case "list":
                reply(_offered.Count == 0
                    ? "No mission list yet - 'mission roll' at a terminal."
                    : string.Join(" | ", _offered.Select((m, i) =>
                        $"{i + 1}) {Line(m)}{(Allowed(m, out var why) ? "" : $" [skip: {why}]")}")));
                break;
            case "accept":
                if (parts.Length < 3 || !int.TryParse(parts[2], out var n) || n < 1 || n > _offered.Count)
                {
                    reply($"Usage: mission accept <1-{Math.Max(_offered.Count, 5)}> (from the last roll)");
                    return;
                }

                Accept(_offered[n - 1], reply);
                break;
            case "buybags":
            {
                if (parts.Length < 3 || !int.TryParse(parts[2], out var bags) || bags < 1 || bags > 20)
                {
                    reply("Usage: mission buybags <1-20> - I go to the shop playfield and buy that many bags.");
                    return;
                }

                if (Active)
                {
                    Stop("buybags");
                    reply("Mission run stopped - buying bags.");
                }

                _resupply.StartContainers(me, bags, reply);
                break;
            }
            case "probe":
            {
                // What the composed building says stands around a point - the wedge question
                // ("is there a wall just east of me?") answered from the placed walls themselves.
                var px = me.Transform.Position.X;
                var pz = me.Transform.Position.Z;
                var py = me.Transform.Position.Y;
                if (parts.Length >= 4 && float.TryParse(parts[2], out var qx) && float.TryParse(parts[3], out var qz))
                {
                    px = qx;
                    pz = qz;
                }

                reply(Probe(px, py, pz));
                break;
            }
            default:
                reply("Usage: mission run | stop | clear [on|off] | skip | status | probe x z | roll | list | accept n | buybags n | " +
                      "want [add <name or query> | remove n | list | mode always|list | status | lines [part] | " +
                      "drop|undrop <nano name> | clear got]");
                break;
        }
    }

    private string Describe()
    {
        var mission = _current == null
            ? "no mission in hand"
            : $"{TypeName(_current.MissionIcon)} in {Zoning.Name(_current.Playfield.Instance)} " +
              $"({_current.Location.X:0},{_current.Location.Z:0})";
        return _phase switch
        {
            Phase.Idle => "idle. " + mission + ". Terminal: " + (_savedTerminal != null
                ? $"pf {_savedTerminal.pf} ({_savedTerminal.x:0},{_savedTerminal.z:0})"
                : "none saved"),
            Phase.Blitz => "blitz" + (ClearOn ? $" [clear: {ClearText}]" : "") + RecordText() + ": " + _goalWhat,
            Phase.WaitingSell => "bags full - waiting on the sell run, then back to the terminal.",
            _ => $"{_phase}: {_goalWhat}; {mission}",
        };
    }

    private string RecordText()
    {
        return _record == null ? "" : $" ({TypeName(_record.Type)}, target {(_record.TargetA?.ToString() ?? "by name")})";
    }

    // One roll, by hand, at the terminal we stand at - the list lands in 'mission list'. No run.
    private void RollHere(LocalPlayer me, Action<string> reply)
    {
        var here = TerminalWithin(me, _config.MissionTerminalRadius);
        if (here == null)
        {
            reply($"No mission terminal within {_config.MissionTerminalRadius:0} m.");
            return;
        }

        TakeTerminal(here);
        if (!_usedTerminals.Contains(_terminal))
        {
            _usedTerminals.Add(_terminal);
            GameCommands.UseObject(me, _terminal);
            _logger.LogInformation($"MISSION: used terminal {_terminal} ('{_terminalName}').");
        }

        SendRoll();
        reply("Rolling - the list lands with 'mission list'.");
    }

    // ---- Want list commands ------------------------------------------------------

    // 'mission want ...' (AOBuddy10's 'mission run want'): edit the list, or bare 'mission want'
    // starts a want run - from the next roll when one is on, otherwise it starts the run.
    private void WantCommand(string[] parts, LocalPlayer me, Action<string> reply)
    {
        var w = Wants;
        w.Reload();
        var rest = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : "";
        if (rest.StartsWith("add "))
        {
            var e = WantList.Parse(rest.Substring(4));
            if (e == null)
            {
                reply("want add <exact item name> | want add nano engineer ql 20-30 | want add implant ql 200+ | want add ncu ql 30-45");
                return;
            }

            w.Entries.Add(e);
            w.Save();
            reply($"Wanting {e}{WantCount(e)}.");
            return;
        }

        if (rest.StartsWith("remove "))
        {
            if (int.TryParse(rest.Substring(7).Trim(), out var k) && k >= 1 && k <= w.Entries.Count)
            {
                var e = w.Entries[k - 1];
                w.Entries.RemoveAt(k - 1);
                w.Save();
                reply($"Removed {e}.");
            }
            else
            {
                reply("want remove <number from 'mission want list'>");
            }

            return;
        }

        if (rest == "list")
        {
            reply(w.Entries.Count == 0
                ? "The want list is empty."
                : $"Wants ({w.Mode}): " + string.Join("; ", w.Entries.Select((e, k) => $"{k + 1}) {e}")));
            return;
        }

        if (rest.StartsWith("mode "))
        {
            var md = rest.Substring(5).Trim();
            if (md == "always" || md == "list")
            {
                w.Mode = md;
                w.Save();
                reply($"Want mode: {md}.");
            }
            else
            {
                reply("want mode always|list");
            }

            return;
        }

        if (rest == "status")
        {
            reply(WantStatus());
            return;
        }

        if (rest == "lines" || rest.StartsWith("lines "))
        {
            var ls = WantList.LineNames(rest.Length > 6 ? rest.Substring(6) : "").ToList();
            reply(ls.Count == 0
                ? "No nano line like that."
                : $"{ls.Count} nano line(s): " + string.Join(", ", ls.Take(40)) + (ls.Count > 40 ? " ..." : ""));
            return;
        }

        if (rest.StartsWith("drop ") || rest.StartsWith("undrop "))
        {
            var drop = rest.StartsWith("drop ");
            var nm = rest.Substring(drop ? 5 : 7).Trim();
            var hits = WantData.Crystals
                .Where(kv => WantList.NameOf(kv.Key)?.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(kv => kv.Value).Distinct().ToList();
            if (hits.Count == 0)
            {
                reply($"No nano crystal named like '{nm}'.");
                return;
            }

            LoadOffered();
            foreach (var n in hits)
            {
                if (drop)
                {
                    _notRollable.Add(n);
                }
                else
                {
                    _notRollable.Remove(n);
                }
            }

            SaveOffered();
            reply($"{(drop ? "Not rollable" : "Rollable again")}: {nm} ({hits.Count} nano{(hits.Count == 1 ? "" : "s")}).");
            return;
        }

        if (rest == "clear got")
        {
            w.Got.Clear();
            w.Save();
            reply("Forgot what the want runs collected.");
            return;
        }

        if (rest.Length > 0)
        {
            reply("mission want | want add <name or query, e.g. nano engi line pet ql 20-60> | want remove <n> | " +
                  "want list | want lines [part] | want mode always|list | want status | want drop|undrop <nano name> | want clear got");
            return;
        }

        // Bare 'mission want': the run rolls for the list from now on. Already running: from the
        // next roll; idle: start the run here (AOBuddy10 fell through to its run start).
        if (w.Entries.Count == 0)
        {
            reply("The want list is empty: 'mission want add ...' first.");
            return;
        }

        _wantRun = true;
        _wantRolls = 0;
        _unreachable.Clear();
        WantAim();
        if (Active)
        {
            reply("Rolling for the want list from the next roll. " + WantStatus());
        }
        else
        {
            Start(me, reply);
            if (Active)
            {
                Tell(WantStatus());
            }
        }
    }

    // ---- Run lifecycle -------------------------------------------------------

    private void Start(LocalPlayer me, Action<string> reply)
    {
        if (Active)
        {
            reply("Already on a mission run — " + Describe());
            return;
        }

        // LOOT BAGS (step 1): the run's whole point is filling them. Without one the rewards pile
        // up in the packs and the run says so once, loudly - it does not stop.
        if (LootBagsOnMe().Count == 0)
        {
            reply(_lootBags.Bags.Count == 0
                ? "No loot bags designated - loot will pile up in my packs. Send 'lootbag add N' ('lootbag list' shows the numbers)."
                : "My designated loot bag(s) are not in the packs right now - loot will pile up in the main inventory.");
        }

        // A mission taken before a restart is still in the quest log: finish it first (AOBuddy10's
        // Start). The login log carries the whole mission back - the typed Quest has identity, type
        // code and reward items, the raw log has the door position - and the mission key in the
        // packs names the building. Only the blitz TYPE is re-checked: the mission is already
        // accepted (and zone-checked at accept time); a held mission inside MissionZones' blind
        // spot must not read as deletable.
        var resumed = HeldMission();
        if (resumed != null && resumed.MissionIcon != 0 && !BlitzCan(resumed.MissionIcon))
        {
            // e.g. taken by hand and a kill person: blitz can't do it - delete it and roll fresh.
            Tell($"The mission I still hold ({Line(resumed)}) is not one I can blitz - deleting it.");
            DeleteHeld("not blitzable");
            resumed = null;
        }

        if (resumed != null)
        {
            _controlArbiter.TakeControl(ControlPriority.Mission);
            TakeCurrent(resumed);
            if (_nav != null)
            {
                // Logged in inside the building: the zone-in already composed the layout - finish it.
                SetPhase(Phase.Blitz);
                reply($"I'm inside the mission already: finishing it first ({KeysText()}).");
                return;
            }

            StartToDoor();
            reply($"First finishing the mission I already have: {Line(resumed)} ({KeysText()}).");
            return;
        }

        if (_nav != null)
        {
            // Logged in inside a mission with nothing left to do (done, or deleted elsewhere):
            // walk out first. NO zoning route exists FROM a mission instance pf - the terminal
            // travel below would dead-end in "nothing usable connects them" (owner, 2026-10-03).
            _controlArbiter.TakeControl(ControlPriority.Mission);
            _goalSet = false;
            _doorStage = 0;
            _exitStands = 0;
            SetPhase(Phase.Leaving);
            reply("I'm inside a mission building with nothing left to do here: walking out first.");
            return;
        }

        // STEP 1: the terminal - the one standing here, else the saved one, else the run says no.
        var here = TerminalWithin(me, _config.MissionTerminalRadius);
        if (here != null)
        {
            TakeTerminal(here);
            _controlArbiter.TakeControl(ControlPriority.Mission);
            SetPhase(Phase.Rolling);
            reply($"Mission run: rolling at '{here.Name}'. " + WhereIAm());
            return;
        }

        if (_savedTerminal == null || _savedTerminal.pf <= 0)
        {
            reply($"No mission terminal within {_config.MissionTerminalRadius:0} m and none saved in " +
                  "missionterminal.json - stand me next to one and send 'mission run' again.");
            return;
        }

        _controlArbiter.TakeControl(ControlPriority.Mission);
        SetPhase(Phase.ToTerminal);
        var line = _movement.PlanTravel(_savedTerminal.pf, null);
        if (_movement.TravelTargetPf != _savedTerminal.pf &&
            (int)Playfield.ModelId != _savedTerminal.pf)
        {
            Stop("no route to the terminal");
            reply($"Can't travel to the saved terminal ({Zoning.Name(_savedTerminal.pf)}): {Truncate(line, 200)}");
            return;
        }

        reply($"Mission run: heading for the saved terminal in {Zoning.Name(_savedTerminal.pf)}. {Truncate(line, 250)}");
    }

    // A terminal we just reached (or started at): remembered for the session, saved for the next.
    private void TakeTerminal(Dynel terminal)
    {
        _terminal = terminal.Identity;
        _terminalName = string.IsNullOrEmpty(terminal.Name) ? terminal.Identity.ToString() : terminal.Name;
        _usedTerminals.Add(_terminal);
        if (_savedTerminal != null && _savedTerminal.id == _terminal.Instance && _savedTerminal.pf == (int)Playfield.ModelId)
        {
            return;
        }

        _savedTerminal = new SavedTerminal
        {
            pf = (int)Playfield.ModelId,
            type = (int)_terminal.Type,
            id = _terminal.Instance,
            x = terminal.Transform.Position.X,
            y = terminal.Transform.Position.Y,
            z = terminal.Transform.Position.Z,
            fx = 0, // the facing is not read by this run (the approach goes from where we stand);
            fz = 0, // the fields stay for AOBuddy10's missionterminal.json shape
        };
        JsonStore.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "missionterminal.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(_savedTerminal, Newtonsoft.Json.Formatting.Indented),
            s => _logger.LogInformation(s));
        _logger.LogInformation($"MISSION: terminal '{_terminalName}' saved to missionterminal.json.");
    }

    private Dynel TerminalWithin(LocalPlayer me, float metres)
    {
        return DynelManager.AllDynels
            .Where(d => d != null && d.Identity.Type == IdentityType.MissionTerminal &&
                        Movement.Flat(me.Transform.Position, d.Transform.Position) <= metres)
            .OrderBy(d => Movement.Flat(me.Transform.Position, d.Transform.Position))
            .FirstOrDefault();
    }

    /// <summary>The ENTRANCE door dynel (IdentityType.Door) nearest the mission's door spot - the
    /// city doors are objects in the facade and want the Use; null when the zone streams none.</summary>
    private Dynel DoorWithin(Vector3 door, float metres)
    {
        return DynelManager.AllDynels
            .Where(d => d != null && d.Identity.Type == IdentityType.Door &&
                        Movement.Flat(door, d.Transform.Position) <= metres)
            .OrderBy(d => Movement.Flat(door, d.Transform.Position))
            .FirstOrDefault();
    }

    /// <summary>The door's state bits, wire-named: stat 0 carries 0x40 locked, 0x80 open
    /// (DoorFullUpdateMessage.Stats remarks).</summary>
    private static string DoorState(uint flags) =>
        (flags & 0x80) != 0 ? "OPEN" : (flags & 0x40) != 0 ? "LOCKED" : "shut";

    /// <summary>The stall log's verdict: the streamed door nearest the goal (within 8 m) and its
    /// state - a walk that cannot reach is usually standing at that door's closed leaf (owner,
    /// 2026-10-09, Subway - Ventil: 11 m yank storms at the shut first doorway).</summary>
    private string DoorVerdict(Vector3 goal)
    {
        var bestDist = double.MaxValue;
        (short room, short adjoining, Vector3 pos, uint flags) best = default;
        foreach (var d in _serverDoors)
        {
            if (d.pf != _missionPf)
            {
                continue;
            }

            var dist = Movement.Flat(goal, d.pos);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = (d.room, d.adjoining, d.pos, d.flags);
            }
        }

        return bestDist <= 8f
            ? $" - the goal sits {bestDist:0.0} m from door r{best.room}~r{best.adjoining} ({DoorState(best.flags)})"
            : "";
    }

    /// <summary>The door's physical identity (room pair + position, pf-less) - the dedupe key the
    /// stream handler uses, and the lockpick beat's "this door is a lost cause" key.</summary>
    private static long DoorKey(short room, short adjoining, Vector3 pos) =>
        ((long)(ushort)room << 40) | ((long)(ushort)adjoining << 24)
        | ((long)(ushort)(short)Math.Round(pos.X * 2) << 12) | (ushort)(short)Math.Round(pos.Z * 2);

    /// <summary>The streamed door of this building that is NOT open and stands within 8 m of the
    /// goal - the one the walk just bounced off. A door the lockpick beat already failed on sits
    /// out three minutes: the random does not turn kind on a re-pick.</summary>
    private (short room, short adjoining, Vector3 pos, uint flags)? ShutDoorNear(Vector3 goal)
    {
        var bestDist = double.MaxValue;
        (short room, short adjoining, Vector3 pos, uint flags)? best = null;
        var now = Environment.TickCount64 / 1000.0;
        foreach (var d in _serverDoors)
        {
            if (d.pf != _missionPf || (d.flags & 0x80) != 0)
            {
                continue; // another building's, or already open
            }

            if (_pickLost.TryGetValue(DoorKey(d.room, d.adjoining, d.pos), out var lostAt) && now - lostAt < 180)
            {
                continue;
            }

            var dist = Movement.Flat(goal, d.pos);
            if (dist <= 8f && dist < bestDist)
            {
                bestDist = dist;
                best = (d.room, d.adjoining, d.pos, d.flags);
            }
        }

        return best;
    }

    /// <summary>Whether the streamed door at this position re-streamed OPEN (the lockpick beat's
    /// success signal - the flags refresh on every re-stream).</summary>
    private bool DoorNowOpen(Vector3 doorPos)
    {
        foreach (var d in _serverDoors)
        {
            if (d.pf == _missionPf && Movement.Flat(d.pos, doorPos) < 1.5f)
            {
                return (d.flags & 0x80) != 0;
            }
        }

        return false;
    }

    /// <summary>The lockpick from the packs whose use req we meet - the mission doors' key.</summary>
    private Item FindLockpick(LocalPlayer me)
    {
        return HealItems.AllInvItems()
            .Where(it => it != null &&
                         string.Equals(it.Name, _config.ResupplyLockpickName, StringComparison.OrdinalIgnoreCase) &&
                         it.MeetsUseReqs(me, false))
            .OrderByDescending(it => it.Ql)
            .FirstOrDefault();
    }

    // ---- Phase: PickDoor (the lockpick beat) ---------------------------------------------
    // The walk bounced off a shut door; walk up to it short of the leaf and use the pick on the
    // door dynel until the re-stream says open - twenty tries, the open is a bit of random
    // (owner, 2026-10-09: with the break/entry skill for it one of the first lands).

    private void StartPicking((short room, short adjoining, Vector3 pos, uint flags) door)
    {
        _phaseBeforePick = _phase;
        _pickDoorPos = door.pos;
        _pickDoorKey = DoorKey(door.room, door.adjoining, door.pos);
        _pickDoorLabel = door.adjoining >= 0 ? $"r{door.room}~r{door.adjoining}" : $"r{door.room}";
        _pickTries = 0;
        _pickAt = double.NegativeInfinity;
        _doorStage = 0;
        _goalSet = false;
        _logger.LogInformation($"MISSION: {_pickDoorLabel} stands {DoorState(door.flags)} on the way - going for the lockpick.");
        SetPhase(Phase.PickDoor);
    }

    private void PickDoorTick(LocalPlayer me)
    {
        if (_doorStage == 0)
        {
            // Stop SHORT of the leaf: the closed door bounces the body (the Subway yank storm).
            var away = me.Transform.Position - _pickDoorPos;
            away.Y = 0;
            var stand = away.Magnitude > 0.01f ? _pickDoorPos + away.Normalize() * DoorPickStandoff : _pickDoorPos;
            WalkTo(me.Transform.Position, stand, 1.0f, "the shut door (lockpick)");
            if (_movement.IsGoalReached(ControlPriority.Mission))
            {
                _doorStage = 1;
                _goalSet = false;
                _goalAt = -1;
                _pickAt = double.NegativeInfinity;
            }
            else if (_phaseTime > DoorPickApproachTimeout)
            {
                PickGiveUp("the approach never got there");
            }

            return;
        }

        if (DoorNowOpen(_pickDoorPos))
        {
            _logger.LogInformation($"MISSION: {_pickDoorLabel} is open - on with the run.");
            ResumeAfterPick();
            return;
        }

        if (_phaseTime - _pickAt < DoorPickEverySec)
        {
            return;
        }

        var pick = FindLockpick(me);
        if (pick == null)
        {
            PickGiveUp($"no '{_config.ResupplyLockpickName}' in the packs - resupply stocks them");
            return;
        }

        var dynel = DoorWithin(_pickDoorPos, DoorUseRadius);
        if (dynel == null)
        {
            PickGiveUp("no door dynel streams at the shut door");
            return;
        }

        _pickAt = _phaseTime;
        _pickTries++;
        GameCommands.UseItemOn(me, pick.Slot, dynel.Identity);
        _logger.LogInformation($"MISSION: lockpick try {_pickTries}/{DoorPickTries} on {_pickDoorLabel} ({dynel.Identity}).");
        if (_pickTries >= DoorPickTries)
        {
            PickGiveUp($"the pick did not open it in {DoorPickTries} tries");
        }
    }

    private void PickGiveUp(string why)
    {
        _pickLost[_pickDoorKey] = Environment.TickCount64 / 1000.0;
        _logger.LogInformation($"MISSION: {_pickDoorLabel} stays shut - {why}.");
        ResumeAfterPick();
    }

    private void ResumeAfterPick()
    {
        _goalSet = false;
        _movement.ClearDesiredGoal(ControlPriority.Mission);
        SetPhase(_phaseBeforePick); // the giving-up branch re-runs there and asides the goal honestly
    }

    public void Stop(string why)
    {
        if (!Active)
        {
            return;
        }

        _logger.LogInformation($"MISSION: run stopped ({why}).");
        TearDown();
    }

    private void TearDown()
    {
        _controlArbiter.ReleaseControl();
        _movement.CancelTravel();
        _movement.ClearDesiredGoal(ControlPriority.Mission);
        _current = null;
        _offered.Clear();
        _rewardIds.Clear();
        _goalSet = false;
        _sideTry = 0;
        _doorStage = 0;
        _exitStands = 0;
        _acts = 0;
        _actTarget = null;
        _stashing = null;
        _pendingButton = Identity.None;
        _completed = false;
        ClearBuilding();
        _phase = Phase.Idle;
        _phaseTime = 0;
    }

    // The building state goes when we leave it (or the run ends).
    private void ClearBuilding()
    {
        _missionPf = -1;
        _nav = null;
        _record = null;
        _items.Clear();
        _searchedRooms.Clear();
        ResetClearing();
        _presses = 0;
        _fullBags.Clear();
    }

    // The clear book of ONE building: rooms walked, mobs seen/dead and their rooms, the person's
    // room, the pull pick and its refusals, the server's %. The mode (ClearOn) is NOT reset - it
    // is the run's setting, not the building's.
    private void ResetClearing()
    {
        ClearPct = -1;
        _clearVisited.Clear();
        _mobRoom.Clear();
        _mobsSeen.Clear();
        _mobsDead.Clear();
        _clearGaveUp = false;
        _clearPasses = 0;
        _clearScanAt = -1;
        _personRoomName = null;
        _personFloor = int.MinValue;
        _personId = Identity.None;
        _pullId = Identity.None;
        PullPick = null;
        _pullAside.Clear();
    }

    // ---- Wire -----------------------------------------------------------------

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(QuestAlternativeHandler, N3MessageType.QuestAlternative, 0);
        router.Register(QuestMessageHandler, N3MessageType.Quest, 0);
        router.Register(QuestFullUpdateHandler, N3MessageType.QuestFullUpdate, 0);
        router.Register(CharacterActionHandler, N3MessageType.CharacterAction, 0);
        router.Register(SimpleItemHandler, N3MessageType.SimpleItemFullUpdate, 0);
        router.Register(ZoneInHandler, N3MessageType.PlayfieldAnarchyF, 0); // raw bytes: the building layout
        router.Register(DoorFullUpdateHandler, N3MessageType.DoorFullUpdate, 0); // server doors: exact
        router.Register(DoorStatusUpdateHandler, N3MessageType.DoorStatusUpdate, 0); // door open/close flips
        router.Register(ClearPctHandler, N3MessageType.FormatFeedback, 0); // the clear %'s own line
    }

    // The zone-in's raw bytes: NavData composes the mission building's rooms out of the
    // BuildingGeneratorData block, which the typed model does not carry.
    private bool ZoneInHandler(AOMessage arg)
    {
        _zoneInRaw = arg.RawPacket;
        return false;
    }

    // The door's open/close flip (the lockpick's success may land here instead of a full update):
    // logged with its identity so the next run proves how the state travels and which id names a
    // door. Matching it back to _serverDoors waits for that wire truth.
    private bool DoorStatusUpdateHandler(AOMessage arg)
    {
        if (arg.Body is DoorStatusUpdateMessage s)
        {
            _logger.LogInformation($"MISSION: door status {s.Identity}: {(s.Open != 0 ? "OPEN" : "closed")}.");
        }

        return false;
    }

    private bool DoorFullUpdateHandler(AOMessage arg)
    {
        // No _nav guard here: the doors arrive right behind the zone-in packet, before the bot
        // loop has composed anything - that guard dropped the whole burst in the blind window.
        // No playfield filter here either: _missionPf is -1 until the first Tick composes the
        // zone-in, and the doors land inside that blind window. The corrector filters by pf.
        if (arg.Body is not DoorFullUpdateMessage door)
        {
            return false;
        }

        // THE STATE BITS (owner, 2026-10-09): stat 0 (Flags) carries 0x40 locked, 0x80 open - the
        // same two bits DoorStatusUpdateMessage's Locked and Open are read from (its remarks). The
        // Subway - Ventil run walked into 11 m yank storms at the first doorway: the doors there
        // stream shut, and the planner (doors walkable by default) kept driving the body into a
        // closed leaf. Logged per door; the walk gates and picks on this from here.
        var flags = 0u;
        if (door.Stats != null)
        {
            foreach (var s in door.Stats)
            {
                if (s.Value1 == Stat.Flags)
                {
                    flags = s.Value2;
                    break;
                }
            }
        }

        // The same door re-streams on every approach; one copy each keeps the corrector's count
        // honest (it re-applies when the count grows) and the search's door walk deduped. The key
        // is the door's physical identity (room pair + position), pf-less: the pre-compose copy
        // carries 0 and the re-stream the real playfield, and they are one door.
        var key = DoorKey(door.Room, door.AdjoiningRoom, door.Coordinate);
        if (!_doorKeys.Add(key))
        {
            // A re-stream REFRESHES the state: the lockpick beat watches these flags flip to open
            // (the door re-streams when it changes, and on every approach).
            for (var i = 0; i < _serverDoors.Count; i++)
            {
                var d = _serverDoors[i];
                if (d.room == door.Room && d.adjoining == door.AdjoiningRoom &&
                    Movement.Flat(d.pos, door.Coordinate) < 0.5f)
                {
                    _serverDoors[i] = (d.room, d.adjoining, d.pos, d.pf, flags);
                    break;
                }
            }

            return false;
        }

        // The packet's playfield field is the server's own naming and NOT our instance id (owner,
        // 2026-10-03: 2151467 while the building is 14624436 - the pf filter dropped every door and
        // the search never checked one). What names the building is OUR compose: store doors under
        // it once composed, under 0 in the pre-compose blind window (OnZoneIn re-keys those).
        var pf = _missionPf >= 0 ? _missionPf : 0;
        _serverDoors.Add((door.Room, door.AdjoiningRoom, door.Coordinate, pf, flags));
        if (door.Room == -1)
        {
            _serverExitByPf[pf] = door.Coordinate;
            _logger.LogInformation($"MISSION: exit door at ({door.Coordinate.X:0.0},{door.Coordinate.Y:0.0}," +
                                   $"{door.Coordinate.Z:0.0}) {DoorState(flags)} pf {door.Playfield}->{pf}.");
        }
        else
        {
            _logger.LogInformation($"MISSION: door room {door.Room} adj {door.AdjoiningRoom} at " +
                                   $"({door.Coordinate.X:0.0},{door.Coordinate.Y:0.0},{door.Coordinate.Z:0.0}) " +
                                   $"{DoorState(flags)} pf {door.Playfield}->{pf}.");
        }

        return false;
    }

    // The terminal's list of five.
    private bool QuestAlternativeHandler(AOMessage arg)
    {
        if (arg.Body is not QuestAlternativeMessage q || q.MissionDetails == null || q.MissionDetails.Length == 0)
        {
            return false;
        }

        _offered.Clear();
        _offered.AddRange(q.MissionDetails);
        _logger.LogInformation($"MISSION: {_offered.Count} missions offered:");
        foreach (var m in _offered)
        {
            _logger.LogInformation($"MISSION:   {Line(m)} [{m.MissionIdentity}]" +
                                   (Allowed(m, out var why) ? "" : $" [skip: {why}]"));
        }

        return false;
    }

    // The quest log: every held mission whose giver is a mission terminal (or our terminal in
    // person - at login it reads as a SimpleChar whose low bytes match ours) is deletable. The raw
    // bytes are kept too: the record parse reads the target out of them.
    private bool QuestFullUpdateHandler(AOMessage arg)
    {
        _questRaw = arg.RawPacket;
        if (arg.Body is not QuestFullUpdateMessage full || full.Quests == null)
        {
            return false;
        }

        var seen = new HashSet<Identity>();
        foreach (var q in full.Quests)
        {
            if (q?.QuestId == null || q.QuestId == Identity.None)
            {
                continue;
            }

            seen.Add(q.QuestId);
            var giver = q.UnknownId1;
            // At login the terminal reads as a SimpleChar (capture 20260923-232359: C0000320 and
            // C0010320, so the third byte is not compared) and no terminal dynel has been seen yet:
            // the SAVED terminal's low bytes answer for it - that is how a restart sees its held
            // mission at all.
            var mine = giver.Type == IdentityType.MissionTerminal ||
                       (giver.Type == IdentityType.SimpleChar &&
                        ((_terminal != Identity.None && (giver.Instance & 0xFFFFFF) == (_terminal.Instance & 0xFFFFFF)) ||
                         (_savedTerminal != null && _savedTerminal.id != 0 &&
                          (giver.Instance & 0xFFFFFF) == ((uint)_savedTerminal.id & 0xFFFFFF))));
            if (mine)
            {
                _heldQuests[q.QuestId] = q;
            }
        }

        foreach (var gone in _heldQuests.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _heldQuests.Remove(gone);
        }

        return false;
    }

    // A held mission removed (our own delete, or the server closing a completed one).
    private bool QuestMessageHandler(AOMessage arg)
    {
        if (arg.Body is not QuestMessage q)
        {
            return false;
        }

        if (q.Action != QuestAction.Delete)
        {
            return false;
        }

        var wasHeld = _heldQuests.Remove(q.Mission);
        // The removal of a held mission counts as completion too (a team holder gets no
        // MissionChanged; solo the removal lands right after it - harmless twice).
        if (wasHeld)
        {
            MarkCompleted("quest removed");
        }

        return false;
    }

    private bool CharacterActionHandler(AOMessage arg)
    {
        if (arg.Body is not CharacterActionMessage ca || (int)ca.Action != MissionChangedAction)
        {
            return false;
        }

        var me = DynelManager.LocalPlayer;
        if (me != null && ca.Identity.Instance == me.Identity.Instance)
        {
            MarkCompleted("MissionChanged");
        }

        return false;
    }

    // The building's items, as the server sends a floor's worth on arrival: floor buttons (walked
    // to and pressed) and find-item targets. Only what stands somewhere counts.
    private bool SimpleItemHandler(AOMessage arg)
    {
        if (arg.Body is not SimpleItemFullUpdateMessage it)
        {
            return false;
        }

        if (it.Identity == Identity.None || it.Identity.Type != IdentityType.Terminal ||
            it.Position == null || it.Stats == null)
        {
            return false;
        }

        var template = 0;
        foreach (var st in it.Stats)
        {
            if (st.Value1 == Stat.ACGItemTemplateID)
            {
                template = st.Value2;
                break;
            }
        }

        _items[it.Identity] = new SeenItem { Template = template, Pos = it.Position.Value };
        if (IsButton(template))
        {
            _logger.LogInformation($"MISSION: button {template} at " +
                                   $"({it.Position.Value.X:0.0} {it.Position.Value.Z:0.0}).");
        }

        return false;
    }

    private static bool IsButton(int template)
    {
        return template >= ButtonLowest && template <= ButtonHighest;
    }

    // The server's share of the building's mobs dead (CLEAR MODE): category 110, message
    // 79979934, one float - after each kill, 38.5, 46.2 ... 100 in steps of 1/13 in AOBuddy10's
    // capture 20260925-110325; at 100 the token came with the reward. The find-person target is
    // not counted. `Message` carries the raw '~&' ext string: category and message id as 5
    // base-85 chars each, then 'f' and the float as 5 base-85 chars (ExtMessageFormatter parses
    // the same string, so the offsets are its wire shape).
    private const int ClearCategory = 110;
    private const int ClearMessage = 79979934;

    private bool ClearPctHandler(AOMessage arg)
    {
        if (arg.Body is FormatFeedbackMessage ff && TryClearPct(ff.Message, out var pct))
        {
            ClearPct = pct;
            _logger.LogInformation($"MISSION: cleared {pct:0.#}% of this mission's mobs.");
        }

        return false;
    }

    /// <summary>'~&amp;' + category and message id (5 base-85 chars each) + 'f' + a base-85 float.</summary>
    private static bool TryClearPct(string s, out float pct)
    {
        pct = 0;
        if (s == null || s.Length < 18 || s[0] != '~' || s[1] != '&')
        {
            return false;
        }

        if (B85(s, 2) != ClearCategory || B85(s, 7) != ClearMessage || s[12] != 'f')
        {
            return false;
        }

        pct = BitConverter.ToSingle(BitConverter.GetBytes((int)(uint)B85(s, 13)), 0);
        return pct >= 0 && pct <= 100.01f;
    }

    private static long B85(string s, int p)
    {
        long v = 0;
        for (var i = 0; i < 5; i++)
        {
            var c = s[p + i] - 33;
            if (c < 0 || c > 84)
            {
                return -1;
            }

            v = v * 85 + c;
        }

        return v;
    }

    // ---- Frame ----------------------------------------------------------------

    /// <summary>
    ///     The decision tick, every update while in play (BotLoop). Returns true while the run owns
    ///     the body - except while it waits on the sell step, when it answers false so the chain
    ///     falls through to the SellController.
    /// </summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        var zone = _zoneInRaw;
        _zoneInRaw = null;
        if (zone != null)
        {
            OnZoneIn(me, zone);
        }

        // The server doors have had their moment (the zone-in burst lands well inside 2.5 s):
        // correct the layout with them, THEN hand the nav over and let the grid build once,
        // from server truth instead of the slot grid.
        // The doors arrive over the first minute (3 by +3 s, the rest as the bot moves): re-apply
        // whenever new ones landed, at most every 3 s. Idempotent - rooms already server-exact
        // move 0 - and the grid only rebuilds when a room actually moved.
        // The hold below sits before _phaseTime's increment, so it needs its own clock - a
        // _phaseTime deadline never arrives while the hold is returning (owner, 2026-10-03:
        // the run froze in the entrance, "finishing it first" and then nothing).
        if (_nav != null && !_navHandedOver)
        {
            _holdAccum += dt;
        }
        else
        {
            _holdAccum = 0;
        }

        var myDoors = _serverDoors.Where(x => x.pf == _missionPf).ToList();
        // Past 12 s with no doors at all, hand the layout over uncorrected - walking the
        // composed building beats freezing in the entrance (owner, 2026-10-03).
        var doorDeadline = _serverExitByPf.ContainsKey(_missionPf) || myDoors.Count > 0 ? 2.5 : 12.0;
        var applyDoors = _nav != null &&
                         (!_navHandedOver
                             ? _holdAccum > doorDeadline || myDoors.Count >= 20
                             : myDoors.Count > _doorsApplied && _phaseTime - _lastDoorApply > 3);
        if (applyDoors)
        {
            _navHandedOver = true;
            _doorsApplied = myDoors.Count;
            _lastDoorApply = _phaseTime;
            var lines = AOBuddyNav.CorrectWithServerDoors(
                AppDomain.CurrentDomain.BaseDirectory, _nav,
                myDoors.Select(x => (x.room, x.adjoining, x.pos)).ToList(), out var movedAny, apply: true);
            foreach (var line in lines)
            {
                _logger.LogInformation("MISSION: " + line);
            }

            if (movedAny)
            {
                _movement.ResetMissionNav(); // (re)build the grid from the corrected rooms
            }

            _movement.SetMissionNav(_missionPf, _nav);
        }

        // No corrected nav handed over yet: hold the phases - walking the uncorrected layout
        // beelines through walls. The deadline releases it: uncorrected beats frozen. Idle
        // claims nothing (false) - the hand-over itself runs above regardless.
        if (!Active)
        {
            return false;
        }

        if (_nav != null && !_navHandedOver && _holdAccum < doorDeadline + 2.0)
        {
            return true;
        }

        _phaseTime += dt;
        switch (_phase)
        {
            case Phase.ToTerminal:
                ToTerminalTick(me);
                break;
            case Phase.Rolling:
                RollingTick(me);
                break;
            case Phase.AwaitList:
                AwaitListTick(me);
                break;
            case Phase.Accepting:
                if (_phaseTime >= AcceptSettle)
                {
                    StartToDoor();
                }

                break;
            case Phase.ToDoor:
                ToDoorTick(me);
                break;
            case Phase.EnterDoor:
                EnterDoorTick(me);
                break;
            case Phase.Blitz:
                BlitzTick(me);
                break;
            case Phase.PickDoor:
                PickDoorTick(me);
                break;
            case Phase.RewardBag:
                RewardBagTick(me);
                break;
            case Phase.Leaving:
                LeavingTick(me);
                break;
            case Phase.WaitingSell:
                WaitingSellTick();
                return false; // the sell run owns the chain (and the body) right now
        }

        return true;
    }

    // A zone landed. A mission instance composes here; leaving one is noticed by the same path.
    private void OnZoneIn(LocalPlayer me, byte[] raw)
    {
        if (me == null)
        {
            return;
        }

        var nav = AOBuddyNav.LoadMission(AppDomain.CurrentDomain.BaseDirectory, raw);
        if (nav?.Layout != null)
        {
            // INTO a mission instance.
            _missionPf = (int)Playfield.ModelId;
            // The burst that landed before this compose (logging in inside the building, the
            // walk-in burst) carries no playfield yet (0): those doors are THIS building's -
            // re-key them. Only doors naming another playfield go (owner, 2026-10-03: the
            // login-inside burst was dropped whole and the search had no doors to check).
            for (var i = 0; i < _serverDoors.Count; i++)
            {
                if (_serverDoors[i].pf == 0)
                {
                    var d = _serverDoors[i];
                    _serverDoors[i] = (d.room, d.adjoining, d.pos, _missionPf, d.flags);
                }
            }

            // The -1 door with it: the EXIT, keyed blind (pf 0) in the pre-compose burst like every
            // other door. _serverExit looks the mission pf up - re-key or it reads "none seen" and
            // the leaving falls back to the layout's guess (owner, 2026-10-03).
            if (_serverExitByPf.TryGetValue(0, out var blindExit))
            {
                _serverExitByPf.Remove(0);
                _serverExitByPf[_missionPf] = blindExit;
            }

            _serverDoors.RemoveAll(x => x.pf != _missionPf); // other instances' doors go
            if (_doorKeysPf > 0 && _doorKeysPf != _missionPf)
            {
                _doorKeys.Clear(); // a new building replays its doors; the same one must not duplicate
            }

            _doorKeysPf = _missionPf;
            _doorsApplied = -1;
            _navHandedOver = false;
            // The nav is handed over once the server's doors arrived and the layout was
            // corrected with them (the Tick below) - the grid then builds from server truth.
            _nav = nav;
            _items.Clear();
            _searchedRooms.Clear();
            _checkedDoors.Clear();
            _lastSearchRoom = -1;
            ResetClearing(); // a new building: the clear book starts over (the mode stays on)
            _presses = 0;
            _completed = false;
            _acts = 0;
            _actTarget = null;
            _goalSet = false;
            var questBytes = _questRaw;
            _questRaw = null;
            _record = ParseRecord(questBytes, me.Identity.Instance, nav.Layout.Instance);
            if (_record != null)
            {
                _logger.LogInformation($"MISSION: record {_record.TypeName} building {_record.Building}, " +
                                       $"target {(_record.TargetA?.ToString() ?? "none (match by name)")}.");
            }
            else
            {
                _logger.LogInformation("MISSION: no quest record yet - waiting for the next quest update.");
            }

            _logger.LogInformation($"MISSION: in {nav.Name} instance {nav.Layout.Instance}, " +
                                   $"{nav.Dungeon.Rooms.Count} room(s), exit {DescribeExit(nav.Exit)}; {AOBuddyNav.DoorCheck}");
            if (_phase is Phase.EnterDoor or Phase.ToDoor)
            {
                _movement.ClearDesiredGoal(ControlPriority.Mission);
                _movement.CancelTravel();
                _goalSet = false;
                SetPhase(Phase.Blitz);
                Tell($"Inside the mission ({nav.Name}).{RecordText()}");
            }

            return;
        }

        if (_nav != null)
        {
            // OUT of a mission instance (the exit door worked, or something moved us).
            _logger.LogInformation("MISSION: outside the mission.");
            ClearBuilding();
            _goalSet = false;
            if (_phase is Phase.Leaving or Phase.Blitz or Phase.RewardBag)
            {
                SetPhase(Phase.RewardBag); // outside now: stash anything not yet bagged, then sell-or-go
            }
        }
    }

    private static string DescribeExit(AOBuddyNav.Doorway ex)
    {
        return ex == null ? "unknown (the zone-in landing point)" : $"on floor {ex.Floor} at ({ex.X:0},{ex.Z:0})";
    }

    // ---- Phase: ToTerminal ----------------------------------------------------

    private void ToTerminalTick(LocalPlayer me)
    {
        var saved = _savedTerminal;
        if (saved == null)
        {
            Stop("terminal lost");
            return;
        }

        var here = TerminalWithin(me, _config.MissionTerminalRadius);
        if (here != null)
        {
            TakeTerminal(here);
            _movement.CancelTravel();
            _movement.ClearDesiredGoal(ControlPriority.Mission);
            _goalSet = false;
            SetPhase(Phase.Rolling);
            return;
        }

        if ((int)Playfield.ModelId == saved.pf)
        {
            // On foot the last metres: the terminal's body keeps travel a few metres out. The radius
            // must keep the stand-off inside the TerminalWithin ring (owner, 2026-10-03: arrive 2 m
            // on the 4 m approach point parked the bot up to 6 m out - past the 5 m default ring -
            // and the run stood "arrived" until it timed out, never rolling).
            if (!_goalSet)
            {
                var stand = TerminalApproach(me, saved);
                _movement.SetDesiredGoal(stand, saved.pf, ControlPriority.Mission, TerminalReach);
                _goalSet = true;
                _goalWhat = "walking the last metres to the terminal";
                _goalAt = _phaseTime;
                _goalPos = stand;
            }

            if (_phaseTime - _goalAt > TerminalTimeout)
            {
                Stop("never reached the terminal");
                Tell("I can't reach the mission terminal - run stopped where I stand.");
            }

            return;
        }

        if (_movement.TravelTargetPf != saved.pf)
        {
            // The plan died or a manual order took the body: try once more, then say no.
            var line = _movement.PlanTravel(saved.pf, null);
            if (_movement.TravelTargetPf != saved.pf &&
                (int)Playfield.ModelId != saved.pf)
            {
                Stop("no route to the terminal");
                Tell($"Travel to the mission terminal ({Zoning.Name(saved.pf)}) didn't work - run stopped. {Truncate(line, 200)}");
            }

            return;
        }

        if (_phaseTime > TerminalTimeout)
        {
            Stop("travel ran out");
            Tell("The trip to the mission terminal ran out of time - run stopped.");
        }
    }

    // A spot this side of the terminal, roll range minus a metre out of its centre: close enough to
    // roll, clear of its body.
    private static Vector3 TerminalApproach(LocalPlayer me, SavedTerminal t)
    {
        var pos = me.Transform.Position;
        var term = new Vector3(t.x, t.y, t.z);
        if (Movement.Flat(pos, term) <= 5f)
        {
            return pos;
        }

        var dir = new Vector3(term.X - pos.X, 0, term.Z - pos.Z).Normalize();
        return new Vector3(term.X - dir.X * 4f, term.Y, term.Z - dir.Z * 4f);
    }

    // ---- Phase: Rolling / AwaitList ---------------------------------------------

    private void RollingTick(LocalPlayer me)
    {
        var here = TerminalWithin(me, _config.MissionTerminalRadius);
        if (here == null)
        {
            if (_phaseTime > 5)
            {
                Stop("walked off the terminal");
            }

            return;
        }

        TakeTerminal(here);

        // Keys and rewards need four free slots (AOBuddy10's hard minimum).
        if (Inventory.NumFreeSlots < 4)
        {
            Stop("no room to roll");
            Tell($"Only {Inventory.NumFreeSlots} free slot(s) - mission keys and rewards need 4. " +
                 "Make room ('sell' empties the bags) and send 'mission run' again.");
            return;
        }

        if (_emptyRolls > 0 && _emptyRolls % MaxRollsWarn == 0)
        {
            Tell($"{_emptyRolls} rolls without an acceptable mission - still trying. 'mission stop' ends the run.");
        }

        if (!_usedTerminals.Contains(_terminal))
        {
            // The owner's client Uses the terminal once, then rolls; rolls after that need no Use.
            _usedTerminals.Add(_terminal);
            GameCommands.UseObject(me, _terminal);
            _logger.LogInformation($"MISSION: used terminal {_terminal} ('{_terminalName}').");
        }

        SendRoll();
        SetPhase(Phase.AwaitList);
    }

    private void SendRoll()
    {
        // The want run steers the difficulty toward the QL band it wants (WantAim); without one,
        // the owner's setting.
        var difficulty = _wantDifficulty ?? _config.MissionDifficulty;
        _lastDifficulty = difficulty;
        var sliders = new MissionSliders
        {
            Difficulty = (byte)Math.Max(0, Math.Min(255, difficulty)),
            GoodBad = Slider(_config.MissionSliderGoodBad),
            OrderChaos = Slider(_config.MissionSliderOrderChaos),
            OpenHidden = Slider(_config.MissionSliderOpenHidden),
            PhysicalMystical = Slider(_config.MissionSliderPhysicalMystical),
            HeadonStealth = Slider(_config.MissionSliderHeadonStealth),
            CreditsXp = Slider(_config.MissionSliderCreditsXp),
        };
        Client.Send(new QuestAlternativeMessage
        {
            VersionId = 4,
            MissionSliders = sliders,
            Unknown2 = 0,
            Scope = MissionScope.Solo,
            Terminal = _terminal,
            MissionDetails = Array.Empty<MissionInfo>(),
        });
        _logger.LogInformation($"MISSION: roll sent: difficulty {sliders.Difficulty}, sliders " +
                               $"{sliders.GoodBad},{sliders.OrderChaos},{sliders.OpenHidden}," +
                               $"{sliders.PhysicalMystical},{sliders.HeadonStealth},{sliders.CreditsXp}, " +
                               $"scope Solo, at {_terminal}.");
    }

    // Slider value (-100..+100, 0 = middle) to its signed byte on the wire: -100 = 0x9C, +100 = 0x64.
    private static byte Slider(int value)
    {
        return unchecked((byte)(sbyte)Math.Max(-100, Math.Min(100, value)));
    }

    private void AwaitListTick(LocalPlayer me)
    {
        if (_offered.Count > 0)
        {
            PickAndAccept();
            return;
        }

        if (_phaseTime < ListTimeout)
        {
            return;
        }

        _emptyRolls++;
        _logger.LogInformation($"MISSION: no list in {ListTimeout:0}s - rolling again ({_emptyRolls}).");
        SetPhase(Phase.Rolling);
    }

    // Blitz finishes find item and find person only (owner, step 2).
    private static bool BlitzCan(int code)
    {
        return code == TypeFindPerson || code == TypeFindItem;
    }

    private bool Allowed(MissionInfo m, out string why)
    {
        if (!BlitzCan(m.MissionIcon))
        {
            why = TypeName(m.MissionIcon) + " not supported in blitz";
            return false;
        }

        var zones = _config.MissionZones;
        if (zones != null && zones.Count > 0)
        {
            var name = Zoning.Name(m.Playfield.Instance);
            var ok = zones.Any(z => string.Equals(z?.Trim(), name, StringComparison.OrdinalIgnoreCase)
                                    || (int.TryParse(z, out var id) && id == m.Playfield.Instance));
            if (!ok)
            {
                why = $"{name} is not in MissionZones";
                return false;
            }
        }

        why = null;
        return true;
    }

    private void PickAndAccept()
    {
        // Every list is evidence: the mission QL this level+difficulty rolled (qlmap) and which
        // rewards were offered (offered.json) - the want run's targeting and its not-rollable
        // judgement are built from it, want run or not.
        var rollQl = QlObserve(_offered);
        RecordOffers(_offered, rollQl);

        if (_wantRun)
        {
            if (WantFilter())
            {
                // Nothing wanted on this list: re-rolling, with the difficulty re-aimed first.
                WantAim();
                return;
            }

            WantAim();
        }

        var pick = _offered.Where(m => Allowed(m, out _)).OrderBy(TripCost).FirstOrDefault();
        if (pick == null)
        {
            _emptyRolls++;
            _logger.LogInformation("MISSION: nothing acceptable on the list - rolling again.");
            _offered.Clear();
            SetPhase(Phase.Rolling);
            return;
        }

        _emptyRolls = 0;
        Accept(pick, null);
        SetPhase(Phase.Accepting);
    }

    // The zoning graph's cost to the door; unreachable doors sort last (and are skipped if nothing else fits).
    private double TripCost(MissionInfo m)
    {
        var route = Zoning.FindRoute((int)Playfield.ModelId, _movement.CurrentPosition,
            m.Playfield.Instance, new Vector3(m.Location.X, _movement.CurrentPosition.Y, m.Location.Z));
        return route == null || route.Hops.Count == 0 ? double.MaxValue : route.Hops[route.Hops.Count - 1].Cost;
    }

    // _current, its reward ids and a fresh completion flag - shared by accept and resume.
    private void TakeCurrent(MissionInfo m)
    {
        _current = m;
        _rewardIds.Clear();
        foreach (var r in m.MissionItemData ?? Array.Empty<MissionItemReward>())
        {
            _rewardIds.Add((r.LowId, r.HighId));
        }

        _completed = false;
    }

    private void Accept(MissionInfo m, Action<string> reply)
    {
        TakeCurrent(m);
        Client.Send(new CreateQuestMessage { MissionId = m.MissionIdentity });
        var rewards = string.Join(", ", (m.MissionItemData ?? Array.Empty<MissionItemReward>()).Select(RewardName));
        var line = $"Accepted: {Line(m)} - rewards: {(rewards.Length == 0 ? "credits" : rewards)}.";
        _logger.LogInformation("MISSION: " + line);
        if (reply != null)
        {
            reply(line);
        }
        else
        {
            Tell(line); // STEP 2: the owner hears the mission, its coordinates and its playfield
        }
    }

    private static string RewardName(MissionItemReward r)
    {
        try
        {
            var item = new Item(r.LowId, r.HighId == 0 ? r.LowId : r.HighId, r.Ql);
            if (!string.IsNullOrEmpty(item.Name))
            {
                return $"{item.Name} QL{r.Ql}";
            }
        }
        catch
        {
            // no item data for it: the ids will do
        }

        return $"item {r.LowId} QL{r.Ql}";
    }

    private static string Line(MissionInfo m)
    {
        return $"{TypeName(m.MissionIcon)} in {Zoning.Name(m.Playfield.Instance)} " +
               $"({m.Location.X:0},{m.Location.Z:0}), {m.Credits:N0} cr";
    }

    public static string TypeName(int code)
    {
        return code switch
        {
            TypeFindPerson => "find person",
            TypeFindItem => "find item",
            TypeRepair => "repair",
            TypeReturnItem => "return item",
            TypeKillPerson => "kill person",
            0 => "mission",
            _ => $"type 0x{code:X}",
        };
    }

    // ---- Phase: ToDoor / EnterDoor ----------------------------------------------

    private void StartToDoor()
    {
        var door = _current.Location;
        var line = _movement.PlanTravel(_current.Playfield.Instance,
            new Vector3(door.X, _movement.CurrentPosition.Y, door.Z));
        if (_movement.TravelTargetPf != _current.Playfield.Instance &&
            (int)Playfield.ModelId != _current.Playfield.Instance)
        {
            // No route to that door: the next candidate, else the round ends here. A plan that
            // COMPLETED on the spot is no failure - already in the door's playfield, PlanTravel
            // keeps no plan and just walks the final leg (owner, 2026-10-03: the restart resume
            // logged in beside the door and the run stopped itself on a working walk order).
            _offered.Remove(_current);
            var next = _offered.Where(m => Allowed(m, out _)).OrderBy(TripCost).FirstOrDefault();
            if (next != null)
            {
                _logger.LogInformation($"MISSION: no route to the door ({Truncate(line, 150)}) - trying the next mission.");
                _current = null;
                Accept(next, null);
                SetPhase(Phase.Accepting);
                return;
            }

            Stop("no route to the door");
            Tell($"No route to the mission door ({Truncate(line, 200)}) - run stopped.");
            return;
        }

        _goalSet = false;
        SetPhase(Phase.ToDoor);
    }

    private void ToDoorTick(LocalPlayer me)
    {
        var door = _current.Location;
        var flat = Movement.Flat(me.Transform.Position, door);
        if ((int)Playfield.ModelId == _current.Playfield.Instance && flat <= 12f)
        {
            // Close enough to work the door on foot.
            _movement.CancelTravel();
            _movement.ClearDesiredGoal(ControlPriority.Mission);
            _baseSide = SideFrom(me.Transform.Position, door);
            _sideTry = 0;
            _doorStage = 0;
            _goalSet = false;
            SetPhase(Phase.EnterDoor);
            return;
        }

        if ((int)Playfield.ModelId == _current.Playfield.Instance && flat > 12f &&
            !_movement.HasGoal(ControlPriority.Travel))
        {
            // In the door's playfield with no leg to walk: the walk gave the final leg up on a
            // yank (owner, 2026-10-03 Aegean - the run then sat here until the trip timeout).
            // Walk the stretch again: the refused cells are blacklisted, so this try routes round.
            _movement.SetDesiredGoal(new Vector3(door.X, me.Transform.Position.Y, door.Z),
                (int)Playfield.ModelId, ControlPriority.Travel, 1.5f);
        }

        if (_movement.TravelTargetPf != _current.Playfield.Instance &&
            (int)Playfield.ModelId != _current.Playfield.Instance)
        {
            // The plan died: re-plan (we may have been pushed off it), and give up after too many.
            var line = _movement.PlanTravel(_current.Playfield.Instance,
                new Vector3(door.X, _movement.CurrentPosition.Y, door.Z));
            if (_movement.TravelTargetPf != _current.Playfield.Instance)
            {
                DropMission($"travel to the door didn't work ({Truncate(line, 150)})");
            }

            return;
        }

        if (_phaseTime > 900)
        {
            DropMission("the trip to the door ran out of time");
        }
    }

    // The direction we came from, horizontal: the first door side tried.
    private static Vector3 SideFrom(Vector3 pos, Vector3 door)
    {
        var d = new Vector3(pos.X - door.X, 0, pos.Z - door.Z);
        return d.Magnitude < 0.5f ? new Vector3(1, 0, 0) : d.Normalize();
    }

    private static Vector3 Turned(Vector3 side, int steps)
    {
        // Rotate (x, z) about Y in 45° steps: each step is (x,z) -> ((x-z)/√2, (x+z)/√2).
        var x = side.X;
        var z = side.Z;
        for (var i = 0; i < ((steps % 8) + 8) % 8; i++)
        {
            var nx = (x - z) * 0.70710678f;
            z = (x + z) * 0.70710678f;
            x = nx;
        }

        return new Vector3(x, 0, z);
    }

    private void EnterDoorTick(LocalPlayer me)
    {
        var door = _current.Location;
        var side = Turned(_baseSide, _sideTry);

        if (_doorStage == 0)
        {
            // A spot a few metres out on this side first - the approach the walk can plan honestly.
            var what = $"door side {_sideTry % DoorSides + 1}/{DoorSides} (round {_sideTry / DoorSides + 1}/{DoorRounds})";
            WalkTo(me.Transform.Position, door + side * (float)EnterApproach, 1.5f, what);
            if (_movement.IsGoalReached(ControlPriority.Mission))
            {
                _doorStage = 1;
                _goalSet = false;
                _goalAt = -1;
            }
            else if (_phaseTime - _goalAt > EnterSideTimeout)
            {
                NextDoorSide("the approach never got there");
            }

            return;
        }

        // ONTO the door's own spot: the server moves us in when we stand there (capture: the owner's
        // client stopped within 0.2 m of the door and was moved in 0.4 s later). The USE beside it
        // (owner, 2026-10-09): city doors do not take a standing body in - Upper Stret East Bank
        // held the body ~2 m out under corrections through every side while the walk believed it
        // stood 0.3 m from the spot. The door dynel gets the wire-proven GenericCmd Use (the same
        // bytes the terminals and lift buttons get, capture 20260923-201746); a wilderness door
        // that enters on its own is unaffected by the extra Use.
        WalkTo(me.Transform.Position, door, 0.4f, "standing on the door");
        if (_movement.IsGoalReached(ControlPriority.Mission))
        {
            if (_goalAt < 0)
            {
                _goalAt = _phaseTime; // the stand beat starts when the door reads reached
            }

            if (_phaseTime - _doorUseAt > DoorUseEverySec)
            {
                _doorUseAt = _phaseTime;
                var dynel = DoorWithin(door, DoorUseRadius);
                if (dynel != null)
                {
                    GameCommands.UseObject(me, dynel.Identity);
                    _logger.LogInformation(
                        $"MISSION: using the entrance door {dynel.Identity} " +
                        $"({Movement.Flat(door, dynel.Transform.Position):0.0} m from the spot).");
                }
                else
                {
                    var near = string.Join(", ", DynelManager.AllDynels
                        .Where(d => d != null && Movement.Flat(door, d.Transform.Position) <= DoorUseRadius)
                        .Select(d => $"{d.Identity.Type}:{d.Identity.Instance}")
                        .Take(6));
                    _logger.LogInformation($"MISSION: no IdentityType.Door within {DoorUseRadius:0} m of the entrance" +
                                           (near.Length > 0 ? $" - nearby dynels: {near}" : " - nothing streams there."));
                }
            }

            if (_phaseTime - _goalAt > DoorStandTimeout)
            {
                NextDoorSide("this side didn't take me in");
            }
        }
        else if (_goalAt > 0 && _phaseTime - _goalAt > DoorStandTimeout + EnterSideTimeout)
        {
            NextDoorSide("never reached the door spot");
        }
    }

    private void NextDoorSide(string why)
    {
        _logger.LogInformation($"MISSION: {why} (side {_sideTry % DoorSides + 1}).");
        _sideTry++;
        _movement.ClearDesiredGoal(ControlPriority.Mission);
        _goalSet = false;
        _goalAt = -1;
        _doorUseAt = double.NegativeInfinity; // the next side's first Use fires on arrival
        if (_sideTry >= DoorSides * DoorRounds)
        {
            DropMission("I can't get through its door from any side");
            return;
        }

        _doorStage = 0;
        SetPhase(Phase.EnterDoor);
    }

    // ---- Phase: Blitz -------------------------------------------------------------

    private void BlitzTick(LocalPlayer me)
    {
        if (_completed)
        {
            SetPhase(Phase.RewardBag);
            return;
        }

        // A clearing building gets three times the clock: every room is walked and fought before
        // the objective (AOBuddy10 MissionRun's clear-mode timeout).
        if (_phaseTime > (ClearOn ? 3 * BlitzTimeout : BlitzTimeout))
        {
            DropMission("it was taking too long");
            return;
        }

        if (_record == null)
        {
            // The zone-in also re-sends the quest log; the record lands a beat after we do.
            var fresh = _questRaw;
                _questRaw = null;
            if (fresh != null)
            {
                _record = ParseRecord(fresh, me.Identity.Instance, _nav.Layout.Instance);
                if (_record != null)
                {
                    _logger.LogInformation("MISSION: record " + RecordText());
                }
            }

            if (_record == null && _phaseTime > RecordWait)
            {
                DropMission("no quest record for this building");
            }

            return;
        }

        // CLEAR MODE: the objective waits until the building is cleared - or clearing gives up
        // (two passes over every reachable room). The tick below walks and fights; the swing is
        // the combat brains' (SuppressCombat answers false while Clearing holds).
        if (Clearing)
        {
            ClearTick(me);
            return;
        }

        // An act is in flight: give it ActTimeout to complete, then select again - at most MaxActs
        // times before the mission is given up.
        if (_actTarget.HasValue)
        {
            if (_phaseTime - _actedAt < ActTimeout)
            {
                return;
            }

            if (_acts >= MaxActs)
            {
                DropMission("the target won't complete");
                return;
            }

            _logger.LogInformation("MISSION: no completion - selecting the target again.");
            _actTarget = null;
            _goalSet = false;
            _movement.ClearDesiredGoal(ControlPriority.Mission);
        }

        var pos = me.Transform.Position;
        var target = FindTarget(me, out var targetPos, out var how);
        if (target.HasValue && targetPos.HasValue)
        {
            WalkTo(pos, targetPos.Value, TargetReach, $"the target ({how})");
            if (_movement.IsGoalReached(ControlPriority.Mission) ||
                Movement.Flat(pos, targetPos.Value) <= TargetReach)
            {
                BeginAct(me, target.Value);
            }

            return;
        }

        // Not in sight. Search: the server sends a floor's items on arrival, so the room walk IS
        // the search - and a button ride is the way to another floor. The current hop owns the
        // walk until it ends: reached, or timed out when its centre proves unreachable (next room
        // then; the room was marked searched on setting out). Without the gate every tick picked
        // and marked the NEXT room - one room per frame: 21 rooms burned in 0.3 s, the mission
        // dropped before the grid had loaded or the floor's items had landed
        // (owner, 2026-10-03: "nothing left to search" 0.4 s after the zone-in).
        if (_goalSet && _movement.HasGoal(ControlPriority.Mission) &&
            !_movement.IsGoalReached(ControlPriority.Mission) &&
            _phaseTime - _goalAt < SearchHopTimeout)
        {
            return; // the room (or button) walk is on - FindTarget re-checks every tick anyway
        }

        var hop = NextSearch(pos);
        if (!hop.pos.HasValue)
        {
            DropMission("nothing left to search and no target in sight");
            return;
        }

        WalkTo(pos, hop.pos.Value, hop.reach, hop.what);
        if (hop.press)
        {
            PressButton(me, hop.button);
        }
    }

    private void WalkTo(Vector3 from, Vector3 to, float radius, string what)
    {
        // The goal must still EXIST: the walk's yank give-up clears it behind our back, and a
        // matching _goalWhat alone would then no-op every tick forever (owner, 2026-10-03: three
        // mobs held the bot in a Subway mission, the goal was given up, and the run stood still).
        // It must also still POINT at the spot: a wandering pull moves while its label stays the
        // same, and the stale goal reads reached - the dedup then swallowed every re-issue and
        // the bot stood still beside a live mob 16 m off (owner, 2026-10-09 11:41, Shade-Y42).
        if (_goalSet && _goalWhat == what && _movement.HasGoal(ControlPriority.Mission) &&
            Movement.Flat(_goalPos, to) < 1f)
        {
            return; // already walking there
        }

        // Outside an instance _missionPf is still -1, and a -1 goal is never serviced: SelectActiveGoal
        // walks only goals of the CURRENT playfield (owner, 2026-10-02: the door-side approaches in
        // Wailing Wastes were planned but never walked - every side timed out standing still).
        var pf = _missionPf >= 0 ? _missionPf : (int)Playfield.ModelId;
        _movement.SetDesiredGoal(to, pf, ControlPriority.Mission, radius);
        _goalSet = true;
        _goalAt = _phaseTime;
        _goalWhat = what;
        _goalPos = to;
        _logger.LogInformation($"MISSION: walking to {what} ({to.X:0.0} {to.Z:0.0}), {Movement.Flat(from, to):0} m.");
    }

    // The next move toward an unseen target: the button toward the boss floor first (AOBuddy10's
    // rule: most pools put the target in the boss room), then the nearest room on my floor not yet
    // searched; when the floor is done, any button out of it.
    private (Vector3? pos, float reach, string what, Identity button, bool press) NextSearch(Vector3 pos)
    {
        var myFloor = FloorAt(pos);
        var bossRoom = _nav.Dungeon.Rooms.FirstOrDefault(r =>
            r.PoolName != null && r.PoolName.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0);
        if (_presses < MaxPresses && bossRoom != null && bossRoom.Floor != myFloor)
        {
            var button = NearestButton(pos);
            if (button != Identity.None)
            {
                return (_items[button].Pos, ButtonReach, "the button to another floor", button, true);
            }
        }

        // The room just set out to: behind its doors before the next room. The centre walk streams
        // the main chamber; an inner section streams only once the body crosses its door.
        var doorHop = NextDoorHop(pos, myFloor);
        if (doorHop.pos.HasValue)
        {
            return doorHop;
        }

        var next = _nav.Dungeon.Rooms
            .Select((r, i) => (r, i))
            .Where(x => x.r.Floor == myFloor && !_searchedRooms.Contains((x.r.Floor, x.i)))
            .OrderBy(x => Movement.Flat(pos, new Vector3(x.r.Pos[0], pos.Y, x.r.Pos[2])))
            .FirstOrDefault();
        if (next.r != null)
        {
            _searchedRooms.Add((next.r.Floor, next.i)); // setting out counts as searched; FindTarget re-checks every hop
            _lastSearchRoom = next.i;
            var centre = new Vector3(next.r.Pos[0], next.r.Pos[1], next.r.Pos[2]);
            return (centre, SearchReach, $"search {next.r.PoolName} (floor {next.r.Floor})", Identity.None, false);
        }

        if (_presses < MaxPresses)
        {
            var button = NearestButton(pos);
            if (button != Identity.None)
            {
                return (_items[button].Pos, ButtonReach, "a button to another floor", button, true);
            }
        }

        return (null, 0, "", Identity.None, false);
    }

    // The nearest unchecked server door of the room just searched, and the hop through it: a point
    // 3.5 m past the door on the line out of the room's centre, so the walk crosses the doorway and
    // whatever it hides streams. The exit door (room -1) is not a search target. Doors the server
    // has not streamed are not checkable - a room without streamed doors just searches as before.
    private (Vector3? pos, float reach, string what, Identity button, bool press) NextDoorHop(Vector3 pos, int myFloor)
    {
        var rm = _lastSearchRoom >= 0 && _nav?.Dungeon?.Rooms != null && _lastSearchRoom < _nav.Dungeon.Rooms.Count
            ? _nav.Dungeon.Rooms[_lastSearchRoom]
            : null;
        if (rm == null || rm.Floor != myFloor)
        {
            return (null, 0, "", Identity.None, false);
        }

        // Only behind a walk that arrived (the centre, or the previous door). A walk that timed
        // out or was yank-given-up says nothing about the doors, and checking them from a stuck
        // position burns a hop timeout per door - the next room is the better move.
        if (!_movement.IsGoalReached(ControlPriority.Mission))
        {
            return (null, 0, "", Identity.None, false);
        }

        var centre = new Vector3(rm.Pos[0], rm.Pos[1], rm.Pos[2]);
        Vector3? best = null;
        var bestDist = float.MaxValue;
        foreach (var sd in _serverDoors)
        {
            if (sd.pf != _missionPf || sd.room == -1 || Math.Abs(sd.pos.Y - rm.Pos[1]) > FloorBand)
            {
                continue;
            }

            var key = DoorKey(sd.pos);
            if (_checkedDoors.Contains(key) || !RoomCovers(rm, sd.pos))
            {
                continue; // not this room's door, or already crossed
            }

            var d = Movement.Flat(pos, sd.pos);
            if (d < bestDist)
            {
                bestDist = d;
                best = sd.pos;
            }
        }

        if (!best.HasValue)
        {
            return (null, 0, "", Identity.None, false);
        }

        _checkedDoors.Add(DoorKey(best.Value)); // setting out counts as checked; FindTarget re-checks every hop
        var outDir = best.Value - centre;
        outDir.Y = 0;
        if (outDir.Magnitude < 0.5f)
        {
            outDir = pos - best.Value; // door on the centre (never seen yet): cross away from us
            outDir.Y = 0;
        }

        outDir = outDir.Normalize();
        var through = best.Value + outDir * DoorThroughMetres;
        return (through, DoorCheckReach, $"the door of {rm.PoolName} at ({best.Value.X:0},{best.Value.Z:0})", Identity.None, false);
    }

    // A door belongs to the room when it stands in the wall around its tiles: the tile rect
    // (rotation-aware) probed at the door and 2.5 m out along both axes.
    private bool RoomCovers(NavDungeon.Room rm, Vector3 p)
    {
        if (_nav.Dungeon.CellOf(rm, p.X, p.Z, out _, out _))
        {
            return true;
        }

        return _nav.Dungeon.CellOf(rm, p.X + 2.5, p.Z, out _, out _)
               || _nav.Dungeon.CellOf(rm, p.X - 2.5, p.Z, out _, out _)
               || _nav.Dungeon.CellOf(rm, p.X, p.Z + 2.5, out _, out _)
               || _nav.Dungeon.CellOf(rm, p.X, p.Z - 2.5, out _, out _);
    }

    private static long DoorKey(Vector3 p) => (long)Math.Round(p.X * 2) * 10_000_000 + (int)Math.Round(p.Z * 2);

    private Identity NearestButton(Vector3 pos)
    {
        var best = Identity.None;
        var bestDist = float.MaxValue;
        foreach (var kv in _items)
        {
            if (!IsButton(kv.Value.Template) || Math.Abs(kv.Value.Pos.Y - pos.Y) > FloorBand)
            {
                continue; // only buttons standing on our floor can be pressed from here
            }

            var d = Movement.Flat(pos, kv.Value.Pos);
            if (d < bestDist)
            {
                bestDist = d;
                best = kv.Key;
            }
        }

        return best;
    }

    private void PressButton(LocalPlayer me, Identity button)
    {
        if (_pendingButton == button)
        {
            if (Movement.Flat(_pressedFrom, me.Transform.Position) > RideMetres)
            {
                _logger.LogInformation("MISSION: the button took me - planning on the new floor.");
                _pendingButton = Identity.None;
                _goalSet = false;
                _movement.ClearDesiredGoal(ControlPriority.Mission);
            }
            else if (_phaseTime - _pressedAt > RideWait)
            {
                _logger.LogInformation("MISSION: no ride from the button - pressing again.");
                _pressedAt = _phaseTime;
                GameCommands.UseObject(me, button);
                _presses++;
            }

            return;
        }

        // Stand on the spot a moment so the server has our stop, then press (AOBuddy10: presses
        // 17 ms after arrival were refused).
        if (!_goalSet || !_movement.IsGoalReached(ControlPriority.Mission))
        {
            return; // still walking there; WalkTo owns the approach
        }

        if (_pressedAt < 0 || _phaseTime - _pressedAt < DoorSettle)
        {
            _pressedAt = _phaseTime; // the settle beat on the reached spot
            return;
        }

        _pendingButton = button;
        _pressedAt = _phaseTime;
        _pressedFrom = me.Transform.Position;
        GameCommands.UseObject(me, button);
        _presses++;
        _logger.LogInformation($"MISSION: pressed button {_items[button].Template} at " +
                               $"({_items[button].Pos.X:0.0} {_items[button].Pos.Z:0.0}) (press {_presses}/{MaxPresses}).");
    }

    // ---- CLEAR MODE (AOBuddy10, owner 2026-09-25) -----------------------------------
    // Kill every mob in the building before the objective: the XP, and for Omni and Clan a side
    // token with the reward. The split is AOBuddy10's (MissionRun fought, MissionController
    // planned): THIS controller walks and books - fight what is on us (never the person we came
    // to find, even when he swings at us), close on the nearest pull one at a time, walk every
    // room (rooms count on ARRIVAL, never on setting out), ride buttons to floors with rooms
    // left, then walk the building again until the server's share of dead mobs passes THE OWNER'S
    // 90% LINE - only then the objective (a session without that line falls back to the own
    // count). The COMBAT BRAIN swings: it reads PullPick and the Awareness books and engages what
    // we walk to, in reach. The server counts the clear for us (the 110/79979934 float above);
    // the seen/dead sets below are the fallback.

    /// <summary>The mode toggle: 'mission clear on|off' (a session toggle, like follow/stay).</summary>
    public bool ClearOn;

    // 'mission clear on|off' and 'mission run clear on|off' - one toggle, both spellings.
    private void ToggleClearMode(string v, Action<string> reply)
    {
        if (v == "on" || v == "off")
        {
            ClearOn = v == "on";
        }

        reply("Clear mode: " + (ClearOn ? "ON" : "off") +
              (Active && ClearPct >= 0 ? $" ({ClearText} of this building)" : "") +
              " - every mob in the building dies before the objective (90% at least). " +
              "'mission clear on|off'.");
    }

    /// <summary>The server's share of the building's mobs dead, or -1 (no line seen this building).</summary>
    public float ClearPct { get; private set; } = -1;

    /// <summary>Clearing is on and still owed: the Blitz tick fights and walks rooms instead of
    /// searching for the objective. It holds until the server's share of dead mobs passes
    /// <see cref="ClearDonePct" /> - THE OWNER'S LINE (2026-10-09): the objective waits for 90% at
    /// least, however long the walk takes (the 3x blitz timeout is the backstop).</summary>
    public bool Clearing => ClearOn && _nav != null && _record != null && !_completed &&
                            !_clearGaveUp && _phase == Phase.Blitz && ClearPct < ClearDonePct;

    /// <summary>The share of dead mobs the objective waits for (owner, 2026-10-09: the last run
    /// finished after 2-3 kills of many - never again below this line).</summary>
    private const float ClearDonePct = 90f;

    /// <summary>The mob the walk is closing on - the combat brain engages it once in reach.</summary>
    public NpcChar PullPick;

    /// <summary>The person a find-person mission sent us to, once sighted: never an enemy. The
    /// moment he is selected the server shows him 'fighting' us though he never lands a blow, and
    /// the swing at him ran 12 and 70+ minutes in AOBuddy10 (Kirby Schatz, Levi McDannold) - and
    /// the owner: abstain from him even if he attacks while clearing.</summary>
    public Identity ClearFoeForbidden => _personId;

    // A room counts as walked by standing in it (ClearScan, every 0.5 s - passing through is
    // enough, not only reaching its centre) or when its hop ends without one (the walk timed out
    // or was given up: the room is not gettable from here). NEVER on setting out - the first run
    // skimmed whole floors that way and handed over to the objective after 2-3 kills. Keyed
    // (floor, room index) like _searchedRooms.
    private readonly HashSet<(int floor, int idx)> _clearVisited = new();

    // Where each live mob was last seen (second pass: only those rooms get walked again - the
    // owner: 'he often visits already cleared rooms, as if there still was a mob').
    private readonly Dictionary<Identity, (int floor, int idx)> _mobRoom = new();

    // Our own count: every mob seen alive in the building, and every one of them seen dead
    // (Health 0 - the server shows it on the death and the corpse). The find-person target is
    // neither.
    private readonly HashSet<Identity> _mobsSeen = new();
    private readonly HashSet<Identity> _mobsDead = new();

    private bool _clearGaveUp;
    private int _clearPasses;
    private double _clearScanAt = -1;
    private string _personRoomName;
    private int _personFloor = int.MinValue;
    private Identity _personId; // Identity.None until the person is sighted
    private Identity _pullId; // the pull pick's identity, kept while it lives
    private (int floor, int idx)? _currentClearRoom; // the room hop in flight - marked walked when it ends
    private readonly Dictionary<Identity, double> _pullAside = new(); // mob -> phase time it may be pulled again

    // AOBuddy10's PullTarget ranges: 20 m in sight (the mob in the next room is seen from its
    // doorway - 'you know the mob is in that room, kill it first'), 3 m of floor band, the walk
    // stands 4 m out - inside the brain's 6 m engage gate. A fight that reaches beyond the swing
    // (a shooter standing off) is walked up to 3 m out, still inside the 8 m fight gate.
    private const float PullSightMetres = 20f;
    private const float PullYBand = 3f;
    private const float PullReach = 4f;
    private const float FightWalkReach = 3f;
    private const float PullAsideSeconds = 60f;

    private void ClearTick(LocalPlayer me)
    {
        var pos = me.Transform.Position;
        ClearScan(me, pos);

        // FIGHT ON (AOBuddy10's 'clear mode: every fight is the job'): the body swinging, or a
        // real foe on us or our pets. The person we came to find is NOT one - he shows as
        // 'fighting' the bot the moment he is selected, and the owner: abstain from him even if
        // he attacks while clearing. In reach we stand and fight; beyond it we close.
        if (me.IsAttacking || RealFoeOnUs)
        {
            _currentClearRoom = null; // a fight replaces whatever hop was walking
            FightTick(me, pos);
            return;
        }

        // A walk the movement gave up from under us (no way through): a pull sits out a minute,
        // a room counts as walked (it is not gettable from here) - the next pick must not be the
        // same one every tick.
        if (_goalSet && !_movement.HasGoal(ControlPriority.Mission))
        {
            // A shut door at the goal gets picked before anything is given up on (owner,
            // 2026-10-09): the yank storm the walk just suffered was the closed leaf bouncing
            // the body, and twenty lockpick tries open it for the re-plan.
            var shut = ShutDoorNear(_goalPos);
            if (shut.HasValue)
            {
                StartPicking(shut.Value);
                return;
            }

            if (_pullId != Identity.None && _goalWhat.StartsWith("clear: the pull", StringComparison.Ordinal))
            {
                _pullAside[_pullId] = _phaseTime + PullAsideSeconds;
                _logger.LogInformation($"MISSION: clearing: no walk to the pull right now{DoorVerdict(_goalPos)} - it sits out a minute.");
                _pullId = Identity.None;
                PullPick = null;
            }
            else if (_currentClearRoom.HasValue)
            {
                _clearVisited.Add(_currentClearRoom.Value);
                _logger.LogInformation($"MISSION: clearing: no walk to the room right now{DoorVerdict(_goalPos)} - it counts as walked.");
                _currentClearRoom = null;
            }

            _goalSet = false;
        }

        // Hurt: no new pulls - the heal rest runs first (AOBuddy10 pulled only at 70%+; the one
        // time it pulled on after a fight it dragged five mobs and fled at 29%).
        var hp = HpPct(me);
        if (hp >= 0 && hp < 70)
        {
            return;
        }

        // A hop in flight owns the walk until it lands or times out (the search's gate): reached
        // falls through to the next move; the timeout ends the hop - a pull that never got walked
        // to sits out two minutes, the room in flight counts as walked.
        if (_goalSet && _movement.HasGoal(ControlPriority.Mission) &&
            !_movement.IsGoalReached(ControlPriority.Mission))
        {
            if (_phaseTime - _goalAt < SearchHopTimeout)
            {
                return;
            }

            if (_pullId != Identity.None && _goalWhat.StartsWith("clear: the pull", StringComparison.Ordinal))
            {
                _pullAside[_pullId] = _phaseTime + 2 * PullAsideSeconds;
                _logger.LogInformation("MISSION: clearing: the pull never arrived in time - it sits out two minutes.");
                _pullId = Identity.None;
                PullPick = null;
            }
            else if (_currentClearRoom.HasValue)
            {
                _clearVisited.Add(_currentClearRoom.Value);
                _logger.LogInformation("MISSION: clearing: the room never arrived in time - it counts as walked.");
                _currentClearRoom = null;
            }

            _movement.ClearDesiredGoal(ControlPriority.Mission);
            _goalSet = false;
        }

        // PULL: the nearest mob close by on our floor, one at a time ('find a mob, stop, kill it,
        // move on'). It replaces the room hop in flight - that room stays unmarked and is walked
        // after.
        var pick = PullTarget(me, pos);
        if (pick != null)
        {
            _currentClearRoom = null;
            WalkTo(pos, pick.Transform.Position, PullReach, $"clear: the pull '{pick.Name}'");
            return;
        }

        // ROOM WALK: the nearest room on my floor not yet walked - the person's rooms LAST (their
        // mobs die like the rest, only after every other room is done).
        var myFloor = FloorAt(pos);
        var next = _nav.Dungeon.Rooms
            .Select((r, i) => (r, i))
            .Where(x => x.r.Floor == myFloor && !_clearVisited.Contains((x.r.Floor, x.i)) && !IsPersonRoom(x.r))
            .OrderBy(x => Movement.Flat(pos, new Vector3(x.r.Pos[0], pos.Y, x.r.Pos[2])))
            .Cast<(NavDungeon.Room r, int i)?>()
            .FirstOrDefault()
            ?? _nav.Dungeon.Rooms
                .Select((r, i) => (r, i))
                .Where(x => x.r.Floor == myFloor && !_clearVisited.Contains((x.r.Floor, x.i)))
                .OrderBy(x => Movement.Flat(pos, new Vector3(x.r.Pos[0], pos.Y, x.r.Pos[2])))
                .Cast<(NavDungeon.Room r, int i)?>()
                .FirstOrDefault();
        if (next.HasValue)
        {
            var (rm, idx) = next.Value;
            _currentClearRoom = (rm.Floor, idx); // marked walked on arrival (standing) or when the hop ends
            _lastSearchRoom = idx; // NextDoorHop streams the room's inner sections on arrival
            var centre = new Vector3(rm.Pos[0], rm.Pos[1], rm.Pos[2]);
            WalkTo(pos, centre, SearchReach, $"clear: {rm.PoolName} (floor {rm.Floor})");
            return;
        }

        // BEHIND THE DOORS of the room just set out to before the next room: an inner section
        // streams only once the body crosses its door - the search's own rule, and mobs hide
        // behind those doors too.
        var doorHop = NextDoorHop(pos, myFloor);
        if (doorHop.pos.HasValue)
        {
            WalkTo(pos, doorHop.pos.Value, doorHop.reach, doorHop.what);
            return;
        }

        // FLOOR CHANGE: a floor with rooms left is reached by a button, as in the search. (The
        // room walk cleared MY floor: this fires only when another floor still has rooms.)
        if (_presses < MaxPresses && _nav.Dungeon.Rooms
                .Select((r, i) => (r, i))
                .Any(x => !_clearVisited.Contains((x.r.Floor, x.i))))
        {
            var button = NearestButton(pos);
            if (button != Identity.None)
            {
                _currentClearRoom = null;
                WalkTo(pos, _items[button].Pos, ButtonReach, "clear: the button to another floor");
                PressButton(me, button);
                return;
            }
        }

        // Every reachable room is walked. What now is THE OWNER'S RULE (2026-10-09): the last run
        // finished after 2-3 kills of many - the objective waits for the server's share of dead
        // mobs to pass the 90% line, however many passes that takes (mobs wander into view; the
        // 3x blitz timeout is the backstop). Only a session the server never sends a share to
        // falls back to AOBuddy10's own count: one narrowing pass over the rooms live mobs were
        // last seen in, then every mob seen dead - or give up.
        if (ClearPct >= 0)
        {
            _clearPasses++;
            _clearVisited.Clear(); // a full pass again: ClearScan re-marks the rooms we stand in
            _currentClearRoom = null;
            _logger.LogInformation($"MISSION: clearing - {ClearText}, below the {ClearDonePct:0}% line; " +
                                   $"walking the building again (pass {_clearPasses + 1}).");
            return;
        }

        if (_clearPasses == 0 && _mobRoom.Count > 0)
        {
            var again = new HashSet<(int floor, int idx)>(_mobRoom.Values);
            _clearPasses = 1;
            _currentClearRoom = null;
            foreach (var rm in _nav.Dungeon.Rooms.Select((r, i) => (r, i)))
            {
                var key = (rm.r.Floor, rm.i);
                if (again.Contains(key))
                {
                    _clearVisited.Remove(key);
                }
                else
                {
                    _clearVisited.Add(key);
                }
            }

            _logger.LogInformation($"MISSION: clearing ({ClearText}) - walked every room I can reach; " +
                                   $"back to the {again.Count} room(s) where mobs were last seen.");
            return;
        }

        _clearGaveUp = true;
        PullPick = null;
        if (_mobsSeen.Count > 0 && _mobsDead.Count == _mobsSeen.Count)
        {
            // No 'x% cleared' line this session, but every mob we saw is dead: cleared by our own count.
            _logger.LogInformation($"MISSION: cleared by my count - all {_mobsSeen.Count} mob(s) seen are dead " +
                                   "and every reachable room is walked; on to the objective.");
            Tell($"Cleared it: all {_mobsSeen.Count} mobs I saw are dead; doing the objective.");
        }
        else
        {
            _logger.LogInformation($"MISSION: clear mode gave up at {ClearText} - no 'x% cleared' line from the " +
                                   "server; on to the objective.");
            Tell($"Couldn't clear this one ({ClearText}); doing the objective.");
        }
    }

    // A REAL foe on us or our pets - the person we came to find does not count, whatever the
    // server shows him doing.
    private bool RealFoeOnUs
    {
        get
        {
            foreach (var s in _awareness.OnBot.Concat(_awareness.OnPets))
            {
                if (s.Mob != null && s.Mob.Identity != _personId)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // The fight in progress: an engaged foe beyond the swing reach is walked up to (a shooter
    // standing off must not pin the body - AOBuddy10 walked up to out-of-reach mobs); in reach
    // the walk goal is cleared once so the body stands and fights.
    private void FightTick(LocalPlayer me, Vector3 pos)
    {
        var foe = FoeInRange(me, FightWalkReach + 5f) ?? FoeInRange(me, 25f);
        if (foe != null && Movement.Flat(pos, foe.Transform.Position) > FightWalkReach)
        {
            WalkTo(pos, foe.Transform.Position, FightWalkReach, $"fight: '{foe.Name}'");
            return;
        }

        if (_goalSet && _movement.HasGoal(ControlPriority.Mission))
        {
            _movement.ClearDesiredGoal(ControlPriority.Mission);
            _goalSet = false;
        }
    }

    // The nearest live attacker of us or our pets within reach - never the person we came to find.
    private NpcChar FoeInRange(LocalPlayer me, float maxMetres)
    {
        NpcChar best = null;
        var bestD = float.MaxValue;
        foreach (var s in _awareness.OnBot.Concat(_awareness.OnPets))
        {
            var n = s.Mob;
            if (n == null || n.Identity == _personId || DynelManager.Dead.Contains(n.Identity) ||
                (n.TryGetStat(Stat.Health, out var hp) && hp <= 0))
            {
                continue;
            }

            var d = Movement.Flat(me.Transform.Position, n.Transform.Position);
            if (d < bestD && d <= maxMetres)
            {
                bestD = d;
                best = n;
            }
        }

        return best;
    }

    // The pull pick, kept while it lives and in sight (one fight at a time); null when nothing is
    // worth closing on.
    private NpcChar PullTarget(LocalPlayer me, Vector3 pos)
    {
        if (_pullId != Identity.None)
        {
            var held = DynelManager.Find(_pullId, out NpcChar cur) ? cur : null;
            if (held != null && Pullable(me, held) &&
                Movement.Flat(pos, held.Transform.Position) <= PullSightMetres + 10f)
            {
                PullPick = held;
                return held;
            }

            _pullId = Identity.None;
            PullPick = null;
        }

        foreach (var n in DynelManager.Npcs
                     .Where(n => n != null && Pullable(me, n))
                     .Where(n => Movement.Flat(pos, n.Transform.Position) <= PullSightMetres &&
                                 Math.Abs(n.Transform.Position.Y - pos.Y) <= PullYBand)
                     .OrderBy(n => Movement.Flat(pos, n.Transform.Position)))
        {
            _pullId = n.Identity;
            PullPick = n;
            _logger.LogInformation($"MISSION: clearing ({ClearText}): going for '{n.Name}' " +
                                   $"({Movement.Flat(pos, n.Transform.Position):0} m).");
            return n;
        }

        return null;
    }

    // Fit to fight: not a pet (ours or anyone's), alive, not the person we came to find, not
    // sitting out a pull refusal.
    private bool Pullable(LocalPlayer me, NpcChar n)
    {
        return !n.Owner.HasValue
               && n.Identity != _personId
               && !me.Pets.Any(p => p.Identity == n.Identity)
               && !DynelManager.Dead.Contains(n.Identity)
               && (!n.TryGetStat(Stat.Health, out var hp) || hp > 0)
               && (!_pullAside.TryGetValue(n.Identity, out var until) || _phaseTime > until);
    }

    // Every half second while clearing: the room we stand in counts as walked, every mob in sight
    // is placed in its room, dead ones drop out - and the find-person target, once sighted, marks
    // his room(s) for last (AOBuddy10's ClearScan).
    private void ClearScan(LocalPlayer me, Vector3 pos)
    {
        if (_phaseTime - _clearScanAt < 0.5)
        {
            return;
        }

        _clearScanAt = _phaseTime;
        var here = RoomIndexAt(pos);
        if (here.HasValue)
        {
            _clearVisited.Add(here.Value);
        }

        foreach (var n in DynelManager.Npcs)
        {
            if (n == null || n.Owner.HasValue || n.Identity == _personId ||
                me.Pets.Any(p => p.Identity == n.Identity))
            {
                continue;
            }

            if (n.TryGetStat(Stat.Health, out var hp) && hp <= 0)
            {
                if (_mobsSeen.Contains(n.Identity))
                {
                    _mobsDead.Add(n.Identity);
                }

                _mobRoom.Remove(n.Identity);
                continue;
            }

            var room = RoomIndexAt(n.Transform.Position);
            if (room.HasValue)
            {
                _mobsSeen.Add(n.Identity);
                _mobRoom[n.Identity] = room.Value;
            }
        }

        // THE PERSON: sighted once, never fought, his rooms (every entry with that name - a
        // duplicated pool room shares the name) come last.
        if (_record.Type == TypeFindPerson && _personId == Identity.None)
        {
            SimpleChar person = null;
            if (_record.TargetA.HasValue)
            {
                DynelManager.Find(_record.TargetA.Value, out SimpleChar byId);
                person = byId;
            }

            if (person == null)
            {
                // No record id (or not streamed yet): the name-match rule FindTarget uses.
                var text = _record.Text ?? "";
                person = DynelManager.Npcs.FirstOrDefault(npc => npc != null && !npc.Owner.HasValue &&
                                                                 !string.IsNullOrEmpty(npc.Name) && npc.Name.Length >= 4 &&
                                                                 text.IndexOf(npc.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (person != null)
            {
                _personId = person.Identity;
                _personFloor = FloorAt(person.Transform.Position);
                var proom = RoomIndexAt(person.Transform.Position);
                _personRoomName = proom.HasValue ? _nav.Dungeon.Rooms[proom.Value.idx].PoolName : null;
                _logger.LogInformation($"MISSION: '{person.Name}', the mission's target, is in room " +
                                       $"'{_personRoomName ?? "?"}' on floor {_personFloor}; " +
                                       "clearing the other rooms first.");
            }
        }
    }

    // The room covering a spot: the floor rule of FloorAt (nearest tile height), then among the
    // rooms of that floor the one whose cells cover the spot, nearest centre as the tiebreak.
    // Null between rooms (the void the corridors cross).
    private (int floor, int idx)? RoomIndexAt(Vector3 p)
    {
        var bestY = double.MaxValue;
        foreach (var rm in _nav.Dungeon.Rooms)
        {
            var y = _nav.Dungeon.FloorHeight(rm, p.X, p.Z);
            if (!double.IsNaN(y))
            {
                bestY = Math.Min(bestY, Math.Abs(y - p.Y));
            }
        }

        if (bestY == double.MaxValue)
        {
            return null;
        }

        (int floor, int idx)? best = null;
        var bestFlat = double.MaxValue;
        var rooms = _nav.Dungeon.Rooms;
        for (var i = 0; i < rooms.Count; i++)
        {
            var rm = rooms[i];
            var y = _nav.Dungeon.FloorHeight(rm, p.X, p.Z);
            if (double.IsNaN(y) || Math.Abs(y - p.Y) > bestY + 1.0 ||
                !_nav.Dungeon.CellOf(rm, p.X, p.Z, out _, out _))
            {
                continue;
            }

            var d = Movement.Flat(p, new Vector3(rm.Pos[0], p.Y, rm.Pos[2]));
            if (d < bestFlat)
            {
                bestFlat = d;
                best = (rm.Floor, i);
            }
        }

        return best;
    }

    // Every room entry with the person's name on his floor is his (duplicated pool rooms share
    // the pool name).
    private bool IsPersonRoom(NavDungeon.Room rm)
    {
        return _personRoomName != null && rm.Floor == _personFloor && rm.PoolName == _personRoomName;
    }

    private string ClearText => ClearPct >= 0
        ? $"{ClearPct:0.#}% cleared"
        : _mobsSeen.Count == 0
            ? "no mob seen yet"
            : $"{_mobsDead.Count} of {_mobsSeen.Count} mobs seen dead";

    private static int HpPct(LocalPlayer me)
    {
        return me.TryGetStat(Stat.Health, out var hp) && me.TryGetStat(Stat.MaxHealth, out var max) && max > 0
            ? (int)(100.0 * Math.Min(hp, max) / max)
            : -1;
    }

    // ---- The objective -----------------------------------------------------------

    private void BeginAct(LocalPlayer me, Identity target)
    {
        // Stand still while selecting: the walk goal is spent either way.
        _movement.ClearDesiredGoal(ControlPriority.Mission);
        _goalSet = false;
        _acts++;
        _actTarget = target;
        _actedAt = _phaseTime;
        var isContainer = target.Type == IdentityType.Container;
        switch (_record.Type)
        {
            case TypeFindPerson:
                // What the client sent: InfoRequest, then LookAt with ReturnInfo=1 - the mission
                // completed 0.47 s later in the capture. Never an attack.
                Client.InfoRequest(target);
                Client.Send(new LookAtMessage { Target = target, ReturnInfo = 1 });
                _logger.LogInformation($"MISSION: selected {target} (find person), act {_acts}/{MaxActs}.");
                break;
            case TypeFindItem:
                // One LookAt with ReturnInfo=0 on the floor item; a container holding it is opened
                // first. Nothing is picked up - the item stays where it is.
                if (isContainer)
                {
                    GameCommands.OpenContainer(me, target);
                }

                Client.Send(new LookAtMessage { Target = target, ReturnInfo = 0 });
                _logger.LogInformation($"MISSION: selected item {target} (find item), act {_acts}/{MaxActs}.");
                break;
        }
    }

    private void MarkCompleted(string how)
    {
        if (_completed || _current == null || _phase is not (Phase.Blitz or Phase.EnterDoor or Phase.ToDoor))
        {
            return;
        }

        _completed = true;
        _actTarget = null;
        _logger.LogInformation($"MISSION: COMPLETED ({how}) - {TypeName(_current.MissionIcon)} in " +
                               $"{Zoning.Name(_current.Playfield.Instance)}.");
        // The want list records what its runs collected: a fitting reward's template goes in the
        // got set, so 'list' mode knows the entry is had (AOBuddy10's MarkDone).
        if (_wantRun && _current.MissionItemData != null)
        {
            foreach (var r in _current.MissionItemData)
            {
                if (Wants.Entries.Any(e => WantList.Fits(e, r.LowId, r.Ql)) && Wants.Got.Add(r.LowId))
                {
                    Wants.Save();
                    Tell($"Got a wanted item: {WantList.NameOf(r.LowId) ?? r.LowId.ToString()} QL {r.Ql}.");
                }
            }
        }
    }

    // ---- The target ----------------------------------------------------------------

    private Identity? FindTarget(LocalPlayer me, out Vector3? pos, out string how)
    {
        pos = null;
        how = "";
        if (_record == null)
        {
            how = "no quest record";
            return null;
        }

        if (_record.Type != TypeFindPerson && _record.Type != TypeFindItem)
        {
            how = $"{_record.TypeName} is not supported in blitz";
            return null;
        }

        if (_record.TargetA.HasValue)
        {
            how = "the quest record's target";
            if (_record.TargetA.Value.Type == (IdentityType)0xC74E)
            {
                // An item-TYPE reference: the target is whichever seen item carries that template
                // (OmniCell RecordData - the record names WHICH item, not which object).
                foreach (var kv in _items)
                {
                    if (kv.Value.Template == _record.TargetA.Value.Instance)
                    {
                        pos = kv.Value.Pos;
                        return kv.Key;
                    }
                }

                how = $"no seen item carries template {_record.TargetA.Value.Instance}";
                return null;
            }

            if (_items.TryGetValue(_record.TargetA.Value, out var it))
            {
                pos = it.Pos;
                return _record.TargetA.Value;
            }

            if (DynelManager.Find(_record.TargetA.Value, out SimpleChar c))
            {
                pos = c.Transform.Position;
                return _record.TargetA.Value;
            }

            how = $"target {_record.TargetA.Value} not seen yet";
            return null;
        }

        // No target in the record: match by name against the mission text (a teammate's copy, and
        // any record layout the parser could not read).
        var text = _record.Text ?? "";
        if (_record.Type == TypeFindPerson)
        {
            foreach (var npc in DynelManager.Npcs)
            {
                if (string.IsNullOrEmpty(npc.Name) || npc.Name.Length < 4 || npc.Owner.HasValue)
                {
                    continue;
                }

                if (text.IndexOf(npc.Name, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                how = $"'{npc.Name}', named in the mission text";
                pos = npc.Transform.Position;
                return npc.Identity;
            }

            how = "nobody in sight is named in the mission text";
            return null;
        }

        foreach (var kv in _items.Where(kv => !IsButton(kv.Value.Template)))
        {
            var name = ItemName(kv.Value.Template);
            if (name.Length >= 4 && text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                how = $"'{name}', named in the mission text";
                pos = kv.Value.Pos;
                return kv.Key;
            }
        }

        how = "no item in sight is named in the mission text";
        return null;
    }

    private static string ItemName(int template)
    {
        try
        {
            var item = new Item(template, template, 1);
            if (!string.IsNullOrEmpty(item.Name))
            {
                return item.Name;
            }
        }
        catch
        {
            // no item data for it
        }

        return $"item {template}";
    }

    // The floor we stand on: the room whose tile height is nearest our Y among the rooms covering
    // this spot; none covering, the nearest room by flat distance.
    // ── /nav for the monitor (AOBuddy10 MissionRun.NavJson's mission object) ─────────────

    // The monitor composes the IDENTICAL floor plan from its own GameData pool files via
    // NavData.ComposeMission - the zone-in placement is a handful of ints per room and changes
    // never, so only it travels the API. Served on the update thread (BotApiService.BuildNav),
    // where every piece of this state is written.
    public bool NavInMission => _nav != null && (int)Playfield.ModelId == _missionPf;

    /// <summary>The /nav "mission" object: the current mission, the building's placement, the
    /// server's doors and the floor the body stands on. Null when there is nothing to draw.</summary>
    public JObject NavMissionJson(Vector3? mePos)
    {
        var mj = new JObject();
        if (_current != null)
        {
            mj["line"] = $"{TypeName(_current.MissionIcon)} in {Zoning.Name(_current.Playfield.Instance)} " +
                         $"({_current.Location.X:0},{_current.Location.Z:0})";
            mj["pf"] = _current.Playfield.Instance;
            mj["door"] = new JArray(Math.Round(_current.Location.X, 1), Math.Round(_current.Location.Y, 1), Math.Round(_current.Location.Z, 1));
        }

        if (_nav?.Layout == null)
        {
            return mj.Count > 0 ? mj : null;
        }

        var lay = _nav.Layout;
        mj["layout"] = new JObject
        {
            ["instance"] = lay.Instance,
            ["poolPf"] = lay.TemplatePlayfield,
            ["width"] = lay.Width,
            ["height"] = lay.Height,
            ["worldHeight"] = lay.WorldHeight,
            ["land"] = new JArray(Math.Round(lay.LandX, 1), Math.Round(lay.LandY, 1), Math.Round(lay.LandZ, 1)),
            ["rooms"] = new JArray(lay.Rooms.Select(r => new JArray(r[0], r[1], r[2], r[3], r[4]))),
        };

        mj["floors"] = new JArray(_nav.Dungeon.Rooms.Select(r => r.Floor).Distinct().OrderBy(f => f).Select(f => new JValue(f)));

        // The server's own doors (DoorFullUpdate), re-keyed with the compose's pf like the
        // planner's - the monitor draws them on the plan with their state bits. DoorStatusUpdate
        // is not tracked yet: the streamed state is the zone-in snapshot (owner, 2026-10-09).
        var doors = new JArray();
        var doorNo = 0;
        foreach (var d in _serverDoors.Where(x => x.pf == _missionPf))
        {
            doors.Add(new JObject
            {
                ["id"] = d.adjoining >= 0 ? $"r{d.room}~r{d.adjoining}#{doorNo}" : $"r{d.room}#{doorNo}",
                ["pos"] = new JArray(Math.Round(d.pos.X, 2), Math.Round(d.pos.Y, 2), Math.Round(d.pos.Z, 2)),
                ["room"] = (int)d.room,
                ["adjoiningRoom"] = (int)d.adjoining,
                ["floor"] = FloorAt(d.pos),
                ["state"] = DoorState(d.flags),
            });
            doorNo++;
        }

        mj["doors"] = doors;

        if (mePos.HasValue && NavInMission)
        {
            mj["floor"] = FloorAt(mePos.Value);
        }

        return mj;
    }

    private int FloorAt(Vector3 pos)
    {
        var best = 0;
        var bestY = double.MaxValue;
        var bestFlat = double.MaxValue;
        foreach (var r in _nav.Dungeon.Rooms)
        {
            var y = _nav.Dungeon.FloorHeight(r, pos.X, pos.Z);
            if (!double.IsNaN(y))
            {
                var dy = Math.Abs(y - pos.Y);
                if (dy < bestY)
                {
                    bestY = dy;
                    best = r.Floor;
                }

                continue;
            }

            var flat = Movement.Flat(pos, new Vector3(r.Pos[0], pos.Y, r.Pos[2]));
            if (flat < bestFlat && bestY == double.MaxValue)
            {
                bestFlat = flat;
                best = r.Floor;
            }
        }

        return best;
    }

    // ---- Phase: RewardBag ---------------------------------------------------------

    private void RewardBagTick(LocalPlayer me)
    {
        if (_nav == null)
        {
            // Outside: anything not bagged stays; bags full means SELL first (step 7), then the
            // terminal (step 8 loops back to step 1).
            AfterOutsideCheck(me);
            return;
        }

        // STEP 5, inside: the reward into a loot bag. The server already paid it out (overflow
        // window) and the SDK moved it to the next free inventory slot - it only needs a bag.
        if (_phaseTime < RewardSettle)
        {
            return;
        }

        if (_stashing != null)
        {
            StashTick();
            return;
        }

        var reward = RewardsInInventory().FirstOrDefault();
        if (reward == null)
        {
            StartLeaving();
            return;
        }

        if (_phaseTime > RewardTimeout)
        {
            Tell("The reward wouldn't move into a bag in time - it stays in my main inventory.");
            StartLeaving();
            return;
        }

        var bag = NextBagWithRoom(me);
        if (bag == null)
        {
            // A bag was just opened (its contents land next ticks) - unless every designated bag
            // has been refused or read full, or none is in the packs: then say so and leave.
            if (LootBagsOnMe().All(b => _fullBags.Contains(b.UniqueIdentity)))
            {
                Tell(_lootBags.Bags.Count == 0
                    ? "No loot bag designated - the reward stays in my main inventory ('lootbag add N' for next time)."
                    : "No loot bag has room - the reward stays in my main inventory.");
                StartLeaving();
            }

            return;
        }

        _stashing = reward;
        _stashAt = _phaseTime;
        reward.MoveToContainer(bag.UniqueIdentity);
        _logger.LogInformation($"MISSION: reward '{reward.Name}' moving into bag {bag.UniqueIdentity}.");
    }

    private void StashTick()
    {
        // The SDK's view of a bag is from when it was opened - a bag our own last move filled still
        // reads as having room. The item still in the inventory two seconds later is the refusal.
        var still = Inventory.Items.Any(i => i != null && i.UniqueIdentity == _stashing.UniqueIdentity &&
                                             i.Slot.Type == IdentityType.Inventory);
        if (!still)
        {
            _logger.LogInformation("MISSION: reward stashed.");
            _stashing = null;
            SetPhase(Phase.RewardBag);
            return;
        }

        if (_phaseTime - _stashAt < BagRefuseSeconds)
        {
            return;
        }

        _fullBags.Add(_stashBag);
        _logger.LogInformation("MISSION: the bag refused the reward (full) - trying the next.");
        _stashing = null;
        SetPhase(Phase.RewardBag);
    }

    private IEnumerable<Item> RewardsInInventory()
    {
        return Inventory.Items.Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                                          i.UniqueIdentity.Type != IdentityType.MissionKey &&
                                          _rewardIds.Any(r => i.Id == r.low || i.HighId == r.high || i.Id == r.high));
    }

    private Item NextBagWithRoom(LocalPlayer me)
    {
        // The designated bags, in pack order, that still have room and haven't refused this phase.
        foreach (var bag in LootBagsOnMe())
        {
            if (_fullBags.Contains(bag.UniqueIdentity))
            {
                continue;
            }

            var container = Inventory.Containers.FirstOrDefault(c => c.Identity == bag.UniqueIdentity);
            if (container == null)
            {
                // Contents never delivered: open it and give the wire a beat - then use it anyway,
                // the two-second refusal check is the judge of a full bag.
                if (_bagJustOpened != bag.UniqueIdentity)
                {
                    _bagJustOpened = bag.UniqueIdentity;
                    _bagOpenedAt = _phaseTime;
                    GameCommands.OpenContainer(me, bag.Slot);
                    _logger.LogInformation($"MISSION: opening bag {bag.UniqueIdentity} to see its room.");
                    return null;
                }

                if (_phaseTime - _bagOpenedAt < BagOpenBeat)
                {
                    return null;
                }
            }
            else if (container.NumFreeSlots <= 0)
            {
                _fullBags.Add(bag.UniqueIdentity);
                continue;
            }

            _stashBag = bag.UniqueIdentity;
            return bag;
        }

        return null;
    }

    // The designated bags that are actually in the packs right now, pack order.
    private List<Item> LootBagsOnMe()
    {
        return Inventory.Items
            .Where(i => i != null && i.Slot.Type == IdentityType.Inventory &&
                        i.UniqueIdentity.Type == IdentityType.Container && _lootBags.IsLootBag(i.UniqueIdentity))
            .OrderBy(i => i.Slot.Instance)
            .ToList();
    }

    // ---- Phase: Leaving ------------------------------------------------------------

    private void StartLeaving()
    {
        // STEP 6: out of the building, fighting nothing. The exit is the building's own doorway
        // (the entrance room's one that faces no neighbour); the landing point is the fallback.
        _goalSet = false;
        _doorStage = 0;
        _exitStands = 0;
        SetPhase(Phase.Leaving);
        var ex = _nav?.Exit;
        Tell(ex != null ? "Done - heading out through the exit door." : "Done - heading out the way I came in.");
        _logger.LogInformation(ex != null
            ? $"MISSION: leaving via the exit door on floor {ex.Floor} ({ex.X:0},{ex.Z:0})."
            : "MISSION: no exit doorway known - leaving via the zone-in landing point.");
    }

    private void LeavingTick(LocalPlayer me)
    {
        var ex = _nav?.Exit;
        var landing = _nav?.Layout == null
            ? me.Transform.Position
            : new Vector3(_nav.Layout.LandX, _nav.Layout.LandY, _nav.Layout.LandZ);
        var doorPos = _serverExit ?? (ex != null ? new Vector3((float)ex.X, (float)ex.Y, (float)ex.Z) : landing);
        var nx = ex?.Nx ?? 0f;
        var nz = ex?.Nz ?? 0f;

        switch (_doorStage)
        {
            case 0:
            {
                // 1.5 m INSIDE the exit door first ('+' caused a server snapback every time, '-' worked).
                var inside = new Vector3(doorPos.X - nx * 1.5f, doorPos.Y, doorPos.Z - nz * 1.5f);
                WalkTo(me.Transform.Position, inside, 1.5f, "the exit door (inside)");
                if (_movement.IsGoalReached(ControlPriority.Mission) || Movement.Flat(me.Transform.Position, inside) <= 1.5f)
                {
                    _doorStage = 1;
                    _goalSet = false;
                    _goalAt = -1;
                }
                else if (_phaseTime > ExitStageTimeout)
                {
                    DropMission("never reached the exit door");
                }

                break;
            }
            case 1:
            {
                // ONTO the door's own coordinates and stand there.
                WalkTo(me.Transform.Position, doorPos, 0.4f, "the exit door itself");
                if (_movement.IsGoalReached(ControlPriority.Mission))
                {
                    if (_goalAt < 0)
                    {
                        _goalAt = _phaseTime;
                    }

                    if (_phaseTime - _goalAt >= ExitDoorStand)
                    {
                        // Standing on it didn't take: one metre out along the door's normal.
                        _doorStage = 2;
                        _goalSet = false;
                        _goalAt = -1;
                        _exitStands++;
                        _logger.LogInformation($"MISSION: pushing a metre out of the exit door (stand {_exitStands}).");
                    }
                }
                else if (_goalAt > 0 && _phaseTime - _goalAt > ExitStageTimeout)
                {
                    DropMission("never got onto the exit door");
                }

                break;
            }
            default:
            {
                var outPos = new Vector3(doorPos.X + nx, doorPos.Y, doorPos.Z + nz);
                WalkTo(me.Transform.Position, outPos, 1.0f, "a metre out of the exit door");
                if (_goalAt < 0)
                {
                    _goalAt = _phaseTime;
                }

                if (_phaseTime - _goalAt > ExitStageTimeout)
                {
                    if (_exitStands >= MaxExitStands)
                    {
                        DropMission("the exit door won't take me");
                        return;
                    }

                    _doorStage = 1; // onto the door again
                    _goalSet = false;
                    _goalAt = -1;
                }

                break;
            }
        }
    }

    // ---- Mission bookkeeping ---------------------------------------------------------

    // The held missions are deleted as the owner's client does it: QuestMessage Delete per quest
    // (capture 20260910-200346 client seq 54). Deleting a mission removes no mission key - the
    // stale ones stay in the packs (AOBuddy10 learned that the hard way, 2026-09-26).
    private void DeleteHeld(string why)
    {
        var keys = MissionKeys();
        foreach (var id in _heldQuests.Keys.ToList())
        {
            Client.Send(new QuestMessage { Action = QuestAction.Delete, Mission = id });
            _logger.LogInformation($"MISSION: deleted held mission {id} ({why}).");
        }

        _heldQuests.Clear();

        // QuestAction.Delete makes the server vanish the door key (a live client sees it go) - but
        // the removal echo is not guaranteed to reach a clientless session's inventory model, so
        // the key would linger THERE (and in the monitor's item view). Drop it locally and fire the
        // same event the wire removal would.
        foreach (var key in keys)
        {
            Inventory.RemoveItem(key);
            Inventory.ItemRemoved?.Invoke(key);
        }

        if (keys.Count > 0)
        {
            _logger.LogInformation($"MISSION: dropped {keys.Count} mission key(s) from the packs ({why}).");
        }
    }

    // A mission that can't be finished is deleted and the loop rolls on: only the owner stops the run.
    private void DropMission(string why)
    {
        _logger.LogInformation($"MISSION: dropping the mission - {why}.");
        Tell($"Dropping the mission ({why}) - rolling another.");
        DeleteHeld(why);
        _current = null;
        _completed = false;

        if (_nav != null)
        {
            // Inside: out first, then the terminal.
            _goalSet = false;
            _doorStage = 0;
            _exitStands = 0;
            SetPhase(Phase.Leaving);
        }
        else
        {
            _movement.CancelTravel();
            _movement.ClearDesiredGoal(ControlPriority.Mission);
            _goalSet = false;
            SetPhase(Phase.ToTerminal);
        }
    }

    // The held mission rebuilt from the quest log - how a restart finds its way back (AOBuddy10's
    // resume). The typed Quest gives identity, type code and reward items; the raw log gives the
    // door. Null when the log holds no terminal mission, or its door couldn't be read (then the
    // run just rolls fresh and the held mission stays held).
    private MissionInfo HeldMission()
    {
        var quest = _heldQuests.Count == 0 ? null : _heldQuests.Values.First();
        if (quest?.QuestId == null || quest.QuestId == Identity.None)
        {
            return null;
        }

        var door = DoorFromQuestLog();
        if (door == null && _nav == null)
        {
            // Outside the door matters; INSIDE a mission it does not (the exit comes from the
            // composed building) and the raw login log may not carry it at all - owner,
            // 2026-10-03: relogged inside the Subway, the held quest then read as unresumable.
            _logger.LogWarning("MISSION: the quest log holds a mission but its door could not be read from the raw log.");
            return null;
        }

        var m = new MissionInfo
        {
            MissionIdentity = quest.QuestId,
            MissionIcon = quest.MissionIconId,
            Playfield = new Identity(IdentityType.Playfield2, door?.pf ?? _missionPf),
            MissionItemData = quest.MissionItemData ?? Array.Empty<MissionItemReward>(),
            Location = door?.at ?? new Vector3(0, 0, 0),
        };
        _logger.LogInformation("MISSION: the quest log holds " + Line(m) + $" [{m.MissionIdentity}]" +
                               (quest.MissionItemData == null || quest.MissionItemData.Length == 0
                                   ? " (no reward items in the log entry)."
                                   : $", rewards: {string.Join(", ", quest.MissionItemData.Select(RewardName))}."));
        return m;
    }

    // The door positions in the raw quest log: each held mission's destination sits in it as an
    // Identity(Playfield2, pf) followed 8 bytes later by the x/y/z floats (AOBuddy10's FromQuestLog,
    // checked on capture 20260923-201746). First hit wins - a character holds one terminal mission.
    private (int pf, Vector3 at)? DoorFromQuestLog()
    {
        var b = _questRaw;
        if (b == null)
        {
            return null;
        }

        for (var i = 0; i + 28 <= b.Length; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 0x9C || b[i + 3] != 0x50)
            {
                continue; // 0x9C50, the Playfield2 identity type
            }

            var pf = (b[i + 4] << 24) | (b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7];
            if (pf <= 0 || pf > 20000)
            {
                continue;
            }

            var x = BeFloat(b, i + 16);
            var y = BeFloat(b, i + 20);
            var z = BeFloat(b, i + 24);
            if (x > 0 && x < 10000 && z > 0 && z < 10000 && y > -500 && y < 3000)
            {
                return (pf, new Vector3(x, y, z));
            }
        }

        return null;
    }

    private static float BeFloat(byte[] b, int i)
    {
        return BitConverter.ToSingle(new[] { b[i + 3], b[i + 2], b[i + 1], b[i] }, 0);
    }

    // The mission key rides in the packs from the accept on (the header: the key only rides along);
    // its name names the building - how a restart shows what the held quest is for (AOBuddy10 MissionKeys).
    private static List<Item> MissionKeys()
    {
        // bags included: the key arrives INSIDE a bag (BAGPROBE: owner = the bag's identity), so
        // the loose-inventory filter missed it and the delete never found it
        return Inventory.Items.Where(i => i != null && i.UniqueIdentity.Type == IdentityType.MissionKey).ToList();
    }

    private static string KeysText()
    {
        var keys = MissionKeys();
        return keys.Count == 0
            ? "no mission key in my packs"
            : $"mission key '{keys[0].Name ?? keys[0].UniqueIdentity.ToString()}' in my packs";
    }

    // ---- The sell step (STEP 7) ---------------------------------------------------

    private void AfterOutsideCheck(LocalPlayer me)
    {
        var withRoom = LootBagsOnMe().Any(b =>
        {
            var c = Inventory.Containers.FirstOrDefault(x => x.Identity == b.UniqueIdentity);
            return c != null && c.NumFreeSlots > 0;
        });
        var mainFull = Inventory.NumFreeSlots <= 1;
        if (withRoom && !mainFull)
        {
            ToTerminalAfterSell();
            return;
        }

        _logger.LogInformation($"MISSION: bags full (loot bag with room: {withRoom}, free slots: {Inventory.NumFreeSlots}) - selling.");
        Tell("Loot bags are full - selling the bag contents, then back to the terminal.");
        _sellWaitAt = _phaseTime;
        SetPhase(Phase.WaitingSell);
        _sell.Command(new[] { "sell" }, s => Tell(s));
    }

    private void ToTerminalAfterSell()
    {
        // The sell run held the arbiter at Selling and released it when it ended - the run owns
        // the body again from here.
        _controlArbiter.TakeControl(ControlPriority.Mission);
        _movement.CancelTravel();
        _movement.ClearDesiredGoal(ControlPriority.Mission);
        _goalSet = false;
        _current = null;
        _completed = false;
        SetPhase(Phase.ToTerminal);
    }

    private void WaitingSellTick()
    {
        if (!_sell.Active)
        {
            _logger.LogInformation("MISSION: sell done - back to the terminal.");
            ToTerminalAfterSell();
            return;
        }

        if (_phaseTime - _sellWaitAt > SellTimeout)
        {
            _logger.LogInformation("MISSION: the sell run is taking too long - going on without it.");
            ToTerminalAfterSell();
        }
    }

    // ---- Quest record (raw QuestFullUpdate) -----------------------------------------

    /// <summary>
    ///     The mission records inside a raw QuestFullUpdate. Read raw because the record layout is
    ///     not the SDK's Quest class. Layout, from four AOBuddy10 captures:
    ///       Identity(0xC350, holder) i32 type(0x2Cxx) i32 0xB40 i32 0xB40 i32 0x7E2 i32 n, then
    ///       n-dependent slots - find item: Identity(0xC73D/0xC749/0xC74E item); find person: 16
    ///       zero bytes, Identity(0xC350 npc); repair: tool then object - and about 230 bytes on,
    ///       Identity(0xC79F, building instance), the same id the zone-in packet carries.
    /// </summary>
    private static MissionRecord ParseRecord(byte[] b, int holder, int building)
    {
        if (b == null)
        {
            return null;
        }

        MissionRecord any = null;
        var text = Strings(b);
        for (var i = 0; i + 40 <= b.Length; i++)
        {
            if (I32(b, i) != 0xC350 || I32(b, i + 4) != holder)
            {
                continue;
            }

            var type = I32(b, i + 8);
            if ((type & 0xFF00) != 0x2C00 || I32(b, i + 12) != 0xB40 || I32(b, i + 16) != 0xB40)
            {
                continue;
            }

            var r = new MissionRecord { Type = type, Text = text };
            var ids = new List<Identity>();
            for (var j = i + 24; j + 8 <= b.Length && j < i + 24 + 56; j += 4)
            {
                var t = I32(b, j);
                var inst = I32(b, j + 4);
                if ((t == 0xC73D || t == 0xC350 || t == 0xC749 || t == 0xC74E) && inst != 0 && inst != holder)
                {
                    ids.Add(new Identity((IdentityType)t, inst));
                    j += 4;
                }
            }

            if (ids.Count > 0)
            {
                r.TargetA = ids[0];
            }

            if (ids.Count > 1)
            {
                r.TargetB = ids[1];
            }

            // The building: the first Identity(0xC79F, x) after the record and before the next one.
            for (var j = i + 24; j + 8 <= b.Length && j < i + 400; j++)
            {
                if (j > i && I32(b, j) == 0xC350 && I32(b, j + 4) == holder && (I32(b, j + 8) & 0xFF00) == 0x2C00)
                {
                    break;
                }

                if (I32(b, j) == 0xC79F)
                {
                    r.Building = I32(b, j + 4);
                    break;
                }
            }

            any = r;
            if (r.Building == building)
            {
                return r; // the record of THIS building wins
            }
        }

        return any; // no building id matched: the only record is as good as it gets
    }

    private static int I32(byte[] b, int p)
    {
        return (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
    }

    private static string Strings(byte[] b)
    {
        var sb = new System.Text.StringBuilder();
        int run = 0, start = 0;
        for (var i = 0; i <= b.Length; i++)
        {
            var ok = i < b.Length && b[i] >= 32 && b[i] < 127;
            if (ok)
            {
                if (run == 0)
                {
                    start = i;
                }

                run++;
                continue;
            }

            if (run >= 6)
            {
                sb.Append(System.Text.Encoding.ASCII.GetString(b, start, run)).Append('\n');
            }

            run = 0;
        }

        return sb.ToString();
    }

    public sealed class MissionRecord
    {
        public int Type;
        public Identity? TargetA;
        public Identity? TargetB;
        public int Building;
        public string Text;

        public string TypeName => MissionController.TypeName(Type);
    }

    // ---- The want run (AOBuddy10, owner 2026-09-25) -----------------------------------

    private WantList Wants => _wants ??= new WantList(_config.Character, s => _logger.LogInformation(s));

    /// <summary>Nano crystals by name: the reward item the runs collect for a nano.</summary>
    private static bool IsNanoCrystal(string name)
    {
        return name != null && (name.StartsWith("Nano Crystal", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("NanoCrystal", StringComparison.OrdinalIgnoreCase));
    }

    // Wanted items never sell (wired into the SellController from the constructor): nano crystals
    // always, and anything that fits a want entry - the run rolled for it, it stays (AOBuddy10's
    // Bankable rule, minus the implant/name rules that were a separate feature there).
    private bool KeptFromSale(Item i)
    {
        return i != null && (IsNanoCrystal(i.Name) ||
                             (_wants != null && _wants.Entries.Any(e => WantList.Fits(e, i.Id, i.Ql))));
    }

    /// <summary>Templates he holds: inventory, bags, and the bank as last seen.</summary>
    private HashSet<int> HeldTemplates()
    {
        var h = new HashSet<int>();
        foreach (var i in Inventory.Items)
        {
            if (i != null)
            {
                h.Add(i.Id);
            }
        }

        foreach (var c in Inventory.Containers)
        {
            if (c?.Items == null)
            {
                continue;
            }

            foreach (var i in c.Items)
            {
                if (i != null)
                {
                    h.Add(i.Id);
                }
            }
        }

        try
        {
            foreach (var i in Inventory.Bank.Items)
            {
                if (i != null)
                {
                    h.Add(i.Id);
                }
            }
        }
        catch
        {
            // the bank is only counted when it was seen
        }

        return h;
    }

    /// <summary>Held templates plus one crystal of each nano judged not rollable, so the list can finish.</summary>
    private HashSet<int> HeldOrDropped()
    {
        var h = HeldTemplates();
        LoadOffered();
        foreach (var kv in WantData.Crystals)
        {
            if (_notRollable.Contains(kv.Value))
            {
                h.Add(kv.Key);
            }
        }

        return h;
    }

    // ---- QL targeting ------------------------------------------------------------------
    // The mission QL (every gear/implant reward of a roll shares it) follows level and difficulty:
    // alt log 2026-09-24/25, difficulty 1 -> QL25, 5 -> QL32-35 (level 36-39), 6 -> QL36. Nano
    // crystals come within about +-9 of it (QL25 -> 16-34, QL35 -> 27-44). Seen difficulties 1-11.
    // Recorded per level in qlmap-<character>.json; the want run sets the difficulty whose QL sits
    // closest to the band it wants, tries unseen settings toward it, and reports a band out of
    // reach once every setting is known.

    private string QlMapPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"qlmap-{_config.Character}.json");

    private Dictionary<string, int> QlMap
    {
        get
        {
            if (_qlMapStore != null)
            {
                return _qlMapStore;
            }

            _qlMapStore = new Dictionary<string, int>();
            var o = JsonStore.Load<JObject>(QlMapPath, s => _logger.LogInformation(s));
            if (o != null)
            {
                foreach (var kv in o)
                {
                    if (kv.Value.Type == JTokenType.Integer)
                    {
                        _qlMapStore[kv.Key] = (int)kv.Value;
                    }
                }
            }

            return _qlMapStore;
        }
    }

    private int MyLevel()
    {
        var me = DynelManager.LocalPlayer;
        return me != null && me.TryGetStat(Stat.Level, out var l) ? l : 0;
    }

    /// <summary>Record this roll's mission QL (the QL its gear/implant rewards share) for level + difficulty.</summary>
    private int? QlObserve(IReadOnlyList<MissionInfo> list)
    {
        if (_lastDifficulty < 0)
        {
            return null;
        }

        var gear = list.SelectMany(m => m.MissionItemData ?? Array.Empty<MissionItemReward>())
            .Where(r => r != null && !WantData.IsCrystal(r.LowId) && r.Ql > 1)
            .Select(r => r.Ql).ToList();
        if (gear.Count == 0)
        {
            return null;
        }

        var ql = gear.GroupBy(q => q).OrderByDescending(g => g.Count()).First().Key;
        var key = $"{MyLevel()}:{_lastDifficulty}";
        if (QlMap.TryGetValue(key, out var old) && old == ql)
        {
            return ql;
        }

        QlMap[key] = ql;
        var o = new JObject();
        foreach (var kv in QlMap.OrderBy(k => k.Key))
        {
            o[kv.Key.ToString()] = kv.Value;
        }

        JsonStore.Save(QlMapPath, o.ToString(), s => _logger.LogInformation(s));
        _logger.LogInformation($"MISSION: level {MyLevel()}, difficulty {_lastDifficulty} -> mission QL {ql}.");
        return ql;
    }

    /// <summary>The QL band to aim at: the open entry with a QL band, most left first. Nano: the
    /// missing crystals' QLs, +-NanoWindow; gear/implant/spirit: its band.</summary>
    private (WantList.Entry e, int lo, int hi, int aim)? WantBand()
    {
        var held = HeldOrDropped();
        (WantList.Entry, int, int, int)? best = null;
        var bestLeft = -1;
        foreach (var r in Wants.Remaining(held))
        {
            if (_unreachable.Contains(r.e) || r.e.Name != null)
            {
                continue;
            }

            if (r.e.Kind == "nano")
            {
                // No QL band ('nano', any QL: owner, 2026-09-25): take whatever the owner's
                // difficulty rolls; don't steer it toward the middle of every nano there is.
                if (r.e.QlMin <= 0 && r.e.QlMax >= 1000)
                {
                    continue;
                }

                if (r.left == null || r.left.Count == 0)
                {
                    continue;
                }

                var qls = r.left.Select(c => ItemData.Find(c, out ItemBase d) && d != null ? d.Ql : 0)
                    .Where(q => q > 0).OrderBy(q => q).ToList();
                if (qls.Count == 0 || qls.Count <= bestLeft)
                {
                    continue;
                }

                var med = qls[qls.Count / 2];
                best = (r.e, med - NanoWindow, med + NanoWindow, med);
                bestLeft = qls.Count;
            }
            else if (r.e.QlMin > 0 || r.e.QlMax < 1000)
            {
                if (bestLeft >= 1)
                {
                    continue;
                }

                var lo = Math.Max(1, r.e.QlMin);
                var hi = Math.Min(r.e.QlMax, 999);
                best = (r.e, lo, hi, (lo + hi) / 2);
                bestLeft = 1;
            }
        }

        return best;
    }

    /// <summary>Set the difficulty for the next roll toward the wanted band; drop a band no setting reaches.</summary>
    private void WantAim()
    {
        var band = WantBand();
        if (band == null)
        {
            _wantDifficulty = null;
            return;
        }

        var (e, lo, hi, aim) = band.Value;
        var lvl = MyLevel();
        // Never above the difficulty the owner set: that is the challenge he chose, and it changes
        // what nano QLs he can get (owner, 2026-09-25: 'drop it to difficulty 3').
        var cap = Math.Min(DiffMax, _config.MissionDifficulty);
        if (cap < DiffMin)
        {
            _wantDifficulty = null;
            return;
        }

        var seen = new Dictionary<int, int>();
        for (var d = DiffMin; d <= cap; d++)
        {
            if (QlMap.TryGetValue($"{lvl}:{d}", out var q))
            {
                seen[d] = q;
            }
        }

        // A seen setting that lands in the band: use it.
        var inBand = seen.Where(kv => kv.Value >= lo && kv.Value <= hi)
            .OrderBy(kv => Math.Abs(kv.Value - aim)).Select(kv => (int?)kv.Key).FirstOrDefault();
        if (inBand.HasValue)
        {
            SetDiff(inBand.Value, e, seen[inBand.Value]);
            return;
        }

        // None yet: step toward the band from the nearest seen setting (QL rises with difficulty),
        // or start mid.
        int next;
        if (seen.Count == 0)
        {
            next = Math.Min(6, cap);
        }
        else
        {
            var near = seen.OrderBy(kv => Math.Abs(kv.Value - aim)).First();
            var dir = near.Value < lo ? 1 : -1;
            next = near.Key + dir;
            while (next >= DiffMin && next <= cap && seen.ContainsKey(next))
            {
                next += dir;
            }

            if (next < DiffMin || next > cap)
            {
                _unreachable.Add(e);
                var range = $"{seen.Values.Min()}-{seen.Values.Max()}";
                Tell($"Can't roll {e}: at level {lvl} the mission QL only goes {range}. Leaving it and carrying on with the rest.");
                _logger.LogInformation($"MISSION: want {e} out of reach at level {lvl} (mission QL {range}).");
                WantAim();
                return;
            }
        }

        SetDiff(next, e, null);
    }

    private void SetDiff(int d, WantList.Entry e, int? ql)
    {
        if (_wantDifficulty == d)
        {
            return;
        }

        _wantDifficulty = d;
        _logger.LogInformation($"MISSION: aiming for {e}: difficulty {d}" +
                               (ql.HasValue ? $" (mission QL {ql})" : " (trying it)") + ".");
    }

    // ---- Offered rewards (owner, 2026-09-25: "some of these may not be rollable, but I don't know which")
    // Every roll's rewards are counted (template -> times offered), and so are the rolls at each
    // mission QL. A wanted nano never offered in UnseenCap rolls whose mission QL was within
    // NanoWindow of its crystal's QL is taken as not a mission reward and dropped from the want
    // list; 'want drop <name>' does it by hand. Kept in offered-<character>.json, so the evidence
    // builds up across runs and restarts.

    private int UnseenCap => Math.Max(50, _config.WantUnseenRolls);

    private string OfferedPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"offered-{_config.Character}.json");

    private void LoadOffered()
    {
        if (_offerTally != null)
        {
            return;
        }

        _offerTally = new Dictionary<int, int>();
        _rollsAtQl = new Dictionary<int, int>();
        _notRollable = new HashSet<int>();
        var o = JsonStore.Load<JObject>(OfferedPath, s => _logger.LogInformation(s));
        if (o == null)
        {
            return;
        }

        foreach (var kv in (JObject)o["offered"] ?? new JObject())
        {
            _offerTally[int.Parse(kv.Key)] = (int)kv.Value;
        }

        foreach (var kv in (JObject)o["rollsAtQl"] ?? new JObject())
        {
            _rollsAtQl[int.Parse(kv.Key)] = (int)kv.Value;
        }

        foreach (var n in (JArray)o["notRollable"] ?? new JArray())
        {
            _notRollable.Add((int)n);
        }
    }

    private void SaveOffered()
    {
        var o = new JObject
        {
            ["offered"] = new JObject(_offerTally.OrderBy(k => k.Key).Select(k => new JProperty(k.Key.ToString(), k.Value))),
            ["rollsAtQl"] = new JObject(_rollsAtQl.OrderBy(k => k.Key).Select(k => new JProperty(k.Key.ToString(), k.Value))),
            ["notRollable"] = new JArray(_notRollable.OrderBy(x => x)),
        };
        JsonStore.Save(OfferedPath, o.ToString(), s => _logger.LogInformation(s));
        _offeredDirty = 0;
    }

    private void RecordOffers(IReadOnlyList<MissionInfo> list, int? ql)
    {
        LoadOffered();
        foreach (var r in list.SelectMany(m => m.MissionItemData ?? Array.Empty<MissionItemReward>()))
        {
            if (r != null)
            {
                _offerTally[r.LowId] = (_offerTally.TryGetValue(r.LowId, out var c) ? c : 0) + 1;
            }
        }

        if (ql.HasValue)
        {
            _rollsAtQl[ql.Value] = (_rollsAtQl.TryGetValue(ql.Value, out var n) ? n : 0) + 1;
        }

        if (++_offeredDirty >= 10)
        {
            SaveOffered(); // every 10 rolls
        }
    }

    /// <summary>Times any crystal of this nano was offered.</summary>
    private int NanoOffered(int nano)
    {
        LoadOffered();
        return WantData.Crystals.Where(kv => kv.Value == nano).Sum(kv => _offerTally.TryGetValue(kv.Key, out var c) ? c : 0);
    }

    /// <summary>Rolls whose mission QL was within the nano window of this crystal's QL.</summary>
    private int RollsNear(int crystal)
    {
        LoadOffered();
        var q = ItemData.Find(crystal, out ItemBase d) && d != null ? d.Ql : 0;
        return _rollsAtQl.Where(kv => Math.Abs(kv.Key - q) <= NanoWindow).Sum(kv => kv.Value);
    }

    /// <summary>Drop wanted nanos never offered in UnseenCap rolls near their QL; true when any was dropped.</summary>
    private bool DropUnseen()
    {
        var any = false;
        foreach (var r in Wants.Remaining(HeldOrDropped()))
        {
            if (r.left == null || r.e.Kind != "nano")
            {
                continue;
            }

            foreach (var c in r.left)
            {
                var nano = WantData.NanoOf(c);
                if (nano == 0 || NanoOffered(nano) > 0)
                {
                    continue;
                }

                var near = RollsNear(c);
                if (near < UnseenCap)
                {
                    continue;
                }

                _notRollable.Add(nano);
                any = true;
                var nm = (WantList.NameOf(c) ?? c.ToString()).Replace("Nano Crystal (", "").TrimEnd(')');
                Tell($"{nm}: never offered in {near} rolls at its QL; taking it as no mission reward and dropping it.");
                _logger.LogInformation($"MISSION: want: {nm} (nano {nano}) never offered in {near} rolls near its QL; dropped.");
            }
        }

        if (any)
        {
            SaveOffered();
        }

        return any;
    }

    private string WantCount(WantList.Entry e)
    {
        if (e.Name != null || e.Kind != "nano")
        {
            return "";
        }

        var n = WantList.NanosFor(e).Count;
        return n == 0 ? " (no nano crystal in the item data fits that)" : $" ({n} nanos)";
    }

    private string WantStatus()
    {
        var w = Wants;
        w.Reload();
        if (w.Entries.Count == 0)
        {
            return "The want list is empty.";
        }

        var held = HeldOrDropped();
        var parts = new List<string>();
        foreach (var r in w.Remaining(held))
        {
            if (r.left == null)
            {
                parts.Add($"{r.e}: open");
                continue;
            }

            if (r.e.Name != null)
            {
                parts.Add($"{r.e}: {(r.left.Count == 0 ? "have it" : "wanted")}");
                continue;
            }

            var all = WantList.NanosFor(r.e).Count;
            var left = r.left.Count > 0 && r.left.Count <= 8
                ? " (left: " + string.Join(", ", r.left.Select(c =>
                {
                    var nano = WantData.NanoOf(c);
                    var seen = NanoOffered(nano);
                    var nm = (WantList.NameOf(c) ?? c.ToString()).Replace("Nano Crystal (", "").TrimEnd(')');
                    return seen > 0 ? $"{nm} [offered {seen}x]" : $"{nm} [never offered in {RollsNear(c)} rolls at its QL]";
                })) + ")"
                : "";
            var dropped = WantList.NanosFor(r.e).Count(c => _notRollable.Contains(WantData.NanoOf(c)));
            parts.Add($"{r.e}: {all - r.left.Count - dropped} of {all} had{(dropped > 0 ? $", {dropped} not rollable" : "")}{left}");
        }

        return $"Wants ({w.Mode}{(_wantRun ? ", rolling for them" : "")}): " + string.Join("; ", parts);
    }

    /// <summary>Want run: keep only missions with a wanted reward. True when it handled the roll
    /// (nothing wanted this roll and it re-rolls). 'list' mode says done and turns itself off once
    /// every entry is had or unreachable; WantRollCap rolls with nothing wanted take an ordinary
    /// mission and go back to the list after it.</summary>
    private bool WantFilter()
    {
        var w = Wants;
        w.Reload();
        DropUnseen();
        var held = HeldOrDropped();
        if (w.Mode == "list")
        {
            var rem = w.Remaining(held);
            if (rem.Count > 0 && rem.All(r => _unreachable.Contains(r.e) || (r.left != null && r.left.Count == 0)))
            {
                // Done: carry on with ordinary missions (owner, 2026-09-25, reversing 'at the end,
                // say done and stop').
                Tell("Want list done: I have everything on it. Carrying on with ordinary missions. " + WantStatus());
                _logger.LogInformation("MISSION: want list done; carrying on with ordinary missions.");
                _wantRun = false;
                _wantDifficulty = null;
                if (_unreachable.Count > 0)
                {
                    Tell("Out of reach: " + string.Join("; ", _unreachable) + ".");
                }

                return false;
            }
        }

        var before = _offered.Count;
        // The ordinary pool (blitzable, zone-allowed) for the cap fallback: AOBuddy10's WantFilter
        // ran on the already-Fits-filtered list, so its "ordinary mission" was one it could take.
        var all = _offered.Where(m => Allowed(m, out _)).ToList();
        _offered.RemoveAll(m => !Allowed(m, out _) || m.MissionItemData == null ||
                                !m.MissionItemData.Any(r => w.Wanted(r.LowId, r.Ql, held) != null));
        if (_offered.Count > 0)
        {
            _wantRolls = 0;
            _logger.LogInformation($"MISSION: {_offered.Count} of {before} mission(s) have a wanted reward.");
            return false;
        }

        if (++_wantRolls >= WantRollCap)
        {
            // No wanted reward for a long stretch: take an ordinary mission from this roll and
            // keep rolling for the wants after it (owner, 2026-09-25: 'just continue').
            Tell($"No wanted reward in {WantRollCap} rolls; taking an ordinary mission, then back to the want list. {WantStatus()}");
            _logger.LogInformation($"MISSION: no wanted reward in {WantRollCap} rolls; an ordinary mission this time.");
            _wantRolls = 0;
            _offered.AddRange(all);
            return false;
        }

        _emptyRolls++;
        _offered.Clear();
        SetPhase(Phase.Rolling);
        return true;
    }

    // ---- Small helpers -------------------------------------------------------------

    private void SetPhase(Phase p)
    {
        _phase = p;
        _phaseTime = 0;
        _goalAt = -1;
    }

    private void Tell(string text)
    {
        if (_tellId != 0)
        {
            Client.SendPrivateMessage(_tellId, text);
            return;
        }

        var owner = DynelManager.Players.FirstOrDefault(pl =>
            string.Equals(_config.Owner, pl.Name, StringComparison.OrdinalIgnoreCase));
        if (owner != null)
        {
            Client.SendPrivateMessage((uint)owner.Identity.Instance, text);
        }
        else
        {
            _logger.LogInformation($"MISSION (owner not reachable, tell not sent): {text}");
        }
    }

    private static string WhereIAm()
    {
        return $"pf {(int)Playfield.ModelId} ({DynelManager.LocalPlayer.Transform.Position.X:0.0} " +
               $"{DynelManager.LocalPlayer.Transform.Position.Z:0.0}).";
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max];
    }

    // The composed building around a point: which rooms cover it, and where the placed WALL
    // triangles (walls.bin through PlaceBin) stand in the body band on each axis - the ground
    // truth "is there a wall just east of me" (owner, 2026-10-03: wedged inside geometry twice).
    private string Probe(float px, float py, float pz)
    {
        if (_nav?.Dungeon == null)
        {
            return "Not in a mission - nothing composed to probe.";
        }

        var roomsHere = new List<string>();
        var roomsEast = new List<string>();
        foreach (var rm in _nav.Dungeon.Rooms)
        {
            if (!_nav.Dungeon.CellOf(rm, px, pz, out _, out _) ||
                double.IsNaN(_nav.Dungeon.FloorHeight(rm, px, pz)))
            {
                continue;
            }

            roomsHere.Add($"{rm.PoolName} f{rm.Floor}");
        }

        foreach (var rm in _nav.Dungeon.Rooms)
        {
            if (!_nav.Dungeon.CellOf(rm, px + 3f, pz, out _, out _) ||
                double.IsNaN(_nav.Dungeon.FloorHeight(rm, px + 3f, pz)))
            {
                continue;
            }

            // what room lies ~3 m east - a doorway there should be open, a wall is a wall
            roomsEast.Add($"{rm.PoolName}@{rm.Pos[0]:0},{rm.Pos[2]:0}");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"probe ({px:0.0},{pz:0.0}) y {py:0.0} - rooms here: {(roomsHere.Count == 0 ? "NONE (void between rooms)" : string.Join(", ", roomsHere))}");
        sb.Append($"; ~3 m east: {(roomsEast.Count == 0 ? "nothing tiled" : string.Join(", ", roomsEast))}");

        // Every probe leaves the full composed layout behind: rooms, doors and server doors in
        // one file for offline comparison against ground truth (the instance itself is gone once
        // the run leaves, and each roll builds a new one).
        try
        {
            var dump = new System.Text.StringBuilder();
            dump.AppendLine($"layout {_nav.Name} pf {_missionPf}, {_nav.Dungeon.Rooms.Count} room(s)" +
                            (_nav.Layout != null ? $" w {_nav.Layout.Width} h {_nav.Layout.Height} wh {_nav.Layout.WorldHeight}" : "") + ":");
            foreach (var rm in _nav.Dungeon.Rooms)
            {
                // slot (X,Z) = the zone-in room table's own grid cell - the exact placement seed,
                // so an offline rebuild needs no centre inversion (its rounding bent rooms up to
                // half a slot in z, which bent every offline door comparison with it). Taken from
                // the room itself: duplicated pool rooms share PoolIndex, so a lookup by it gave
                // every twin the first placement's slot.
                dump.AppendLine(
                    $"  room {rm.Index} {rm.PoolName} f{rm.Floor} centre ({rm.Pos[0]:0.0},{rm.Pos[2]:0.0}) y {rm.Pos[1]:0.0} rot {rm.Rot}" +
                    (rm.Slot != null ? $" slot ({rm.Slot[0]},{rm.Slot[1]})" : ""));
            }

            dump.AppendLine($"doorways: {string.Join(" | ", _nav.MissionDoorways.Select(dw => $"({dw.X:0.0},{dw.Y:0.0},{dw.Z:0.0}) n({dw.Nx:0.0},{dw.Nz:0.0}) f{dw.Floor}"))}");
            dump.AppendLine($"server doors: {string.Join(" | ", _serverDoors.Select(sd => $"Room={sd.room} Adj={sd.adjoining} ({sd.pos.X:0.0},{sd.pos.Y:0.0},{sd.pos.Z:0.0}) {DoorState(sd.flags)}"))}");
            dump.AppendLine($"server exit: {(_serverExitByPf.TryGetValue(_missionPf, out var sx) ? $"{sx.X:0.0},{sx.Z:0.0}" : "none seen")}");
            dump.AppendLine($"target: {(TryGetTargetPos(out var tp) ? $"{tp.X:0.0},{tp.Z:0.0}" : "not seen")}");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"mission-layout-{_missionPf}.txt"), dump.ToString());
            // the zone-in bytes beside it: LoadMission replays them offline, so the composed
            // dungeon - and its grid - can be rebuilt byte-exact without logging the bot in
            if (_zoneInRaw != null)
            {
                File.WriteAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"mission-zonein-{_missionPf}.bin"), _zoneInRaw);
            }
        }
        catch
        {
            // a dump must never break a probe
        }
        foreach (var (label, dx, dz) in new[] { ("east(+x)", 1f, 0f), ("west(-x)", -1f, 0f), ("south(+z)", 0f, 1f), ("north(-z)", 0f, -1f) })
        {
            sb.Append($" | {label}: ");
            var hits = new List<string>();
            for (var dist = 0.5f; dist <= 4.01f; dist += 0.5f)
            {
                if (WallAt(px + dx * dist, pz + dz * dist, py))
                {
                    hits.Add($"{dist:0.0} m");
                }
            }

            sb.Append(hits.Count == 0 ? "open" : $"wall at {string.Join(",", hits)}");
        }

        if (_nav.Walls == null)
        {
            sb.Append(" (no walls.bin placed)");
        }

        return sb.ToString();
    }

    private bool TryGetTargetPos(out Vector3 pos)
    {
        pos = default;
        if (_record?.TargetA == null || !_items.TryGetValue(_record.TargetA.Value, out var seen))
        {
            return false;
        }

        pos = seen.Pos;
        return true;
    }

    private bool WallAt(float x, float z, float y)
    {
        var w = _nav.Walls;
        if (w == null)
        {
            return false;
        }

        for (var o = 0; o + 8 < w.Length; o += 9)
        {
            float ax = w[o], az = w[o + 2], bx = w[o + 3], bz = w[o + 5], cx = w[o + 6], cz2 = w[o + 8];
            var det = (bz - cz2) * (ax - cx) + (cx - bx) * (az - cz2);
            if (Math.Abs(det) < 1e-9f)
            {
                continue;
            }

            float l1 = ((bz - cz2) * (x - cx) + (cx - bx) * (z - cz2)) / det;
            float l2 = ((cz2 - az) * (x - cx) + (ax - cx) * (z - cz2)) / det;
            if (l1 < -0.001f || l2 < -0.001f || 1 - l1 - l2 < -0.001f)
            {
                continue;
            }

            var h = l1 * w[o + 1] + l2 * w[o + 4] + (1 - l1 - l2) * w[o + 7];
            if (h >= y + 0.3f && h <= y + 1.9f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One saved mission terminal, missionterminal.json beside the executable (AOBuddy10's shape).</summary>
    private sealed class SavedTerminal
    {
        public int pf;
        public int type;
        public int id;
        public float x, y, z;
        public float fx, fz;
    }

    private sealed class SeenItem
    {
        public int Template;
        public Vector3 Pos;
    }
}
