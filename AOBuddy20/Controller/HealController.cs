// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: HealController.cs
//
// Last modified: 2026-10-04
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Brains;
using AOBuddy20.Configuration;
using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Controlling;

/// <summary>
///     LOW HEALTH / NANO EMERGENCY (<see cref="ControlPriority.LowHealthNanoEmergency" />): the
///     in-combat heal decision and the out-of-combat recharger rest.
///     IN COMBAT the higher heal wins, and the priority is MOMENTARY - it holds the arbiter only
///     for as long as a healing action occupies the body, never between uses. The candidates:
///       - the stim, after its First-Aid lock: one use costs LockSkill(123, 40) - 40 s by the
///         item's own record, shortened by stat 382 (SkillLockModifier) through
///         <see cref="AccountInfo.SkillLockFactor" /> (retail applies the lock client-side and
///         omnicell not at all, so the clock is ours and the factor is an owner knob at 0). The
///         use is an instant item: take, send, release inside one tick - the lock then runs
///         released, because a locked stim is a clock, not an emergency.
///       - the best learned one-shot heal nano (SpellList ∩ NanoLibrary, the 643 heal stat on,
///         the 8 duration stat off: a one-shot has no duration, every HoT and timed buff does -
///         fixer long/short, doctor combos, all excluded by construction). Castable when no cast
///         is running, the nano-cast recharge lockout has passed and the pool can pay stat 407.
///         A cast DOES occupy the body (it lands only after its attack time, and moving
///         interrupts it): take, send, HOLD until the wire says it landed - CastNanoSpell set
///         IsCasting, FinishNanoCasting clears it - then release and arm the recharge lock
///         (stat 210, a full nano-cast lockout, server-enforced but never echoed, so it lives
///         on our clock). A send the server never acknowledged (the echo window) or one lost
///         mid-flight (the guard window) is abandoned without a recharge - a refused cast was
///         never on the wire, and the server only starts the recharge after a real land.
///     NOTHING AVAILABLE - stim locked, nano recharging or unaffordable, packs empty - releases
///     immediately and says so once: the chain walks out while the timers run. The wound does
///     not have to close for the hold to end; waiting for that is how the body died standing.
///     OUT OF COMBAT the recharger REST CYCLE, wire-proven from AOBuddy10's SupportController:
///     sit, WAIT for the server's echo of the sit (MovementController.SeatedConfirmed - the
///     only reliable seated proof there is; stat 173 never updates after login), use once, KEEP
///     SITTING while the ticks run, and stand when the thresholds are met - or on combat, a
///     stall, or HealRestMaxSeconds. The rest takes the arbiter for its duration ("stay sitting
///     until fully healed" is the body's job; a stage timer of a lower system must not run out
///     from under a seated bot). The walk the rest interrupted resumes on the stand-up (goals
///     were kept); a blitz never sits (SuppressCombat). Stims and rechargers sit on the game's
///     shared stim timer (one gate covers both, so a stim just fired delays the next rest use).
///     The deficits are NET of what the BUFFING controllers report (SetRegen): the combined
///     heal-over-time and nano-over-time of all running buffs. In combat only one cooldown's
///     worth counts; out of combat the whole rest of the buffs. The rates never FIRE a trigger.
///     The decision tick runs on the update thread (BotLoop), the same one the packet handlers
///     run on. (ReleaseControl, like resupply's and sell's, writes None unconditionally: a
///     system above us that took control meanwhile re-takes its own.)
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class HealController
{
    // Stims and rechargers share the game's 20 s stim timer; +1 s so a use the server put on the
    // last tick of its timer cannot race this gate. The COMBAT stim lock is separate (40 s, the
    // item's own LockSkill) - _stimLockedUntil.
    private const double UseCooldownSeconds = 21.0;

    // The fight is not over when the last blow lands: blows in flight, dots ticking, the mob still
    // swinging. A drop in health keeps "in combat" alive this long after the drop.
    private const double CombatLingerSeconds = 10.0;

    // A cast send that never lit IsCasting within this window was refused (not enough nano, still
    // recharging - the server answers a refuse with FinishNanoCasting(0), which clears a cast
    // state that was never set). And a confirmed cast running this long is lost (zoned mid-cast
    // and the like): abandon it WITHOUT a recharge - only a real land starts that lock.
    private const double CastEchoSeconds = 2.0;
    private const double CastGuardSeconds = 30.0;

    // THE REST CYCLE timings, from AOBuddy10's wire-proven SupportController: a sit whose echo was
    // lost gets one hopeful use after this long; a rest whose health AND nano stopped climbing for
    // this long is a dead item - stand (the window is long because the server sends the stats
    // sparsely - 5 s stood him up mid-recharge once, and the re-sit made the sit/stand loop); and
    // after any rest ends, no instant re-sit.
    private const double SitConfirmFallbackSeconds = 1.5;
    private const double RestStallSeconds = 15.0;
    private const double RestCooldownSeconds = 6.0;

    private enum Kind { Stim, Recharger }

    private readonly ILogger<HealController> _logger;
    private readonly ControlArbiter _controlArbiter;
    private readonly MovementController _movement;
    private readonly MissionController _mission;
    private readonly AccountInfo _config;

    private double _clock; // the controller's own clock, accumulated from dt
    private double _cooldownLeft;
    private int _lastHealth = -1;
    private double _lastHurtAt = double.NegativeInfinity;
    private int _pf = -1;

    private bool _holding; // the arbiter is ours: a recharger rest, or a heal cast in flight

    // The in-combat heal clocks. _stimLockedUntil: the First-Aid lock one stim use costs (the
    // item's own LockSkill(123,40), the factor knob aside). _nanoRechargeUntil: the nano-cast
    // lockout one landed cast costs (stat 210). Both server truths without a wire echo - ours.
    private double _stimLockedUntil = double.NegativeInfinity;
    private double _nanoRechargeUntil = double.NegativeInfinity;

    // A heal cast we sent, from the send to the land: confirmed when the wire lights IsCasting,
    // resolved on the clear (land - recharge) or by the windows above (refusal / loss - none).
    private bool _ourCastInFlight;
    private double _castSentAt;
    private bool _castConfirmed;
    private double _castRecharge; // stat 210 of the cast nano, seconds - armed on the land

    private bool _loggedShort; // "nothing usable" once per drought, not once per tick

    // The rest cycle's state (out of combat, recharger): we sat for it, when, and what the body
    // has gained since - the stall guard reads the peaks, not the tick-to-tick deltas.
    private bool _resting;
    private double _restStartedAt;
    private double _restLastGainAt;
    private int _restPeakHealth = -1, _restPeakNano = -1;
    private bool _sitHopefulLogged; // the use-without-echo fallback, logged once per rest
    private double _restCooldownLeft; // no instant re-sit after a rest ends

    // The buff controllers' regen forecast (SetRegen): what all running buffs pour in per second,
    // and how much longer the shortest of them lasts. _regenRemaining decays with the tick, so a
    // buff side that stops reporting fades out by construction - no staleness flag needed.
    private double _regenHealth, _regenNano, _regenRemaining;

    /// <summary>
    ///     The buff controllers' (Selfbuffing / ExternalBuffing) answer to "what are the running
    ///     buffs worth": their combined heal-over-time and nano-over-time per second, valid for
    ///     remainingSeconds (the shortest buff's rest). Replaces the previous forecast. The rates
    ///     never FIRE a trigger - they only shrink the deficits the triggers measure, over one
    ///     cooldown in combat and over the buffs' whole rest out of combat.
    /// </summary>
    public void SetRegen(double healthPerSecond, double nanoPerSecond, double remainingSeconds)
    {
        _regenHealth = Math.Max(0, healthPerSecond);
        _regenNano = Math.Max(0, nanoPerSecond);
        _regenRemaining = Math.Max(0, remainingSeconds);
    }

    public HealController(ILogger<HealController> logger, ControlArbiter controlArbiter,
        MovementController movement, MissionController mission, AccountInfo config)
    {
        _logger = logger;
        _controlArbiter = controlArbiter;
        _movement = movement;
        _mission = mission;
        _config = config;
        _logger.LogInformation("Heal controller initialized.");
    }

    /// <summary>
    ///     The decision tick. True while the arbiter is ours - a heal cast in flight, or a
    ///     recharger rest (BotLoop claims Tasks.Heal and nothing lower ticks).
    /// </summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (_cooldownLeft > 0)
        {
            _cooldownLeft -= dt;
        }

        if (_restCooldownLeft > 0)
        {
            _restCooldownLeft -= dt;
        }

        if (_regenRemaining > 0)
        {
            _regenRemaining = Math.Max(0, _regenRemaining - dt);
        }

        if (me == null)
        {
            EndEpisode();
            return false;
        }

        var pf = (int)Playfield.ModelId;
        if (pf != _pf)
        {
            // A zone change ends every fight and every wound count we were tracking - and a rest,
            // and any cast of ours (the body arrives wherever the server put it; a mid-flight cast
            // did not follow).
            _pf = pf;
            _lastHealth = -1;
            _lastHurtAt = double.NegativeInfinity;
            AbandonCast("zoned");
            DropRest();
        }

        if (!me.TryGetStat(Stat.Health, out var health) ||
            !me.TryGetStat(Stat.MaxHealth, out var maxHealth) ||
            !me.TryGetStat(Stat.CurrentNano, out var nano) ||
            !me.TryGetStat(Stat.MaxNanoEnergy, out var maxNano) ||
            maxHealth <= 0 || maxNano <= 0)
        {
            EndEpisode(); // stats unreadable: nothing fires blind
            return false;
        }

        if (health <= 0)
        {
            EndEpisode(); // dead - the claws are for the living
            return false;
        }

        // Every drop in health marks the hurt time; "in combat" stays alive CombatLingerSeconds past it.
        if (_lastHealth >= 0 && health < _lastHealth)
        {
            _lastHurtAt = _clock;
        }

        _lastHealth = health;

        // A cast we sent owns the tick, combat or not: the heal is committed, the body is rooted
        // until it lands, and the hold runs until the wire resolves it (or the windows do).
        if (_ourCastInFlight)
        {
            return HealCastTick(me);
        }

        var missingHealth = maxHealth - health;
        var nanoPct = nano * 100.0 / maxNano;
        var combat = me.IsAttacking || FoughtOver(me) || _clock - _lastHurtAt < CombatLingerSeconds;

        if (_resting && combat)
        {
            // The fight interrupts the rest: up and onto the heals - a seated body cannot move.
            EndRest("combat interrupts the rest");
        }

        // THE WANT, per scenario: in combat stim-or-nano, out of combat rechargers. Both deficits
        // are NET of the buffs' regen: what the running HoTs pour in over the horizon we care
        // about is need we do not have to spend an item on.
        string reason;
        bool want;
        if (combat)
        {
            // In combat the horizon is one lock: the next stim may go in when its lock is up, so
            // regen beyond it does not argue against this one.
            var horizon = Math.Min(_regenRemaining, _config.StimLockSeconds);
            var missingNet = missingHealth - _regenHealth * horizon;
            var nanoShortNet = _config.HealNanoCombatPct / 100.0 * maxNano - nano - _regenNano * horizon;
            want = nanoShortNet > 0 || (missingNet > 0 && HealCapacity(me) < missingNet);
            reason = want
                ? nanoShortNet > 0
                    ? $"in combat, nano {nanoPct:0}% short {nanoShortNet:0}{RegenNote()}"
                    : $"in combat, one stim heals {HealCapacity(me)} < missing {missingNet:0}{RegenNote()}"
                : "";
        }
        else
        {
            // Out of combat the horizon is the whole rest of the buffs: a long HoT that will close
            // the wound on its own is exactly the case the recharger is not for.
            var missingNet = missingHealth - _regenHealth * _regenRemaining;
            var nanoShortNet = _config.HealNanoOutOfCombatPct / 100.0 * maxNano - nano - _regenNano * _regenRemaining;
            want = missingNet > 0 || nanoShortNet > 0;
            reason = want
                ? $"out of combat, {(missingNet > 0 ? $"{missingNet:0} health missing" : $"nano {nanoPct:0}%")}{RegenNote()}"
                : "";
        }

        if (!want)
        {
            if (_resting)
            {
                // The recharger's ticks carried us over the thresholds: up. The walk this rest
                // interrupted resumes on its own (the goals were kept when we sat).
                EndRest($"healed (nano {nanoPct:0}%, health {health * 100.0 / maxHealth:0}%)");
                return false;
            }

            EndEpisode();
            return false;
        }

        if (combat)
        {
            return CombatHealTick(me, reason, stimHealNow: HealCapacity(me));
        }

        // OUT OF COMBAT: the recharger REST CYCLE. The item is sit-only and the server checks
        // posture at processing time - a use sent standing is refused ("Target must be sitting on
        // ground": every login, the stand-up stood the body before the heal's first use arrived)
        // and the healing ticks only run while seated. So: sit, WAIT for the server's echo, use
        // once, KEEP SITTING while the ticks run. See RestTick for how the rest ends.
        if (_mission.SuppressCombat)
        {
            return false; // a blitz owns the body: no sitting mid-run (a standing use would be refused anyway)
        }

        if (_resting)
        {
            RestTick(me);
            return _holding;
        }

        if (!_movement.Standing || _restCooldownLeft > 0)
        {
            return false; // a stand-up is in flight (the login one, say) or a rest just ended: no sit races it
        }

        // The rest takes the arbiter for its duration: "stay sitting until fully healed" is the
        // body's job, and a lower system's stage timer must not run out from under a seated bot.
        _holding = true;
        _controlArbiter.TakeControl(ControlPriority.LowHealthNanoEmergency);
        _movement.Sit("recharger");
        _resting = true;
        _restStartedAt = _clock;
        _restLastGainAt = _clock;
        _restPeakHealth = -1;
        _restPeakNano = -1;
        _sitHopefulLogged = false;
        _loggedShort = false;
        _logger.LogInformation($"HEAL: sitting for a recharger ({reason}).");
        return _holding;
    }

    // ── The combat heal decision ────────────────────────────────────────────────────────────

    /// <summary>
    ///     The higher heal wins, and the priority is momentary: the stim is take-send-release in
    ///     this tick (its lock then runs released); the nano is take-send-HOLD, the hold running
    ///     until the wire lands the cast (HealCastTick). Nothing available: release at once - a
    ///     locked stim or a recharging cast is a clock, not an emergency, and the chain walks.
    /// </summary>
    private bool CombatHealTick(LocalPlayer me, string reason, int stimHealNow)
    {
        var stim = _clock >= _stimLockedUntil ? BestUsable(Kind.Stim, me) : null;
        int nanoHeal = 0, nanoCost = 0;
        var nano = _clock >= _nanoRechargeUntil ? BestHealNano(me, out nanoHeal, out nanoCost) : null;

        if (stim == null && nano == null)
        {
            if (!_loggedShort)
            {
                _loggedShort = true;
                _logger.LogInformation(_clock < _stimLockedUntil
                    ? "HEAL: want a heal but the stim is locked and no castable heal nano - riding the lock out."
                    : "HEAL: want a heal but nothing usable in the packs - resupply stocks them.");
            }

            return _holding;
        }

        // The tie goes to the stim: it is instant, and a nano's heal only lands after its cast.
        if (stim != null && stimHealNow >= nanoHeal)
        {
            var lockSeconds = StimLockSeconds(me);
            _controlArbiter.TakeControl(ControlPriority.LowHealthNanoEmergency);
            stim.Use();
            _cooldownLeft = UseCooldownSeconds; // the shared stim timer still gates the next recharger
            _stimLockedUntil = _clock + lockSeconds; // the First-Aid lock, from the item's own data
            _loggedShort = false;
            _logger.LogInformation($"HEAL: used {stim.Name} QL {stim.Ql} ({reason}; heals {stimHealNow}); " +
                                   $"{Carry(Kind.Stim, me) - 1} left, First Aid locked {lockSeconds:0}s.");
            _controlArbiter.ReleaseControl();
            return false;
        }

        // THE NANO CAST: the hold IS the cast. Send it, hold until the land - the heal applies
        // only there - then release and let the recharge clock gate the next one.
        _holding = true;
        _controlArbiter.TakeControl(ControlPriority.LowHealthNanoEmergency);
        me.Cast(me.Identity, nano.NanoId);
        _ourCastInFlight = true;
        _castSentAt = _clock;
        _castConfirmed = false;
        _castRecharge = nano.Stat(210) / 100.0; // hundredths of seconds, per the server's own ×10 ms
        _loggedShort = false;
        _logger.LogInformation($"HEAL: casting {NanoLibrary.NameOf(nano.NanoId)} ({reason}; heals {nanoHeal}, " +
                               $"cost {nanoCost}, recharge {_castRecharge:0.#}s) - holding until it lands.");
        return true;
    }

    /// <summary>
    ///     One tick of a heal cast we sent. The wire resolves it: CastNanoSpell lit IsCasting
    ///     (confirmed), FinishNanoCasting clears it (landed - the heal applies, the recharge
    ///     lockout starts). A send that never lit within the echo window was refused - abandon
    ///     without a recharge; a confirmed cast still running past the guard window was lost
    ///     (zoned mid-cast) - same. Interrupts read as lands: the server only recharges a real
    ///     land, but the SDK's path merges InterruptNanoCasting into the same clear, and while
    ///     we hold the body nothing below us can move it into one.
    /// </summary>
    private bool HealCastTick(LocalPlayer me)
    {
        var elapsed = _clock - _castSentAt;

        if (me == null)
        {
            AbandonCast("we are gone");
            return false;
        }

        if (me.IsCasting)
        {
            _castConfirmed = true;
            if (elapsed > CastGuardSeconds)
            {
                AbandonCast("the cast never landed");
                return false;
            }

            return _holding; // the hold runs on - the heal applies only at the land
        }

        if (!_castConfirmed)
        {
            if (elapsed < CastEchoSeconds)
            {
                return _holding; // the echo may still be in flight - hold a moment longer
            }

            AbandonCast("the server refused the cast");
            return false;
        }

        // THE LAND: the nano's heal is on, and the recharge lockout starts - a full nano-cast
        // lock, server-enforced (a cast sent while recharging is refused) but never echoed, so
        // it lives on our clock.
        _ourCastInFlight = false;
        _nanoRechargeUntil = _clock + _castRecharge;
        _holding = false;
        _controlArbiter.ReleaseControl();
        _logger.LogInformation($"HEAL: cast landed - recharge {_castRecharge:0.#}s.");
        return false;
    }

    private void AbandonCast(string why)
    {
        if (!_ourCastInFlight)
        {
            return;
        }

        _ourCastInFlight = false;
        _holding = false;
        _controlArbiter.ReleaseControl();
        _logger.LogInformation($"HEAL: cast abandoned - {why}.");
    }

    /// <summary>The First-Aid lock one stim use costs: the stims' own LockSkill(123, 40), cut by
    /// stat 382 through the owner's factor knob (retail's formula never told us; 0 = flat lock,
    /// which never fires a stim into a real lock).</summary>
    private double StimLockSeconds(LocalPlayer me)
    {
        var modifier = me.TryGetStat(Stat.SkillLockModifier, out var v) ? v : 0;
        return Math.Max(0, _config.StimLockSeconds - modifier * _config.SkillLockFactor);
    }

    /// <summary>
    ///     The best learned one-shot heal we can cast right now, out for its heal amount and cost:
    ///     learned (SpellList) ∩ the library, the 643 heal stat on and the 8 duration stat off -
    ///     a one-shot has no duration, every HoT and timed buff does, whichever profession's line
    ///     it rides - and the current pool must pay the 407 cost (the server charges stat 407 and
    ///     refuses a cast it cannot afford with FinishNanoCasting(0)). Highest heal wins; stacking
    ///     does not matter for one-shots.
    /// </summary>
    private NanoProfile? BestHealNano(LocalPlayer me, out int heal, out int cost)
    {
        heal = 0;
        cost = 0;
        if (me.SpellList == null || !me.TryGetStat(Stat.CurrentNano, out var pool))
        {
            return null;
        }

        NanoProfile? best = null;
        foreach (var id in me.SpellList)
        {
            var n = NanoLibrary.Find(id);
            if (n == null)
            {
                continue;
            }

            var h = n.Stat(643);
            if (h <= 0 || n.Stat(8) != 0)
            {
                continue; // flat heals only: a heal amount, no duration
            }

            var c = n.Stat(407);
            if (c > pool)
            {
                continue; // the pool cannot pay
            }

            if (best == null || h > heal)
            {
                best = n;
                heal = h;
                cost = c;
            }
        }

        return best;
    }

    private void EndEpisode()
    {
        AbandonCast("episode closed");
        if (_holding)
        {
            _holding = false;
            _controlArbiter.ReleaseControl();
            _logger.LogInformation("HEAL: episode closed.");
        }

        DropRest();
        _loggedShort = false;
    }

    // One rest tick: the sit is out, the echo may or may not have landed, the item may or may not
    // be off cooldown. Staying seated IS the treatment - the recharger's ticks run under the sit.
    private void RestTick(LocalPlayer me)
    {
        if (_mission.SuppressCombat)
        {
            // The blitz took the body while we sat: up, out of the run's way.
            EndRest("the blitz needs the body");
            return;
        }

        // Progress, peak-tracked: the server sends health/nano sparsely, so a working recharger's
        // climb can look flat for several seconds (the stall window is long for exactly that).
        if (me.TryGetStat(Stat.Health, out var hp) && (_restPeakHealth < 0 || hp > _restPeakHealth))
        {
            _restPeakHealth = hp;
            _restLastGainAt = _clock;
        }

        if (me.TryGetStat(Stat.CurrentNano, out var nano) && (_restPeakNano < 0 || nano > _restPeakNano))
        {
            _restPeakNano = nano;
            _restLastGainAt = _clock;
        }

        if (_clock - _restStartedAt > _config.HealRestMaxSeconds)
        {
            EndRest("the rest ran out of time");
            return;
        }

        if (_clock - _restLastGainAt > RestStallSeconds)
        {
            EndRest("no gain - the item is not helping");
            return;
        }

        // REALLY seated? The server's echo of our sit is the proof (stat 173 never updates). A
        // lost echo gets one hopeful use after the fallback window, logged - AOBuddy10 shipped
        // the same 1.5 s and pressed anyway.
        var seated = _movement.SeatedConfirmed;
        if (!seated && _clock - _restStartedAt >= SitConfirmFallbackSeconds)
        {
            if (!_sitHopefulLogged)
            {
                _sitHopefulLogged = true;
                _logger.LogInformation("HEAL: no sit confirmation from the server - using the recharger anyway.");
            }

            seated = true;
        }

        if (!seated || _cooldownLeft > 0)
        {
            return; // waiting for the echo, or the stim timer runs: the sit does the work
        }

        var it = BestUsable(Kind.Recharger, me);
        if (it == null)
        {
            if (!_loggedShort)
            {
                _loggedShort = true;
                _logger.LogInformation("HEAL: seated for a recharger but none usable in the packs - resupply stocks them.");
            }

            return; // stay seated: the last use's ticks may still be running; the stall guard ends the rest
        }

        it.Use();
        _cooldownLeft = UseCooldownSeconds;
        _logger.LogInformation($"HEAL: used {it.Name} QL {it.Ql} (seated {(_clock - _restStartedAt):0.#}s); " +
                               $"{Carry(Kind.Recharger, me) - 1} left - staying seated while it ticks.");
    }

    // The rest is over on purpose: up, the arbiter back, and a cooldown so the next want does not
    // re-sit on the spot (the sit/stand loop guard). The stand goes through the echo-driven
    // campaign, never blind.
    private void EndRest(string why)
    {
        _resting = false;
        ReleaseHold($"rest over - {why}");
        _restCooldownLeft = RestCooldownSeconds;
        _loggedShort = false;
        _movement.Stand("rest over: " + why);
    }

    // The rest dies without a stand (dead, zoned, stats unreadable): the posture is the track's
    // business again, and a later movement command re-arms the stand-up campaign from there.
    private void DropRest()
    {
        if (!_resting)
        {
            return;
        }

        _resting = false;
        ReleaseHold("rest dropped");
        _restCooldownLeft = RestCooldownSeconds;
        _loggedShort = false;
    }

    // The arbiter is ours (a rest, or a cast in flight): let it go. Writes None unconditionally -
    // a system above us that took control meanwhile re-takes its own.
    private void ReleaseHold(string why)
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        _controlArbiter.ReleaseControl();
        _logger.LogInformation($"HEAL: hold released ({why}).");
    }

    // Someone is fighting US - not near us, not fighting the owner: their FightingIdentity points at us.
    // DynelManager.Characters covers the NPC chars too (NpcChar is a SimpleChar), so one pass does both.
    private static bool FoughtOver(LocalPlayer me)
    {
        var mine = me.Identity;
        foreach (var c in DynelManager.Characters)
        {
            if (c != null && c.FightingIdentity == mine)
            {
                return true;
            }
        }

        return false;
    }

    // ---- The heal items ------------------------------------------------------

    // By EXACT name from the resupply config, the same rule resupply buys by: a keyword like "Stim"
    // also matches Boosted Stim, Burst of Speed Stim...
    private bool Is(Item it, Kind kind)
    {
        return it?.Name != null &&
               string.Equals(it.Name, kind == Kind.Stim ? _config.ResupplyStimName : _config.ResupplyRechargerName,
                   StringComparison.OrdinalIgnoreCase);
    }

    // The best one we carry that our skills can use (First Aid / Treatment decide, as everywhere).
    private Item? BestUsable(Kind kind, LocalPlayer me)
    {
        Item? best = null;
        foreach (var it in HealItems.AllInvItems())
        {
            if (it == null || !Is(it, kind) || !HealItems.MeetsHealReqs(it, me))
            {
                continue;
            }

            if (best == null || it.Ql > best.Ql)
            {
                best = it;
            }
        }

        return best;
    }

    // How many of the kind we carry and can use, by stack count (the resupply count).
    private int Carry(Kind kind, LocalPlayer me)
    {
        var items = HealItems.AllInvItems();
        return items.Where(it => Is(it, kind) && HealItems.MeetsHealReqs(it, me))
            .Sum(it => Math.Max(1, it.Count));
    }

    // The heal capacity of the stim we would pop (the best usable), from its Use-modifier health
    // stat, interpolated by QL by the item model (Item.CreateItem). Unreadable means 0 - the health
    // trigger then fires on any wound, which only makes us use a stim earlier, and the cooldown
    // keeps it at one per timer.
    private int HealCapacity(LocalPlayer me)
    {
        var it = BestUsable(Kind.Stim, me);
        if (it == null)
        {
            return 0;
        }

        return it.Modifiers.TryGetValue(SpellListType.Use, out var mods) && mods.TryGetValue(Stat.HealthChange, out var v)
            ? v
            : 0;
    }

    // The regen context for a fire line, only when a forecast is live (why the recharger/stim was
    // still needed despite it - or how much of the deficit it ate).
    private string RegenNote()
    {
        return _regenRemaining > 0
            ? $" (buffs: {_regenHealth:0} hp/s, {_regenNano:0} nano/s, {_regenRemaining:0}s left)"
            : "";
    }
}
