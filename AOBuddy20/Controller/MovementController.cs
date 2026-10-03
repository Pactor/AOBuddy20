// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MovementController.cs
//
// Last modified: 2026-10-03
// Created:       2026-09-30 10:09
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.Diagnostics;
using AOBuddy20.Components;
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
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     The movement cycle (Readme points 1, 4 and 5) on its own thread. Other components never move
///     the body: they hand in a goal (position + playfield) with their priority and read back the
///     server-confirmed CurrentPosition. The highest-priority goal for the current playfield wins -
///     its move replaces any unsent lower one (review.md #7) - and every CharDCMove that leaves the
///     bot goes out through here.
///     Goal contract: one goal per priority level; setting again replaces it. Several goals for the
///     current playfield coexist, and a controller walks a route by setting its next goal when
///     <see cref="IsGoalReached" /> turns true. A goal is NOT consumed on arrival - it stays until
///     its owner sets the next one or clears it, so the owner can advance its inner state (Readme
///     point 5). ONLY the active (highest) goal may ever read as reached: while a higher one is
///     set - walking or already reached - lower ones stay unsatisfied even if the body stands
///     inside their radius, and one reached earlier re-arms the moment a higher goal takes over.
///     Threading: SDK state (LocalPlayer, stats, Playfield) is read only on the update thread, which
///     publishes a snapshot each tick (review.md #9: foreign threads consume snapshots). The walk
///     thread is the only writer of the body's MovementComponent; a server SetPos is queued here and
///     applied by the walk thread, never from the packet thread. Set/Clear/IsGoalReached may be
///     called from any thread.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class MovementController : IPacketConsumer
{
    // Walk tuning, carried over from AOBuddy10's proven defaults: SendIntervalMs = 100 and
    // MaxStep = 1.5 (AOBuddy10 Config.cs:363-364), manual-target arrival radius 1.5 m
    // (AOBuddy10 FollowController.cs:532).
    private const int SendIntervalMs = 100;
    private const float MaxStep = 1.5f;
    private const float ArriveRadius = 1.5f;

    // WATER, the captured client's exact contract (AOBuddy10 OverlandController, capture 20260924-215811
    // s116, this very shore): NO swim-mode packet is ever sent - Y is THE WATER SURFACE while the bottom
    // is deeper than a wade, THE BOTTOM once it rises inside wading range, plain Update packets
    // throughout. Wet speed = RunVelocity * SwimSpeedFactor (~5.5 u/s, Config.cs:357-358).
    private const float SwimWadeMeters = 1.2f;
    private const float SwimSpeedFactor = 0.5f;
    private const float WadeProbeMeters = 0.5f; // probe this far ahead for the water verdict
    private const float WadeDepth = 0.3f; // SwimY's wade depth for the probe

    // ROUTING (AOBuddy10 OverlandController): grid-route snap and reaches, waypoint advance, and the
    // stuck watch that blocks the few metres ahead and replans round what the data does not show.
    private const float SnapMeters = 8f;
    private const float GoalReach = 3f;
    private const float WideGoalReach = 8f; // a goal in a wall: the nearest reachable ground within this
    private const float WaypointRange = 0.5f;
    private const float WaypointLastRange = 3f; // == GoalReach: the route's last point stands for the goal
    private const double StuckSeconds = 4;
    private const int MaxStuck = 4; // re-routes around a stuck spot per goal

    // TRAVEL legs (AOBuddy10 OverlandController): stand still a moment before using (the server must
    // have our stop before we use from where it has us), wait per kind for the zone, and give each exit
    // a try budget before it is written off and the plan re-routes round it. Booths, grid exits and lift
    // beams (kind Line) are pads WALKED ONTO (PadReach, exactly on the centre); whompas and teleporters
    // are terminals walked up to (ObjectReach) and used with GenericCmd Use.
    private const int MaxLegTries = 3;
    private const double SettleSeconds = 0.6;
    private const double ZoneLineWait = 6;
    private const double PadWait = 10;
    private const double ObjectWait = 8;
    private const float PadReach = 0.6f;
    private const float ObjectReach = 2.5f;

    // A proxy playfield's exit door takes the crossing only from inside it: activation radius is
    // under half a metre (owner, 2026-10-01) - walk ONTO the door, then use it. Entry proxy doors
    // (legs of kind Proxy) get the same treatment.
    private const float DoorReach = 0.4f;

    // POST-ZONE HOLD (Newland City -> 1187 on a fresh gameserver, 2026-10-02 00:44: the walk
    // started 50 ms after the zone-in and the server refused every step - four rubberbands back
    // to the landing point in 3.3 s, the goal given up). The retail client never sees this lock
    // because its zone load takes just as long. Hold the walk this long after every playfield
    // switch; bookkeeping (travel legs, proxy origin) still runs.
    private const double ZoneSettleSeconds = 3.0;

    // And after a YANK the server has just corrected us - walking on immediately re-collects the
    // same rejection. Hold briefly, then try the re-planned route.
    private const double YankReholdSeconds = 2.0;

    // The stand-up campaign: re-send the toggle until the server's 0x57 echo confirms it, at most
    // this often and this many times - then walk anyway and let the server have the last word.
    private const int MaxStandTries = 3;
    private const double StandRetrySeconds = 1.5;
    private const double StandRearmSeconds = 5.0; // the slow beat once the fast retries are spent
    private const int MaxStandRearms = 6; // slow re-arms before the walk assumes standing
    private const double LoginModeTimeout = 30.0; // no login movement mode on the wire this long: assume standing
    private const double DriftHoldSeatedSeconds = 15.0; // a drift hold this long with no server truth: the body is seated

    // DRIFT GUARD (Newland City, 2026-10-01 22:10: the pad quarter's ground reads blocked in the
    // grid, the route wove around it, the server silently rejected the steps and rubberbanded the
    // bot 19 m later). While the body walks, the gap between the dictated position and the
    // server's last confirmed one must stay small (normal echo lag is ~1.5 m); past this, stop and
    // wait for the server's truth instead of ghost-walking on.
    private const float DriftGuardMetres = 6f;

    // A server correction this big while walking is a YANK, not the usual sub-metre Y noise: hold,
    // re-plan from where the server actually has us, and count. After this many yanks on one goal
    // the data and the server disagree too often - give the goal up instead of walking into the
    // same rejection again.
    private const float YankMetres = 5f;
    private const int MaxYanks = 3;

    // Grid-route plan failures in a row before the goal is given up (the overland reopen loop):
    // a plan that finds no route walks nothing, so no yank ever lands - without this counter a
    // sealed-in body re-plans at forty plans a second forever (Varmint Woods, 2026-10-03).
    private const int MaxNoRoute = 5;

    // How far the geometry outranks the grid: a failed plan this close to the goal gets its straight
    // line judged against the real triangles (GeometryLine) before the stamps' verdict stands. The
    // grid's near-field failures - a one-cell doorway at big-map cell size, a landing on stamped
    // ground, the yank band's own cells - are all inside this radius; beyond it, long-range routing
    // is the grid's job and the stamps steer the long way around.
    private const float GeometryLineMaxMeters = 25f;

    // Escape legs in a row before the escapes are declared circling (a pocket walked round in
    // 20 m hops would never terminate otherwise); the give-up path takes over after that.
    private const int MaxEscapes = 6;

    // How long the SAME goal stays refused after a no-route give-up: the mission travel tick
    // re-sets the identical goal 1-5 ms after the give-up, which turned the give-up into a
    // 30-plans-a-second loop (Varmint Woods 2026-10-03 14:41). Within the cooldown the goal is
    // not accepted, and the escape legs get the time to actually walk.
    private const float GiveUpCooldownSeconds = 6f;

    // Server's CurrentMovementMode (Stat 173) values — OmniCell MoveModes (AOBuddy10 Main.cs:48-50). A
    // character in a SEATED mode cannot move: the server rejects every move packet and snaps him back
    // to where he sat. You log out by sitting, so you can log back in seated.
    // Server's CurrentMovementMode (stat 173) values that CANNOT walk - a seated body's every
    // step is rejected. The set is empirical, the modes seen on the wire:
    //   3 = sit (the live client / OmniCell LOGOUT sit - a login after a client session reads 3;
    //       2026-10-02: the bot came up seated three logins in a row and "standing" was logged)
    //   8 = sit (the emulator's sit), 11 = sleep, 12 = lounge
    // Stat 173 is only set from the login FullCharacter and never updates afterwards, so this
    // read is the ONE sitting/standing signal in the starting packets.
    private static readonly HashSet<int> SeatedModes = new HashSet<int> { 3, 8, 11, 12 };

    private readonly ILogger<MovementController> _logger;

    // The ONE place the body moves (Components/Movement.cs), ported from AOBuddy10.
    private readonly Movement _movement = new Movement();

    private readonly Dictionary<int, GoalLocation> goals = new Dictionary<int, GoalLocation>();
    private readonly object _goallock = new object();
    private readonly object _poslock = new object();

    // TRAVEL (PlanTravel): the cross-playfield plan - the destination playfield, its optional
    // coordinates, and the leg currently walked. Legs are re-planned from wherever we actually are
    // after every zone, so a surprise zone cannot derail the plan. Guarded by _travelLock; the lock
    // order is always _travelLock -> _goallock, never the reverse. _travelFailed is the exits written
    // off on THIS trip (an exit that gave nothing MaxLegTries times is routed round), same lock.
    private TravelPlan? _travel;
    private readonly HashSet<ZoneExit> _travelFailed = new();
    private readonly object _travelLock = new object();
    private int _wetPf = -1; // the playfield whose wet verdict Zoning carries for line crossings

    private Vector3 _confirmedPosition; // where the server last had US
    private Quaternion _confirmedHeading;
    private Vector3? _pendingCorrection; // SetPos the walk thread will apply
    private bool _pendingYank; // that SetPos overrode a big drift: hold and re-plan after applying
    private int _yanks; // yanks on the current goal (reset when a new goal is set)
    private int _noRoute; // consecutive grid-route plan failures (reset when a route is found)
    private int _escapes; // consecutive geometry escape legs (reset when a route is found)
    private int _giveUpPf = -1; // the goal the last no-route give-up abandoned,
    private Vector3 _giveUpGoal; // ...refused again for GiveUpCooldownSeconds (the re-set loop)
    private double _giveUpAt = -1;
    private bool _driftHeld; // the drift guard is holding the body
    private double _holdWalkUntil = -1; // post-zone / post-yank: no walking before this (wet clock)

    // The playfield's nav data and walk grid, built off every loop thread (NavGridCache). Nav supplies
    // the Y of every dictated step - without it the walk holds the login Y and floats (owner, 2026-10-01).
    private readonly NavGridCache _nav = new();
    private readonly Stopwatch _wetClock = Stopwatch.StartNew();

    // MISSION INSTANCE nav, composed from the zone-in packet and handed in by the MissionController
    // (SetMissionNav) - a mission playfield has no disk data, so this is the only source for its walk
    // grid. Keyed by playfield; the cache is reset on every hand-in so a re-entry rebuilds.
    /// <summary>Drops the loaded nav/grid so the next walk tick rebuilds them - the mission
    /// controller re-hands a CORRECTED layout after its server-door corrections moved rooms
    /// (owner, 2026-10-03).</summary>
    public void ResetMissionNav()
    {
        _missionNav = null;
        _missionNavPf = -1;
        _nav.Reset();
    }

    private volatile AOBuddyNav _missionNav;
    private volatile int _missionNavPf = -1;

    /// <summary>
    ///     A mission instance was entered: here is its composed nav (rooms, walls, doorways, exit).
    ///     Update thread (the zone-in compose); the walk thread picks it up on its next Request.
    /// </summary>
    public void SetMissionNav(int playfieldId, AOBuddyNav nav)
    {
        _missionNav = nav;
        _missionNavPf = playfieldId;
        _nav.Reset();
        _logger.LogInformation($"Movement: mission nav for pf {playfieldId} handed in ({nav?.Name ?? "null"}).");
    }

    // How far the server's floor sits above our data here (AOBuddy10: Rome Park walked y 16 over ground
    // the data puts at 13.6; with corrections ignored the bot sank back each step, log 2026-09-24 01:30).
    // _wetY: the server's own Y while in water, wet truth for 3 s (AOBuddy10 OverlandController).
    private float _yBias;
    private float _wetY;
    private double _wetYAt = -99;
    private float _pendingBias; // the bias the SetPos that queued _pendingCorrection carried

    // The route to the active goal through the walk grid: walls, zone lines and doors only stop the
    // body when the step is ROUTED round them - a beeline walks straight through (owner, 2026-10-01:
    // 'come' ran him into a door to another playfield). Replanned when the goal changes.
    private readonly Movement.StuckWatch _stuck = new();
    private readonly HashSet<int> _stuckCells = new(); // cells we got stuck walking into, this goal
    private readonly HashSet<int> _serverNo = new(); // cells the server REFUSED walking into (a yank), this session
    private Vector3 _lastPin; // the last correction landing, for the pinned-storm watch
    private double _lastPinAt = -99;
    private int _pins;

    // THE SERVER-PINNED BODY: wedge cycles stacking on the same spot - each blacklists and
    // re-plans, the next walks straight back into the same refusal. Three cycles in a minute on
    // one spot is the server holding the body somewhere it refuses every step from (its own
    // catch-up snapback parked it there). The clean unstick is the gameserver reconnect: the
    // login respawns the body at a server-valid position (owner, 2026-10-03: pinned at
    // (279.4,264.4) through the whole leaving walk, wedged cycle after cycle, minutes on end).
    private int _wedgeCycles;
    private Vector3 _wedgePos;
    private double _wedgeLastAt = -99;
    private bool _reconnectSent;
    private List<Vector3> _route = new();
    private int _routeIdx;
    private int _routePrio = -1; // the priority the route was planned for
    private Vector3 _routeGoal; // and the goal position it was planned to
    private int _routePf = -1;
    private int _stuckCount;

    // Set once the ONE login stand-up decision is made (see Tick): the login mode decides, at most
    // one toggle goes out, and never again - stat 173 never updates, so re-reading it re-toggles.
    private volatile bool _stoodUp;

    // The posture we believe the body is in, tracked from the only sources there are (owner,
    // 2026-10-01: "only stand up if needed"): the login FullCharacter's stat 173, the toggles the
    // bot itself sends (SitNow/StandNow, and the stand-up every movement goal requires), and the
    // server's 0x57 echo - a toggle that never came back means the body is still sitting, so the
    // stand-up is re-sent, bounded (owner, 2026-10-01: 'goto' left the bot sitting because the one
    // stand-up went unheard and nothing ever checked again).
    private volatile bool _seated;
    private volatile bool _standEchoPending; // a stand-up toggle is on the wire, no 0x57 echo yet
    private volatile bool _postureGivenUp; // the stand-up budget ran out: walk assumes standing,
                                           // _seated stays true so the next command re-arms
    // What the next 0x57 echo is for: the sit WE sent, the stand we sent, or nothing (an
    // unsolicited echo reads as a stand - the common forced one). The heal's rest cycle hangs
    // its "really seated" proof (SeatedConfirmed) on the sit answer; without the split, the
    // sit's echo would clear the track and stand a seated body on paper.
    private volatile PostureAwait _postureAwait;
    private volatile bool _sitConfirmed; // the server echoed the sit we asked for
    private double _standSentAt = -1;
    private int _standTries;
    private double _loginSeenAt = -1; // the walk clock's first tick with a body: the login-mode timeout runs from here
    private double _driftHeldSince = -1; // the drift hold began (the seated signature reads it)

    private enum PostureAwait { None, Sit, Stand }

    // Published by the update thread, consumed by the walk thread. One immutable snapshot per tick
    // so the walk never sees a torn combination (LocalPlayer is swapped on zone-in).
    private volatile Snapshot _snap = new Snapshot();

    private Thread? _thread;
    private volatile bool _running;
    private int _pf = -1; // playfield the walk state belongs to

    // Run-speed stickiness, as AOBuddy10 BotContext.RunVelocity: the last real reading holds until
    // a new one arrives, so a momentary unreadable stat does not reset the walk speed.
    private bool _runRead;
    private int _lastRunSpeed;

    public MovementController(ILogger<MovementController> logger, AccountInfo config)
    {
        _logger = logger;
        _config = config;
        _follow = new FollowController(logger, _movement, SendIntervalMs, MaxStep);
        _logger.LogInformation("Movement controller initialized.");
    }

    private readonly AccountInfo _config; // the owner name: follow's target
    private readonly FollowController _follow;

    // The body driver and the owner's wire fingerprint, published for the packet thread. He is known
    // by dynel instance: his CharDCMove packets carry it.
    private volatile bool _followOn;
    private volatile int _ownerInstance;
    private long _ownerLastMoveAt = long.MinValue;

    /// <summary>Follow mode: when on and no goal wants the body, stack on the owner and mirror him.</summary>
    public void SetFollow(bool on)
    {
        if (_followOn == on)
        {
            return;
        }

        _followOn = on;
        if (!on)
        {
            _follow.BreakMirror("follow off");
        }

        _logger.LogInformation(on
            ? $"FOLLOW: on - will stack on '{_config.Owner}' and mirror his movement."
            : "FOLLOW: off (stay).");
    }

    // The bot's own folder (Build\): GameData\Nav and gridcache live beside the executable, as
    // AOBuddy10 kept them beside the plugin.
    private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>Where the server last confirmed us. What other components read (Readme point 5).</summary>
    public Vector3 CurrentPosition
    {
        get
        {
            lock (_poslock)
            {
                return _confirmedPosition;
            }
        }
    }

    public Quaternion CurrentHeading
    {
        get
        {
            lock (_poslock)
            {
                return _confirmedHeading;
            }
        }
    }

    ///     Hand in where the body should go, at this controller's priority. Setting again replaces
    ///     the level's goal (and re-arms the reached flag). A goal for another playfield is not
    ///     walked from here - the component that set it owns getting us to that playfield first.
    ///     Travel (<see cref="PlanTravel" />) is the one built-in cross-playfield order: a zone-line
    ///     route from the Zoning graph, one walked leg per hop, re-planned from wherever we actually
    ///     are after every zone.
    ///     arriveRadius: how close counts as arrived (default from AOBuddy10's walker).
    ///     A seated body is stood up first: a movement order is the one thing that may end a sit.
    /// </summary>
    public void SetDesiredGoal(Vector3 desiredGoal, int playfieldId, ControlPriority priority, float arriveRadius = ArriveRadius)
    {
        lock (_goallock)
        {
            // THE GIVE-UP COOLDOWN: the same goal refused for a while after its no-route give-up.
            // The mission travel tick re-sets the identical goal 1-5 ms after the give-up, and
            // that re-set was the 30-plans-a-second loop (Varmint Woods 2026-10-03 14:41). A
            // moved goal, another playfield, or an expired cooldown passes untouched.
            var since = _wetClock.Elapsed.TotalSeconds - _giveUpAt;
            if (_giveUpPf == playfieldId && since >= 0 && since < GiveUpCooldownSeconds &&
                Movement.Flat(_giveUpGoal, desiredGoal) < 1f)
            {
                _logger.LogDebug($"Goal refused for {GiveUpCooldownSeconds - since:0.0} s more: the priority {priority} " +
                                 $"goal at ({desiredGoal.X:0.0} {desiredGoal.Z:0.0}) was given up {since:0.0} s ago (no route).");
                return;
            }

            goals[(int)priority] = new GoalLocation
            {
                Position = desiredGoal,
                PlayfieldId = playfieldId,
                ArriveRadius = arriveRadius,
            };
            _escapes = 0;
            _logger.LogDebug($"Goal set: priority {priority} ({(int)priority}), playfield {playfieldId}, " +
                             $"arrive {arriveRadius:0.0} m, ({desiredGoal.X:0.0} {desiredGoal.Y:0.0} {desiredGoal.Z:0.0}).");
        }

        if (_seated)
        {
            var me = DynelManager.LocalPlayer;
            if (me != null)
            {
                if (!_postureGivenUp)
                {
                    _standTries = 0; // a fresh order, a fresh campaign
                    SendStandUp(me, "new goal");
                }
                else
                {
                    // The stand-up budget ran out and the walk assumed standing - a blind toggle
                    // now would SIT a possibly-walking body. Walk; if the body proves still
                    // seated, the drift guard re-arms the stand-up from the rejections.
                    _seated = false;
                }
            }
        }

        _yanks = 0; // a fresh order starts with a clean yank count
        _noRoute = 0;
    }

    /// <summary>The owner's sit command: stop (goals go) and sit. The posture track follows the order.</summary>
    public void SitNow()
    {
        ClearAllGoals();
        Sit("owner command", clearGoalsAlreadyDone: true);
    }

    /// <summary>The owner's stand command - the explicit way out of a sit whose echo went missing.</summary>
    public void StandNow()
    {
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            return;
        }

        _standTries = 0;
        SendStandUp(me, "owner command");
    }

    /// <summary>The posture track believes the body standing with no stand-up in flight - the only
    /// state a NEW sit may be requested from: a sit raced against a stand-up toggle loses one of the two.</summary>
    public bool Standing => !_seated && !_standEchoPending;

    /// <summary>The server's echo confirmed the sit WE asked for - the "really seated" proof a
    /// sit-only item's use waits for (a send alone proves nothing; stat 173 never updates after login).</summary>
    public bool SeatedConfirmed => _sitConfirmed;

    /// <summary>THE HEAL'S SIT: sit without touching goals, so the walk this interrupts resumes on the
    /// stand-up. Tracked end to end: the server's echo marks <see cref="SeatedConfirmed"/>, and until
    /// then nothing may be pressed that needs the sit.</summary>
    public void Sit(string why)
    {
        Sit(why, clearGoalsAlreadyDone: false);
    }

    private void Sit(string why, bool clearGoalsAlreadyDone)
    {
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            return;
        }

        me.MovementComponent.ChangeMovement(MovementAction.SwitchToSit);
        _seated = true;
        _standEchoPending = false; // the sit is ours; no stand-up is in flight any more
        _postureGivenUp = false;
        _postureAwait = PostureAwait.Sit;
        _sitConfirmed = false;
        _standTries = 0;
        _follow.BreakMirror("sitting");
        _logger.LogInformation($"Movement: sitting ({why}); " +
                               (clearGoalsAlreadyDone ? "goals cleared." : "goals kept (the walk resumes on the stand-up)."));
    }

    /// <summary>Ends a rest: the echo-driven stand-up campaign. Only to be called when the posture
    /// track believes the body seated (after our own <see cref="Sit"/>, it does).</summary>
    public void Stand(string why)
    {
        var me = DynelManager.LocalPlayer;
        if (me == null)
        {
            return;
        }

        _standTries = 0;
        SendStandUp(me, why);
    }

    // The stand-up toggle (action 87) and the campaign around it. Sent only when the posture track
    // says seated - never blind, a blind toggle SITS a standing character. The body is believed
    // seated until the server's 0x57 echo says otherwise - a send alone proves nothing: the
    // live-client logout leaves the character seated, and the login stand-up raced the character
    // load and went unheard (owner, 2026-10-01 and 2026-10-02: after a client session the bot
    // came up seated and no travel/goto ever stood it up, because the track had believed
    // "standing" since the first unheard send). Every send continues the campaign: fast retries,
    // then a slow re-arm, until the echo lands - and if the budget runs out the walk assumes
    // standing (the echo is occasionally lost even when the stand applied), but _seated stays
    // true, so the NEXT movement command re-arms the whole campaign.
    private void SendStandUp(LocalPlayer me, string why)
    {
        me.MovementComponent.ChangeMovement(MovementAction.LeaveSit); // the StandUp toggle (action 87)
        _standEchoPending = true;
        _postureGivenUp = false;
        _postureAwait = PostureAwait.Stand;
        _sitConfirmed = false;
        _standSentAt = _wetClock.Elapsed.TotalSeconds;
        _standTries++;
        _logger.LogInformation($"Movement: standing up ({why}, try {_standTries}).");
    }

    public void ClearDesiredGoal(ControlPriority priority)
    {
        lock (_goallock)
        {
            if (goals.Remove((int)priority))
            {
                _logger.LogDebug($"Goal cleared: priority {priority}.");
            }
        }
    }

    /// <summary>
    ///     Has the walk reached the goal this priority set? Readable from the originating controller,
    ///     from any thread. False when no goal is set at this priority, when the goal is for another
    ///     playfield, or while ANY higher-priority goal is set - walking or already reached: only the
    ///     active goal can be satisfied, so a preempted goal re-arms the moment a higher one takes
    ///     over the body, even if the body happens to stand inside its radius. It is satisfied again
    ///     once the walk comes back down to it. If the body is pushed off the goal (a big SetPos), it
    ///     re-arms and the walk returns.
    /// </summary>
    public bool IsGoalReached(ControlPriority priority)
    {
        lock (_goallock)
        {
            return goals.TryGetValue((int)priority, out var goal) && goal.Reached;
        }
    }

    /// <summary>Is there a goal set at this priority at all (walking or reached)? The walk can take a
    /// goal back on a yank give-up - a goal that is GONE is the owner's or a controller's to re-issue.</summary>
    public bool HasGoal(ControlPriority priority)
    {
        lock (_goallock)
        {
            return goals.ContainsKey((int)priority);
        }
    }

    /// <summary>Clears every goal - the body stops on its next tick, with nothing left to walk.
    /// A travel plan is part of what "all" means: stop and sit end a travel too.</summary>
    public void ClearAllGoals()
    {
        lock (_goallock)
        {
            if (goals.Count > 0)
            {
                _logger.LogDebug("All goals cleared.");
            }

            goals.Clear();
        }

        lock (_travelLock)
        {
            if (_travel != null)
            {
                _travel = null;
                _logger.LogInformation("TRAVEL: plan cleared with the goals.");
            }

            _travelFailed.Clear(); // the write-offs belonged to the trip, not to the bot
        }
    }

    /// <summary>
    ///     Travel to another playfield, optionally to coordinates in it: the Zoning graph's Dijkstra
    ///     plans the cheapest way - zone lines walked across, whompas and teleporters walked up to and
    ///     used, booth/grid/lift pads walked onto - every hop is one leg, and the final leg is the
    ///     coordinates themselves. Returns a one-line summary for the owner's tell, including the
    ///     refusal when nothing usable connects the two. Safe from any thread.
    /// </summary>
    public string PlanTravel(int targetPf, Vector3? targetPos)
    {
        if (_pf < 0)
        {
            return "Not in a playfield yet - travel needs us on the ground first.";
        }

        if (!Zoning.Loaded)
        {
            return "No zoning data loaded - travel needs GameData/Zoning.json.";
        }

        lock (_travelLock)
        {
            if (targetPf == _pf)
            {
                _travel = null;
                if (targetPos.HasValue)
                {
                    SetDesiredGoal(targetPos.Value, targetPf, ControlPriority.Travel);
                    return $"Already in {Zoning.Name(targetPf)} - walking to ({targetPos.Value.X:0.0} {targetPos.Value.Z:0.0}).";
                }

                ClearDesiredGoal(ControlPriority.Travel); // any leg goal of a former plan is spent
                return $"Already in {Zoning.Name(targetPf)}.";
            }

            _travelFailed.Clear(); // a fresh order starts with a clean exit blacklist
            var route = Zoning.FindRoute(_pf, CurrentPosition, targetPf, targetPos, TravelOptions(null));
            if (route == null)
            {
                return $"No route from {Zoning.Name(_pf)} to {Zoning.Name(targetPf)} - nothing usable connects them in the zoning data.";
            }

            _travel = new TravelPlan { TargetPf = targetPf, TargetPos = targetPos };
            SetLeg(route.Hops[0].Exit);
            return $"Travel to {Zoning.Name(targetPf)} - {route.Describe()}" +
                   (targetPos.HasValue ? $", then ({targetPos.Value.X:0.0} {targetPos.Value.Z:0.0})." : ".");
        }
    }

    /// <summary>
    ///     Drops the travel plan - a manual order (goto/come) takes the body from it. The plan's
    ///     own leg goal goes with it: a cancelled trip must not leave the walk heading for the old
    ///     crossing point. A manual order that already replaced the leg goal is untouched - the
    ///     goto/come flow cancels BEFORE setting its own goal, and a plan that is already gone
    ///     (manual order cancelled it) makes this a no-op.
    /// </summary>
    public void CancelTravel()
    {
        lock (_travelLock)
        {
            if (_travel == null)
            {
                return;
            }

            _travel = null;
            ClearDesiredGoal(ControlPriority.Travel); // the leg goal was the plan's own (travelLock -> goalLock, as everywhere)
            _logger.LogInformation("TRAVEL: cancelled (a manual order takes the body).");
        }
    }

    /// <summary>
    ///     The target playfield of the active travel plan, or 0 when none is running - how another
    ///     controller tells "the plan died / was cancelled" from "still en route". Safe from any thread.
    /// </summary>
    public int TravelTargetPf
    {
        get
        {
            lock (_travelLock)
            {
                return _travel?.TargetPf ?? 0;
            }
        }
    }

    /// <summary>Compact one-line state for status replies. Safe from any thread.</summary>
    public string DescribeState()
    {
        string goalText;
        lock (_goallock)
        {
            goalText = goals.Count == 0
                ? "none"
                : string.Join("; ", goals.OrderByDescending(g => g.Key)
                    .Select(g => $"{(ControlPriority)g.Key} ({g.Key}) @ " +
                                 $"({g.Value.Position.X:0.0} {g.Value.Position.Y:0.0} {g.Value.Position.Z:0.0}) pf {g.Value.PlayfieldId}" +
                                 $"{(g.Value.Reached ? " REACHED" : "")}"));
        }

        return $"pf {_pf}, pos ({CurrentPosition.X:0.0} {CurrentPosition.Y:0.0} {CurrentPosition.Z:0.0}) | " +
               $"nav: {NavState()} | travel: {DescribeTravel()} | goals: {goalText}";
    }

    private string NavState()
    {
        if (_nav.LoadedPf != _pf)
        {
            return _nav.LoadedPf >= 0 ? $"loading (have {_nav.LoadedPf})" : "loading";
        }

        var nav = _nav.Nav;
        return nav == null ? "none (straight lines)" : $"{nav.Kind}, yBias {_yBias:+0.0;-0.0}";
    }

    /// <summary>Everything the nav data says about our current position (AOBuddy10's 'navdata' command,
    /// which exists so the data can be checked against the live character before anything relies on it).</summary>
    public string ExplainNav()
    {
        var nav = _nav.Nav;
        var p = CurrentPosition;
        if (nav == null || _nav.LoadedPf != _pf)
        {
            return $"nav: no data for pf {_pf} (loaded: {_nav.LoadedPf}).";
        }

        return nav.Explain(p.X, p.Y, p.Z);
    }

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(DCMoveHandler, N3MessageType.CharDCMove, (int)ControlPriority.None);
        router.Register(SetPosHandler, N3MessageType.SetPos, (int)ControlPriority.None);
        router.Register(ZoneInHandler, N3MessageType.PlayfieldAnarchyF, (int)ControlPriority.None); // the proxy return playfield
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        Client.OnUpdate += PublishSnapshot;
        Client.PostureToggled += OnPostureToggled;
        _thread = new Thread(WalkLoop)
        {
            IsBackground = true,
            Name = "AOBuddy-Movement",
        };
        _thread.Start();
        _logger.LogInformation("Movement loop started.");
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        Client.OnUpdate -= PublishSnapshot;
        Client.PostureToggled -= OnPostureToggled;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _logger.LogInformation("Movement loop stopped.");
    }

    // The server's echo of the sit/stand toggle (action 0x57, Client.cs): the only reliable
    // "the posture change took effect" signal there is. Which toggle it answers is what we sent
    // last (_postureAwait): the sit answer is the heal's SeatedConfirmed proof, the stand answer
    // releases the seated track. Evidence only - the login stand-up decision itself is made once
    // from the login mode and is never re-toggled.
    private void OnPostureToggled(Identity identity)
    {
        var me = DynelManager.LocalPlayer;
        if (me == null || identity != me.Identity)
        {
            return;
        }

        var awaited = _postureAwait;
        _postureAwait = PostureAwait.None;
        _postureGivenUp = false;
        if (awaited == PostureAwait.Sit)
        {
            _seated = true;
            _sitConfirmed = true;
            _standEchoPending = false;
            _logger.LogInformation("Movement: server confirmed the sit.");
            return;
        }

        _standEchoPending = false; // the toggle we were waiting for landed (or an unsolicited one)
        _seated = false;
        _sitConfirmed = false;
        _logger.LogInformation("Movement: server confirmed the posture change (stand-up echo).");
    }

    // Runs on the SDK update thread: the only place SDK state may be read (review.md #9).
    // OnUpdate fires only while in play, so a snapshot is proof we are up; the initial snapshot
    // (null player) keeps the walk idle until then.
    private void PublishSnapshot(object? sender, double deltaTime)
    {
        var me = DynelManager.LocalPlayer;
        var runSpeed = -1;
        if (me != null && me.TryGetStat(Stat.RunSpeed, out var rs) && rs != -1)
        {
            runSpeed = rs;
        }

        // The login movement mode: authoritative ONCE, at login. It never updates afterwards, so the
        // walk reads it only until the stand-up decision is made.
        var movementMode = -1;
        if (me != null && me.TryGetStat(Stat.CurrentMovementMode, out var mm))
        {
            movementMode = mm;
        }

        // The owner, for follow: visible or not, his latest reported spot and facing ride the snapshot.
        PlayerChar? owner = null;
        if (me != null && !string.IsNullOrEmpty(_config.Owner))
        {
            owner = DynelManager.Players.FirstOrDefault(pl =>
                string.Equals(pl.Name, _config.Owner, StringComparison.OrdinalIgnoreCase));
        }

        _ownerInstance = owner?.Identity.Instance ?? 0;
        var moveFresh = owner != null && Environment.TickCount64 - Interlocked.Read(ref _ownerLastMoveAt) < 600;

        _snap = new Snapshot
        {
            Me = me,
            Playfield = (int)Playfield.ModelId,
            RunSpeed = runSpeed,
            MovementMode = movementMode,
            OwnerVisible = owner != null,
            OwnerPos = owner?.Transform.Position ?? default,
            OwnerHeading = owner?.Transform.Heading ?? Quaternion.Identity,
            OwnerMoveFresh = moveFresh,
        };
    }

    // ── the movement thread ───────────────────────────────────────────────────────────────

    private void WalkLoop()
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        while (_running)
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = Math.Min(now - last, 0.25d); // a stall (GC, debugger) must not become one giant step
            last = now;
            try
            {
                Tick(dt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Movement tick failed.");
            }

            Thread.Sleep(15); // ~64 Hz, the SDK UpdateLoop cadence
        }
    }

    private void Tick(double dt)
    {
        var snap = _snap;
        if (snap.Me == null)
        {
            return; // not in play yet - nothing to move
        }

        var me = snap.Me;
        var now = _wetClock.Elapsed.TotalSeconds;
        if (_loginSeenAt < 0)
        {
            _loginSeenAt = now; // the login-mode timeout runs from the first tick with a body
        }

        // The ONE login stand-up decision (AOBuddy10 Main.cs, wire-proven): the stand-up wire action
        // (CharacterAction 87 / 0x57) is a sit/stand TOGGLE, and stat 173 is only set from the login
        // FullCharacter and NEVER updates afterwards. Read it once through the snapshot, send at most
        // one stand-up if the login mode was seated, and never toggle again - firing it blind sat a
        // standing character, and re-reading a stuck 8=Sit and re-sending made him sit/stand in a
        // loop. Until the decision is made nothing walks: a seated body cannot move, the server
        // rejects every step and snaps him back to where he sat.
        if (!_stoodUp)
        {
            if (snap.MovementMode > 0)
            {
                _stoodUp = true;
                if (SeatedModes.Contains(snap.MovementMode))
                {
                    _seated = true;
                    SendStandUp(me, "login mode " + snap.MovementMode);
                    _movement.Hold(me, SendIntervalMs);
                    return; // give the server the beat to apply it before the first step
                }

                _logger.LogInformation($"Movement: login mode {snap.MovementMode} (standing) - no stand-up needed.");
            }
            else if (now - _loginSeenAt > LoginModeTimeout)
            {
                // The login FullCharacter carried no movement mode at all (it happens after a
                // live-client session): decide standing - if the body turns out seated, the
                // stand-up campaign below still stands it up on the first command.
                _stoodUp = true;
                _logger.LogInformation("Movement: no login movement mode on the wire - assuming standing.");
            }
            else
            {
                return; // the login FullCharacter has not carried the mode yet: hold, never walk blind
            }
        }

        // A believed-seated body with no stand-up in flight is a CHOSEN sit (the owner's sit
        // command) or a login sit whose first toggle has yet to go out: hold. Seated bodies do not
        // walk - the server rejects every step.
        if (_seated && !_standEchoPending && !_postureGivenUp)
        {
            _movement.Hold(me, SendIntervalMs);
            return;
        }


        if (snap.Playfield != _pf)
        {
            // A new playfield puts the body wherever the server placed it; gait, mode, route and
            // follow state start over. The STOP GOES TO THE SERVER FIRST: a zone interrupts a run
            // mid-stream (the follow's lost-push onto the zone line, say), and wiping the local
            // state alone would leave the server holding ForwardStart with no ForwardStop - it
            // keeps the body running in the new playfield (owner, 2026-10-01: "after zoning is
            // done, the bot just runs on"). Movement.Stop sends the FullStop while Moving still
            // says so; Reset then clears the rest.
            var prevPf = _pf;
            Vector3? legExitPos = null; // the object exit we crossed through, for the proxy origin
            lock (_travelLock)
            {
                var leg = _travel?.LegExit;
                if (leg != null && leg.ObjInstance != 0)
                {
                    legExitPos = leg.A;
                }
            }

            _pf = snap.Playfield;
            _holdWalkUntil = now + ZoneSettleSeconds; // the post-zone movement lock (see the constant)
            _movement.Stop(me, SendIntervalMs);
            _movement.Reset();
            _follow.Reset();
            _route.Clear();
            _routeIdx = 0;
            _routePrio = -1;
            _routePf = -1;
            _stuckCells.Clear();
            _serverNo.Clear(); // its cells are grid-local keys: last playfield's marks are noise here
            _stuck.Reset();
            _wedgeCycles = 0;
            _reconnectSent = false; // the respawn is a new body: a fresh pin may escalate again
            _logger.LogInformation($"Movement: playfield {_pf}, run state cleared (server stopped).");
            lock (_poslock)
            {
                _confirmedPosition = me.MovementComponent.Position;
                _confirmedHeading = me.MovementComponent.Heading;
                _pendingCorrection = null;
            }

            _logger.LogInformation($"Movement: playfield {_pf}, position " +
                                   $"({_confirmedPosition.X:0.0} {_confirmedPosition.Y:0.0} {_confirmedPosition.Z:0.0}).");

            TravelAfterZone(); // a travel plan takes its next leg here, or lands its final walk
            ProxyCrossed(prevPf, legExitPos);
        }

        ApplyPendingCorrection(me);

        // THE POST-ZONE / POST-YANK HOLD: no walking until the server's lock lets go (see the
        // constants). Bookkeeping above still ran; the walk below starts fresh afterwards.
        if (now < _holdWalkUntil)
        {
            _movement.Hold(me, SendIntervalMs);
            return;
        }

        // THE DRIFT GUARD: the server stopped following our steps (an area it forbids - it rejects
        // silently and rubberbands much later). Past a few metres of gap between the body's
        // dictated position and the server's last confirmed one, stop and wait for the truth; the
        // SetPos resets the position, the guard releases, and the route re-plans from there.
        var drift = Movement.Flat(me.MovementComponent.Position, CurrentPosition);
        if (drift > DriftGuardMetres && _movement.Moving)
        {
            _movement.Hold(me, SendIntervalMs);
            if (!_driftHeld)
            {
                _driftHeld = true;
                _routePrio = -1; // re-plan from the server's truth once it lands
                _driftHeldSince = now;
                _logger.LogInformation($"Movement: the server is {drift:0.0} m behind the walk - holding until it catches up.");
            }
            else if (now - _driftHeldSince > DriftHoldSeatedSeconds && !_standEchoPending && !_seated)
            {
                // Held this long with no server truth at all is the seated signature: the server
                // silently rejects every step of a seated body (no corrections, no movement). The
                // login mode can be missing entirely after a live-client session - the drift is
                // what tells us. One stand-up from here; the campaign owns it from there.
                _driftHeldSince = now;
                _seated = true;
                SendStandUp(me, "the walk is held with no server movement - seated?");
            }

            return;
        }

        _driftHeld = false;
        _driftHeldSince = -1;

        // The playfield's nav data, built off every loop thread (Lush Fields took 6.3 s, log 2026-09-24):
        // while it loads there is no honest Y, so hold rather than walk blind. A mission instance's
        // data comes composed from the MissionController, not from disk.
        if (!_nav.Request(snap.Playfield, BaseDir, s => _logger.LogInformation(s), "MOVE",
                _missionNavPf == snap.Playfield ? _missionNav : null))
        {
            _movement.Hold(me, SendIntervalMs);
            return;
        }

        // This playfield's wet verdict, so a zone line is crossed at a dry point: Zoning.CrossLine
        // moves along the line to dry ground (Stret East Bank's line to Andromeda runs the whole south
        // border, and its nearest point was open water that never took the bot, owner 2026-09-27).
        if (_wetPf != snap.Playfield)
        {
            Func<float, float, bool> wet = null;
            var ground = _nav.Nav?.Ground;
            if (ground != null)
            {
                wet = (x, z) => !double.IsNaN(ground.SwimY(x, z, WadeDepth));
            }

            Zoning.SetWet(snap.Playfield, wet);
            _wetPf = snap.Playfield;
        }

        TravelTick(); // the leg watchdog: a line that never zones us must not strand the plan

        // The stand-up campaign: an unresolved posture holds the body (walking seated only
        // collects yanks) while the toggle goes out again and again - fast at first, then on a
        // slow re-arm, until the 0x57 echo lands. When the budget runs out the walk assumes
        // standing (the echo is occasionally lost even when the stand applied), but _seated stays
        // true, so the NEXT movement command re-arms the whole campaign (owner, 2026-10-02).
        if (_standEchoPending)
        {
            if (now - _standSentAt >= (_standTries <= MaxStandTries ? StandRetrySeconds : StandRearmSeconds))
            {
                if (_standTries >= MaxStandTries + MaxStandRearms)
                {
                    _standEchoPending = false;
                    _postureGivenUp = true;
                    _logger.LogWarning(
                        $"Movement: no posture echo after {_standTries} stand-ups - assuming standing. " +
                        "If I'm still seated, the next movement command re-arms the stand-up ('stand' works too).");
                }
                else
                {
                    SendStandUp(me, _standTries <= MaxStandTries ? "no posture echo" : "still no echo - slow re-arm");
                }
            }

            _movement.Hold(me, SendIntervalMs);
            return;
        }

        var goal = SelectActiveGoal(_pf);
        if (goal != null)
        {
            if (_yanks > MaxYanks)
            {
                // The data and the server disagree too often on this one (three yanks): walking on
                // only collects more rubberbands. Give the goal up; the owner can re-order.
                ClearDesiredGoal((ControlPriority)goal.Value.Key);
                _yanks = 0;
                _movement.Hold(me, SendIntervalMs);
                _logger.LogWarning($"Movement: the server kept overruling the walk ({MaxYanks}+ yanks) - giving up the " +
                                   $"priority {goal.Value.Key} goal at ({goal.Value.Value.Position.X:0.0} {goal.Value.Value.Position.Z:0.0}).");
                return;
            }

            if (_follow.MirrorLocked)
            {
                _follow.BreakMirror("a goal took the body");
            }

            GoalWalk(me, snap, goal.Value, dt);
            return;
        }

        // No goal: follow drives the body (stack on the owner and mirror his movement packets);
        // without it, hold.
        if (_followOn)
        {
            _follow.Tick(me, snap.OwnerVisible, snap.OwnerPos, snap.OwnerHeading, snap.OwnerMoveFresh,
                RunVelocity(snap), dt, (m, target, stepDt) => WalkStep(m, snap, target, stepDt));
            return;
        }

        _movement.Hold(me, SendIntervalMs); // nowhere to go: hold position (guarded, no packet spam)
    }

    // The goal walk: arrival marking, the grid route round doors and zone lines, the stuck watch,
    // and the nav-Y step toward the current step target.
    private void GoalWalk(LocalPlayer me, Snapshot snap, KeyValuePair<int, GoalLocation> goal, double dt)
    {
        var pos = me.MovementComponent.Position;
        var dist = Movement.Flat(pos, goal.Value.Position);
        if (dist <= goal.Value.ArriveRadius)
        {
            _movement.Hold(me, SendIntervalMs);
            if (!goal.Value.Reached)
            {
                goal.Value.Reached = true;
                _logger.LogInformation($"Movement: reached the priority {goal.Key} goal " +
                                       $"({goal.Value.Position.X:0.0} {goal.Value.Position.Y:0.0} {goal.Value.Position.Z:0.0}), {dist:0.0} m out.");
            }

            // A manual order (priority Travel) hands the body straight back: 'come' should not park
            // the follow until 'stop'. Controllers' own goals are theirs to clear. A travel LEG is
            // not a handback: its crossing point is reached only to stand there while the zone
            // lands - mid-plan the body stays with the plan.
            if (_followOn && goal.Key == (int)ControlPriority.Travel)
            {
                bool travelActive;
                lock (_travelLock)
                {
                    travelActive = _travel != null;
                }

                if (!travelActive)
                {
                    ClearDesiredGoal(ControlPriority.Travel);
                    _logger.LogInformation("Movement: manual goal done - handing the body back to follow.");
                }
            }

            return;
        }

        // Not there (yet, or anymore): the flag only ever says reached while we stand on it.
        goal.Value.Reached = false;

        // WHERE TO STEP: the goal itself, or the next point of a grid ROUTE to it. The grid's
        // per-search blocked set keeps 2 m off every zone line and 3 m off every door/whompa/
        // teleporter that is not the goal itself (AOBuddy10 OverlandController.BeginLeg).
        var target = goal.Value.Position;
        var grid = _nav.Grid;
        if (grid != null)
        {
            if (_routePrio != goal.Key || _routePf != _pf || Movement.Flat(_routeGoal, target) > 0.01f)
            {
                PlanRoute(pos, target, goal.Key);
            }

            if (_route.Count == 0)
            {
                // The grid covers the whole playfield and says the goal cannot be walked to. HOLD:
                // the old beeline respected nothing and ran the bot through walls until the server
                // yanked it back (Newland City, 2026-10-01 21:25 and 21:44 - both log lines ended
                // in "walking straight"). Only a playfield WITHOUT grid data may walk straight
                // lines; with data, unreachable means stand and say so (the goal stays set, and a
                // changed goal or playfield re-plans).
                _movement.Hold(me, SendIntervalMs);
                return;
            }

            while (_routeIdx < _route.Count &&
                   Movement.Flat(pos, _route[_routeIdx]) <= (_routeIdx == _route.Count - 1 ? WaypointLastRange : WaypointRange))
            {
                _routeIdx++;
                _stuck.Reset();
            }

            if (_routeIdx < _route.Count)
            {
                target = _route[_routeIdx];
            }
        }

        var tdist = Movement.Flat(pos, target);
        var delta = target - pos;
        var flat = new Vector3(delta.X, 0f, delta.Z);
        if (flat.Magnitude < 0.05f)
        {
            _movement.Hold(me, SendIntervalMs); // straight up/down (a lift, a stacked floor): nothing to walk
            return;
        }

        var dir = flat.Normalize();

        // STUCK (AOBuddy10): no progress toward the step target for StuckSeconds - something the data
        // does not show is in the way (a gap in the walls, a crate, a fence). Block the few metres
        // ahead and route round them; after MaxStuck of those, say where we are and try again.
        if (grid != null && _stuck.Tick(tdist, dt, 0.3f, StuckSeconds))
        {
            _stuckCount++;
            _stuck.Reset();
            if (_stuckCount > MaxStuck)
            {
                _logger.LogInformation($"Movement: stuck at ({pos.X:0.0} {pos.Z:0.0}), {dist:0.0} m short of the goal - something is in the way.");
            }
            else
            {
                grid.CellsAlong(new Vector3(pos.X + dir.X, 0f, pos.Z + dir.Z),
                    new Vector3(pos.X + dir.X * 3f, 0f, pos.Z + dir.Z * 3f), 1f, _stuckCells);
                _logger.LogInformation($"Movement: no progress for {StuckSeconds:0} s at ({pos.X:0.0} {pos.Z:0.0}), routing round it ({_stuckCount}/{MaxStuck}).");
                _movement.Hold(me, SendIntervalMs);
                PlanRoute(pos, goal.Value.Position, goal.Key);
                return;
            }
        }

        WalkStep(me, snap, target, dt);
    }

    // One capped step toward a target, the proven outdoor walker (AOBuddy10 OverlandController):
    // mouse-look facing, then Advance at the run-speed formula with the step's Y taken from the NAV
    // DATA (the floor under the next position, the water surface over it), never from the target.
    // Shared by the goal walk and follow's approach/chase, so both ride the terrain identically.
    private void WalkStep(LocalPlayer me, Snapshot snap, Vector3 target, double dt)
    {
        var pos = me.MovementComponent.Position;
        var dist = Movement.Flat(pos, target);
        var delta = target - pos;
        var flat = new Vector3(delta.X, 0f, delta.Z);
        if (flat.Magnitude < 0.05f)
        {
            _movement.Hold(me, SendIntervalMs);
            return;
        }

        var dir = flat.Normalize();

        // WATER - the captured client's contract: probe just ahead for the surface verdict. The
        // SWIM GAIT is part of the verdict, not just the speed: deep water refuses run-mode steps
        // outright and the server holds the body on the shore (Newland lake, 2026-09-24 20:46) -
        // so the verdict comes from the data AND from the gait we are already in (a river the
        // liquids never mapped reads dry forever, and only EnterSwim gets it crossed; Varmint
        // Woods 2026-10-03 15:00, pinned at (2541.9,2175.4) under corrections alone).
        var probe = Math.Min(dist, WadeProbeMeters);
        var plane = _nav.Nav?.Ground != null
            ? _nav.Nav.Ground.SwimY(pos.X + dir.X * probe, pos.Z + dir.Z * probe, WadeDepth)
            : double.NaN;
        if (double.IsNaN(plane) && _movement.Swimming)
        {
            // data-dry water we are already swimming: the surface is the server's last word on
            // our height (fresh corrections), or our own height standing in for it
            var fresh = _wetClock.Elapsed.TotalSeconds - _wetYAt < 8;
            plane = fresh ? _wetY : pos.Y;
        }

        var inWater = !double.IsNaN(plane);
        var speed = inWater ? RunVelocity(snap) * SwimSpeedFactor : RunVelocity(snap);
        var step = Movement.CappedStep(speed, dt, MaxStep, dist);
        var nx = pos.X + dir.X * step;
        var nz = pos.Z + dir.Z * step;
        var floorY = FloorY(nx, pos.Y, nz);

        // UPHILL THE SERVER IS STRICTER THAN ON THE FLAT (capture 20260925-141138, Wailing Wastes: a
        // 0.1/m rise refused every 1.5 m step at 15 u/s on the wire, while the captured client walks the
        // same slope in 0.1-0.2 m moves every 10-30 ms). Climb in steps small enough to be under run
        // speed per send interval.
        if (!inWater && floorY > pos.Y)
        {
            step = Math.Min(step, Math.Max(0.3f, speed * SendIntervalMs / 1000f * 0.6f));
            nx = pos.X + dir.X * step;
            nz = pos.Z + dir.Z * step;
            floorY = FloorY(nx, pos.Y, nz);
        }

        var now = _wetClock.Elapsed.TotalSeconds;
        var floating = inWater && now - _wetYAt < 3 && _wetY > floorY + 0.4f && _wetY <= plane + 0.3f;
        var nextY = floating ? _wetY // the server's own surface Y, while fresh
            : inWater && floorY < (float)plane - SwimWadeMeters ? (float)plane // swim at the surface
            : floorY; // wade the bottom / walk the shore

        // NEVER BELOW THE TERRAIN (2026-09-25, the Wailing Wastes rubberband): the heightfield is solid
        // ground outdoors - the step we claim can never be under it.
        var terr = _nav.Nav?.Ground != null ? _nav.Nav.Ground.HeightAt(nx, nz) : double.NaN;
        if (!double.IsNaN(terr) && terr > nextY)
        {
            nextY = (float)terr;
        }

        if (!inWater)
        {
            // ...AND STEP UP ONTO WHAT WE WALK INTO (the ICC steps, same day): under a staircase the
            // nearest floor is the terrain BENEATH THE STAIRS, so the walk would claim the plaza's height
            // while stepping into the rising treads. The surface we stand on at the next position is the
            // HIGHEST floor at most a step above us (a riser or two - anything higher is a wall).
            var sf = StepFloor(nx, pos.Y, nz);
            if (!float.IsNaN(sf) && sf >= pos.Y - 4f && sf > nextY)
            {
                nextY = sf;
            }
        }
        else if (_movement.Swimming && _nav.Nav?.Ground != null &&
                 double.IsNaN(_nav.Nav.Ground.SwimY(nx, nz, WadeDepth)) && floorY > pos.Y - 0.6f &&
                 now - _wetYAt > 2)
        {
            // THE FAR BANK (the data-dry river, Varmint Woods 2026-10-03): the floor has risen to
            // the body where the liquids still say dry - land under the next step, back to the run
            // gait. AND ONLY THERE: this river's server holds the body ON the bed (floor +-0.0 in
            // the corrections), so floor-near-body is true MID-RIVER too - the first version left
            // the swim on the very next step and the gait flip-flopped, pinned, forever. The 2 s
            // without a refusal correction is the tell that steps are being ACCEPTED here (refusals
            // re-mark _wetYAt every few hundred ms). Mid-river sandbars leave the swim and the next
            // refusal re-enters it.
            _movement.LeaveSwim(me, SendIntervalMs);
        }

        var want = Movement.SafeLook(dir, me.MovementComponent.Heading);
        _movement.Advance(me, new Vector3(nx, nextY, nz), want, run: true, dt, SendIntervalMs);
    }

    // Plan the grid route to a goal (AOBuddy10 OverlandController.BeginLeg): every zone line is a
    // corridor of blocked cells (a same-playfield goal never wants one), and every contact exit
    // (door, whompa, teleporter, proxy) is a 3 m disc - except the one the goal stands on, and one we
    // are standing in (the first step must be able to leave it; owner, 2026-09-27: a Longest Road
    // walk stepped onto the Broken Shores booth 4 m from the whompa landing). No grid, or no route
    // even at wide reach: the walk beelines, and says so.
    private void PlanRoute(Vector3 from, Vector3 goalPos, int priority)
    {
        _routePrio = priority;
        _routeGoal = goalPos;
        _routePf = _pf;
        _routeIdx = 0;
        _stuckCount = 0;
        _stuck.Reset();
        _route.Clear();

        var grid = _nav.Grid;
        if (grid == null)
        {
            return; // no walk grid for this playfield: straight lines
        }

        var extra = new HashSet<int>(_stuckCells);
        extra.UnionWith(_serverNo); // spots the server already refused us, this session

        // The travel leg's own line stays open: its goal IS the crossing point beyond it (a travel
        // goal never matches by accident - a manual goal in the same spot cancelled the plan).
        ZoneExit? passLine = null;
        lock (_travelLock)
        {
            var t = _travel;
            if (t?.LegExit != null && t.LegPf == _pf && Movement.Flat(t.LegGoal, goalPos) <= 0.01f)
            {
                passLine = t.LegExit;
            }
        }

        foreach (var e in Zoning.ExitsFrom(_pf))
        {
            if (e.Kind == ExitKind.ZoneLine)
            {
                if (!ReferenceEquals(e, passLine))
                {
                    grid.CellsAlong(e.A, e.B, 2f, extra);
                }
            }
            else if ((e.Kind == ExitKind.Line || e.Kind == ExitKind.Proxy || e.Kind == ExitKind.Teleport)
                     && Movement.Flat(e.A, goalPos) > 1.5f && Movement.Flat(e.A, from) > 3.5f)
            {
                grid.CellsAlong(e.A, e.A, 3f, extra);
            }
        }

        // THE BLACKLISTS NEVER SEAL THE BODY IN (owner, 2026-10-03: "shouldn't blacklist cells
        // which were walked already"): every cell within ~3 m of where the server has us right
        // now is walkable again for THIS plan. A yank band runs from where the server pulled us
        // back THROUGH the cells the walk claimed - and those cells can be the body's only way
        // out of a landing pocket: the Varmint Woods zone-in yank blacklisted the pocket's exit,
        // and every re-plan after failed "walled off" at the goal 1.2 km on with south and east
        // wide open - no walk, no further yank, so the give-up never came. The marks still steer
        // the route away from refused ground further out, and a spot that is truly bad collects
        // its yank and its share of the give-up honestly. Unsealed LAST, so it wins over every
        // band added above (a zone line we stand on included).
        var unseal = new HashSet<int>();
        grid.CellsAlong(from, from, 3f, unseal);
        extra.ExceptWith(unseal);

        var route = grid.FindPath(from, goalPos, extra, SnapMeters, GoalReach, out var why)
                    ?? grid.FindPath(from, goalPos, extra, SnapMeters, WideGoalReach, out _);

        // THE GEOMETRY OUTRANKS THE GRID AT CLOSE RANGE (owner, 2026-10-03): a plan that found no
        // route may still be a straight walk the cells never saw. Within reach of the goal the
        // triangles answer exactly - floor end to end, no wall crossing the body band - and that
        // line is the route; the blacklists don't apply to it (a sealed-in body needs its way out,
        // the same reasoning as the unseal above). Beyond the radius the answer is the grid's.
        if (route == null && Movement.Flat(from, goalPos) <= GeometryLineMaxMeters)
        {
            var gwhy = "";
            var lineClear = grid switch
            {
                OverlandGrid og => og.GeometryLine(from, goalPos, out gwhy),
                FloorGrid fg => fg.GeometryLine(from, goalPos, out gwhy),
                _ => false,
            };
            if (lineClear)
            {
                _noRoute = 0;
                _route = new List<Vector3> { from, goalPos };
                _logger.LogInformation($"Movement: geometry-direct {Movement.Flat(from, goalPos):0} m line to the goal " +
                                       $"- the grid had no route ({why}).");
                return;
            }
        }

        // THE STUCK ESCAPE (owner, 2026-10-03: "we need an exacter way to pathfind if he's
        // stuck"): the grid found nothing and the goal is beyond the near line - the fan of
        // geometry-clear lines is the exacter instrument now. One 20 m leg of real ground, then
        // the next tick re-plans from there. Counted: MaxEscapes in a row and the escapes are
        // circling, not escaping - the give-up below takes over honestly.
        if (route == null)
        {
            var esc = grid.Escape(from, goalPos, out var ewhy);
            if (esc.HasValue && ++_escapes <= MaxEscapes)
            {
                _noRoute = 0;
                _route = new List<Vector3> { from, esc.Value };
                _logger.LogWarning($"Movement: no grid route ({why}) - geometry escape leg to " +
                                   $"({esc.Value.X:0.0} {esc.Value.Z:0.0}) ({_escapes}/{MaxEscapes}; {ewhy}).");
                return;
            }

            _logger.LogWarning(esc.HasValue
                ? $"Movement: escape leg refused - {_escapes} in a row is circling, not escaping."
                : $"Movement: no grid route ({why}) and no geometry escape ({ewhy}).");
        }

        if (route == null && grid is OverlandGrid overland)
        {
            // The body can stand where the stamps sealed the ground under it - a mission door
            // (no zoning exit, so no kept-open disc) or any other placed spot. Reopen a disc at
            // where the server has us RIGHT NOW and search once more; if that still finds
            // nothing, the failure is honest (owner, 2026-10-03: out of a mission, every route
            // out of the entrance pocket failed "walled off" with the body at the door).
            overland.Reopen(from, 4f);
            _routePrio = -1; // re-plan from scratch on the next tick with the reopened disc
            if (++_noRoute >= MaxNoRoute)
            {
                // The reopened disc bought no route either. Re-planning on from here loops at
                // forty plans a second forever: no walk means no yank, so the yank give-up
                // never fires (Varmint Woods, same day - four minutes pinned between re-plans).
                // Give the goal up the way that path does; the owner can re-order.
                ClearDesiredGoal((ControlPriority)priority);
                _yanks = 0;
                _noRoute = 0;
                _giveUpPf = _pf; // refused again for a while: the travel tick re-sets the identical
                _giveUpGoal = goalPos; // goal in milliseconds, and that was the 30-plans loop
                _giveUpAt = _wetClock.Elapsed.TotalSeconds;
                _logger.LogWarning($"Movement: no grid route after {MaxNoRoute} tries ({why}) - giving up the " +
                                   $"priority {priority} goal at ({goalPos.X:0.0} {goalPos.Z:0.0}).");
                return;
            }

            _logger.LogInformation(
                $"Movement: no grid route to the goal ({why}) - reopened the ground I stand on; trying again ({_noRoute}/{MaxNoRoute}).");
            return;
        }

        if (route == null)
        {
            _logger.LogInformation($"Movement: no grid route to the goal ({why}) - holding; a beeline would cross walls.");
            return;
        }

        _noRoute = 0;
        _escapes = 0;
        _route = route;
        _logger.LogInformation($"Movement: {route.Count}-point route to the priority {priority} goal.");
    }

    // ── proxy origin (what a proxy playfield's back exit resolves against) ────────────────

    // PROXY playfields (shops, houses - entered through a proxy door) have no static destination
    // in their exit data: the server wires the door per instance. Zoning carries the door as a
    // back exit (to 0). Its destination is LIVE SERVER DATA: the zone-in message names the return
    // playfield (PlayfieldAnarchyFMessage.ProxyReturn, 0xC0090000 | pf) - never a remembered file,
    // so a bot moved to another instance by the live client, or one that relogs inside a shop,
    // reads the right way out from the packet it is actually in (owner, 2026-10-02). Mission
    // instances send none (their way out is the composed building's doorway); a plain crossing
    // falls back to the playfield we came from. Arrives through the PacketRouter (update thread);
    // read on the walk thread's playfield switch - a volatile int, no torn reads.
    private volatile int _zoneInReturnPf = -1; // the latest zone-in's ReturnPlayfield (-1 = none yet)

    private bool ZoneInHandler(AOMessage arg)
    {
        if (arg.Body is PlayfieldAnarchyFMessage zoneIn)
        {
            _zoneInReturnPf = zoneIn.ReturnPlayfield;
        }

        return false;
    }

    private void ProxyCrossed(int prevPf, Vector3? legExitPos)
    {
        var returnPf = _zoneInReturnPf;
        _zoneInReturnPf = -1;
        var fromPf = returnPf > 0 ? returnPf : prevPf;
        if (fromPf <= 0)
        {
            return; // no origin from packet or crossing: the back exits stay unusable until a real one
        }

        // The position we stood at is ours only when it agrees with the server's playfield; on a
        // login inside the instance there is no crossing position at all.
        Vector3? pos = returnPf > 0 && prevPf >= 0 && returnPf != prevPf ? null : legExitPos;
        Zoning.SetProxyOrigin(_pf, fromPf, pos);
        _logger.LogInformation($"Movement: {Zoning.Name(_pf)} (proxy) entered from {Zoning.Name(fromPf)}" +
                               $"{(returnPf > 0 ? " (zone-in)" : " (crossing)")}, " +
                               $"{(pos.HasValue ? $"came out at ({pos.Value.X:0.0} {pos.Value.Z:0.0})" : "no crossing position")}.");
    }

    // ── travel legs (the PlanTravel state machine, owner 2026-10-01) ──────────────────────

    // The current leg, shaped by its exit's kind (AOBuddy10 OverlandController.BeginLeg): a zone line
    // is walked THROUGH (the goal is a few metres OUT of the playfield, so the crossing - which is
    // what the server zones on - happens while the walk still has metres in hand); a booth, grid exit
    // or lift beam (kind Line) is a pad walked ONTO, exactly on its centre; a whompa or teleporter is
    // a terminal walked up to and then used. Called under _travelLock.
    private void SetLeg(ZoneExit hop)
    {
        var t = _travel!;
        t.LegExit = hop;
        t.LegPf = _pf;
        t.LegReachedAt = -1;
        t.AwaitAt = -1;
        if (hop.Back)
        {
            // A proxy playfield's exit door: the crossing is taken from inside the doorway only -
            // activation radius under half a metre (owner, 2026-10-01) - so walk ONTO the door and
            // use it on the stand (TravelTick), not through it.
            t.LegGoal = hop.A;
            SetDesiredGoal(hop.A, _pf, ControlPriority.Travel, DoorReach);
            _logger.LogInformation($"TRAVEL: leg out of {Zoning.Name(_pf)} - stepping into the exit door at ({hop.A.X:0.0} {hop.A.Z:0.0}).");
            return;
        }

        switch (hop.Kind)
        {
            case ExitKind.ZoneLine:
            {
                var (at, beyond, _) = Zoning.CrossLine(hop, CurrentPosition);
                t.LegGoal = beyond;
                SetDesiredGoal(beyond, _pf, ControlPriority.Travel, 1.5f);
                _logger.LogInformation($"TRAVEL: leg to {Zoning.Name(hop.ToPf)} - crossing the zone line at ({at.X:0.0} {at.Z:0.0}).");
                break;
            }
            case ExitKind.Proxy:
                // A proxy door into an instanced playfield is a DOOR, with the same tight
                // activation as the exit doors (under half a metre): walk ONTO it and use it on
                // the stand. The old 2.5 m terminal reach used the door from outside its
                // activation - stood before the shop door, used, no transition (owner, 2026-10-02).
                t.LegGoal = hop.A;
                SetDesiredGoal(hop.A, _pf, ControlPriority.Travel, DoorReach);
                _logger.LogInformation($"TRAVEL: leg to {Zoning.Name(hop.ToPf)} - stepping into the proxy door at ({hop.A.X:0.0} {hop.A.Z:0.0}).");
                break;
            case ExitKind.Line:
                t.LegGoal = hop.A;
                SetDesiredGoal(hop.A, _pf, ControlPriority.Travel, PadReach);
                _logger.LogInformation($"TRAVEL: leg to {Zoning.Name(hop.ToPf)} - stepping onto the pad at ({hop.A.X:0.0} {hop.A.Z:0.0}).");
                break;
            default:
                t.LegGoal = hop.A;
                SetDesiredGoal(hop.A, _pf, ControlPriority.Travel, ObjectReach);
                _logger.LogInformation($"TRAVEL: leg to {Zoning.Name(hop.ToPf)} - walking up to the {hop.Kind.ToString().ToLower()} at ({hop.A.X:0.0} {hop.A.Z:0.0}).");
                break;
        }
    }

    // A zone during a travel plan: arrived (final coordinates or done), or on to the next leg -
    // re-planned from wherever the server actually put us, so even a surprise zone (a beeline that
    // crossed a line the data placed elsewhere) cannot derail the plan.
    private void TravelAfterZone()
    {
        TravelPlan? t;
        lock (_travelLock)
        {
            t = _travel;
        }

        if (t == null)
        {
            return;
        }

        if (_pf == t.TargetPf)
        {
            lock (_travelLock)
            {
                if (_travel != t)
                {
                    return; // replaced meanwhile (a fresh travel order)
                }

                _travel = null;
            }

            if (t.TargetPos.HasValue)
            {
                SetDesiredGoal(t.TargetPos.Value, _pf, ControlPriority.Travel); // replaces the leg goal
                _logger.LogInformation($"TRAVEL: arrived in {Zoning.Name(_pf)} - walking to " +
                                       $"({t.TargetPos.Value.X:0.0} {t.TargetPos.Value.Z:0.0}).");
            }
            else
            {
                ClearDesiredGoal(ControlPriority.Travel); // the old playfield's leg goal is spent
                _logger.LogInformation($"TRAVEL: complete - {Zoning.Name(_pf)}.");
            }

            return;
        }

        var route = Zoning.FindRoute(_pf, CurrentPosition, t.TargetPf, t.TargetPos, TravelOptions(SnapshotFailed()));
        bool dead;
        lock (_travelLock)
        {
            if (_travel != t)
            {
                return;
            }

            if (route == null || route.Hops.Count == 0)
            {
                _travel = null;
                dead = true;
            }
            else
            {
                dead = false;
                SetLeg(route.Hops[0].Exit);
            }
        }

        if (dead)
        {
            ClearDesiredGoal(ControlPriority.Travel);
            _logger.LogWarning($"TRAVEL: no way on from {Zoning.Name(_pf)} to {Zoning.Name(t.TargetPf)} - travel abandoned.");
        }
    }

    // The blacklist as the planner sees it: a stable copy (the live set is mutated under the lock).
    private HashSet<ZoneExit> SnapshotFailed()
    {
        lock (_travelLock)
        {
            return new HashSet<ZoneExit>(_travelFailed);
        }
    }

    // The route ask for travel: requirements against our live stats (TryGetStat is the sanctioned
    // cross-thread read - the ConcurrentDictionary stats), Scotty off until its data lands, an exit
    // we cannot name (no object identity) or that failed on this trip left out.
    private ZoneRouteOptions TravelOptions(HashSet<ZoneExit> failed)
    {
        var o = Zoning.RouteOptions(DynelManager.LocalPlayer);
        o.Filter = e =>
            (e.Kind == ExitKind.ZoneLine || e.Kind == ExitKind.Scotty || e.ObjInstance != 0) &&
            (failed == null || !failed.Contains(e));
        return o;
    }

    // The wire-proven use (AOBuddy10 GameCommands.UseObject, capture 20260923-201746): GenericCmd Use
    // on a WORLD object - whompa, grid terminal, lift - Count=1, Temp4=1, the exact bytes the owner's
    // client sends. Sent from the walk thread; the send lock (the packet id) makes that safe.
    private void SendUse(ZoneExit e)
    {
        var me = DynelManager.LocalPlayer;
        if (me == null || e.ObjInstance == 0)
        {
            return; // no character, or an exit the data cannot name
        }

        Client.Send(new GenericCmdMessage
        { Action = GenericCmdAction.Use, User = me.Identity, Target = new Identity((IdentityType)e.ObjType, e.ObjInstance), Count = 1, Temp4 = 1 });
        _logger.LogInformation($"TRAVEL: used {e.ObjType}:{e.ObjInstance} at ({e.A.X:0.0} {e.A.Y:0.0} {e.A.Z:0.0}).");
    }

    // The leg watchdog. The NORMAL beat is the zone-in above, which advances the plan while the
    // server is still handing us the new playfield. This fires when a leg's goal reads reached and
    // the leg must finish its own business (AOBuddy10's Settle/Use/AwaitZone): stand still a moment
    // so the server has our stop, use terminals (a pad only on its last stand - standing is what
    // takes you), then wait per kind for the zone. When the wait runs out with no zone, the exit is
    // walked up to again - until the try budget is spent, and then it is written off and the plan
    // re-routes round it. A same-playfield exit (lift beam, inner teleporter) zones nobody: when a
    // server correction lands us at its arrival point, the ride is simply taken and the plan moves
    // on from there.
    private void TravelTick()
    {
        TravelPlan? t;
        lock (_travelLock)
        {
            t = _travel;
        }

        if (t?.LegExit == null)
        {
            return;
        }

        bool retry = false, writeOff = false, rideTaken = false;
        lock (_goallock)
        {
            if (!goals.TryGetValue((int)ControlPriority.Travel, out var g) ||
                g.PlayfieldId != t.LegPf || Movement.Flat(g.Position, t.LegGoal) > 0.01f || !g.Reached)
            {
                // Not (any more) standing on the leg goal: restart any window. But a same-playfield
                // exit that has meanwhile MOVED us to its arrival point has done its job.
                t.LegReachedAt = -1;
                t.AwaitAt = -1;
                var e = t.LegExit;
                if (e.ToPf == t.LegPf && e.Arrival.HasValue &&
                    Movement.Flat(CurrentPosition, e.Arrival.Value) < 5f &&
                    Movement.Flat(CurrentPosition, t.LegGoal) > 2f)
                {
                    rideTaken = true;
                }
                else
                {
                    return;
                }
            }
            else
            {
                var now = _wetClock.Elapsed.TotalSeconds;
                if (t.LegReachedAt < 0)
                {
                    t.LegReachedAt = now;
                }

                if (now - t.LegReachedAt < SettleSeconds)
                {
                    return; // settle: the server has our stop before we use from where it has us
                }

                if (t.AwaitAt < 0)
                {
                    t.AwaitAt = now;
                    t.Tries++;
                    var kind = t.LegExit.Kind;
                    if (t.LegExit.Back || kind == ExitKind.Teleport || kind == ExitKind.Proxy
                        || kind == ExitKind.Line && t.Tries >= MaxLegTries)
                    {
                        SendUse(t.LegExit); // terminals and exit doors are used; a pad only ever on its last stand
                    }

                    return; // the window starts
                }

                var wait = t.LegExit.Back || t.LegExit.Kind == ExitKind.Teleport || t.LegExit.Kind == ExitKind.Proxy ? ObjectWait
                    : t.LegExit.Kind == ExitKind.Line ? PadWait
                    : ZoneLineWait;
                if (now - t.AwaitAt < wait)
                {
                    return; // the zone normally lands long before this
                }

                if (t.Tries < MaxLegTries)
                {
                    retry = true; // walk up / over again
                }
                else
                {
                    writeOff = true; // the exit gave nothing: route round it
                }
            }
        }

        if (rideTaken)
        {
            _logger.LogInformation("TRAVEL: the exit moved us within its own playfield - planning on from there.");
            TravelAfterZone();
            return;
        }

        if (retry)
        {
            _logger.LogInformation($"TRAVEL: try {t.Tries + 1}/{MaxLegTries} at {t.LegExit}.");
            lock (_travelLock)
            {
                if (_travel == t)
                {
                    SetLeg(t.LegExit);
                }
            }

            return;
        }

        if (writeOff)
        {
            WriteOffLeg(t);
        }
    }

    // An exit that gave nothing MaxLegTries times: on the blacklist, and the plan re-routes round it
    // (AOBuddy10 FailExit) - or, when nothing left connects, ends honestly.
    private void WriteOffLeg(TravelPlan t)
    {
        HashSet<ZoneExit> failed;
        lock (_travelLock)
        {
            if (_travel != t)
            {
                return;
            }

            _travelFailed.Add(t.LegExit);
            failed = new HashSet<ZoneExit>(_travelFailed);
            _logger.LogInformation($"TRAVEL: {t.LegExit} gave nothing after {MaxLegTries} tries - routing round it.");
        }

        var route = Zoning.FindRoute(_pf, CurrentPosition, t.TargetPf, t.TargetPos, TravelOptions(failed));
        bool dead;
        lock (_travelLock)
        {
            if (_travel != t)
            {
                return;
            }

            if (route == null || route.Hops.Count == 0)
            {
                _travel = null;
                dead = true;
            }
            else
            {
                dead = false;
                SetLeg(route.Hops[0].Exit);
            }
        }

        if (dead)
        {
            ClearDesiredGoal(ControlPriority.Travel);
            _logger.LogWarning($"TRAVEL: nothing left between {Zoning.Name(_pf)} and {Zoning.Name(t.TargetPf)} " +
                               "once the dead exits are out - travel abandoned.");
        }
    }

    // The travel line for 'status'.
    private string DescribeTravel()
    {
        lock (_travelLock)
        {
            if (_travel == null)
            {
                return "none";
            }

            var to = $"to {Zoning.Name(_travel.TargetPf)}";
            if (_travel.TargetPos.HasValue)
            {
                to += $" at ({_travel.TargetPos.Value.X:0.0} {_travel.TargetPos.Value.Z:0.0})";
            }

            return _travel.LegExit == null
                ? to
                : $"{to}, leg: {_travel.LegExit} (try {_travel.Tries}/{MaxLegTries})";
        }
    }

    // ── floor sampling (AOBuddy10 OverlandController's FloorY/StepFloor, verbatim rules) ──

    private float FloorY(float x, float y, float z)
    {
        var h = RawFloorY(x, y - _yBias, z);
        if (float.IsNaN(h))
        {
            return y;
        }

        // THE BIAS IS A LAND WAGE (Varmint Woods 600, 2026-10-03 17:49): it lifts our claims onto the
        // server's decks above the heightfield - and it must NEVER ride into the water. There the bed
        // comes back as bed + bias, which hovers just UNDER the liquid plane, so neither the floating
        // nor the swim branch of WalkStep fires and the wade claim sits below the server's surface:
        // every step into the river refused, the body pinned on the bank, wedged through four yanks.
        // Over water the raw bed and the absolute SwimY plane rule.
        var wet = _nav.Nav?.Ground != null ? _nav.Nav.Ground.SwimY(x, z, 0.05f) : double.NaN;
        if (double.IsNaN(wet))
        {
            h += _yBias;
        }

        return Math.Abs(h - y) > 4f ? y : h; // a jump of more than 4 m is a roof or a cave, not our floor
    }

    // Our data's floor under (x, z) nearest y; NaN when there is none.
    private float RawFloorY(float x, float y, float z)
    {
        var nav = _nav.Nav;
        if (nav == null)
        {
            return float.NaN;
        }

        double h = nav.FloorNear(x, y, z, out _);
        return double.IsNaN(h) ? float.NaN : (float)h;
    }

    // The HIGHEST surface at the next position that is at most a step above y (stairs, kerbs, sills) —
    // the surface we would walk ONTO. NaN when nothing qualifies.
    private float StepFloor(float x, float y, float z)
    {
        float best = float.NaN;
        void Consider(double h)
        {
            if (double.IsNaN(h) || h > y + 0.8f)
            {
                return;
            }

            if (float.IsNaN(best) || h > best)
            {
                best = (float)h;
            }
        }

        var nav = _nav.Nav;
        if (nav?.Ground != null)
        {
            Consider(nav.Ground.HeightAt(x, z));
        }

        if (nav?.Collision != null)
        {
            foreach (double h in nav.Collision.HeightsUnder(x, z))
            {
                Consider(h);
            }
        }

        return best;
    }

    // SetPos the packet thread accepted: applied here, on the thread that owns the body. Its height
    // against our floor data becomes the yBias carried forward, and its Y is wet truth for 3 s in water.
    private void ApplyPendingCorrection(LocalPlayer me)
    {
        Vector3 pos;
        bool yank;
        lock (_poslock)
        {
            if (_pendingCorrection == null)
            {
                return;
            }

            pos = _pendingCorrection.Value;
            yank = _pendingYank;
            _pendingCorrection = null;
            _pendingYank = false;
            _confirmedPosition = pos;
            _yBias = _pendingBias;
            _wetY = pos.Y;
            _wetYAt = _wetClock.Elapsed.TotalSeconds;
        }

        if (yank && _movement.Moving)
        {
            // A YANK while walking: the server overrode a big drift. Stop, and let the route
            // re-plan from where the server actually has us (_routePrio reset); the yank counts
            // against this goal (Tick gives it up after MaxYanks). Measured before the SetPose
            // below, this is the drift the server just erased. A short hold, too: walking on
            // immediately re-collects the same rejection.
            var yankDist = Movement.Flat(me.MovementComponent.Position, pos);
            _yanks++;
            _routePrio = -1;
            _stuck.Reset();
            // Mark what the server just refused: the segment we walked into, from where it pulled
            // us back to where it hauled us back from. This goal's replan avoids it via
            // _stuckCells - and _serverNo keeps it across goals, or a fresh goal (a manual 'goto'
            // after a give-up) walks straight into the same refusal again
            // (owner, 2026-10-03 Aegean: a descent the heightfield showed but the cliff hid -
            // three yanks, goal given up, and every retry yanked identically).
            var grid = _nav.Grid;
            if (grid != null)
            {
                var walked = me.MovementComponent.Position;
                grid.CellsAlong(pos, walked, 1f, _stuckCells);
                if (_serverNo.Count > 128)
                {
                    _serverNo.Clear();
                }

                grid.CellsAlong(pos, walked, 1f, _serverNo);
            }
            _holdWalkUntil = _wetClock.Elapsed.TotalSeconds + YankReholdSeconds;
            _movement.Hold(me, SendIntervalMs);
            _logger.LogInformation($"Movement: the server yanked the walk {yankDist:0.0} m - holding and re-planning (yank {_yanks}/{MaxYanks}).");
        }

        // A SetPos STORM: the server re-asserting (nearly) the same spot correction after
        // correction - the body is wedged in something the grid walked it into. The stuck watch
        // never sees this (each pull lands by a waypoint and reads as progress), so count the
        // pins here: eight landings within ~1.5 m and 5 s of each other are one wedge -
        // blacklist the spot, re-plan, and count it as a yank so the goal is given up honestly
        // (owner, 2026-10-03: pinned for minutes at (39.2,92.4) under 1-2.4 m corrections).
        if (_movement.Moving)
        {
            var now = _wetClock.Elapsed.TotalSeconds;
            var chained = _pins > 0 && Movement.Flat(pos, _lastPin) < 1.5f && now - _lastPinAt < 5;
            _lastPin = pos;
            _lastPinAt = now;
            if (chained)
            {
                _pins++;
                // THE DATA MISSED THIS WATER (owner, 2026-10-03, the Varmint Woods river, pinned
                // at (2541.9,2175.4) under corrections alone): run-mode steps refused with only
                // SetPos coming back, at a spot the liquids call dry. The gait is the fix, not
                // the blacklist - enter the swim and cross at the surface the server holds us
                // at, from the FOURTH chained pin (every extra second pinned is a second lost).
                // A real wedge (a fence, a crate) gains nothing from the swim, and its pins keep
                // chaining while Swimming, so the wedge blacklist at eight still gets its turn.
                if (_pins >= 4 && _nav.Nav?.Ground != null && !_movement.Swimming &&
                    double.IsNaN(_nav.Nav.Ground.SwimY(pos.X, pos.Z, WadeDepth)))
                {
                    _pins = 0;
                    // The surface estimate is the pin itself: the server refuses steps ON the
                    // depth-threshold contour (capture 20261003-152301: the body pinned at bed
                    // 3.38 with the walked surface at 4.39 = bed + 1.0), so one wade-depth above
                    // the bed we stand on IS the water level. Corrections re-anchor it from here.
                    _wetY = pos.Y + WadeDepth;
                    _wetYAt = now;
                    _movement.EnterSwim(me, SendIntervalMs);
                    _logger.LogWarning($"Movement: run steps refused at ({pos.X:0.0} {pos.Z:0.0}) where the liquids say " +
                                       $"dry - the data missed this water; swimming at the {_wetY:0.0} surface.");
                    _follow.BreakMirror("server correction");
                    Movement.SetPose(me, pos, me.MovementComponent.Heading);
                    _movement.ResetKeepGait();
                    return; // the correction is applied; the wedge blacklist is not the cure here
                }

                if (_pins >= 8)
                {
                    _pins = 0;
                    _yanks++;
                    _routePrio = -1;
                    _stuck.Reset();
                    _holdWalkUntil = now + YankReholdSeconds;
                    _movement.Hold(me, SendIntervalMs);
                    var grid = _nav.Grid;
                    if (grid != null)
                    {
                        var a = new Vector3(pos.X - 2.5f, 0, pos.Z - 2.5f);
                        var b = new Vector3(pos.X + 2.5f, 0, pos.Z + 2.5f);
                        grid.CellsAlong(a, b, 2f, _stuckCells);
                        if (_serverNo.Count > 128)
                        {
                            _serverNo.Clear();
                        }

                        grid.CellsAlong(a, b, 2f, _serverNo);
                    }

                    _logger.LogInformation(
                        $"Movement: the server keeps pinning me at ({pos.X:0.0} {pos.Z:0.0}) - wedged; blacklisted and re-planning (yank {_yanks}/{MaxYanks}).");

                    // The wedge is not a spot the route can dodge: the same spot cycles again and
                    // again. Three in a minute means the body is server-pinned - reconnect, and
                    // the login respawns it somewhere the server accepts steps from.
                    if (Movement.Flat(pos, _wedgePos) < 1.5f && now - _wedgeLastAt < 60)
                    {
                        _wedgeCycles++;
                    }
                    else
                    {
                        _wedgeCycles = 1;
                        _wedgePos = pos;
                    }

                    _wedgeLastAt = now;
                    if (_wedgeCycles >= 3 && !_reconnectSent)
                    {
                        _reconnectSent = true;
                        _logger.LogWarning(
                            $"Movement: the body is server-pinned at ({pos.X:0.0} {pos.Z:0.0}) - every step refused " +
                            $"through {_wedgeCycles} wedge cycles. Reconnecting the gameserver: the login respawns me " +
                            "at a valid position.");
                        Client.ReconnectSession();
                    }
                }
            }
            else
            {
                _pins = 1;
            }
        }

        // A correction moved us off his stream: the mirror cannot copy what the server overrode
        // (AOBuddy10 OnServerCorrectedMe broke the mirror here too); the catch-up re-locks.
        _follow.BreakMirror("server correction");

        Movement.SetPose(me, pos, me.MovementComponent.Heading);
        _movement.ResetKeepGait(); // a correction is not a gait change (Movement.cs, Newland 21:40)
    }

    // Velocity = 4.82 + 0.003615 x Run Speed (stat 156), capped at 15.5 (review.md, wire-proven).
    // A snare drives the stat negative (-289, 2026-09-23): a real reading, floored at 1.5 so the
    // walker slows instead of snapping every 3 s. Only -1 means unreadable: base 4.82 then.
    private float RunVelocity(Snapshot snap)
    {
        if (snap.RunSpeed != -1)
        {
            _lastRunSpeed = snap.RunSpeed;
            _runRead = true;
        }

        if (!_runRead)
        {
            return 4.82f;
        }

        return Math.Max(1.5f, Math.Min(15.5f, 4.82f + _lastRunSpeed * 0.003615f));
    }

    // The highest-priority goal for the playfield, with the ONLY one allowed to read as reached
    // (owner, 2026-10-01): while a higher goal is set - walking or already reached - a lower one is
    // not being serviced, even if the body happens to stand inside its radius, and one that was
    // reached before a higher goal took the body over re-arms here. Selection and disarming sit
    // under one lock so a goal set mid-tick cannot slip between the two.
    private KeyValuePair<int, GoalLocation>? SelectActiveGoal(int playfield)
    {
        lock (_goallock)
        {
            KeyValuePair<int, GoalLocation>? best = null;
            foreach (var g in goals)
            {
                if (g.Value.PlayfieldId != playfield)
                {
                    continue; // a goal for another playfield is not walkable from here
                }

                if (best == null || g.Key > best.Value.Key)
                {
                    best = g;
                }
            }

            foreach (var g in goals.Values)
            {
                if (best == null || !ReferenceEquals(g, best.Value.Value))
                {
                    g.Reached = false;
                }
            }

            return best;
        }
    }

    // ── packet handlers (update thread) ───────────────────────────────────────────────────

    // Our own CharDCMove echo is the server's confirmation of where it has us: that is
    // CurrentPosition. The OWNER's moves are follow's raw material: stamped as "he is moving" and,
    // while the mirror is locked, queued for the walk thread to replay as ours (AOBuddy10 Main.cs:
    // "his move is our move"). Everyone's packets return false - the SDK's DynelManager keeps the
    // transforms fresh, and we are observers here.
    private bool DCMoveHandler(AOMessage arg)
    {
        if (arg.Body is CharDCMoveMessage m)
        {
            var me = DynelManager.LocalPlayer;
            if (me != null && m.Identity == me.Identity)
            {
                lock (_poslock)
                {
                    _confirmedPosition = m.Position;
                    _confirmedHeading = m.Heading;
                }
            }
            else if (_ownerInstance != 0 && m.Identity.Instance == _ownerInstance)
            {
                Interlocked.Exchange(ref _ownerLastMoveAt, Environment.TickCount64);
                if (_follow.MirrorLocked && Movement.IsMirrorable(m.MoveType))
                {
                    _follow.MirrorQueue.Enqueue(m);
                }
            }
        }

        return false;
    }

    // SetPos self-corrections. WALKING ON NAV DATA, every correction is taken and its height against our
    // floor data becomes the yBias carried forward (AOBuddy10 OverlandController: Rome Park walked y 16
    // over data ground 13.6, and with corrections ignored the bot sank back each step and was pulled
    // every 3 s). WITHOUT nav data the settled rule stands (review.md): apply 10 m or more, ignore
    // smaller ones while moving (the retail client ignores corrections under 10 m 86% of the time and
    // takes those of 10 m or more 64%); standing, everything applies. Application itself always happens
    // on the walk thread (the body's only writer), queued here.
    private bool SetPosHandler(AOMessage arg)
    {
        if (arg.Body is not SetPosMessage m)
        {
            return false;
        }

        var me = DynelManager.LocalPlayer;
        if (me == null || m.Identity != me.Identity)
        {
            return false;
        }

        // The correction's height against our floor data: how far the server's floor sits above ours here.
        // Over water the floor says nothing about the bias (AOBuddy10), and neither does a wild jump.
        var raw = RawFloorY(m.Position.X, m.Position.Y, m.Position.Z);
        var bias = float.IsNaN(raw) ? 0f : m.Position.Y - raw;
        if (Math.Abs(bias) > 4f || _movement.Swimming)
        {
            bias = 0f;
        }

        var onNav = _nav.LoadedPf == _pf;
        var off = Movement.Flat(me.MovementComponent.Position, m.Position);

        // WHAT the correction landed on decides what its height is worth (Varmint Woods 600, capture
        // 2026-10-03 16:42). ON A WATER PLANE it is the swim surface holding a body, not the land floor -
        // the far-bank SetPos pinned him at the river surface 4.406 over bed 2.87, and the +1.53 "bias"
        // floated him a metre and a half above the terrain for the rest of the walk (the owner watched him
        // fall that ~2 m over and over; 189 of 228 float samples had no collision floor under them). The
        // Swimming flag misses this: riding the surface via _wetY needs no swim gait, so the flag was off
        // when the bank correction landed. And a YANK's Y is our own claim echoed back from wherever it
        // pulled us to, so it re-learns any float we already carry - the seq-1858 yank re-armed the same
        // +1.53. Only a dry, in-place correction vouches for a floor offset.
        if (bias != 0f)
        {
            var plane = onNav && _nav.Nav?.Ground != null
                ? _nav.Nav.Ground.SwimY(m.Position.X, m.Position.Z, 0.05f)
                : double.NaN;
            if ((!double.IsNaN(plane) && Math.Abs(m.Position.Y - plane) <= 0.6f) || off >= YankMetres)
            {
                bias = 0f;
            }
        }
        if (onNav || off >= 10f || !_movement.Moving)
        {
            lock (_poslock)
            {
                _pendingCorrection = m.Position;
                _pendingBias = bias;
                _pendingYank = off >= YankMetres && _movement.Moving; // a yank: hold and re-plan after applying
            }

            _logger.LogInformation($"Movement: SetPos APPLIED ({(onNav ? "nav walk" : off >= 10f ? "resync" : "standing")}): " +
                                   $"server ({m.Position.X:0.0} {m.Position.Y:0.0} {m.Position.Z:0.0}), {off:0.0} m off, " +
                                   $"floor {bias:+0.0;-0.0} m vs data.");
        }
        else
        {
            _logger.LogDebug($"Movement: SetPos of {off:0.0} m ignored while moving (no nav data).");
        }

        return false;
    }

    /// <summary>What the update thread publishes for the walk thread each tick.</summary>
    private sealed class Snapshot
    {
        public LocalPlayer? Me;
        public int Playfield;
        public int RunSpeed; // -1 = unreadable this tick
        public int MovementMode; // the login CurrentMovementMode (stat 173); -1 = not sent yet
        public bool OwnerVisible;
        public Vector3 OwnerPos;
        public Quaternion OwnerHeading;
        public bool OwnerMoveFresh; // his last movement packet is under 600 ms old
    }

    // A travel order's state (guarded by _travelLock): where the trip ends and the leg currently
    // walked - its goal, the settle/use/await beat, and the try budget. The route itself is re-derived
    // from the Zoning graph after every zone, not stored.
    private sealed class TravelPlan
    {
        public int TargetPf;
        public Vector3? TargetPos; // null = just get to the playfield
        public ZoneExit LegExit; // the exit this leg takes; null between legs
        public Vector3 LegGoal; // where the leg walks: past the line, on the pad, or at the terminal
        public int LegPf; // the playfield the leg walks in
        public double LegReachedAt = -1; // standing on the leg goal since (settle beat starts here)
        public double AwaitAt = -1; // the post-settle window: use sent (terminals) or standing (pads)
        public int Tries; // visits to this leg's goal: uses, stand-ons, walk-throughs
    }

    internal sealed class GoalLocation
    {
        internal Vector3 Position { get; set; }
        internal int PlayfieldId { get; set; }
        internal float ArriveRadius { get; set; }

        // Written by the walk thread (arrival sets it, preemption and walking off re-arm it), read
        // from any thread by the goal's owner through IsGoalReached.
        internal volatile bool Reached;
    }
}