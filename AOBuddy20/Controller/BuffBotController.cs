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
using AOBuddy20.Utils;
using AOSharp.Clientless;
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
///     NOT yet (later sub-steps): travel to the buff spot (4a.1b), and computing WHICH buffs to ask
///     for from the bot's catalogue, NCU- and receiver-aware (4a.2). For now the tells come from the
///     conf (BuffRequestTells). Moves nothing; runs on the update thread (BotLoop ticks it, and the
///     packet pump raises Team.TeamRequest on the same thread).
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

    public BuffBotController(AccountInfo config, BuffCatalog catalog, ILogger<BuffBotController> logger)
    {
        _config = config;
        _catalog = catalog;
        _logger = logger;
        Team.TeamRequest += OnTeamRequest; // the buff bot answers by inviting us
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

        _stage = Stage.Idle;
        _joined = false;
        _tellIndex = 0;
        _reply = null;
    }
}
