// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: TeamController.cs
//
// Last modified: 2026-10-06
// Created:       2026-10-06
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Interfaces;
using AOBuddy20.Network;
using AOSharp.Clientless;
using Microsoft.Extensions.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     GROUP WITH THE OWNER (AOBuddy10's passive teaming, Main.cs): the bot does not invite anyone in
///     normal play; it simply ACCEPTS the owner's team invite so the two are grouped (needed for
///     team-cast buffs, shared XP, and so the owner's pets/teammates are recognised). Only the owner's
///     invite - every other invite is left to whoever owns it (Scotty warps -> MovementController, buff
///     bots -> BuffBotController; both gate on their own state, so on an owner invite they no-op and this
///     accepts). The invite arrives ROUTED (PacketRouter, several guarded recipients - owner, 2026-10-08:
///     a single event chain died whole when any subscriber threw and the rest missed the invite); the
///     owner is matched by configured name off the invite packet (the inviter is often a toon the client
///     never streams).
/// </summary>
public sealed class TeamController : IPacketConsumer
{
    private readonly AccountInfo _config;
    private readonly ILogger<TeamController> _logger;

    public TeamController(AccountInfo config, ILogger<TeamController> logger)
    {
        _config = config;
        _logger = logger;
        Team.TeamRequest += OnTeamRequest; // the CharacterAction form of an invite
    }

    public void RegisterPackets(PacketRouter router)
    {
        // The N3 TeamInvite form - the one that carries the inviter's name. false = the other
        // recipients (the Scotty warp, the buff bots) see the invite too.
        router.Register(TeamInviteHandler, N3MessageType.TeamInvite, 0);
    }

    private bool TeamInviteHandler(AOMessage e)
    {
        var invite = (TeamInviteMessage)e.Body;
        OnTeamRequest(this, new TeamRequestEventArgs(invite.Requestor, invite.Name));
        return false;
    }

    private void OnTeamRequest(object? sender, TeamRequestEventArgs e)
    {
        if (!_config.AutoAcceptOwnerTeamInvite || string.IsNullOrWhiteSpace(_config.Owner) || Team.IsInTeam)
        {
            return;
        }

        var name = e.RequesterName;
        if (string.IsNullOrEmpty(name) && DynelManager.Find(e.Requester, out SimpleChar requester))
        {
            name = requester.Name;
        }

        if (string.IsNullOrEmpty(name) || !name.Equals(_config.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return; // not the owner's invite - not ours to answer
        }

        Team.Accept(e.Requester);
        _logger.LogInformation($"TEAM: accepted the owner's ('{name}') team invite - grouped up.");
    }
}
