// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: TeleportWatcher.cs
//
// Created:       2026-10-13
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Network;

/// <summary>
///     THE TELEPORT WATCH: N3Teleport is the server's own word that the body is being WARPED
///     (an engineer's warp nano, a summon, ...) - exactly what the NAV memory needs to tell a
///     real crossing from a desync. Until 2026-10-13 nobody handled it and every warp landed
///     as "pf X -> Y with NO teleport - desync/partial zone" (owner, 15:06: pf 600 -> 655 on
///     an engineer warp at the Varmint mission entrance). On arrival it records where we stood
///     (the old playfield's exit spot) and holds the warp as pending; BotLoop consumes it the
///     tick the playfield id changes, so NavController.SetPlayfield records an honest
///     Kind="teleport" transition. Same-playfield warps (lifts) never surface: Consume only
///     answers once the pf actually changed.
/// </summary>
public sealed class TeleportWatcher
{
    /// <summary>A warp's zone lands within a second or two on the wire; older pendings are dropped.</summary>
    private const double LandingSeconds = 10.0;

    private readonly ILogger<TeleportWatcher> _logger;
    private readonly object _sync = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private Vector3 _exitPos;
    private int _fromPf;
    private double _atSec = -1;
    private int _logged; // the first arrivals log in full: which fields a warp carries is wire knowledge
    private int _foreign; // the first rejections log: what a foreign warp's identity looks like

    public TeleportWatcher(ILogger<TeleportWatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     The pending warp's exit spot, when the playfield has since changed (the warp landed us
    ///     across a pf boundary). Consumes the pending warp; returns null for a same-pf warp, a
    ///     stale one, or while the zone has not landed yet.
    /// </summary>
    public Vector3? Consume(int currentPf)
    {
        lock (_sync)
        {
            if (_atSec < 0)
            {
                return null;
            }

            if (_clock.Elapsed.TotalSeconds - _atSec > LandingSeconds)
            {
                _atSec = -1; // the zone never landed (same-pf warp): drop, don't poison a later crossing
                return null;
            }

            if (currentPf == _fromPf)
            {
                return null;
            }

            _atSec = -1;
            return _exitPos;
        }
    }

    public void RegisterPackets(PacketRouter router)
    {
        router.Register(OnTeleport, N3MessageType.N3Teleport, 0);
    }

    private bool OnTeleport(AOMessage arg)
    {
        if (arg.Body is not N3TeleportMessage tp)
        {
            return false;
        }

        // FOREIGN WARPS (owner, 2026-10-13): the server broadcasts N3Teleport for other
        // characters' teleports too - a shop-door warp next to us arrived, no zone of ours
        // followed. Only the body's own teleport may arm a pending crossing; the rest is
        // someone else's travel. The first rejections stay in the log: which identity a
        // foreign warp carries is wire knowledge (a warp of ours NOT carrying our identity
        // would show up as a teleport that never lands a crossing).
        var me = DynelManager.LocalPlayer;
        if (me == null || tp.Identity != me.Identity)
        {
            if (_foreign++ < 3)
            {
                _logger.LogInformation(
                    "TELEPORT: foreign N3Teleport ignored (id {t}:{i}, ours {ot}:{oi}).",
                    tp.Identity.Type, tp.Identity.Instance,
                    me?.Identity.Type ?? IdentityType.None, me?.Identity.Instance ?? 0);
            }

            return false;
        }

        lock (_sync)
        {
            // Where we stand now is the old playfield's exit spot - the body has not moved yet.
            _exitPos = DynelManager.LocalPlayer?.Transform?.Position ?? default;
            _fromPf = (int)Playfield.ModelId;
            _atSec = _clock.Elapsed.TotalSeconds;
            if (_logged++ < 3)
            {
                _logger.LogInformation(
                    "TELEPORT: N3Teleport at pf {pf} ({x:0.0} {y:0.0} {z:0.0}) -> dest " +
                    "({dX:0.0} {dY:0.0} {dZ:0.0}) pf=({pfT}:{pfI}) change=({cpT}:{cpI}) pf2=({p2T}:{p2I}).",
                    _fromPf, _exitPos.X, _exitPos.Y, _exitPos.Z,
                    tp.Destination.X, tp.Destination.Y, tp.Destination.Z,
                    tp.Playfield.Type, tp.Playfield.Instance,
                    tp.ChangePlayfield.Type, tp.ChangePlayfield.Instance,
                    tp.Playfield2.Type, tp.Playfield2.Instance);
            }
        }

        return false;
    }
}
