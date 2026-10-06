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
///     BUFF BOTS (step 4a.1 - the handshake PLUMBING; the buff SELECTION is 4a.2). Getting buffs from
///     a public buff bot (Chewy on RubiKa, Codedoc on RubiKa2019). The contract, owner 2026-10-05:
///     you must NOT be in a team; the bot invites you; you accept; it tells "preparing to buff you";
///     you request the buff your level allows; it buffs and AUTO-KICKS you from the team.
///     This runs the handshake off the packet router PLUS the TeamRequest event (owner,
///     2026-10-06: "register that you want to receive the teaminvitemessage and react on that"):
///     every family invite (botFamily prefix - "some bot starting with Chewys") is taken whenever
///     we are un-teamed, in a session or out of one (an out-of-session accept is the standing
///     team the next session tells over), and the REQUEST TELLS GO OUT FIRST (owner: "you never
///     send the 'cast ncu' tell though" - the request is what engages the bot), one code per tell
///     as "cast <code>" (BuffTellPrefix), spaced 1.5 s. A session ends when the asked buffs are
///     all running, when the bot kicks us, or when the window runs out. Every team invite on
///     every path is logged (owner, 2026-10-06).
///     NOT yet (later sub-steps): travel to the buff spot (4a.1b), and computing WHICH buffs to ask
///     for from the bot's catalogue, NCU- and receiver-aware (4a.2). For now the tells come from the
///     conf (BuffRequestTells). Moves nothing; runs on the update thread (BotLoop ticks it, and the
///     packet pump raises Team.TeamRequest on the same thread).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BuffBotController
{
    private const double RequestSpacingSec = 1.5; // space the tells so they don't flood

    // The Waiting stage's hard backstop once the team is up: the bot's queue sets the pace,
    // but a queue that never delivers must not hang the dance forever.
    private const double WaitingHardCapSec = 600.0;

    private enum Stage
    {
        Idle,
        AwaitingInvite, // sent nothing yet; waiting for the bot to invite us
        Requesting,     // teamed: sending the request tells, one every RequestSpacingSec
        Waiting,        // tells sent: waiting for the buffs + the auto-kick
    }

    private readonly AccountInfo _config;
    private readonly BuffCatalog _catalog;
    private readonly PacketRouter _packetRouter;
    private readonly ILogger<BuffBotController> _logger;

    private Stage _stage = Stage.Idle;
    private double _clock;
    private double _stageAt;
    private double _lastTellAt;
    private int _tellIndex;
    private bool _joined;
    private bool _sawTeam; // we OBSERVED ourselves teamed once - only then does "not in team" mean kicked
    private bool _teamedWithBot; // the bot teamed us OUTSIDE a session - the next asks tell over that team
    private string? _teamedBotName; // the toon that teamed us (a family member, maybe not the catalog's)
    private string? _bot; // who THIS session tells - the conf's bot, or the catalog's (dimension-picked)
    private List<string> _tells = new(); // the tells for THIS session (config list or a computed plan)
    private List<int> _tellIds = new(); // the nano ids behind those tells - "all landed" ends the session
    private Action<string>? _reply; // the owner tell to answer as the session progresses

    public bool Active => _stage != Stage.Idle;

    /// <summary>Standing in the bot's team outside a session (its invite came early and we
    /// took it) - a session may tell directly instead of waiting for another invite.</summary>
    public bool TeamedWithBot => _teamedWithBot && Team.IsInTeam;

    public BuffBotController(AccountInfo config, BuffCatalog catalog, PacketRouter packetRouter,
        ILogger<BuffBotController> logger)
    {
        _config = config;
        _catalog = catalog;
        _packetRouter = packetRouter;
        _logger = logger;
        // THE INVITE, BOTH WAYS IN (owner, 2026-10-06: "why not use also the teaminvitemessage?
        // what did I write the packetrouter for?"): the N3 TeamInviteMessage carries the
        // inviter's NAME on the wire, and the router hands it to us raw - ahead of and
        // independent of the SDK's own Team.TeamRequest raising (the CharacterAction invite
        // form reaches that event without a name). Both paths share one accept.
        packetRouter.Register(OnTeamInvitePacket, N3MessageType.TeamInvite, receivePriority: 0);
        packetRouter.RegisterN3Observer(ObserveWindowTraffic);
        packetRouter.RegisterSystemObserver(ObserveSystemTraffic);
        Team.TeamRequest += OnTeamRequest;
    }

    // What a session ACTUALLY receives - each distinct N3 type once, each distinct
    // character-action code once, each distinct system type once. Watched for the WHOLE
    // session, not just the window: the bot's invite lands while we are telling or waiting
    // (owner, 2026-10-06: "I will be processing your team buffs. Please accept my invite" -
    // and nothing landed on any path we had hooked).
    private readonly HashSet<string> _windowSeen = new();
    private readonly HashSet<string> _systemSeen = new();

    private void ObserveWindowTraffic(N3Message message)
    {
        if (_stage == Stage.Idle)
        {
            return;
        }

        if (message is CharacterActionMessage action)
        {
            if (_windowSeen.Add($"CharacterAction:{action.Action}"))
            {
                _logger.LogInformation($"BUFFS: window sees CharacterAction {action.Action} (target {action.Target}).");
            }

            return;
        }

        if (_windowSeen.Add(message.GetType().Name))
        {
            _logger.LogInformation($"BUFFS: window sees N3 {message.GetType().Name}.");
        }
    }

    private void ObserveSystemTraffic(SystemMessage message)
    {
        if (_stage == Stage.Idle)
        {
            return;
        }

        if (_systemSeen.Add(message.SystemMessageType.ToString()))
        {
            _logger.LogInformation($"BUFFS: window sees System {message.SystemMessageType}.");
        }
    }

    /// <summary>
    ///     The raw N3 invite: requester and name straight off the wire - and this one REACTS
    ///     ALWAYS, not only inside a wait window (owner, 2026-10-06: "register that you want to
    ///     receive the teaminvitemessage and react on that"). The bots auto-invite a toon
    ///     standing at their spot, so the invite usually lands LONG before a dance opens its
    ///     window - every earlier build ignored exactly that invite and then died as "no invite
    ///     came in the window". Taken whenever we are un-teamed; a dance (or a 'buffs' command)
    ///     tells its requests over the standing team. Observe-only return - never ends the
    ///     sequence, so the SDK's own raising stays intact for Scotty and everyone else.
    /// </summary>
    private bool OnTeamInvitePacket(AOMessage message)
    {
        if (message.Body is not TeamInviteMessage invite)
        {
            return false;
        }

        // EVERY team invite on the wire is logged - accepted or not, in a window or not
        // (owner, 2026-10-06: add log entries for ALL team invite messages).
        _logger.LogInformation(
            $"BUFFS: TeamInvite packet - inviter '{invite.Name ?? "(no name)"}' ({invite.Requestor}), session {_stage}, teamed {Team.IsInTeam}.");

        var family = _catalog.BotFamily ?? _config.BuffBotName;
        var resolved = !string.IsNullOrEmpty(invite.Name);
        if (resolved && (string.IsNullOrEmpty(family)
                         || !invite.Name!.StartsWith(family, StringComparison.OrdinalIgnoreCase)))
        {
            if (_stage != Stage.Idle)
            {
                _logger.LogInformation(
                    $"BUFFS: N3 invite during the session from '{invite.Name}' (family '{family}') - not our bot, left unanswered.");
            }

            return false;
        }

        if (_stage != Stage.Idle)
        {
            // Any open session (telling, waiting) takes the family invite - the team-up is
            // what lets the bot cast.
            TryAcceptInvite(invite.Requestor, resolved ? invite.Name : null);
            return false;
        }

        if (Team.IsInTeam)
        {
            return false; // already teamed - with the bot, presumably; nothing to take
        }

        // Outside a session: take the invite and stand in the bot's team. The next asks tell
        // directly over it.
        Team.Accept(invite.Requestor);
        _teamedWithBot = true;
        _teamedBotName = resolved ? invite.Name : _teamedBotName;
        _logger.LogInformation(
            $"BUFFS: took the bot's invite outside a session ({(resolved ? $"'{invite.Name}'" : "unnamed")}) - the next asks tell over this team.");
        return false;
    }

    /// <summary>The accept, shared by the N3 and the TeamRequest paths: taken during ANY open
    /// session (the bot may invite while we are already telling or waiting), so whichever form
    /// arrives first wins and the duplicate is a no-op. Idle takes nothing.</summary>
    private void TryAcceptInvite(Identity requester, string? name)
    {
        if (_stage == Stage.Idle || Team.IsInTeam)
        {
            return;
        }

        var unnamed = string.IsNullOrEmpty(name);
        if (unnamed)
        {
            // The CharacterAction invite form carries no inviter name; during a session the
            // event only fires for invites sent to US, at the bot's spot for exactly this one.
            _logger.LogInformation("BUFFS: unnamed invite during the session - taking it as the bot's.");
        }

        var who = unnamed ? $"unnamed ({requester})" : $"'{name}'";
        Team.Accept(requester);
        _joined = true;
        _teamedWithBot = true;
        _teamedBotName = name ?? _teamedBotName;
        if (_stage == Stage.AwaitingInvite)
        {
            Enter(Stage.Requesting);
            _lastTellAt = -99;
        }

        _logger.LogInformation($"BUFFS: accepted {who} invite - requesting buffs.");
        _reply?.Invoke($"Teamed with {who} - requesting buffs.");
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
                StartSession(_config.BuffBotName, _catalog.PlanForPetSummon(DynelManager.LocalPlayer, int.MaxValue, int.MaxValue),
                    "computed pet-summon plan", reply);
                break;
            default: // start: the tells configured in the conf
                StartSession(_config.BuffBotName, _config.BuffRequestTells ?? new List<string>(), "configured tells", reply);
                break;
        }
    }

    /// <summary>
    ///     Programmatic start (the pet brain's buff-first, 4b): open a session with a computed plan,
    ///     told to the conf's bot. Returns false when it cannot start (no bot configured, already
    ///     active, in a team, or an empty plan) so the caller can fall back to summoning what it can now.
    /// </summary>
    public bool RequestBuffs(List<string> tells, string why)
    {
        return RequestBuffs(_config.BuffBotName, tells, why);
    }

    /// <summary>
    ///     Programmatic start with an EXPLICIT tell target - the extbuff brains name the CATALOG's
    ///     bot (dimension-picked: Chewysfix / Codedoc); the conf's BuffBotName stays the 'buffs'
    ///     owner command's business.
    /// </summary>
    public bool RequestBuffs(string botName, List<string> tells, string why)
    {
        if (Active || string.IsNullOrWhiteSpace(botName) || tells == null || tells.Count == 0)
        {
            return false;
        }

        if (Team.IsInTeam && !TeamedWithBot)
        {
            return false; // teamed with someone else - the buff bot must be the one to team us
        }

        StartSession(botName, tells, why, s => _logger.LogInformation($"BUFFS: {s}"));
        return true;
    }

    private void StartSession(string botName, List<string> tells, string what, Action<string> reply)
    {
        if (string.IsNullOrWhiteSpace(botName))
        {
            reply("No buff bot configured (set BuffBotName in the conf).");
            return;
        }

        if (Team.IsInTeam && !TeamedWithBot)
        {
            reply("Already in a team - leave it first; the buff bot must be the one to team us.");
            return;
        }

        if (tells.Count == 0)
        {
            reply($"No buffs to request ({what} is empty).");
            return;
        }

        _bot = TeamedWithBot && !string.IsNullOrEmpty(_teamedBotName) ? _teamedBotName : botName;
        _tells = tells;
        _tellIds = ResolveTellIds(tells);
        _reply = reply;
        _joined = TeamedWithBot;
        _sawTeam = TeamedWithBot && Team.IsInTeam;
        _tellIndex = 0;
        _windowSeen.Clear(); // each window watches its own traffic fresh
        _systemSeen.Clear();

        Enter(Stage.AwaitingInvite);

        if (!string.IsNullOrWhiteSpace(_config.BuffInviteTell))
        {
            // The manual trigger: a tell reaches the bot's client regardless of whether its
            // auto-inviter ever sees a clientless toon - the owner knows the word that makes
            // it invite (empty conf = off).
            Tell(_config.BuffInviteTell);
        }

        // TELL FIRST (owner, 2026-10-06: "you never send the 'cast ncu' tell though"): the
        // request itself is what engages the bot - the old order waited for an invite before
        // telling, so the tell never went out at all. The tells go out NOW, spaced, and the
        // bot's invite is taken whenever it comes (the accept runs mid-telling too); the
        // buffs landing - or the kick - closes the session.
        Enter(Stage.Requesting);
        _lastTellAt = -99;
        var family = _catalog.BotFamily ?? botName;
        _logger.LogInformation(
            $"BUFFS: session open ({what}: {string.Join(" ", tells)}) - telling now" +
            (TeamedWithBot ? $" over the standing team with '{_bot}'" : "") +
            $", expecting a '{family}*' invite any time.");
        reply($"Buff session open ({tells.Count} tells, {what}) - telling now.");
    }

    /// <summary>The nano ids behind a tell list - "all of these are running" is the session's
    /// success signal (the bot does not always kick between batches). Unresolvable tells just
    /// leave the list short; an EMPTY list disables the landed check for that session.</summary>
    private List<int> ResolveTellIds(List<string> tells)
    {
        var ids = new List<int>();
        foreach (var tell in tells)
        {
            foreach (var code in tell.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = _catalog.Find(code)?.NanoId;
                if (id.HasValue)
                {
                    ids.Add(id.Value);
                }
            }
        }

        return ids;
    }

    // ---- The handshake ---------------------------------------------------------------------

    public void Tick(LocalPlayer me, double dt)
    {
        _clock += dt;

        // The standing team is a state, not a session: when it ends (the bot kicked us, or the
        // team dissolved) the flag goes with it, wherever the stage machinery stands.
        if (_teamedWithBot && !Team.IsInTeam)
        {
            _teamedWithBot = false;
            _teamedBotName = null;
        }

        // Mark the team as OBSERVED: Team.Accept's round-trip lags the local team stat, so a
        // "not in team" seen before this flag means "the accept is in flight", not "kicked".
        if (_joined && Team.IsInTeam)
        {
            _sawTeam = true;
        }

        if (_stage == Stage.Idle)
        {
            return;
        }

        if (me == null)
        {
            End("lost the character");
            return;
        }

        // The window bounds the PRE-TEAM phases only (no invite). Once the team is up and the
        // tells are out, the bot's queue sets the pace: wait until the asked buffs are all
        // running in me.Buffs, or the kick comes (owner, 2026-10-06: "just wait until the
        // buffs all are active in our me.Buffs").
        if (_clock - _stageAt > _config.BuffHandshakeSeconds && _stage != Stage.Requesting && !_sawTeam)
        {
            End(_joined ? "the buff window closed" : "no invite came in the window");
            return;
        }

        switch (_stage)
        {
            case Stage.AwaitingInvite:
                // Nothing to do here - the invite handlers accept the bot's invite and advance us.
                break;

            case Stage.Requesting:
                SendNextTell();
                break;

            case Stage.Waiting:
                // Done when the bot kicks us AFTER the team was actually joined (the accept's
                // round-trip lag is not a kick), when the asked buffs are all RUNNING, or - as
                // a dead-queue backstop - when the hard cap runs out.
                if (_joined && _sawTeam && !Team.IsInTeam)
                {
                    End("the buff bot kicked us - buffs done");
                }
                else if (_tellIds.Count > 0 && _tellIds.All(id => me.Buffs.Find(id, out _)))
                {
                    End("the asked buffs are up");
                }
                else if (_clock - _stageAt > WaitingHardCapSec)
                {
                    End("the buff wait ran out - not everything landed");
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
        var bot = _bot ?? _config.BuffBotName;
        if (Client.Chat == null)
        {
            _logger.LogInformation($"BUFFS: no chat client - tell to '{bot}' not sent: '{text}'.");
            return;
        }

        try
        {
            var full = _config.BuffTellPrefix + text; // the wire format is "cast <code>" (the
            // Codedoc sniff: codes are what follows 'cast ' in the tell) - conf-tunable
            Client.Chat.SendPrivateMessage(bot, full, false);
            _logger.LogInformation($"BUFFS: /tell {bot} {full}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"BUFFS: the tell to '{bot}' failed: {ex.Message}");
        }
    }

    // THE BUFF BOT'S INVITE (mirrors MovementController's Scotty handler): during an active session,
    // accept ONLY the configured buff bot's invite; leave every other invite alone (the owner's own
    // stay manual, and Scotty's are MovementController's). Runs on the update thread.
    private void OnTeamRequest(object? sender, TeamRequestEventArgs e)
    {
        // EVERY team invite on this path is logged too (owner, 2026-10-06) - this is the
        // CharacterAction invite form, whose event carries no inviter name.
        _logger.LogInformation(
            $"BUFFS: TeamRequest event - inviter '{e.RequesterName ?? "(no name)"}' ({e.Requester}), session {_stage}, teamed {Team.IsInTeam}.");

        var name = e.RequesterName;
        if (string.IsNullOrEmpty(name) && DynelManager.Find(e.Requester, out SimpleChar requester))
        {
            name = requester.Name;
        }

        var resolved = !string.IsNullOrEmpty(name);
        // The FAMILY is the matcher - never the session's tell target (owner, 2026-10-06: wait
        // for "a character where the name starts with Chewys", not for 'Chewysfix').
        var family = _catalog.BotFamily ?? _config.BuffBotName;
        var ours = resolved && !string.IsNullOrEmpty(family)
                   && name!.StartsWith(family, StringComparison.OrdinalIgnoreCase);

        if (resolved && !ours)
        {
            if (_stage != Stage.Idle)
            {
                // A stranger's invite during an open session MUST leave a trace - a silent
                // drop is indistinguishable from no invite at all.
                _logger.LogInformation(
                    $"BUFFS: invite during the session from '{name}' (family '{family}') - not our bot, left unanswered.");
            }

            return; // not our buff bot - the owner's own invites stay manual, silently
        }

        if (_stage != Stage.Idle)
        {
            // Any open session takes the family invite - the team-up is what lets the bot cast.
            TryAcceptInvite(e.Requester, resolved ? name : null);
            return;
        }

        // Idle: the always-on N3 path owns the standing-team accept; the event form has no
        // name to match an idle invite against, so it stays unanswered here.
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
        _sawTeam = false;
        _tellIndex = 0;
        _bot = null;
        _reply = null;
    }
}
