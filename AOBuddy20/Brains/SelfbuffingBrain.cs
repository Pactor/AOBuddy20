// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: SelfbuffingBrain.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Controlling;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;


namespace AOBuddy20.Brains;

/// <summary>
///     SELFBUFFING BRAIN BASE - the cast engine every selfbuffing brain shares, with the policy
///     seams a profession overrides. The charter (AOBuddy10 SupportController's KeepBuffs):
///     keep the character's own learned nanos up, entirely DATA-DRIVEN from the wire (rules.md
///     #4 - no nano-id tables): a learned nano (me.SpellList) is keepable iff it has Use
///     modifiers, NCU > 0, and is no pet line / debuff; of one NanoLine only the best is kept
///     (StackingOrder promotion as skills rise); a recast happens when the running buff's
///     remaining time falls under the margin; NCU must fit (MaxNCU - what is running); a buff
///     whose Use-modifiers cover a strict superset of another's replaces it (composite
///     suppression). EVERYTHING casts through the one queue below - one cast in flight, gated
///     on IsCasting, affordability (breed-adjusted nano cost, the rule Buff.GetCost encodes)
///     and free NCU. Sitting down to recharge before an unaffordable buff (never burning a
///     stim for nano) is the family implementation's job.
///     The HoT / nano-regen forecast of the running buffs is reported to the HealController
///     (ReportRegen -> HealController.SetRegen, the contract that controller already exposes).
///     While an episode is open the brain holds the arbiter at ControlPriority.Selfbuffing
///     (above Resupply, below Combat) - the dormant general never claims.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public abstract class SelfbuffingBrain
{
    // A cast with no observable completion for this long is dropped (the family implementation
    // narrows this with Client.NanoSeen verdicts; until then the constant bounds a stuck queue).
    private const double CastTimeoutSeconds = 8.0;

    protected SelfbuffingBrain(ILogger logger, ControlArbiter controlArbiter, HealController heal)
    {
        _logger = logger;
        _controlArbiter = controlArbiter;
        _heal = heal;
    }

    protected readonly ILogger _logger;
    protected readonly ControlArbiter _controlArbiter;
    protected readonly HealController _heal;

    protected double _clock; // the brain's own clock, accumulated from dt

    private readonly Queue<CastRequest> _castQueue = new();
    private CastRequest? _inFlight;
    private NanoItem? _inFlightNano;
    private double _sentAt;
    private bool _loggedBroke; // "out of nano" once per burst, not once per queue entry

    private sealed class CastRequest
    {
        public int NanoId;
        public Identity Target;
        public string Reason = "";
    }

    /// <summary>True while a selfbuffing episode is open (BotLoop claims Tasks.Selfbuff).</summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            return false; // nothing casts blind
        }

        var holding = PolicyTick(me, dt); // POLICY: scan and enqueue (the family's decisions)
        TickDrain(me, dt); // ENGINE: one cast in flight
        return holding;
    }

    /// <summary>
    ///     POLICY: this tick's decisions. Return true only while an episode holds the arbiter at
    ///     ControlPriority.Selfbuffing (TakeControl on open, ReleaseControl on close - the
    ///     HealController pattern). The general brain returns false (dormant).
    /// </summary>
    protected virtual bool PolicyTick(LocalPlayer me, double dt)
    {
        return false;
    }

    /// <summary>
    ///     POLICY: the learned nano ids worth keeping up this pass, best first. Default: none
    ///     (dormant). A family implementation walks me.SpellList, filters keepable nanos and
    ///     promotes per NanoLine (NeedsBuff decides "is it up"); for each one due it calls
    ///     EnqueueCast. NOT called by the base - the family's PolicyTick drives it.
    /// </summary>
    protected virtual IEnumerable<int> ScanKeepups(LocalPlayer me)
    {
        yield break;
    }

    /// <summary>ENGINE: enqueue a cast. The queue is the ONLY cast path of this brain.</summary>
    protected void EnqueueCast(int nanoId, Identity target, string reason)
    {
        _castQueue.Enqueue(new CastRequest { NanoId = nanoId, Target = target, Reason = reason });
    }

    /// <summary>
    ///     ENGINE: drain the queue - one cast in flight, gated on IsCasting. Before a send it
    ///     checks the item data exists, CanAfford and FreeNcu (never fits -> drop with a log);
    ///     after a send it waits the nano's own AttackDelay + RechargeDelay before the next; a
    ///     cast outstanding for CastTimeoutSeconds is dropped with a log.
    /// </summary>
    protected void TickDrain(LocalPlayer me, double dt)
    {
        if (_inFlight != null)
        {
            if (_clock - _sentAt >= CastTimeoutSeconds)
            {
                _logger.LogInformation(
                    $"SELFBUFF: cast of {_inFlightNano?.Name ?? _inFlight.NanoId.ToString()} never completed - dropped ({_inFlight.Reason}).");
                _inFlight = null;
                _inFlightNano = null;
            }

            return; // one cast in flight: nothing else goes out
        }

        if (_castQueue.Count == 0)
        {
            _loggedBroke = false;
            return;
        }

        var req = _castQueue.Dequeue();
        if (!ItemData.Find(req.NanoId, out NanoItem nano) || nano == null)
        {
            _logger.LogInformation($"SELFBUFF: nano {req.NanoId} has no item data - dropped ({req.Reason}).");
            return;
        }

        if (!CanAfford(me, nano))
        {
            if (!_loggedBroke)
            {
                _loggedBroke = true;
                _logger.LogInformation($"SELFBUFF: out of nano for {nano.Name} - dropped ({req.Reason}).");
            }

            return;
        }

        if (nano.NCU > FreeNcu(me))
        {
            _logger.LogInformation($"SELFBUFF: {nano.Name} needs {nano.NCU} NCU, {FreeNcu(me)} free - dropped ({req.Reason}).");
            return;
        }

        me.Cast(req.Target, req.NanoId);
        _inFlight = req;
        _inFlightNano = nano;
        _sentAt = _clock;
        _logger.LogInformation($"SELFBUFF: cast {nano.Name} ({req.Reason}).");
    }

    /// <summary>
    ///     ENGINE: whether the character can pay the nano's cost - the breed/NPCostModifier rule
    ///     Buff.GetCost encodes (Nanomage floor 45, Atrox 55, others 50, times the modifier),
    ///     against the live nano pool. No item data -> not affordable (never cast blind).
    /// </summary>
    protected static bool CanAfford(LocalPlayer me, NanoItem nano)
    {
        if (nano.Cost <= 0)
        {
            return true;
        }

        if (!me.TryGetStat(Stat.CurrentNano, out var nanoPool))
        {
            return false;
        }

        return nanoPool >= AdjustedCost(me, nano.Cost);
    }

    /// <summary>The cost the character actually pays (Buff.GetCost's breed-adjusted rule).</summary>
    protected static int AdjustedCost(LocalPlayer me, int baseCost)
    {
        var costModifier = me.TryGetStat(Stat.NPCostModifier, out var mod) ? mod : 100;
        switch ((Breed)(me.TryGetStat(Stat.Breed, out var breed) ? breed : 0))
        {
            case Breed.Nanomage:
                costModifier = Math.Max(costModifier, 45);
                break;
            case Breed.Atrox:
                costModifier = Math.Max(costModifier, 55);
                break;
            default:
                costModifier = Math.Max(costModifier, 50);
                break;
        }

        return (int)(baseCost * (costModifier / 100.0));
    }

    /// <summary>
    ///     ENGINE: is the buff MISSING on the target - nothing of this nano's id or NanoLine is
    ///     running with more than minRemainingSeconds left, and the target meets the nano's use
    ///     requirements (the same-line check is the simple form; StackingOrder promotion is the
    ///     family implementation's refinement).
    /// </summary>
    protected static bool NeedsBuff(NanoItem nano, SimpleChar target, double minRemainingSeconds)
    {
        foreach (var b in target.Buffs)
        {
            if (b == null || b.NanoItem == null)
            {
                continue;
            }

            if (b.Id == nano.Id || b.NanoItem.NanoLine == nano.NanoLine)
            {
                var remaining = b.Cooldown?.RemainingTime ?? 0;
                if (remaining > minRemainingSeconds)
                {
                    return false; // up, and not about to run out
                }
            }
        }

        return nano.MeetsUseReqs(target, false, false);
    }

    /// <summary>
    ///     ENGINE: free NCU = MaxNCU - what is running. CurrentNCU sometimes reads 0 while buffs
    ///     are running (AOBuddy10's finding) - in that case the running buffs' own NCU sum stands
    ///     in for it. (The shared implementation lives on BuffCatalog.FreeNcu - the pet-first cycle
    ///     and the Engineer survival fill budget through it too.)
    /// </summary>
    protected static int FreeNcu(LocalPlayer me)
    {
        return BuffCatalog.FreeNcu(me);
    }

    /// <summary>
    ///     SEAM: report the running buffs' combined regen to the HealController - its triggers
    ///     measure their deficits net of it (HealController.SetRegen).
    /// </summary>
    protected void ReportRegen(double healthPerSecond, double nanoPerSecond, double shortestRemaining)
    {
        _heal.SetRegen(healthPerSecond, nanoPerSecond, shortestRemaining);
    }
}