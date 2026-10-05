// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: EngineerPetBrain.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO;
using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.PacketConsumers;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     ENGINEER PET BRAIN - step 2 (summon + maintain + command, the simplest path: skills already
///     high enough, no buff routing yet - see PETBRAIN-DESIGN.md). Keeps ONE attack robot up and
///     commanded. A pure overlay: it casts and commands but never moves the body, so it holds no
///     movement arbiter.
///     THE ENGINEER SUMMON IS TWO-STEP (SHELLS) - ported from AOBuddy10 PetController (owner's
///     verified mechanic): the summon nano (SpawnItem, FunctionType 53064) makes a SHELL ITEM in
///     the bags, and USING the shell makes the robot. So:
///     - a usable shell already in the bags is USED first (data-driven: a shell is any inventory
///       item whose UseCriteria gates on our Profession plus a free attack-pet slot - TestNumPets,
///       Param2/1000 == 0; that excludes towers 5001 and charm critters 7001/7002);
///     - only with no shell there do we CAST a summon nano, and the shell it makes is used next
///       pass. A shell we cannot use yet holds the nano back - re-casting would only make another
///       and most robot nanos charge credits per shell.
///     The summon-nano id set is the Engineer's robot summons from engineer-pets.json (cited client
///     data, 90 robots, tower excluded); among those the character has actually learned (SpellList)
///     and can cast (MeetsUseReqs), the highest-QL one is chosen - no hardcoded ids, works for the
///     character's own learned nanos. One cast at a time (gated on IsCasting) with a per-nano recast
///     cooldown so a pet gets time to appear.
///     OE and buff-first summon (the 80% control math, the Chewy/Codedoc routing, the sustain gate)
///     are LATER steps; this one assumes the character can already cast its chosen robot.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Pet, Profession.Engineer)]
public sealed class EngineerPetBrain : PetBrain
{
    // The Engineer fields exactly one attack robot.
    private const int AttackPetTarget = 1;

    // A summon (nano cast or shell use) needs a few seconds to become a pet; re-casting/using
    // before then only makes a second shell and drains nano/credits (AOBuddy10 SummonRecastSec).
    private const double SummonRecastSec = 12.0;

    // Decide roughly once a second, not every frame.
    private const double DecideEverySec = 1.0;

    private double _sinceDecide;
    private readonly Dictionary<int, double> _summonAt = new();          // nanoId -> last cast (_clock)
    private readonly Dictionary<(int, int), double> _shellUsedAt = new(); // (shellId, slotInstance) -> last used (_clock)
    private bool _shellPending;
    private int _shellWarned;

    // Watchdog: count summon casts / shell uses since we last had a pet. If they pile up with no pet
    // appearing, something is wrong (no credits, over-equip, or the pet isn't registering on the wire) -
    // report it (throttled) instead of churning silently. Reset the moment a pet is actually up.
    private int _summonTries;
    private double _lastStuckWarnAt = double.NegativeInfinity;
    private double _lastNoCastWarnAt = double.NegativeInfinity;
    private const int SummonTriesWarn = 4;
    private const double StuckWarnEverySec = 30.0;

    private readonly HashSet<int> _robotSummonIds = new();
    private bool _warnedNoSummonData;

    // Pet-only buffs (engineer-pets.json "petBuffs"): nanos the Engineer casts ON the robot - pet AC /
    // defense / proc / scale. They land on the pet, so they never touch the player's NCU and are kept up
    // whenever a pet is out, independent of PetAutoBuff. Pet HEALS in that list are excluded at runtime
    // (they Hit Health, carry no stat Modify - owner's AOBuddy10 model). Repair/heal is a separate path.
    private readonly HashSet<int> _petBuffIds = new();
    private const double PetBuffRecastSec = 20.0;
    private readonly Dictionary<string, double> _petBuffAt = new(); // "petInstance:nanoId" -> last cast

    private readonly HuntController _hunt;
    private readonly Awareness _awareness;
    private readonly BuffCatalog _catalog;
    private readonly BuffBotController _buffBot;
    private readonly AccountInfo _config;

    private const double BuffRetrySec = 300.0; // don't re-ask the buff bot more often than this
    private double _buffAskedAt = -1e9;
    private int _loggedBuffOppFor; // the want-pet id we last logged a buff opportunity for

    // RefreshSoonSec lives on the base PetBrain (shared timer model).
    private const double MaintainEverySec = 8.0; // re-check control margin + survival about this often while a pet is up
    private const double RefreshMargin = 0.85; // refresh durable buffs before the pet's OE margin falls toward 0.80

    private double _sinceMaintain;
    private int _activeReqMc; // the MC/TS the CURRENT pet was summoned needing (0 if summoned from a shell we didn't cast)
    private int _activeReqTs;

    public EngineerPetBrain(ILogger<EngineerPetBrain> logger, ControlArbiter controlArbiter,
        HuntController hunt, Awareness awareness, BuffCatalog catalog, BuffBotController buffBot, AccountInfo config)
        : base(logger, controlArbiter)
    {
        _hunt = hunt;
        _awareness = awareness;
        _catalog = catalog;
        _buffBot = buffBot;
        _config = config;
        LoadRobotSummonIds();
        LoadPetBuffIds();
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        var attack = AttackPet(me);
        if (attack != null)
        {
            _summonTries = 0; // a pet is up - the summon watchdog resets
            // The robot is up: drive it onto the hunt target (hunt off / no target -> Follow).
            DriveAttack(me, attack, _hunt.Tick(me, _awareness, dt));

            // Post-summon: keep control (refresh a fading durable buff before OE bites) and spend freed
            // NCU on survival. Throttled, never during a heal/rest or a cast. The (short) wrangle is NOT
            // refreshed here - it was a summon-moment prop; letting it lapse IS the downshift.
            _sinceMaintain += dt;
            if (_sinceMaintain >= MaintainEverySec && !me.IsCasting
                && _controlArbiter.HasControl(ControlPriority.LowHealthNanoEmergency))
            {
                _sinceMaintain = 0;
                // Pet-only buffs first (free of player NCU, always); then control upkeep + survival
                // (PetAutoBuff-gated). One cast per pass - BuffPets returning true means it took the slot.
                if (!BuffPets(me))
                {
                    MaintainAndSurvive(me);
                }
            }

            return true;
        }

        // No robot: work on summoning one. Claim the task while establishing it so the bot gets its
        // pet before the lower-priority work (selfbuff/resupply/mission) runs.
        _sinceDecide += dt;
        if (_sinceDecide < DecideEverySec)
        {
            return true;
        }

        _sinceDecide = 0;

        if (me.IsCasting)
        {
            return true; // one cast at a time
        }

        // Don't cast a summon while heal/emergency owns the body: a summon cast stands the bot up and
        // would break an out-of-combat recharger REST (owner, 2026-10-05). IsCasting above covers a
        // heal nano in flight; this covers the SEATED rest between recharger ticks. Combat (700) is
        // left alone, so a dead pet can still be re-summoned mid-fight.
        if (!_controlArbiter.HasControl(ControlPriority.LowHealthNanoEmergency))
        {
            return true;
        }

        TrySummon(me);
        return true;
    }

    // ---- Summon -----------------------------------------------------------------------------

    private void TrySummon(LocalPlayer me)
    {
        var have = me.Pets.Count(p => p.Role == PetType.Attack);
        if (have >= AttackPetTarget)
        {
            return;
        }

        // 1) Use a shell already in the bags.
        var shell = ShellCheck(me, out var unusable);
        if (shell != null)
        {
            _shellUsedAt[(shell.Id, shell.Slot.Instance)] = _clock;
            shell.Use();
            _summonTries++;
            StuckCheck(me, $"using shell '{shell.Name}' (ql {shell.Ql})");
            _logger.LogInformation($"PET: using shell '{shell.Name}' id={shell.Id} ql={shell.Ql}.");
            return;
        }

        if (_shellPending)
        {
            return; // a shell we just used is still becoming a pet
        }

        if (unusable != null)
        {
            if (_shellWarned != unusable.Id)
            {
                _shellWarned = unusable.Id;
                _logger.LogInformation(
                    $"PET: shell '{unusable.Name}' ql={unusable.Ql} is in the bags but its requirements aren't met - not casting another.");
            }

            return;
        }

        // 2) No shell: cast the best robot summon we know and can cast (it makes a shell).
        CastBestSummon(me);
    }

    private void CastBestSummon(LocalPlayer me)
    {
        if (_robotSummonIds.Count == 0)
        {
            if (!_warnedNoSummonData)
            {
                _warnedNoSummonData = true;
                _logger.LogWarning(
                    "PET: no robot summon ids loaded (engineer-pets.json) - can only use shells already in the bags.");
            }

            return;
        }

        var learned = me.SpellList ?? Array.Empty<int>();
        NanoItem? best = null;
        foreach (var nanoId in learned)
        {
            if (!_robotSummonIds.Contains(nanoId))
            {
                continue;
            }

            if (!ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            // ignorePetLimit: rank on skills/level, not on a pet slot being free (it is - we have none).
            bool castable;
            try
            {
                castable = ni.MeetsUseReqs(me, false, true);
            }
            catch
            {
                castable = false;
            }

            if (!castable)
            {
                continue;
            }

            if (best == null || ni.Ql > best.Ql)
            {
                best = ni;
            }
        }

        // BUFF-FIRST (4b): is a BETTER robot learned but out of reach only for lack of MC/TS? Ask the
        // buff bot first and wait, rather than summoning a weaker one now ("not before").
        if (BuffFirst(me, best))
        {
            return;
        }

        if (best == null)
        {
            // Nothing castable now (skills/level/credits/expansion) and no buff path taken - say so, so
            // it is visible via the log instead of a silent no-op. Throttled; left to our own devices.
            if (_clock - _lastNoCastWarnAt > StuckWarnEverySec)
            {
                _lastNoCastWarnAt = _clock;
                var mc = me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0;
                var ts = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
                _logger.LogWarning(
                    $"PET: no robot castable at MC {mc}/TS {ts} (level/credits/expansion), and no buffs available - " +
                    "holding without a pet. Raise skills, enable PetAutoBuff near a buff bot, or check credits.");
            }

            return;
        }

        if (_summonAt.TryGetValue(best.Id, out var last) && _clock - last < SummonRecastSec)
        {
            return; // just cast this one - wait for its shell
        }

        _summonAt[best.Id] = _clock;
        (_activeReqMc, _activeReqTs) = PetReq(best); // remember what this pet needs, for control maintenance
        me.Cast(best.Id);
        _summonTries++;
        StuckCheck(me, $"casting '{best.Name}' (ql {best.Ql})");
        _logger.LogInformation($"PET: summon - casting '{best.Name}' ({best.Id}, ql {best.Ql}) to make a shell.");
    }

    /// <summary>
    ///     Buff-first + sustain-gate: reach for the BIGGEST robot we could DURABLY control at 80% once
    ///     buffed (not merely summon), and get the buffs for it - self-casting what we can, asking the
    ///     bot for the rest, and skipping buffs already stably running. Returns true while working toward
    ///     the buffs (the caller waits); false when there is nothing better to hold than we can already
    ///     cast, or the owner has not enabled auto-buffing - then the caller summons the best it can now.
    /// </summary>
    private bool BuffFirst(LocalPlayer me, NanoItem? castableBest)
    {
        var paid = IsPaid(me);
        var baseMc = UnbuffedBase(me, Stat.MaterialCreation);
        var baseTs = UnbuffedBase(me, Stat.SpaceTime);

        // Sustain-gate: the biggest learned robot we can hold at 80% durably once buffed - NOT merely the
        // highest QL. A pet we could summon at peak but not keep is skipped in favour of a smaller one.
        var want = BestControllableRobot(me, baseMc, baseTs, paid);
        if (want == null || (castableBest != null && want.Ql <= castableBest.Ql))
        {
            return false; // nothing better that we could hold than what we can already cast
        }

        var (reqMc, reqTs) = PetReq(want);
        if (OeMargin(me, reqMc, reqTs) >= 1.0)
        {
            return false; // already castable now (no buffs needed)
        }

        var plan = _catalog.BuildControlPlan(me, baseMc, baseTs, reqMc, reqTs, paid, "Engineer");
        if (!plan.CanControl)
        {
            return false; // BestControllableRobot already filtered on this, but stay safe
        }

        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            if (_loggedBuffOppFor != want.Id)
            {
                _loggedBuffOppFor = want.Id;
                var who = _config.BuffBotName.Length > 0 ? _config.BuffBotName : "the buff bot";
                _logger.LogInformation(
                    $"PET: '{want.Name}' (ql {want.Ql}) needs MC {reqMc}/TS {reqTs} - out of reach now but holdable " +
                    $"at {plan.MarginPct:F0}% once buffed; buffs would unlock it ('buffs pet' near {who}, or set PetAutoBuff).");
            }

            return false; // the owner controls buffing; summon the best we can now
        }

        // The buffs we still need = the plan minus the strains already stably running (timer-aware).
        var learned = me.SpellList ?? Array.Empty<int>();
        var actions = _catalog.RoutePlan(plan, "Engineer", _config.BuffBotName, learned.Contains, StableStrains(me));

        // Self-cast what we can ourselves (Generics / Engineer own-prof), one per pass (the cast gate is
        // upstream in PolicyTick). This covers Composite Nano Expertise / Attribute Boost when learned.
        var self = actions.FirstOrDefault(a => a.Source == BuffCatalog.BuffSource.SelfCast && !BuffUp(me, a.SelfCastNanoId));
        if (self != null)
        {
            me.Cast(self.SelfCastNanoId);
            _logger.LogInformation($"PET: self-casting {self.Name} ({self.SelfCastNanoId}) toward holding '{want.Name}'.");
            return true;
        }

        var tells = actions.Where(a => a.Source == BuffCatalog.BuffSource.BuffBot).Select(a => a.Tell).ToList();
        if (tells.Count == 0)
        {
            return false; // nothing left to request - summon now
        }

        if (_buffBot.Active)
        {
            return true; // a buff session is running - wait for it
        }

        if (_clock - _buffAskedAt >= BuffRetrySec)
        {
            if (_buffBot.RequestBuffs(tells, $"pet-first for {want.Name}"))
            {
                _buffAskedAt = _clock;
                _logger.LogInformation(
                    $"PET: asking {_config.BuffBotName} for [{string.Join(" ", tells)}] to hold '{want.Name}' " +
                    $"(MC {reqMc}/TS {reqTs}, durable {plan.DurableMc}/{plan.DurableTs} = {plan.MarginPct:F0}%).");
                return true;
            }

            return false; // couldn't start (not near the bot) - summon what we can
        }

        return _clock - _buffAskedAt < _config.PetBuffWaitSeconds; // hold for the wait window
    }

    // ---- Buff state (sustain-gate, maintenance) ---------------------------------------------
    // IsPaid / BuffUp / UnbuffedBase / StableStrains are shared primitives on the base PetBrain - every
    // pet profession reads state the same way; only the pieces below (which robots, their MC/TS reqs,
    // the shell summon, pet buffs) are Engineer-specific.

    /// <summary>
    ///     The sustain-gate: the highest-QL learned robot we could DURABLY control at the 80% floor once
    ///     buffed (BuildControlPlan.CanControl), from the unbuffed base. The pet we KEEP is chosen by what
    ///     we can hold, never by what a wrangle could briefly summon.
    /// </summary>
    private NanoItem? BestControllableRobot(LocalPlayer me, int baseMc, int baseTs, bool paid)
    {
        NanoItem? best = null;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!_robotSummonIds.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            var (reqMc, reqTs) = PetReq(ni);
            if (!_catalog.BuildControlPlan(me, baseMc, baseTs, reqMc, reqTs, paid, "Engineer").CanControl)
            {
                continue;
            }

            if (best == null || ni.Ql > best.Ql)
            {
                best = ni;
            }
        }

        return best;
    }

    /// <summary>
    ///     While a pet is up: keep it controlled (refresh a fading DURABLE buff before the OE margin falls
    ///     toward 0.80) and spend freed NCU on survival. Gated behind PetAutoBuff - off (the conservative
    ///     first run), this does nothing and the pet simply rides its summon buffs. The wrangle is never
    ///     refreshed here; letting it lapse is the intended downshift.
    /// </summary>
    private void MaintainAndSurvive(LocalPlayer me)
    {
        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            return; // owner controls buffing
        }

        var paid = IsPaid(me);
        var learned = me.SpellList ?? Array.Empty<int>();

        // 1) Control upkeep: if the current pet's OE margin is nearing the floor, refresh the durable set.
        if ((_activeReqMc > 0 || _activeReqTs > 0) && OeMargin(me, _activeReqMc, _activeReqTs) < RefreshMargin)
        {
            var plan = _catalog.BuildControlPlan(me, UnbuffedBase(me, Stat.MaterialCreation),
                UnbuffedBase(me, Stat.SpaceTime), _activeReqMc, _activeReqTs, paid, "Engineer");
            if (TryApply(me, _catalog.RoutePlan(plan, "Engineer", _config.BuffBotName, learned.Contains, StableStrains(me)),
                    "pet control refresh"))
            {
                return;
            }
        }

        // 2) Survival: fill whatever NCU is now free (the wrangle has lapsed) with HP/HoT/AC/shield.
        var free = (me.TryGetStat(Stat.MaxNCU, out var mx) ? mx : 0) - (me.TryGetStat(Stat.CurrentNCU, out var cu) ? cu : 0);
        if (free > 0)
        {
            TryApply(me, _catalog.SurvivalFill(me, free, paid, "Engineer", _config.BuffBotName, learned.Contains,
                StableStrains(me)), "pet-tank survival");
        }
    }

    // Do the first not-yet-up action from a routed list: self-cast it, or ask the bot (throttled). Returns
    // true when it acted (cast or sent a request) so the caller stops for this pass.
    private bool TryApply(LocalPlayer me, List<BuffCatalog.BuffAction> actions, string why)
    {
        var self = actions.FirstOrDefault(a => a.Source == BuffCatalog.BuffSource.SelfCast && !BuffUp(me, a.SelfCastNanoId));
        if (self != null)
        {
            me.Cast(self.SelfCastNanoId);
            _logger.LogInformation($"PET: self-casting {self.Name} ({self.SelfCastNanoId}) - {why}.");
            return true;
        }

        var tells = actions.Where(a => a.Source == BuffCatalog.BuffSource.BuffBot).Select(a => a.Tell).ToList();
        if (tells.Count == 0 || _buffBot.Active || _clock - _buffAskedAt < BuffRetrySec)
        {
            return _buffBot.Active; // waiting on a running session counts as acted
        }

        if (_buffBot.RequestBuffs(tells, why))
        {
            _buffAskedAt = _clock;
            _logger.LogInformation($"PET: asking {_config.BuffBotName} for [{string.Join(" ", tells)}] - {why}.");
            return true;
        }

        return false;
    }

    private NanoItem? BestLearnedRobot(LocalPlayer me)
    {
        NanoItem? best = null;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!_robotSummonIds.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            if (best == null || ni.Ql > best.Ql)
            {
                best = ni;
            }
        }

        return best;
    }

    // A robot summon's MC/TS requirement, read from its cast criteria (GreaterThan means stat > N, so
    // the requirement is N + 1). 0 when the nano has no such criterion.
    private static (int Mc, int Ts) PetReq(NanoItem nano)
    {
        var mc = 0;
        var ts = 0;
        if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            foreach (var c in use)
            {
                if (c.Operator != UseCriteriaOperator.GreaterThan)
                {
                    continue;
                }

                if (c.Param1 == (int)Stat.MaterialCreation)
                {
                    mc = Math.Max(mc, c.Param2 + 1);
                }
                else if (c.Param1 == (int)Stat.SpaceTime)
                {
                    ts = Math.Max(ts, c.Param2 + 1);
                }
            }
        }

        return (mc, ts);
    }

    // ---- Shells -----------------------------------------------------------------------------

    /// <summary>
    ///     The best shell in the bags we can use now (highest QL), or null; <paramref name="unusable" />
    ///     is one we hold but cannot use yet. Sets <see cref="_shellPending" /> while a shell we used is
    ///     still waiting to become a pet. Only inventory slots count - never bank or a worn slot.
    /// </summary>
    private Item? ShellCheck(LocalPlayer me, out Item? unusable)
    {
        unusable = null;
        _shellPending = false;
        if (Inventory.Items == null || !me.TryGetStat(Stat.Profession, out var prof))
        {
            return null;
        }

        Item? best = null;
        foreach (var i in Inventory.Items)
        {
            if (i == null || i.Slot.Type != IdentityType.Inventory || !IsOurShell(i, prof))
            {
                continue;
            }

            if (_shellUsedAt.TryGetValue((i.Id, i.Slot.Instance), out var at) && _clock - at < SummonRecastSec)
            {
                _shellPending = true;
                continue;
            }

            bool ok;
            try
            {
                ok = i.MeetsUseReqs(me, false, false);
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                if (unusable == null || i.Ql > unusable.Ql)
                {
                    unusable = i;
                }

                continue;
            }

            if (best == null || i.Ql > best.Ql)
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    ///     A shell is known by its requirements, not its name (AOBuddy10, item data 2026-09-23): its
    ///     UseCriteria gates on OUR profession plus a free ATTACK-pet slot (TestNumPets with
    ///     Param2/1000 == 0 - that excludes towers 5001 and charm critters 7001/7002).
    /// </summary>
    private static bool IsOurShell(Item i, int profession)
    {
        if (i?.Criteria == null || !i.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            return false;
        }

        return use.Any(c => c.Operator == UseCriteriaOperator.TestNumPets && c.Param2 / 1000 == 0)
               && use.Any(c => c.Operator == UseCriteriaOperator.EqualTo
                               && c.Param1 == (int)Stat.Profession && c.Param2 == profession);
    }

    // The summon watchdog: several attempts with no pet appearing means something is wrong (no credits,
    // over-equip, or the pet not registering on the wire). Report it - throttled - so it surfaces in the
    // log for tuning. We keep trying: a pet may yet appear or skills may rise (always recoverable).
    private void StuckCheck(LocalPlayer me, string what)
    {
        if (_summonTries < SummonTriesWarn || _clock - _lastStuckWarnAt < StuckWarnEverySec)
        {
            return;
        }

        _lastStuckWarnAt = _clock;
        var mc = me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0;
        var ts = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
        _logger.LogWarning(
            $"PET: {_summonTries} summon attempts, still no pet ({what}; MC {mc}/TS {ts}). Likely no credits, " +
            "over-equip, or the pet isn't registering on the wire - still trying.");
    }

    // ---- Pet-only buffs (cast on the robot; no player NCU) ----------------------------------

    /// <summary>
    ///     Keep the robot's own buffs up (AC / defense / proc / scale) - the pet-targeted nanos from
    ///     engineer-pets.json. Per NanoLine, the best learned nano castable ON this pet (its own target
    ///     reqs - NPCFamily / Breed - decide), cast only if the pet lacks an equal-or-better buff of that
    ///     line (checked against the pet's own buff list + a refresh margin), in range, past the recast
    ///     guard. One cast per pass; returns true when it took the cast slot. Pet HEALS are filtered out
    ///     (they Hit Health and carry no stat Modify) - repair is a separate path.
    /// </summary>
    private bool BuffPets(LocalPlayer me)
    {
        var spells = me.SpellList;
        if (spells == null || spells.Length == 0 || _petBuffIds.Count == 0)
        {
            return false;
        }

        var pets = me.Pets?.Where(p => p.IsCombatPet).ToList();
        if (pets == null || pets.Count == 0)
        {
            return false;
        }

        // Learned pet-buff nanos that actually MODIFY pet stats (heals Hit Health -> empty Modifies ->
        // skipped), grouped per NanoLine, best StackingOrder first.
        var byLine = new Dictionary<NanoLine, List<NanoItem>>();
        foreach (var id in spells)
        {
            if (!_petBuffIds.Contains(id))
            {
                continue;
            }

            var np = NanoLibrary.Find(id);
            if (np == null || np.Modifies.Count == 0 || !ItemData.Find(id, out NanoItem ni) || ni == null)
            {
                continue;
            }

            if (!byLine.TryGetValue(ni.NanoLine, out var list))
            {
                byLine[ni.NanoLine] = list = new List<NanoItem>();
            }

            list.Add(ni);
        }

        foreach (var list in byLine.Values)
        {
            list.Sort((a, b) => (b.StackingOrder & 0xFFFFF).CompareTo(a.StackingOrder & 0xFFFFF));
        }

        foreach (var pet in pets)
        {
            foreach (var kv in byLine)
            {
                var best = kv.Value.FirstOrDefault(ni => CastableOnPet(ni, pet));
                if (best == null || !PetNeedsBuff(pet, best))
                {
                    continue;
                }

                var key = pet.Identity.Instance + ":" + best.Id;
                if (_petBuffAt.TryGetValue(key, out var last) && _clock - last < PetBuffRecastSec)
                {
                    continue;
                }

                if (best.Range > 0 && me.DistanceFrom(pet) > best.Range)
                {
                    continue; // wait until it's in reach
                }

                _petBuffAt[key] = _clock;
                me.Cast(pet.Identity, best.Id);
                _logger.LogInformation(
                    $"PET: buffing '{best.Name}' [{best.Id}] ({kv.Key}) on {pet.Name}#{pet.Identity.Instance}.");
                return true; // one cast per pass
            }
        }

        return false;
    }

    private static bool CastableOnPet(NanoItem ni, NpcChar pet)
    {
        try
        {
            return ni.MeetsUseReqs(pet, false, false);
        }
        catch
        {
            return false;
        }
    }

    // Missing, or no equal/better buff of the line is up, or the one up is about to run out (refresh
    // margin capped at a quarter of its duration so short buffs aren't recast the instant they land).
    private static bool PetNeedsBuff(NpcChar pet, NanoItem best)
    {
        var up = pet.Buffs?.FirstOrDefault(b => b.Id == best.Id
            || (b.NanoItem != null && b.NanoItem.NanoLine == best.NanoLine
                && b.NanoItem.StackingOrder >= best.StackingOrder));
        if (up == null)
        {
            return true;
        }

        var remaining = up.Cooldown?.RemainingTime ?? -1;
        if (remaining < 0)
        {
            return false; // up, timer unknown - leave it
        }

        return remaining < Math.Min(30.0, best.TotalTime / 4.0);
    }

    // ---- Data -------------------------------------------------------------------------------

    /// <summary>
    ///     The Engineer's pet-targeted buff nano ids from engineer-pets.json ("petBuffs"). Loaded whole;
    ///     BuffPets filters to the ones that modify pet stats (excludes the Hit-based pet heals) and that
    ///     the character has learned. Best-effort - a missing file just leaves pet-buffing off.
    /// </summary>
    private void LoadPetBuffIds()
    {
        try
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData", "profiles", "engineer-pets.json");
            if (!File.Exists(file))
            {
                return;
            }

            var doc = JObject.Parse(File.ReadAllText(file));
            foreach (var b in doc["petBuffs"] ?? new JArray())
            {
                var id = (int?)b["id"];
                if (id.HasValue && id.Value > 0)
                {
                    _petBuffIds.Add(id.Value);
                }
            }

            _logger.LogInformation($"PET: {_petBuffIds.Count} Engineer pet-buff ids loaded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PET: failed to read engineer-pets.json pet buffs - pet-buffing disabled.");
        }
    }

    /// <summary>
    ///     The Engineer's robot summon nano ids from GameData/profiles/engineer-pets.json (cited
    ///     client data). The Jamming Tower (a deployable, not an attack robot) is excluded by its
    ///     role. Best-effort: a missing file leaves the set empty and the brain falls back to using
    ///     shells already in the bags.
    /// </summary>
    private void LoadRobotSummonIds()
    {
        try
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData", "profiles", "engineer-pets.json");
            if (!File.Exists(file))
            {
                _logger.LogWarning($"PET: {file} not found - robot summon by nano is disabled (shells still work).");
                return;
            }

            var doc = JObject.Parse(File.ReadAllText(file));
            foreach (var pt in doc["petTypes"] ?? new JArray())
            {
                var role = (string?)pt["role"] ?? "";
                if (role.Contains("Tower", StringComparison.OrdinalIgnoreCase)
                    || role.Contains("Deployable", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var p in pt["progression"] ?? new JArray())
                {
                    var id = (int?)p["id"];
                    if (id.HasValue && id.Value > 0)
                    {
                        _robotSummonIds.Add(id.Value);
                    }
                }
            }

            _logger.LogInformation($"PET: {_robotSummonIds.Count} Engineer robot summon ids loaded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PET: failed to read engineer-pets.json - robot summon by nano disabled.");
        }
    }
}
