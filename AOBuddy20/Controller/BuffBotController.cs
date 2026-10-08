// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BuffBotController.cs
//
// Last modified: 2026-10-07
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Interfaces;
using AOBuddy20.Network;
using AOBuddy20.Nav;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     BUFF BOTS (step 4a.1 - the handshake PLUMBING; the buff SELECTION is 4a.2). Getting buffs from
///     a public buff bot (Chewy on RubiKa, Codedoc on RubiKa2019). The contract, owner 2026-10-05:
///     you must NOT be in a team; the bot invites you; you accept; it tells "preparing to buff you";
///     you request the buff your level allows; it buffs and AUTO-KICKS you from the team.
///     This runs the handshake generically off config (BuffBotName + the request tells + the window),
///     mirroring the Scotty invite pattern (MovementController.OnTeamRequest): during an active session
///     only the configured buff bot's team invite is accepted; everything else is left alone (the
///     owner's own invites stay manual). It sends the request tells by NAME through the chat client
///     (Client.Chat.SendPrivateMessage, logMessage false - off the console) and ends when the bot
///     kicks us or the window runs out.
///     Travel to the buff spot (4a.1b) is the BeginTravelToSpot/AtSpot API: the standing spot comes from
///     the one Buffs system (BuffCatalog.SpotPf/SpotPos, resolved per server), driven on the
///     MovementController's travel machinery; a caller walks there first, then runs the handshake.
///     NOT yet: computing WHICH buffs to ask for from the bot's catalogue, NCU- and receiver-aware
///     (4a.2). Runs on the update thread (BotLoop ticks it, and the packet pump raises Team.TeamRequest
///     on the same thread).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BuffBotController
{
    // Per-step waits (AOBuddy10 CodedocBuffs SelfTimeout/TeamTimeout/SettleSec): a self cast lands fast; a
    // team buff waits out the invite + cast; stats settle a beat before the next step is evaluated.
    private const double SelfTimeoutSec = 30, TeamTimeoutSec = 60, SettleSec = 2, SessionCapSec = 360;

    private enum Stage
    {
        Idle,
        Walking,  // traveling to the dimension's buff spot before we ask
        Working,  // at the spot: stepping through the acquisition queue
    }

    private readonly AccountInfo _config;
    private readonly BuffCatalog _catalog;
    private readonly MovementController _movement;
    private readonly ILogger<BuffBotController> _logger;

    private Stage _stage = Stage.Idle;
    private double _clock;
    private double _startedAt;
    private double _settleUntil;
    private double _stepSentAt;

    // The acquisition queue for THIS session: ordered BuffActions (self-cast + bot tells). The NCU buff is
    // first and, being team-cast, gates the rest - because each step WAITS for its nano to land before the
    // next is sent, the expanded Max NCU is already up when the following buffs go out.
    private List<BuffCatalog.BuffAction> _steps = new();
    private int _stepIdx;
    private BuffCatalog.BuffAction? _current;
    private bool _walkFirst;
    private bool _teamWindow; // a team step is out: accept the buffer toons' invites (they are not the owner)
    private int _landed, _failed, _skipped;
    private string _what = "";
    private Action<string>? _reply; // the owner tell to answer as the session progresses

    // GUIDED MODE (the pet-first buff cycle, owner 2026-10-07): instead of a fixed queue, the
    // caller supplies a turn source consulted whenever the session is ready to send the next
    // step. The source embodies the whole dance (strip, NCU, per-line buff+cast) and says
    // "wait" (Action null) between its own phases. Only the pet-cycle brains use this - the
    // plain RequestBuffs overloads and every other caller are untouched.
    private Func<LocalPlayer, GuidedTurn>? _guided;
    private double _sessionCap = SessionCapSec;

    /// <summary>One consult of a guided session's turn source: the step to send now, or "keep
    /// waiting"; <see cref="Done" /> closes the session.</summary>
    public sealed class GuidedTurn
    {
        public BuffCatalog.BuffAction? Action; // null: nothing to send this consult (keep waiting)
        public bool Done;                      // true: the dance is over - close the session
        public string? DoneWhy;                // the closing reason when Done
    }

    public bool Active => _stage != Stage.Idle;

    public BuffBotController(AccountInfo config, BuffCatalog catalog, MovementController movement, ILogger<BuffBotController> logger)
    {
        _config = config;
        _catalog = catalog;
        _movement = movement;
        _logger = logger;
        Team.TeamRequest += OnTeamRequest; // the CharacterAction form; the N3 form comes routed (below)
    }

    // THE INVITE, ROUTED (owner, 2026-10-08): the N3 TeamInvite - the form that carries the inviter's
    // name - is delivered by the PacketRouter to every recipient separately guarded (owner auto-accept,
    // the Scotty warp, us). The old single Team.TeamRequest chain died whole when any subscriber threw,
    // and ours was sometimes never reached. False = the next recipient sees the invite too.
    public void RegisterPackets(PacketRouter router)
    {
        router.Register(TeamInviteHandler, N3MessageType.TeamInvite, 0);
    }

    private bool TeamInviteHandler(AOMessage e)
    {
        var invite = (TeamInviteMessage)e.Body;
        OnTeamRequest(this, new TeamRequestEventArgs(invite.Requestor, invite.Name));
        return false;
    }

    // ---- The buff spot (4a.1b): walk to the bot before asking --------------------------------

    // The buff spot and the bot name come from the ONE Buffs system (BuffCatalog), resolved by server;
    // nothing here forks on dimension. Travel-to-spot can still be turned off (ask from where we stand).

    /// <summary>Standing at the buff bot? True when travel-to-spot is off (nothing to walk to) OR we are
    /// in the system's spot playfield and the travel goal is reached / within the arrive radius.</summary>
    public bool AtSpot()
    {
        if (!_config.BuffTravelToSpot || _catalog.SpotPf <= 0)
        {
            return true;
        }

        if ((int)Playfield.ModelId != _catalog.SpotPf)
        {
            return false;
        }

        return _movement.IsGoalReached(ControlPriority.Travel)
               || Vector3.Distance(_movement.CurrentPosition, _catalog.SpotPos) <= _config.BuffSpotArriveMeters;
    }

    /// <summary>Set the travel goal for the buff spot on the MovementController's travel machinery (the
    /// body walks there on its own, in-zone or cross-zone). Returns a human-readable line, or null when
    /// there is no spot to walk to.</summary>
    public string? BeginTravelToSpot()
    {
        if (!_config.BuffTravelToSpot || _catalog.SpotPf <= 0)
        {
            return null;
        }

        return _movement.PlanTravel(_catalog.SpotPf, _catalog.SpotPos);
    }

    /// <summary>Is a buff-spot walk currently in flight? True when the MovementController holds a Travel
    /// goal (the in-zone case) or a cross-zone travel plan is running. Lets a caller tell "already
    /// walking" from "need to (re)issue" - the login race sets the goal only once we are on the ground.</summary>
    public bool TravelGoalActive()
    {
        return _movement.HasGoal(ControlPriority.Travel) || _movement.TravelTargetPf != 0;
    }

    /// <summary>Drop the buff-spot travel goal once we have arrived (covers both the cross-zone plan and
    /// the in-zone Travel goal PlanTravel leaves when already in the spot's playfield).</summary>
    public void ClearTravelToSpot()
    {
        _movement.CancelTravel();
        _movement.ClearDesiredGoal(ControlPriority.Travel);
    }

    /// <summary>A readable one-liner of where we are relative to the spot, for narration/logs.</summary>
    public string SpotStatus()
    {
        if (!_config.BuffTravelToSpot || _catalog.SpotPf <= 0)
        {
            return "no buff spot (travel-to-spot off)";
        }

        if ((int)Playfield.ModelId == _catalog.SpotPf)
        {
            return $"{Vector3.Distance(_movement.CurrentPosition, _catalog.SpotPos):0}m from {_catalog.BotName}'s spot in {Zoning.Name(_catalog.SpotPf)}";
        }

        return $"traveling to {Zoning.Name(_catalog.SpotPf)} for {_catalog.BotName}";
    }

    // ---- The 'buffs' owner command ---------------------------------------------------------

    public void Command(string[] parts, Action<string> reply)
    {
        var arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "start";
        switch (arg)
        {
            case "stop":
                End("stopped by the owner");
                reply("Buff session stopped.");
                break;
            case "status":
                reply($"Buffs: {(Active ? $"{_stage} ({_landed} landed, {_skipped} already up, {_failed} failed)" : "idle")}. " +
                      $"Bot '{_catalog.BotName}', catalog {(_catalog.Loaded ? $"{_catalog.Buffs.Count} entries" : "not loaded")}.");
                break;
            case "pet":
                // Convenience manual trigger: the simple pet-summon tells (NCU + the biggest safe nano-skill
                // buff). The ACCURATE, land-gated, self-cast-aware plan is the pet brain's auto path
                // (PetAutoBuff); this command is a quick way to drive the bot by hand.
                RequestBuffs(_catalog.PlanForPetSummon(DynelManager.LocalPlayer, int.MaxValue, int.MaxValue), "pet-summon tells");
                reply("Pet-summon buff acquire requested.");
                break;
            default: // start: the tells configured in the conf
                RequestBuffs(_config.BuffRequestTells ?? new List<string>(), "configured tells");
                reply("Configured-tell buff acquire requested.");
                break;
        }
    }

    /// <summary>
    ///     String-tell start (owner 'buffs' command, the MP external-buffing brain, a config list): each
    ///     tell is a single-target bot request with no landed-id to watch, so it is bounded by the per-step
    ///     timeout rather than land detection. The structured overload below is preferred where a routed
    ///     plan is available (it land-gates and self-casts).
    /// </summary>
    public bool RequestBuffs(List<string> tells, string why)
    {
        if (tells == null || tells.Count == 0)
        {
            return false;
        }

        var steps = tells
            .Select(t => new BuffCatalog.BuffAction { Source = BuffCatalog.BuffSource.BuffBot, Name = t, Tell = t, BotName = _catalog.BotName })
            .ToList();
        return RequestBuffs(steps, why);
    }

    /// <summary>
    ///     Programmatic start (the pet brain's buff-first, 4b): open a session with computed steps. Returns
    ///     false when it cannot start (already active, in a team, or an empty list) so the caller can fall
    ///     back to summoning what it can now.
    /// </summary>
    public bool RequestBuffs(List<BuffCatalog.BuffAction> steps, string why)
    {
        if (Active || Team.IsInTeam || steps == null || steps.Count == 0)
        {
            return false;
        }

        return StartAcquire(steps, why, true, s => _logger.LogInformation($"BUFFS: {s}"));
    }

    /// <summary>
    ///     GUIDED start (the pet-first buff cycle): the caller drives WHAT happens next via
    ///     <paramref name="nextTurn" />, consulted every time the session is ready to send a step.
    ///     A turn's Action is sent like any queue step (tell or self-cast, landing watched, per-step
    ///     timeout); Action null keeps the session open while the source works through its own
    ///     phases (stripping, waiting for a pet to appear); Done closes it. Walks to the buff spot
    ///     first like every session. Returns false when it cannot start (already active, in a team).
    ///     <paramref name="capSeconds" /> lifts the session cap - the full cycle (strip + NCU + a
    ///     buff-then-summon pass per pet line) outlives the plain tell runs' 6 minutes.
    /// </summary>
    public bool RequestGuidedBuffs(string why, Func<LocalPlayer, GuidedTurn> nextTurn, double capSeconds = SessionCapSec)
    {
        if (Active || Team.IsInTeam || nextTurn == null)
        {
            return false;
        }

        _steps = new List<BuffCatalog.BuffAction>();
        _stepIdx = 0;
        _guided = nextTurn;
        _sessionCap = Math.Max(SessionCapSec, capSeconds);
        _current = null;
        _teamWindow = false;
        _landed = _failed = _skipped = 0;
        _settleUntil = 0;
        _startedAt = _clock;
        _what = why;
        _reply = s => _logger.LogInformation($"BUFFS: {s}");
        _walkFirst = !AtSpot();
        Enter(_walkFirst ? Stage.Walking : Stage.Working);

        _logger.LogInformation($"BUFFS: guided session open ({why}), cap {_sessionCap:0}s{(_walkFirst ? ", walking to the spot first" : "")}.");
        _reply($"Guided buff session open ({why}).");
        return true;
    }

    private bool StartAcquire(List<BuffCatalog.BuffAction> steps, string what, bool walkFirst, Action<string> reply)
    {
        if (steps == null || steps.Count == 0)
        {
            reply($"No buffs to acquire ({what} is empty).");
            return false;
        }

        // Any bot tell needs a toon name to /tell. The Buffs system resolves it per server (Chewysfix /
        // Codedoc by default, config.BuffBotName overrides), so this only fails if that came back empty.
        if (steps.Any(s => s.Source == BuffCatalog.BuffSource.BuffBot) && string.IsNullOrWhiteSpace(_catalog.BotName))
        {
            reply("Bot buffs are in the plan but no buff bot resolved for this server.");
            return false;
        }

        if (Team.IsInTeam)
        {
            reply("Already in a team - leave it first; the buff bot teams us itself.");
            return false;
        }

        _steps = steps;
        _stepIdx = 0;
        _current = null;
        _teamWindow = false;
        _landed = _failed = _skipped = 0;
        _settleUntil = 0;
        _startedAt = _clock;
        _what = what;
        _reply = reply;
        _walkFirst = walkFirst && !AtSpot();
        Enter(_walkFirst ? Stage.Walking : Stage.Working);

        var botCount = steps.Count(s => s.Source == BuffCatalog.BuffSource.BuffBot);
        _logger.LogInformation($"BUFFS: acquire open ({what}): {steps.Count} steps ({botCount} from '{_catalog.BotName}'), in order " +
            $"[{string.Join(", ", steps.Select(DescribeStep))}].");
        reply($"Buff acquire open ({what}): {steps.Count} steps{(_walkFirst ? ", walking to the spot first" : "")}.");
        return true;
    }

    private static string DescribeStep(BuffCatalog.BuffAction a) => a.Source switch
    {
        BuffCatalog.BuffSource.SelfCast => $"self {a.Name}",
        BuffCatalog.BuffSource.BuffBot => $"{a.Tell}{(a.NeedsTeam ? "[team]" : "")}",
        _ => $"{a.Name}?",
    };

    // ---- The acquisition loop --------------------------------------------------------------

    public void Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (_stage == Stage.Idle)
        {
            return;
        }

        if (me == null)
        {
            End("lost the character");
            return;
        }

        if (_clock - _startedAt > _sessionCap)
        {
            End($"session cap {_sessionCap:0}s reached");
            return;
        }

        if (_stage == Stage.Walking)
        {
            if (AtSpot())
            {
                ClearTravelToSpot();
                Enter(Stage.Working);
                _logger.LogInformation($"BUFFS: at the buff spot ({SpotStatus()}) - starting the asks.");
            }
            else if (!TravelGoalActive())
            {
                BeginTravelToSpot();
            }

            return;
        }

        // Working: accept the buffer toons' invites while a team step is out, then step the queue.
        if (_clock < _settleUntil)
        {
            return;
        }

        if (_current == null)
        {
            StartNextStep(me);
            return;
        }

        WaitOnCurrent(me);
    }

    // Advance to the next step not already satisfied, and send/cast it.
    private void StartNextStep(LocalPlayer me)
    {
        // GUIDED: the turn source decides what goes out - a step, "keep waiting" (it is between its
        // own phases: stripping, a pet appearing), or done. No skip-if-have here: the source owns
        // what still needs asking for.
        if (_guided != null)
        {
            var turn = _guided(me);
            if (turn.Done)
            {
                _guided = null;
                if (!string.IsNullOrEmpty(turn.DoneWhy))
                {
                    _logger.LogInformation($"BUFFS: {turn.DoneWhy}.");
                }

                Finish(me);
                return;
            }

            if (turn.Action != null)
            {
                _current = turn.Action;
                SendStep(me, _current);
            }

            return;
        }

        while (_stepIdx < _steps.Count)
        {
            var step = _steps[_stepIdx];
            _stepIdx++;

            if (step.Source == BuffCatalog.BuffSource.Unavailable)
            {
                _logger.LogInformation($"BUFFS: can't get '{step.Name}' (not learned / not in the bot's menu) - skipping.");
                continue;
            }

            if (Have(me, step))
            {
                _skipped++;
                _logger.LogInformation($"BUFFS: already have '{step.Name}' - not re-requesting.");
                continue;
            }

            _current = step;
            SendStep(me, step);
            return;
        }

        Finish(me);
    }

    // Send one step out (self-cast or bot tell) - shared by the static queue and guided mode. A
    // team buff opens the invite window; a single-target buff needs no team.
    private void SendStep(LocalPlayer me, BuffCatalog.BuffAction step)
    {
        _stepSentAt = _clock;

        if (step.Source == BuffCatalog.BuffSource.SelfCast)
        {
            try
            {
                me.Cast(step.SelfCastNanoId);
                _logger.LogInformation($"BUFFS: self-casting '{step.Name}' ({step.SelfCastNanoId}).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"BUFFS: self-cast of '{step.Name}' failed: {ex.Message}");
                _current = null;
                _failed++;
            }

            return;
        }

        _teamWindow = step.NeedsTeam;
        _logger.LogInformation($"BUFFS: asking for '{step.Name}'" +
            $"{(step.NeedsTeam ? " (team - waiting for the invite)" : " (self cast)")}.");
        SendTell(step.Tell);
    }

    // Wait for the current step's nano to land, or time out and move on.
    private void WaitOnCurrent(LocalPlayer me)
    {
        var step = _current!;
        if (Have(me, step))
        {
            _landed++;
            _logger.LogInformation($"BUFFS: '{step.Name}' landed after {_clock - _stepSentAt:0.0}s.");
            CloseStep();
            _settleUntil = _clock + SettleSec; // let Max NCU / skills settle before the next step
            return;
        }

        var limit = step.Source == BuffCatalog.BuffSource.SelfCast ? SelfTimeoutSec
            : step.NeedsTeam ? TeamTimeoutSec : SelfTimeoutSec;
        if (_clock - _stepSentAt > limit)
        {
            _failed++;
            _logger.LogInformation($"BUFFS: '{step.Name}' did not land in {limit:0}s{(step.NeedsTeam ? (Team.IsInTeam ? " (teamed)" : " (no invite)") : "")} - moving on.");
            CloseStep();
        }
    }

    private void CloseStep()
    {
        if (_current?.NeedsTeam == true)
        {
            _teamWindow = false;
        }

        _current = null;
    }

    // "We have it" = any of the step's landed nano ids is running on us - or, for a RequireAll
    // step (a multi-code tell), ALL of them: the stack must be complete before the next phase.
    private static bool Have(LocalPlayer me, BuffCatalog.BuffAction step)
    {
        if (step.LandIds.Length == 0)
        {
            return false;
        }

        var buffs = me.Buffs;
        if (buffs == null)
        {
            return false;
        }

        return step.RequireAll
            ? step.LandIds.All(id => buffs.Any(b => b.Id == id))
            : buffs.Any(b => step.LandIds.Contains(b.Id));
    }

    private void SendTell(string code)
    {
        var bot = _catalog.BotName;
        var text = BuffCatalog.WireTell(code); // "cast <code>" (the Buffs system owns the wire format)
        if (Client.Chat == null)
        {
            _logger.LogInformation($"BUFFS: no chat client - tell to '{bot}' not sent: '{text}'.");
            return;
        }

        try
        {
            Client.Chat.SendPrivateMessage(bot, text, false);
            _logger.LogInformation($"BUFFS: /tell {bot} {text}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"BUFFS: the tell to '{bot}' failed: {ex.Message}");
        }
    }

    private void Finish(LocalPlayer me)
    {
        var mc = me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0;
        var ts = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
        var ncu = me.TryGetStat(Stat.MaxNCU, out var n) ? n : 0;
        _logger.LogInformation($"BUFFS: done - {_landed} landed, {_skipped} already up, {_failed} failed. " +
            $"Skills now MC {mc}, TS {ts}, MaxNCU {ncu}.");
        End($"{_landed} landed, {_skipped} already up, {_failed} failed; MC {mc}/TS {ts}");
    }

    // THE BUFFER TOONS' INVITE. Codedoc casts team buffs from several toons (Enfocode, Fixyourcode, ...),
    // not from the one BuffBotName; Chewy likewise. So while a TEAM step is out, accept the first invite
    // from anyone who is NOT the owner; otherwise leave every invite alone (the owner's own stay manual) -
    // EXCEPT the Chewys rule below, which does not wait for a team step.
    private void OnTeamRequest(object? sender, TeamRequestEventArgs e)
    {
        if (_stage != Stage.Working || Team.IsInTeam)
        {
            return;
        }

        var name = e.RequesterName;
        if (string.IsNullOrEmpty(name) && DynelManager.Find(e.Requester, out SimpleChar requester))
        {
            name = requester.Name;
        }

        if (!string.IsNullOrEmpty(_config.Owner) && string.Equals(name, _config.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return; // the owner's invite stays manual
        }

        // THE CHEWYS INVITE (RubiKa only - RK2019's Codedoc invites only on a team step, gated by the
        // _teamWindow check below): the Chewys toons (name prefix "Chewys") team you the moment they
        // process a tell - often before any team-cast step is out, so the window gate never opens and
        // the invite was being left unanswered (owner, 2026-10-07). While the session is open, their
        // invite IS the bot's: accept it.
        if (Client.Dimension == AOSharp.Clientless.Common.Dimension.RubiKa
            && !string.IsNullOrEmpty(name) && name.StartsWith("Chewys", StringComparison.OrdinalIgnoreCase))
        {
            Team.Accept(e.Requester);
            _logger.LogInformation($"BUFFS: accepted the Chewys invite from '{name}' (they team on the first tell).");
            return;
        }

        var rk19list = new string[] { "Codedoc", "Enfocode", "Pocketsize", "Codesolja", "Trandethecode", "Codesmp", "Gonnablastya" };

        if (Client.Dimension == AOSharp.Clientless.Common.Dimension.RubiKa2019 && !string.IsNullOrEmpty(name) && rk19list.Contains(name))
        {
            Team.Accept(e.Requester);
            _logger.LogInformation($"BUFFS: accepted the Codedocs invite from '{name}' (they team on the first tell).");
            return;
        }
        
        if (!_teamWindow)
        {
            return; // no team step is out - not ours to answer
        }

        Team.Accept(e.Requester);
        _logger.LogInformation($"BUFFS: accepted the buff team invite from '{name ?? "?"}' (team buff incoming).");
    }

    // ---- Stage helpers ---------------------------------------------------------------------

    private void Enter(Stage next)
    {
        _stage = next;
    }

    private void End(string why)
    {
        if (_stage != Stage.Idle)
        {
            _logger.LogInformation($"BUFFS: session closed ({why}).");
            _reply?.Invoke($"Buff session done ({why}).");
        }

        // Recovery: a team buff normally auto-disbands, but a crashed/unresponsive buffer could leave us
        // teamed - which would SILENTLY block every future acquire (the Team.IsInTeam guard in StartAcquire).
        // If we are still teamed at session end, leave so the next run can start clean.
        if (Team.IsInTeam)
        {
            try
            {
                Team.LeaveTeam();
                _logger.LogWarning("BUFFS: still teamed at session end - leaving the team to recover.");
            }
            catch
            {
                // best-effort; even if the leave send fails, we reset to Idle below and retry later
            }
        }

        _stage = Stage.Idle;
        _current = null;
        _teamWindow = false;
        _reply = null;
        _guided = null;
        _sessionCap = SessionCapSec;
    }
}
