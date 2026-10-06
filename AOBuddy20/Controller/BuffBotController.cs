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
    private const double RequestSpacingSec = 1.5; // space the tells so they don't flood

    private enum Stage
    {
        Idle,
        AwaitingInvite, // sent nothing yet; waiting for the bot to invite us
        Requesting,     // teamed: sending the request tells, one every RequestSpacingSec
        Waiting,        // tells sent: waiting for the buffs + the auto-kick
    }

    private readonly AccountInfo _config;
    private readonly BuffCatalog _catalog;
    private readonly MovementController _movement;
    private readonly ILogger<BuffBotController> _logger;

    private Stage _stage = Stage.Idle;
    private double _clock;
    private double _stageAt;
    private double _lastTellAt;
    private int _tellIndex;
    private bool _joined;
    private List<string> _tells = new(); // the tells for THIS session (config list or a computed plan)
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
                reply($"Buffs: {(Active ? $"{_stage}" : "idle")}. Bot '{_config.BuffBotName}', catalog " +
                      $"{(_catalog.Loaded ? $"{_catalog.Buffs.Count} entries" : "not loaded")}, window {_config.BuffHandshakeSeconds:0} s.");
                break;
            case "pet":
                // Computed plan: NCU first, then enough MC/TS for the best pet we could want (we
                // over-request here - int.MaxValue picks the biggest safe nano-skill buff; the pet
                // brain will request the exact amount in 4b).
                StartSession(_catalog.PlanForPetSummon(DynelManager.LocalPlayer, int.MaxValue, int.MaxValue),
                    "computed pet-summon plan", reply);
                break;
            default: // start: the tells configured in the conf
                StartSession(_config.BuffRequestTells ?? new List<string>(), "configured tells", reply);
                break;
        }
    }

    /// <summary>
    ///     Programmatic start (the pet brain's buff-first, 4b): open a session with a computed plan.
    ///     Returns false when it cannot start (no bot configured, already active, in a team, or an
    ///     empty plan) so the caller can fall back to summoning what it can now.
    /// </summary>
    public bool RequestBuffs(List<string> tells, string why)
    {
        if (Active || string.IsNullOrWhiteSpace(_config.BuffBotName) || Team.IsInTeam || tells == null || tells.Count == 0)
        {
            return false;
        }

        StartSession(tells, why, s => _logger.LogInformation($"BUFFS: {s}"));
        return true;
    }

    private void StartSession(List<string> tells, string what, Action<string> reply)
    {
        if (string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            reply("No buff bot configured (set BuffBotName in the conf).");
            return;
        }

        if (Team.IsInTeam)
        {
            reply("Already in a team - leave it first; the buff bot must be the one to team us.");
            return;
        }

        if (tells.Count == 0)
        {
            reply($"No buffs to request ({what} is empty).");
            return;
        }

        _tells = tells;
        _reply = reply;
        _joined = false;
        _tellIndex = 0;
        Enter(Stage.AwaitingInvite);
        _logger.LogInformation($"BUFFS: session open ({what}: {string.Join(" ", tells)}) - waiting for '{_config.BuffBotName}' to invite us.");
        reply($"Buff session open ({tells.Count} tells, {what}) - waiting for '{_config.BuffBotName}' to invite us (be near it and un-teamed).");
    }

    // ---- The handshake ---------------------------------------------------------------------

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

        // The whole session is bounded by the window; the bot auto-kicks within it.
        if (_clock - _stageAt > _config.BuffHandshakeSeconds && _stage != Stage.Requesting)
        {
            End(_joined ? "the buff window closed" : "no invite came in the window");
            return;
        }

        switch (_stage)
        {
            case Stage.AwaitingInvite:
                // Nothing to do here - OnTeamRequest accepts the bot's invite and advances us.
                break;

            case Stage.Requesting:
                SendNextTell();
                break;

            case Stage.Waiting:
                // Done when the bot kicks us (we were teamed and no longer are), or the window closes.
                if (_joined && !Team.IsInTeam)
                {
                    End("the buff bot kicked us - buffs done");
                }

                break;
        }
    }

    private void SendNextTell()
    {
        var tells = _tells;
        if (_tellIndex >= tells.Count)
        {
            Enter(Stage.Waiting);
            _logger.LogInformation("BUFFS: all request tells sent - waiting for the buffs and the auto-kick.");
            return;
        }

        if (_clock - _lastTellAt < RequestSpacingSec)
        {
            return; // space them out
        }

        var text = tells[_tellIndex];
        _tellIndex++;
        _lastTellAt = _clock;
        Tell(text);
    }

    private void Tell(string text)
    {
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

    // THE BUFF BOT'S INVITE (mirrors MovementController's Scotty handler): during an active session,
    // accept ONLY the configured buff bot's invite; leave every other invite alone (the owner's own
    // stay manual, and Scotty's are MovementController's). Runs on the update thread.
    private void OnTeamRequest(object? sender, TeamRequestEventArgs e)
    {
        if (_stage != Stage.AwaitingInvite)
        {
            return;
        }

        var name = e.RequesterName;
        if (string.IsNullOrEmpty(name) && DynelManager.Find(e.Requester, out SimpleChar requester))
        {
            name = requester.Name;
        }

        if (string.IsNullOrEmpty(name) || !name.Equals(_config.BuffBotName, StringComparison.OrdinalIgnoreCase))
        {
            return; // not our buff bot - not ours to answer
        }

        if (Team.IsInTeam)
        {
            _logger.LogInformation($"BUFFS: '{name}' invited us but we are already in a team - not accepted.");
            return;
        }

        Team.Accept(e.Requester);
        _joined = true;
        Enter(Stage.Requesting);
        _lastTellAt = -99;
        _logger.LogInformation($"BUFFS: accepted '{name}'s invite - requesting buffs.");
        _reply?.Invoke($"Teamed with '{name}' - requesting buffs.");
    }

    // ---- Stage helpers ---------------------------------------------------------------------

    private void Enter(Stage next)
    {
        _stage = next;
        _stageAt = _clock;
    }

    private void End(string why)
    {
        if (_stage != Stage.Idle)
        {
            _logger.LogInformation($"BUFFS: session closed ({why}).");
            _reply?.Invoke($"Buff session done ({why}).");
        }

        // Recovery: a crashed / unresponsive buff bot can invite us, have us join, then never buff OR
        // kick - which would leave us teamed forever and SILENTLY block every future buff request (the
        // Team.IsInTeam guard in RequestBuffs). If we are still in the team we joined for this session,
        // leave it so the bot falls back to its own devices and can try again later.
        if (_joined && Team.IsInTeam)
        {
            try
            {
                Team.LeaveTeam();
                _logger.LogWarning("BUFFS: buff bot never released us - leaving the team to recover (self-buff fallback).");
            }
            catch
            {
                // best-effort; even if the leave send fails, we reset to Idle below and retry later
            }
        }

        _stage = Stage.Idle;
        _joined = false;
        _tellIndex = 0;
        _reply = null;
    }
}
