// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: FollowController.cs
//
// Last modified: 2026-10-01
// Created:       2026-10-01 (ported from AOBuddy10 FollowController.cs, stack/mirror tier)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.Diagnostics;
using System.Collections.Concurrent;
using AOBuddy20.Components;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace AOBuddy20.Controlling;

/// <summary>
///     FOLLOW as a MIRROR of the owner (ported from AOBuddy10's stack tier, its FollowDistance = 0
///     default). Three beats: run at him; slide onto his spot keeping HIS facing; once standing on him
///     and facing his way, LOCK - from then on every CharDCMove packet his client sends is replayed as
///     ours, verbatim, so the server simulates both of us identically (players don't collide in AO, so
///     sharing his coordinates is legal, and standing on the zone line he stands on means his crossing
///     is our crossing). The one hard limit is physics: our step is capped at OUR run speed, so a
///     faster owner pulls us out of the mirror and we re-stack the moment we catch up.
///     Threading: Tick runs on the movement thread (called by MovementController when no goal wants
///     the body). The owner's movement packets arrive on the update thread and are queued into
///     MirrorQueue while locked; the next Tick replays them in order - a ~15 ms quantization of a
///     9-29 ms stream, each packet keeping its own DeltaTime, so the server sees his exact stream.
///     MirrorLocked is the one word handed back across threads.
/// </summary>
public sealed class FollowController
{
    // AOBuddy10 values: StackSlideMeters / MirrorBreakMeters / LostPushMeters (Config.cs:273-285),
    // the 2-degree lock heading and the 0.15 m on-him distance from StackTick.
    private const float StackSlideMeters = 4f;
    private const float MirrorBreakMeters = 2.5f;
    private const float LostPushMeters = 10f;
    private const float OnHimDist = 0.15f;
    private const float LockHeadingDeg = 2f;

    // When the owner leaves our view while mirror-locked (almost always because he just crossed a
    // zone line / stepped into a whompa booth), HOLD the mirror this long and keep replaying his queued
    // crossing steps - riding his EXACT booth across - instead of breaking into a blind heading-push
    // that lands in the wrong booth. A real cross fires a zone Reset well inside this window.
    private const double MirrorCrossGraceSec = 3.0;

    private readonly ILogger _logger;
    private readonly Movement _movement;
    private readonly int _sendIntervalMs;
    private readonly float _maxStep;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>His movement packets, queued by the packet thread while locked, replayed by the walk thread.</summary>
    public ConcurrentQueue<CharDCMoveMessage> MirrorQueue { get; } = new();

    public bool MirrorLocked => _mirrorLocked;

    private volatile bool _mirrorLocked;

    // Walk-thread state.
    private Vector3? _lostTarget;
    private Vector3 _lastSeenPos;
    private Quaternion _lastSeenHeading = Quaternion.Identity;
    private bool _lostPushed;
    private bool _sawHim; // (0,0,0) is a real coordinate: a flag, not a default position
    private double _crossGrace; // seconds the mirror has been held after the owner left view (the crossing ride)

    // His speed, measured off his own position samples, for the outrun warning.
    private Vector3? _speedRefPos;
    private double _speedRefAt = -1;
    private double _ownerSpeed;
    private double _warnAt = -99;

    public FollowController(ILogger logger, Movement movement, int sendIntervalMs, float maxStep)
    {
        _logger = logger;
        _movement = movement;
        _sendIntervalMs = sendIntervalMs;
        _maxStep = maxStep;
    }

    /// <summary>Hand the body back to the catch-up walker; it re-locks once we are on him again.</summary>
    public void BreakMirror(string why)
    {
        if (!_mirrorLocked)
        {
            return;
        }

        _mirrorLocked = false;
        _crossGrace = 0;
        MirrorQueue.Clear();
        _logger.LogInformation($"FOLLOW: mirror broken - {why}. Re-stacking.");
    }

    /// <summary>A zone change voids every position the follow state holds.</summary>
    public void Reset()
    {
        _mirrorLocked = false;
        _crossGrace = 0;
        MirrorQueue.Clear();
        _lostTarget = null;
        _lostPushed = false;
        _sawHim = false;
        _speedRefPos = null;
        _ownerSpeed = 0;
    }

    /// <summary>
    ///     One tick on the movement thread. ownerVisible/ownerPos/ownerHeading/ownerMoving are the
    ///     update thread's latest picture of him; walkStep drives one capped nav-Y step toward a
    ///     target (the MovementController's own walker, so approach steps ride the terrain).
    /// </summary>
    public void Tick(LocalPlayer me, bool ownerVisible, Vector3 ownerPos, Quaternion ownerHeading,
        bool ownerMoving, double mySpeed, double dt, Action<LocalPlayer, Vector3, double> walkStep)
    {
        var pos = me.MovementComponent.Position;

        if (ownerVisible)
        {
            _lastSeenPos = ownerPos;
            _lastSeenHeading = ownerHeading;
            _sawHim = true;
            _lostTarget = null;
            _lostPushed = false;
            MeasureOwnerSpeed(ownerPos, dt);
        }

        // LOCKED: his stream is the body. Replay whatever queued, then hold the lock while we sit on
        // him. When he stands still his client sends nothing, and neither do we.
        if (_mirrorLocked)
        {
            // Replay whatever of his movement stream has queued. At a zone/whompa crossing these are his
            // exact steps INTO the booth - replaying them walks us into the SAME booth he took, which is
            // the whole point of the mirror (a blind heading-push lands in the wrong booth).
            while (MirrorQueue.TryDequeue(out var m))
            {
                _movement.Mirror(me, m);
            }

            if (ownerVisible)
            {
                _crossGrace = 0;
                var off = Vector3.Distance(me.MovementComponent.Position, ownerPos);
                if (off > MirrorBreakMeters)
                {
                    BreakMirror($"{off:0.0} m off him (server moved us / couldn't copy a move)");
                }
                else
                {
                    return; // locked and on him
                }
            }
            else
            {
                // He left our view - almost always because he just crossed. DON'T break into a blind
                // push: HOLD the mirror and keep replaying his queued crossing steps so we ride his exact
                // booth across. A real cross fires a zone Reset (clearing all this) well inside the grace;
                // only if the grace runs out with no cross do we give up and fall back to the lost-walk.
                _crossGrace += dt;
                if (_crossGrace < MirrorCrossGraceSec)
                {
                    _movement.Hold(me, _sendIntervalMs); // sit tight on the crossing; replayed steps above move us
                    return;
                }

                BreakMirror($"he left the playfield and did not cross in {MirrorCrossGraceSec:0}s");
            }
        }

        if (!ownerVisible)
        {
            LostTick(me, pos, dt, walkStep);
            return;
        }

        // ---- approach: the stack beats (AOBuddy10 StackTick) ----
        var dist = Vector3.Distance(pos, ownerPos);
        var maxStep = Math.Min((float)(mySpeed * dt), _maxStep);

        if (dist > maxStep)
        {
            // Can't reach his spot this frame - run flat out at it. Sliding close in, keep HIS facing
            // so joining never needs an about-face. The outrun warning names the one cause a
            // stack-follow can lag: he is simply faster.
            WarnIfOutrun(mySpeed, ownerMoving);
            if (dist > StackSlideMeters)
            {
                walkStep(me, ownerPos, dt); // stack-chase
                return;
            }

            var d = (ownerPos - pos).Normalize();
            _movement.Advance(me, pos + d * maxStep, ownerHeading, run: true, dt, _sendIntervalMs);
            return;
        }

        if (!ownerMoving && dist < OnHimDist)
        {
            // On his spot, both standing: stop the stream, match his facing, and once we face his way
            // - lock. From here his packets move us.
            _movement.Hold(me, _sendIntervalMs);
            _movement.Face(me, ownerHeading, 0f, dt, _sendIntervalMs); // mouse-look: instant facing
            if (Movement.HeadingOffsetDeg(me.MovementComponent.Heading, ownerHeading) < LockHeadingDeg)
            {
                _mirrorLocked = true;
                _logger.LogInformation("FOLLOW: stacked on owner - mirroring his movement packets.");
            }

            return;
        }

        // Caught him on the move: take his reported spot and facing, then lock - his next packet puts
        // us exactly on his position and from then on we move as he does.
        _movement.Advance(me, ownerPos, ownerHeading, run: true, dt, _sendIntervalMs);
        if (ownerMoving && !_mirrorLocked)
        {
            _mirrorLocked = true;
            _logger.LogInformation("FOLLOW: joined owner on the move - mirroring his movement packets.");
        }
    }

    // He's out of view: run to his last-seen spot, then lean one push further along his heading -
    // which also carries us onto a zone line he just crossed (AOBuddy10 LostTick/LostPush).
    private void LostTick(LocalPlayer me, Vector3 pos, double dt, Action<LocalPlayer, Vector3, double> walkStep)
    {
        if (!_lostTarget.HasValue)
        {
            if (!_sawHim)
            {
                _movement.Hold(me, _sendIntervalMs); // never saw him this playfield: nothing to chase
                return;
            }

            _lostTarget = _lastSeenPos;
            _logger.LogInformation($"FOLLOW: lost sight - running to his last spot " +
                                   $"({_lastSeenPos.X:0.0} {_lastSeenPos.Z:0.0}), then a {LostPushMeters:0} m push along his heading.");
        }

        var target = _lostTarget.Value;
        var dist = Movement.Flat(pos, target);
        if (!_lostPushed && dist <= 1.5f)
        {
            var fwd = new Vector3(_lastSeenHeading.Forward.X, 0f, _lastSeenHeading.Forward.Z);
            if (fwd.Magnitude > 0.001f)
            {
                target = pos + fwd.Normalize() * LostPushMeters;
            }

            _lostTarget = target;
            _lostPushed = true;
            dist = Movement.Flat(pos, target);
            _logger.LogInformation($"FOLLOW: at his last spot - pushing {LostPushMeters:0} m along his heading.");
        }

        if (dist <= 0.5f)
        {
            _movement.Hold(me, _sendIntervalMs); // chase spent: hold here until he reappears
            return;
        }

        walkStep(me, target, dt);
    }

    // His speed off his own position samples: only when his reported spot actually moved (between his
    // packets it does not), smoothed, teleports ignored (AOBuddy10 RecordAt's rule).
    private void MeasureOwnerSpeed(Vector3 ownerPos, double dt)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (_speedRefPos.HasValue)
        {
            var moved = Vector3.Distance(ownerPos, _speedRefPos.Value);
            var elapsed = now - _speedRefAt;
            if (moved > 0.1f && elapsed > 0.05)
            {
                var v = moved / elapsed;
                if (v < 40)
                {
                    _ownerSpeed = _ownerSpeed <= 0 ? v : _ownerSpeed * 0.7 + v * 0.3;
                }

                _speedRefPos = ownerPos;
                _speedRefAt = now;
            }
            else if (elapsed > 1.0)
            {
                _speedRefPos = ownerPos; // standing: refresh the reference, keep the smoothed speed
                _speedRefAt = now;
            }
        }
        else
        {
            _speedRefPos = ownerPos;
            _speedRefAt = now;
        }
    }

    // At most every 5 s: the one thing no follow algorithm fixes - he is faster than we are
    // (AOBuddy10 WarnIfOutrun).
    private void WarnIfOutrun(double mySpeed, bool ownerMoving)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _warnAt < 5.0 || !ownerMoving || _ownerSpeed <= 0.1)
        {
            return;
        }

        _warnAt = now;
        if (_ownerSpeed > mySpeed + 0.5)
        {
            _logger.LogInformation($"FOLLOW: he's faster than me - his {_ownerSpeed:0.0} u/s vs my {mySpeed:0.0} u/s. Can't close while he runs; I re-stack when he slows.");
        }
    }
}