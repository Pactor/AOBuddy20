// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: UseTravelController.cs
//
// Last modified: 2026-10-06
// Created:       2026-10-06 (ported from AOBuddy10 TravelController.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Components;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     USE-OBJECT TRAVEL (ported from AOBuddy10 TravelController, wire-proven) - the owner rode a lift /
///     grid terminal / whompa / mission door-button / portal item. That is a GenericCmd 'Use' on the
///     object (a right-click), NOT a walked zone line, so FOLLOW's mirror cannot trace it: the owner
///     simply vanishes to another zone server. We watch the owner use objects (DynelManager.DynelUsed,
///     forwarded by MovementController with the owner filter) and, if he then zones right after, walk to
///     that same object and Use() it so the bot rides it too, up or down with him.
///
///     While a use-travel is pending this OWNS the follow body (MovementController consults it before
///     FollowController in the follow branch); it hands the body back the instant the owner is visible
///     again. All methods run on the movement/walk thread except OnOwnerUsed, raised on the packet pump
///     and only writing the "last used" trio - read under the same tick, single producer.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class UseTravelController
{
    private const double UseTravelWindowSeconds = 5.0; // owner-use counts as travel if he vanishes within this
    private const double UseTravelGrace = 2.5;         // owner must stay gone this long before we commit (ride past flickers)
    private const float UseTravelRange = 4.0f;         // how close (flat) the bot must be to Use() the object
    private const double UseTravelTimeout = 15.0;      // give up if the object cannot be reached in this long
    private const int MaxUseTries = 3;

    private readonly ILogger _logger;
    private readonly Movement _movement;
    private readonly int _sendIntervalMs;

    private Identity? _ownerUsedTarget; // last object the owner used
    private Vector3? _ownerUsedPos;     // its position when used
    private double _ownerUsedAge;       // seconds since the owner used it

    private Identity? _pending;  // confirmed travel object the bot must go use
    private Vector3? _pendingPos;
    private double _elapsed;
    private double _arrivedAt = -1;
    private double _lastUseAt = -99;
    private int _tries;

    public bool Active => _pending.HasValue;

    public UseTravelController(ILogger logger, Movement movement, int sendIntervalMs)
    {
        _logger = logger;
        _movement = movement;
        _sendIntervalMs = sendIntervalMs;
    }

    /// <summary>The owner used an object (already filtered to the owner by the caller). Remember what and
    /// where; if he then vanishes within the window, OnOwnerLost turns this into a pending ride.</summary>
    public void OnOwnerUsed(Identity target, Vector3? pos)
    {
        _ownerUsedTarget = target;
        _ownerUsedPos = pos;
        _ownerUsedAge = 0;
        _logger.LogInformation($"TRAVEL: owner used object {target.Type}:{target.Instance}" +
                               (pos.HasValue ? $" at ({pos.Value.X:0} {pos.Value.Y:0} {pos.Value.Z:0})" : "") + ".");
    }

    public void Age(double dt)
    {
        if (_ownerUsedTarget.HasValue)
        {
            _ownerUsedAge += dt;
        }
    }

    /// <summary>Owner just dropped out of view. If he used a travel object within the window, arm the ride.</summary>
    public void OnOwnerLost(Vector3? lastOwnerPos)
    {
        if (!_ownerUsedTarget.HasValue || _ownerUsedAge > UseTravelWindowSeconds)
        {
            return;
        }

        _pending = _ownerUsedTarget;
        _pendingPos = _ownerUsedPos ?? lastOwnerPos;
        _elapsed = 0;
        _arrivedAt = -1;
        _lastUseAt = -99;
        _tries = 0;
        _ownerUsedTarget = null; // consumed
        _logger.LogInformation($"TRAVEL: owner vanished right after a Use - riding {_pending!.Value.Type}:{_pending.Value.Instance} to follow him.");
    }

    /// <summary>
    ///     Move one frame toward the travel object and Use() it. Returns true when it consumed the body
    ///     (the follow branch then skips FollowController). Aborts the instant the owner is visible again.
    ///     <paramref name="walkStep" /> is MovementController's own terrain-aware walker (as FollowController
    ///     uses), so the approach rides the floor.
    /// </summary>
    public bool Tick(LocalPlayer me, double dt, bool ownerVisible, double ownerLostSeconds,
        Action<LocalPlayer, Vector3, double> walkStep)
    {
        if (!_pending.HasValue)
        {
            return false;
        }

        if (ownerVisible)
        {
            _logger.LogInformation("TRAVEL: owner visible again - handing back to follow.");
            Clear();
            return false;
        }

        // A short grace so a brief visibility flicker near a terminal does not commit us.
        if (ownerLostSeconds < UseTravelGrace)
        {
            return false;
        }

        _elapsed += dt;
        if (_elapsed > UseTravelTimeout)
        {
            _logger.LogWarning($"TRAVEL: gave up reaching {_pending!.Value.Type}:{_pending.Value.Instance} after {UseTravelTimeout:0} s.");
            Clear();
            _movement.Hold(me, _sendIntervalMs);
            return true;
        }

        var pos = me.MovementComponent.Position;
        // Prefer the object's live position if it is still spawned (it may have shifted slightly).
        if (DynelManager.Find(_pending.Value, out Dynel found))
        {
            _pendingPos = found.Transform.Position;
        }

        var goal = _pendingPos ?? pos;
        // Flat distance: the object's own Y sits above the floor, and walking at it in 3D lifts the body
        // off the ground - the server then refuses the Use ("you can't do this while falling", AOBuddy10).
        var dist = Movement.Flat(pos, goal);

        if (dist > UseTravelRange)
        {
            walkStep(me, goal, dt); // terrain-aware step toward the object
            return true;
        }

        // On it: stand still a beat (landed), then Use; again every 2 s, up to MaxUseTries, until we zone.
        _movement.Hold(me, _sendIntervalMs);
        if (_arrivedAt < 0)
        {
            _arrivedAt = _elapsed;
        }

        if (_elapsed - _arrivedAt < 1.0 || _elapsed - _lastUseAt < 2.0)
        {
            return true;
        }

        if (_tries >= MaxUseTries)
        {
            _logger.LogWarning($"TRAVEL: {_pending!.Value.Type}:{_pending.Value.Instance} did not take me after {MaxUseTries} tries.");
            Clear();
            return true;
        }

        _tries++;
        _lastUseAt = _elapsed;
        // Send the Use straight to the captured identity - travel objects (floor buttons, terminals, grid,
        // whompas) are not tracked as findable dynels, so a lookup-based Use never fires (AOBuddy10). The
        // exact bytes the owner's client sends: Count=1, Temp4=1 (MovementController.SendUse).
        Client.Send(new GenericCmdMessage
        {
            Action = GenericCmdAction.Use, User = me.Identity, Target = _pending.Value, Count = 1, Temp4 = 1
        });
        _logger.LogInformation($"TRAVEL: used {_pending.Value.Type}:{_pending.Value.Instance} at ({goal.X:0} {goal.Y:0} {goal.Z:0}), try {_tries} - expecting to zone.");
        return true;
    }

    private void Clear()
    {
        _pending = null;
        _pendingPos = null;
        _elapsed = 0;
        _arrivedAt = -1;
        _lastUseAt = -99;
        _tries = 0;
    }

    /// <summary>A zone change voids every pending ride and the remembered Use.</summary>
    public void Reset()
    {
        Clear();
        _ownerUsedTarget = null;
        _ownerUsedPos = null;
        _ownerUsedAge = 0;
    }
}
