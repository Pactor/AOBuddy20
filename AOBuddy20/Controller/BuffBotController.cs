// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BuffBotController.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Nav;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
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
///     Travel to the buff spot (4a.1b) is the SpotForDimension/BeginTravelToSpot/AtSpot API: the
///     dimension's standing spot (Chewy at ICC, Codedoc at Borealis - AOBuddy10's coords) is driven on
///     the MovementController's travel machinery; a caller walks there first, then runs the handshake.
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

    public bool Active => _stage != Stage.Idle;

    public BuffBotController(AccountInfo config, BuffCatalog catalog, MovementController movement, ILogger<BuffBotController> logger)
    {
        _config = config;
        _catalog = catalog;
        _movement = movement;
        _logger = logger;
        Team.TeamRequest += OnTeamRequest; // the buff bot answers by inviting us
    }

    // ---- The buff spot (4a.1b): walk to the bot before asking --------------------------------

    /// <summary>The dimension's public buff spot (Chewy at ICC on RubiKa, Codedoc at Borealis on
    /// RubiKa2019 - AOBuddy10's coords), or null when travel-to-spot is off or no spot is configured.
    /// Resolved off Client.Dimension, the same gate CodedocBuffs/Zoning use.</summary>
    public (int Pf, Vector3 Pos, string Label)? SpotForDimension()
    {
        if (!_config.BuffTravelToSpot)
        {
            return null;
        }

        var is2019 = Client.Dimension == AOSharp.Clientless.Common.Dimension.RubiKa2019;
        var pf = is2019 ? _config.CodedocBuffPf : _config.ChewyBuffPf;
        if (pf <= 0)
        {
            return null;
        }

        var pos = is2019
            ? new Vector3(_config.CodedocBuffX, _config.CodedocBuffY, _config.CodedocBuffZ)
            : new Vector3(_config.ChewyBuffX, _config.ChewyBuffY, _config.ChewyBuffZ);
        return (pf, pos, is2019 ? "Codedoc" : "Chewy");
    }

    /// <summary>Standing at the buff bot? True when travel-to-spot is off (nothing to walk to) OR we are
    /// in the spot's playfield and the travel goal is reached / within the arrive radius.</summary>
    public bool AtSpot()
    {
        var s = SpotForDimension();
        if (s == null)
        {
            return true;
        }

        if ((int)Playfield.ModelId != s.Value.Pf)
        {
            return false;
        }

        return _movement.IsGoalReached(ControlPriority.Travel)
               || Vector3.Distance(_movement.CurrentPosition, s.Value.Pos) <= _config.BuffSpotArriveMeters;
    }

    /// <summary>Set the travel goal for the buff spot on the MovementController's travel machinery (the
    /// body walks there on its own, in-zone or cross-zone). Returns a human-readable line, or null when
    /// there is no spot to walk to.</summary>
    public string? BeginTravelToSpot()
    {
        var s = SpotForDimension();
        return s == null ? null : _movement.PlanTravel(s.Value.Pf, s.Value.Pos);
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
        var s = SpotForDimension();
        if (s == null)
        {
            return "no buff spot (travel-to-spot off)";
        }

        if ((int)Playfield.ModelId == s.Value.Pf)
        {
            return $"{Vector3.Distance(_movement.CurrentPosition, s.Value.Pos):0}m from the {s.Value.Label} spot in {Zoning.Name(s.Value.Pf)}";
        }

        return $"traveling to {Zoning.Name(s.Value.Pf)} for {s.Value.Label}";
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
                      $"Bot '{_config.BuffBotName}', catalog {(_catalog.Loaded ? $"{_catalog.Buffs.Count} entries" : "not loaded")}.");
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
            .Select(t => new BuffCatalog.BuffAction { Source = BuffCatalog.BuffSource.BuffBot, Name = t, Tell = t, BotName = _config.BuffBotName })
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

    private bool StartAcquire(List<BuffCatalog.BuffAction> steps, string what, bool walkFirst, Action<string> reply)
    {
        if (steps == null || steps.Count == 0)
        {
            reply($"No buffs to acquire ({what} is empty).");
            return false;
        }

        // Any bot tell needs a real toon name to /tell. The dimension label ("Codedoc"/"Chewy") is only a
        // placeholder for the dry-run trace - a live ask needs BuffBotName set.
        if (steps.Any(s => s.Source == BuffCatalog.BuffSource.BuffBot) && string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            reply("Bot buffs are in the plan but BuffBotName is not set - set it (e.g. 'Codedoc') to ask.");
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
        _logger.LogInformation($"BUFFS: acquire open ({what}): {steps.Count} steps ({botCount} from '{_config.BuffBotName}'), in order " +
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

        if (_clock - _startedAt > SessionCapSec)
        {
            End($"session cap {SessionCapSec:0}s reached");
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

            // Bot tell. A team buff: open the window so the buffer toons' invites are accepted; a self
            // (single-target) buff needs no team - they cast straight on us.
            _teamWindow = step.NeedsTeam;
            _logger.LogInformation($"BUFFS: asking for '{step.Name}'" +
                $"{(step.NeedsTeam ? " (team - waiting for the invite)" : " (self cast)")}.");
            SendTell(step.Tell);
            return;
        }

        Finish(me);
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

    // "We have it" = any of the step's landed nano ids is running on us.
    private static bool Have(LocalPlayer me, BuffCatalog.BuffAction step)
    {
        if (step.LandIds.Length == 0)
        {
            return false;
        }

        var buffs = me.Buffs;
        return buffs != null && buffs.Any(b => step.LandIds.Contains(b.Id));
    }

    /// <summary>The wire text for a buff code: both Codedoc and Chewy expect "cast &lt;code&gt;" (AOBuddy10
    /// CodedocBuffs/ChewyBuffs both prepend "cast "). The catalog stores the bare code (e.g. "60ncu"), so we
    /// add the keyword here - guarded so a code already written "cast ..." is not doubled.</summary>
    public static string WireTell(string code) =>
        string.IsNullOrWhiteSpace(code) || code.StartsWith("cast ", StringComparison.OrdinalIgnoreCase)
            ? code
            : "cast " + code;

    private void SendTell(string code)
    {
        var text = WireTell(code);
        if (Client.Chat == null)
        {
            _logger.LogInformation($"BUFFS: no chat client - tell to '{_config.BuffBotName}' not sent: '{text}'.");
            return;
        }

        try
        {
            Client.Chat.SendPrivateMessage(_config.BuffBotName, text, false);
            _logger.LogInformation($"BUFFS: /tell {_config.BuffBotName} {text}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"BUFFS: the tell to '{_config.BuffBotName}' failed: {ex.Message}");
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
    // from anyone who is NOT the owner; otherwise leave every invite alone (the owner's own stay manual).
    private void OnTeamRequest(object? sender, TeamRequestEventArgs e)
    {
        if (_stage != Stage.Working || !_teamWindow || Team.IsInTeam)
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
    }
}
