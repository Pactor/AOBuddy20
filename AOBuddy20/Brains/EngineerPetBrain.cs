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
    private const double ShellDeleteCooldownSec = 5.0; // don't re-send a delete while the shell is still disappearing

    private readonly Dictionary<int, double> _summonAt = new();          // nanoId -> last cast (_clock)
    private readonly Dictionary<(int, int), double> _shellUsedAt = new(); // (shellId, slotInstance) -> last used (_clock)
    private readonly Dictionary<(int, int), double> _shellDeletedAt = new(); // (shellId, slotInstance) -> last deleted (_clock)
    private readonly Dictionary<(int, int), double> _crystalLearnedAt = new(); // (crystalId, slotInstance) -> last Use() (_clock)
    private const double CrystalLearnCooldownSec = 10.0; // learning uploads the nano; don't re-Use while it registers
    private int _loggedLearnOppFor; // the crystal id we last logged a learn opportunity for
    private bool _shellPending;
    private int _shellWarned;

    // Watchdog: count summon casts / shell uses since we last had a pet. If they pile up with no pet
    // appearing, something is wrong (no credits, over-equip, or the pet isn't registering on the wire) -
    // report it (throttled) instead of churning silently. Reset the moment a pet is actually up.
    private int _summonTries;
    private double _lastStuckWarnAt = double.NegativeInfinity;
    private double _lastNoCastWarnAt = double.NegativeInfinity;
    private double _lastNarrateAt = double.NegativeInfinity;
    private double _lastWalkLogAt = double.NegativeInfinity;
    private bool _dryArrived; // dry run: are we standing at the buff spot (narrate from here)?
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
        // DRY RUN: WALK to the dimension's buff spot, stand there, then narrate what it WOULD do - but
        // take no buff/summon/learn/cast action. Owner's first-run safety: the only real movement is the
        // walk to the bot (4a.1b), so the owner can watch it path to the right buffer (Chewy on RubiKa,
        // Codedoc on RubiKa2019) before anything irreversible is enabled.
        if (_config.PetDryRun)
        {
            DryRun(me);
            return false; // never hold brain control in dry run (the walk rides the Travel goal)
        }

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

        // LEARN-FIRST: an un-learned robot crystal in the bags that beats our best learned pet and we can
        // durably control is learned (uploaded) before summoning - buffing toward its req first if needed.
        // Learning uploads the nano and consumes the crystal (irreversible), so it is gated on actually
        // meeting the use reqs and on durable control, never on a brief wrangle peak. (Dormant in dry run:
        // PolicyTick returns before TrySummon when PetDryRun is set.)
        if (TryLearnCrystal(me))
        {
            return;
        }

        var targetQl = BestCastableSummonQl(me); // the best pet we could cast right now (0 if none)
        var shell = ShellCheck(me, out var unusable);

        // SHELL CLEANUP (owner's mechanic: an old shell must be gone before a new one can be summoned).
        // If a shell in the bags is a LESSER pet than one we could cast now, bin it and cast the better.
        // Only ever our OWN robot shells (ShellCheck/IsOurShell identified them) - never your items.
        if (shell != null && targetQl > shell.Ql)
        {
            DeleteStray(shell, $"inferior shell ql{shell.Ql} < castable ql{targetQl}");
            return;
        }

        // 1) Use the shell when it is already the best we would make.
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
            // An unusable shell INFERIOR to a pet we can cast is just in the way - bin it, then cast the
            // better one. A BETTER unusable shell (we simply can't use it yet) is KEPT: buffs/skills may
            // unlock it, and we must not delete a pet we cannot replace.
            if (targetQl > 0 && targetQl >= unusable.Ql)
            {
                DeleteStray(unusable, $"unusable inferior shell ql{unusable.Ql}, casting ql{targetQl} instead");
                return;
            }

            if (_shellWarned != unusable.Id)
            {
                _shellWarned = unusable.Id;
                _logger.LogInformation(
                    $"PET: shell '{unusable.Name}' ql={unusable.Ql} is in the bags, requirements not met and nothing better castable - keeping it.");
            }

            return;
        }

        // 2) No shell: cast the best robot summon we know and can cast (it makes a shell).
        CastBestSummon(me);
    }

    // ---- Learn a crystal from the bags ------------------------------------------------------

    /// <summary>
    ///     Engineer robot-summon crystals in the bags/inventory (pairs of the inventory item and its
    ///     resolved data). A crystal is a DummyItem whose UseCriteria gates on MC+TS+Profession==Engineer -
    ///     the same detection the dry run reports. Used only to LEARN an upgrade we don't yet know.
    /// </summary>
    private List<(Item Inv, ItemBase Data)> BagsCrystals(LocalPlayer me)
    {
        var list = new List<(Item, ItemBase)>();

        void Consider(Item it)
        {
            if (it == null || !ItemData.Find(it.Id, out ItemBase ib) || ib == null)
            {
                return;
            }

            var (mc, ts) = PetReq(ib);
            if (mc > 0 && ts > 0 && IsEngineerSummonCrystal(ib))
            {
                list.Add((it, ib));
            }
        }

        foreach (var it in Inventory.Items ?? Enumerable.Empty<Item>())
        {
            Consider(it);
        }

        foreach (var c in Inventory.Containers ?? Enumerable.Empty<Container>())
        {
            foreach (var it in c.Items ?? Enumerable.Empty<Item>())
            {
                Consider(it);
            }
        }

        return list;
    }

    /// <summary>
    ///     Learn-first: if the bags hold a robot crystal that BEATS our best learned pet and we could
    ///     DURABLY control once buffed, learn it (upload) before summoning - meeting its req first by
    ///     buffing if PetAutoBuff is on, else logging the opportunity. Returns true when it acted or is
    ///     working toward it (caller stops this pass); false to fall through to the normal summon. Learning
    ///     is irreversible (consumes the crystal) so it fires only when the use reqs are actually met.
    /// </summary>
    private bool TryLearnCrystal(LocalPlayer me)
    {
        var crystals = BagsCrystals(me);
        if (crystals.Count == 0)
        {
            return false;
        }

        var paid = IsPaid(me);
        var baseMc = UnbuffedBase(me, Stat.MaterialCreation);
        var baseTs = UnbuffedBase(me, Stat.SpaceTime);
        var bestLearnedQl = BestLearnedRobot(me)?.Ql ?? 0;

        // The best upgrade crystal we could hold durably at the floor once buffed (never a wrangle peak).
        (Item Inv, ItemBase Data)? pick = null;
        foreach (var c in crystals.OrderByDescending(x => x.Data.Ql))
        {
            if (c.Data.Ql <= bestLearnedQl)
            {
                continue; // not an upgrade over what we already know
            }

            var (rmc, rts) = PetReq(c.Data);
            if (!_catalog.BuildControlPlan(me, baseMc, baseTs, rmc, rts, paid, "Engineer").CanControl)
            {
                continue; // couldn't keep it controlled - not worth consuming the crystal
            }

            pick = c;
            break;
        }

        if (pick == null)
        {
            return false;
        }

        var (reqMc, reqTs) = PetReq(pick.Value.Data);
        var plan = _catalog.BuildControlPlan(me, baseMc, baseTs, reqMc, reqTs, paid, "Engineer");

        // Reqs met right now (we have buffed enough, or the owner/bot buffed us) -> learn it.
        bool meets;
        try
        {
            meets = pick.Value.Data.MeetsUseReqs(me, false, false);
        }
        catch
        {
            meets = false;
        }

        if (meets)
        {
            LearnCrystal(pick.Value.Inv);
            return true;
        }

        // Not castable yet. Buff toward the learn req the same way the summon sustain-gate does, when the
        // owner has enabled auto-buffing; otherwise leave buffing to the owner and summon what we can now.
        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_catalog.BotName))
        {
            if (_loggedLearnOppFor != pick.Value.Inv.Id)
            {
                _loggedLearnOppFor = pick.Value.Inv.Id;
                var who = _catalog.BotName.Length > 0 ? _catalog.BotName : "the buff bot";
                _logger.LogInformation(
                    $"PET: crystal '{pick.Value.Data.Name}' (ql {pick.Value.Data.Ql}) needs MC {reqMc}/TS {reqTs} to learn - " +
                    $"holdable at {plan.MarginPct:F0}% once buffed; buffs would unlock it ('buffs pet' near {who}, or set PetAutoBuff).");
            }

            return false;
        }

        return DriveBuffsToward(me, plan, $"learning '{pick.Value.Data.Name}'",
            $"to learn {pick.Value.Data.Name}",
            $"to learn '{pick.Value.Data.Name}' (needs MC {reqMc}/TS {reqTs}, durable {plan.DurableMc}/{plan.DurableTs} = {plan.MarginPct:F0}%)");
    }

    // Upload the crystal's nano (right-click / Use). Throttled per item so we don't re-Use while the learn
    // registers. Irreversible - only ever called once the use reqs are confirmed met (TryLearnCrystal).
    private void LearnCrystal(Item crystal)
    {
        if (_crystalLearnedAt.TryGetValue((crystal.Id, crystal.Slot.Instance), out var at) && _clock - at < CrystalLearnCooldownSec)
        {
            return;
        }

        _crystalLearnedAt[(crystal.Id, crystal.Slot.Instance)] = _clock;
        crystal.Use();
        _logger.LogInformation($"PET: learning crystal '{crystal.Name}' id={crystal.Id} ql={crystal.Ql} - uploading the nano, then I will summon it.");
    }

    // The QL of the best robot we could CAST right now (learned + reqs met), 0 if none. Used to decide
    // whether a shell in the bags is worth keeping or should be binned for a better pet. No side effects.
    private int BestCastableSummonQl(LocalPlayer me)
    {
        var best = 0;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!_robotSummonIds.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            bool castable;
            try
            {
                castable = ni.MeetsUseReqs(me, false, true); // ignorePetLimit: rank on skill/level, not slot
            }
            catch
            {
                castable = false;
            }

            if (castable && ni.Ql > best)
            {
                best = ni.Ql;
            }
        }

        return best;
    }

    // Delete one of OUR leftover robot shells (owner authorized: "recover and act right"). Throttled per
    // item so we don't re-send while it is disappearing. Shells are cheap/re-summonable - never your gear.
    private void DeleteStray(Item shell, string why)
    {
        if (_shellDeletedAt.TryGetValue((shell.Id, shell.Slot.Instance), out var at) && _clock - at < ShellDeleteCooldownSec)
        {
            return;
        }

        _shellDeletedAt[(shell.Id, shell.Slot.Instance)] = _clock;
        shell.Delete();
        _logger.LogInformation($"PET: deleting stray shell '{shell.Name}' id={shell.Id} ql={shell.Ql} - {why}.");
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

        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_catalog.BotName))
        {
            if (_loggedBuffOppFor != want.Id)
            {
                _loggedBuffOppFor = want.Id;
                var who = _catalog.BotName.Length > 0 ? _catalog.BotName : "the buff bot";
                _logger.LogInformation(
                    $"PET: '{want.Name}' (ql {want.Ql}) needs MC {reqMc}/TS {reqTs} - out of reach now but holdable " +
                    $"at {plan.MarginPct:F0}% once buffed; buffs would unlock it ('buffs pet' near {who}, or set PetAutoBuff).");
            }

            return false; // the owner controls buffing; summon the best we can now
        }

        // The buffs we still need = the plan minus the strains already stably running (timer-aware).
        return DriveBuffsToward(me, plan, $"holding '{want.Name}'",
            $"pet-first for {want.Name}",
            $"to hold '{want.Name}' (MC {reqMc}/TS {reqTs}, durable {plan.DurableMc}/{plan.DurableTs} = {plan.MarginPct:F0}%)");
    }

    /// <summary>
    ///     Work the buff plan one step per pass toward a goal (holding a pet, or meeting a crystal's learn
    ///     req): self-cast the first own-castable buff not yet up; otherwise ask the buff bot (throttled).
    ///     Returns true while buffs are being worked (caller waits), false when there is nothing we can do
    ///     to close the gap (no self-cast left, no bot reachable) - the caller then acts with what it has.
    ///     Shared by the summon sustain-gate and the crystal-learn gate so there is ONE buff path.
    /// </summary>
    private bool DriveBuffsToward(LocalPlayer me, BuffCatalog.ControlPlan plan, string goal, string askReason, string askDetail)
    {
        var learned = me.SpellList ?? Array.Empty<int>();
        var actions = _catalog.RoutePlan(plan, "Engineer", _catalog.BotName, learned.Contains, StableStrains(me));

        // Self-cast what we can ourselves (Generics / Engineer own-prof), one per pass (the cast gate is
        // upstream in PolicyTick). This covers Composite Nano Expertise / Attribute Boost when learned.
        var self = actions.FirstOrDefault(a => a.Source == BuffCatalog.BuffSource.SelfCast && !BuffUp(me, a.SelfCastNanoId));
        if (self != null)
        {
            me.Cast(self.SelfCastNanoId);
            _logger.LogInformation($"PET: self-casting {self.Name} ({self.SelfCastNanoId}) toward {goal}.");
            return true;
        }

        // The bot steps to acquire = the routed bot actions, minus the short wrangle unless enabled (the
        // durable set is requested first; the wrangle is a summon-moment prop added only when summoning).
        var botSteps = actions
            .Where(a => a.Source == BuffCatalog.BuffSource.BuffBot && (_config.BuffIncludeWrangle || !a.IsWrangleStep))
            .ToList();
        if (botSteps.Count == 0)
        {
            return false; // nothing left to request - act now
        }

        if (_buffBot.Active)
        {
            return true; // a buff session is running - wait for it
        }

        if (_clock - _buffAskedAt >= BuffRetrySec)
        {
            if (_buffBot.RequestBuffs(botSteps, askReason))
            {
                _buffAskedAt = _clock;
                _logger.LogInformation($"PET: asking {_catalog.BotName} for [{string.Join(" ", botSteps.Select(a => a.Tell))}] {askDetail}.");
                return true;
            }

            return false; // couldn't start (not near the bot) - act with what we can
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
        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_catalog.BotName))
        {
            return; // owner controls buffing
        }

        var paid = IsPaid(me);
        var learned = me.SpellList ?? Array.Empty<int>();

        // 1) Control upkeep: refresh the durable set when the OE margin is nearing the floor (a skill sagged)
        // OR when a control buff is due under the 15-min lead (StableStrains drops it from the request list, so
        // RoutePlan returns it to refresh). Either reason tops the pet's control back up before it lapses.
        if (_activeReqMc > 0 || _activeReqTs > 0)
        {
            var plan = _catalog.BuildControlPlan(me, UnbuffedBase(me, Stat.MaterialCreation),
                UnbuffedBase(me, Stat.SpaceTime), _activeReqMc, _activeReqTs, paid, "Engineer");
            var actions = _catalog.RoutePlan(plan, "Engineer", _catalog.BotName, learned.Contains, StableStrains(me));
            var marginLow = OeMargin(me, _activeReqMc, _activeReqTs) < RefreshMargin;
            if ((marginLow || actions.Count > 0) && TryApply(me, actions, "pet control refresh"))
            {
                return;
            }
        }

        // 2) Survival: fill whatever NCU is now free (the wrangle has lapsed) with HP/HoT/AC/shield.
        var free = (me.TryGetStat(Stat.MaxNCU, out var mx) ? mx : 0) - (me.TryGetStat(Stat.CurrentNCU, out var cu) ? cu : 0);
        if (free > 0)
        {
            TryApply(me, _catalog.SurvivalFill(me, free, paid, "Engineer", _catalog.BotName, learned.Contains,
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

        var botSteps = actions
            .Where(a => a.Source == BuffCatalog.BuffSource.BuffBot && (_config.BuffIncludeWrangle || !a.IsWrangleStep))
            .ToList();
        if (botSteps.Count == 0 || _buffBot.Active || _clock - _buffAskedAt < BuffRetrySec)
        {
            return _buffBot.Active; // waiting on a running session counts as acted
        }

        if (_buffBot.RequestBuffs(botSteps, why))
        {
            _buffAskedAt = _clock;
            _logger.LogInformation($"PET: asking {_catalog.BotName} for [{string.Join(" ", botSteps.Select(a => a.Tell))}] - {why}.");
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
    private static (int Mc, int Ts) PetReq(ItemBase nano)
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

    /// <summary>
    ///     Every skill/ability requirement on the crystal's UseCriteria, formatted for the dry-run report
    ///     (e.g. "MaterialCreation>=554, SpaceTime>=554, Level>=50"). GreaterThan reqs are inclusive-of
    ///     value+1 in AO; we show the real threshold. Not Engineer-specific - dumps whatever gates exist
    ///     (an MP pet shows MatMet/BioMet/PsychoModi) so nothing is hidden.
    /// </summary>
    private static string ReqSkills(ItemBase nano)
    {
        if (nano.Criteria == null || !nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            return "(no use reqs)";
        }

        var parts = new List<string>();
        foreach (var c in use)
        {
            // Skip the structural profession/expansion gates - those are reported elsewhere; show stat thresholds.
            if (c.Operator == UseCriteriaOperator.GreaterThan)
            {
                parts.Add($"{(Stat)c.Param1}>={c.Param2 + 1}");
            }
            else if (c.Operator == UseCriteriaOperator.EqualTo && (c.Param1 == (int)Stat.Profession || c.Param1 == 368))
            {
                parts.Add($"Profession=={(Profession)c.Param2}");
            }
        }

        return parts.Count == 0 ? "(no use reqs)" : string.Join(", ", parts);
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

    // ---- Dry run: walk to the buff spot, then look and narrate ------------------------------

    /// <summary>
    ///     Dry run top (PetDryRun): first WALK to the dimension's buff spot (4a.1b) - the body rides the
    ///     MovementController's Travel goal there; the brain holds no control. Only once standing at the
    ///     spot do we narrate the plan (NarrateFind). Nothing here buffs, summons, learns or casts: the
    ///     walk is the only real action, so the owner can confirm the pathing to the right buffer first.
    /// </summary>
    private void DryRun(LocalPlayer me)
    {
        // No spot to walk to (travel-to-spot off / unset) -> AtSpot() is true -> narrate in place, as before.
        if (!_buffBot.AtSpot())
        {
            _dryArrived = false;

            // (Re)issue the walk whenever no travel goal is in flight. This SELF-HEALS the login race: the
            // first attempt can land before we are on the ground, when PlanTravel has nothing to plan yet
            // (it returns "not in a playfield" and sets no goal) - so we must keep trying until a goal
            // actually takes, rather than latching after one failed attempt.
            if (!_buffBot.TravelGoalActive())
            {
                _buffBot.BeginTravelToSpot();
            }

            if (_clock - _lastWalkLogAt >= 20.0)
            {
                _lastWalkLogAt = _clock;
                _logger.LogInformation(
                    $"PET DRYRUN: walking to the buff spot ({_buffBot.SpotStatus()}) - I will narrate the plan once I'm standing at the bot.");
            }

            return; // don't narrate until we're at the buffer
        }

        // Arrived (or nothing to walk to): drop the travel goal once, announce, then narrate from here.
        if (!_dryArrived)
        {
            _dryArrived = true;
            _buffBot.ClearTravelToSpot();
            _logger.LogInformation($"PET DRYRUN: arrived - standing at the buff spot ({_buffBot.SpotStatus()}). Here is what I would do:");
        }

        NarrateFind(me);
    }

    /// <summary>
    ///     Dry run (PetDryRun): scan the bags for a pet-summon nano crystal and narrate, step by step,
    ///     what was found and what the bot WOULD do - the pet it would go for, whether it can control it,
    ///     and the buffs it would ask for - WITHOUT doing any of it. Nothing here casts, summons, learns,
    ///     or spends credits. Throttled so the log reads cleanly.
    /// </summary>
    private void NarrateFind(LocalPlayer me)
    {
        if (_clock - _lastNarrateAt < 20.0)
        {
            return;
        }

        _lastNarrateAt = _clock;

        // Skills + running buffs first, every pass - so the owner can verify MC/TS read right (perks in)
        // and that NO castable buffs are up for the raw-skill test.
        var mcNow0 = me.TryGetStat(Stat.MaterialCreation, out var mm0) ? mm0 : 0;
        var tsNow0 = me.TryGetStat(Stat.SpaceTime, out var tt0) ? tt0 : 0;
        var ncuNow0 = me.TryGetStat(Stat.MaxNCU, out var nn0) ? nn0 : 0;
        var running = me.Buffs ?? (IReadOnlyList<Buff>)Array.Empty<Buff>();
        var runNames = running.Count == 0
            ? "NONE"
            : $"{running.Count} [{string.Join(", ", running.Select(b => NanoLibrary.NameOf(b.Id)))}]";
        _logger.LogInformation($"PET DRYRUN: skills now - MC {mcNow0}, TS {tsNow0}, MaxNCU {ncuNow0}. Running buffs: {runNames}.");

        // Buff-refresh survey: only the buffs DUE for refresh (under the 15-min lead) - one compact line, or
        // nothing when all are stable. (The stable ones are not spelled out; keeps the dry run readable.)
        var due = running.Where(b => (b.Cooldown?.RemainingTime ?? 0) < RefreshSoonSec).ToList();
        if (due.Count > 0)
        {
            _logger.LogInformation("PET DRYRUN: refresh due (<15m): " +
                string.Join(", ", due.Select(b => $"{NanoLibrary.NameOf(b.Id)} {(b.Cooldown?.RemainingTime ?? 0) / 60:F0}m")));
        }

        var crystals = new List<ItemBase>();
        var gated = new List<(ItemBase Item, string Where)>(); // anything with a use-gate, for diagnostics

        // A nano CRYSTAL in inventory is a DummyItem (it uploads a nano on use) - NOT a NanoItem - so we
        // resolve as the common ItemBase, which matches both. (Find<NanoItem> silently missed every crystal.)
        void Consider(Item it, string where)
        {
            if (it == null || !ItemData.Find(it.Id, out ItemBase ib) || ib == null)
            {
                return;
            }

            if (ib.Criteria != null && ib.Criteria.ContainsKey(ItemActionInfo.UseCriteria))
            {
                gated.Add((ib, where));
            }

            var (mc, ts) = PetReq(ib);
            if (mc > 0 && ts > 0 && IsEngineerSummonCrystal(ib))
            {
                crystals.Add(ib);
            }
        }

        foreach (var it in Inventory.Items ?? Enumerable.Empty<Item>())
        {
            Consider(it, "inventory");
        }

        foreach (var c in Inventory.Containers ?? Enumerable.Empty<Container>())
        {
            foreach (var it in c.Items ?? Enumerable.Empty<Item>())
            {
                Consider(it, "bag");
            }
        }

        var mcNow = me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0;
        var tsNow = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;

        if (crystals.Count == 0)
        {
            _logger.LogInformation(
                "PET DRYRUN: no Engineer robot crystal matched (needs MC+TS+Profession==Engineer gate). " +
                $"My MC/TS now {mcNow}/{tsNow}. Items with a use-gate I can see: {gated.Count}.");
            foreach (var (ib, where) in gated.OrderByDescending(x => x.Item.Ql).Take(25))
            {
                _logger.LogInformation($"PET DRYRUN:   [{where}] '{ib.Name}' id{ib.Id} ql{ib.Ql} - reqs: {ReqSkills(ib)}.");
            }

            _logger.LogInformation("PET DRYRUN: taking NO action.");
            return;
        }

        _logger.LogInformation($"PET DRYRUN: found {crystals.Count} pet crystal(s) in my bags (my MC/TS now {mcNow}/{tsNow}):");
        foreach (var ni in crystals.OrderByDescending(x => x.Ql))
        {
            _logger.LogInformation($"PET DRYRUN:   - '{ni.Name}' ql{ni.Ql}, needs {ReqSkills(ni)}.");
        }

        var best = BestLearnedRobot(me);
        var target = crystals.OrderByDescending(x => x.Ql).FirstOrDefault(x => best == null || x.Ql > best.Ql);
        if (target == null)
        {
            _logger.LogInformation(
                $"PET DRYRUN: none beat my best learned robot '{best?.Name}' ql{best?.Ql} - I would summon what I " +
                "already know rather than learn a crystal. NO action.");
            return;
        }

        var (rmc, rts) = PetReq(target);
        var paid = IsPaid(me);
        var baseMc = UnbuffedBase(me, Stat.MaterialCreation);
        var baseTs = UnbuffedBase(me, Stat.SpaceTime);
        var plan = _catalog.BuildControlPlan(me, baseMc, baseTs, rmc, rts, paid, "Engineer");

        var myLevel = me.TryGetStat(Stat.Level, out var lv) ? lv : 0;
        _logger.LogInformation(
            $"PET DRYRUN: want '{target.Name}' ql{target.Ql} (needs {rmc}/{rts}); best known '{best?.Name}' ql{best?.Ql ?? 0}; " +
            $"base {baseMc}/{baseTs}, MaxNCU {ncuNow0}.");

        // NCU buff - the pillar. One readable line: already up (and when to refresh), what we CAN get now, or
        // nothing gettable at our level.
        if (plan.Ncu == null)
        {
            _logger.LogInformation(
                $"PET DRYRUN: NCU - need a buff, but none I can get on me at L{myLevel} (self-only/level-locked). Budget stays {plan.MaxNcu}.");
        }
        else if (plan.NcuAlreadyUp)
        {
            _logger.LogInformation(
                $"PET DRYRUN: NCU - '{plan.Ncu.Name}' +{plan.Ncu.MaxNcuAdded} already UP ({plan.NcuRemainingSec / 60:F0}m left) - refresh at T-15m. MaxNCU {plan.MaxNcu}.");
        }
        else
        {
            _logger.LogInformation(
                $"PET DRYRUN: NCU - need it; CAN get +{plan.Ncu.MaxNcuAdded} '{plan.Ncu.Name}' now " +
                $"({(plan.Ncu.NeedsTeam ? "team-cast" : "single-target")}) -> MaxNCU {ncuNow0}->{plan.MaxNcu}.");
        }

        // Step-by-step build-up: each DURABLE skill buff in turn with the running MC/TS and NCU, then the
        // durable hold verdict, then the WRANGLE last (short summon-moment peak) with the summon yes/no.
        var runMc = baseMc;
        var runTs = baseTs;
        var usedNcu = 0;
        var maxNcu = plan.MaxNcu;
        foreach (var step in plan.Steps.Where(s => !s.Wrangle && s.MaxNcuAdded == 0))
        {
            var addMc = step.AddFor((int)Stat.MaterialCreation);
            var addTs = step.AddFor((int)Stat.SpaceTime);
            runMc += addMc;
            runTs += addTs;
            usedNcu += step.Ncu;
            _logger.LogInformation(
                $"PET DRYRUN:   + {step.Name} +{addMc}/{addTs} -> skills {runMc}/{runTs}, NCU {usedNcu}/{maxNcu} ({maxNcu - usedNcu} free).");
        }

        _logger.LogInformation(
            $"PET DRYRUN: durable hold {runMc}/{runTs} (floor {plan.FloorMc}/{plan.FloorTs}, {plan.MarginPct:F0}%) -> " +
            (plan.CanControl ? "CAN hold." : "CANNOT hold - would take a smaller pet."));

        var wrangleStep = plan.Steps.FirstOrDefault(s => s.Wrangle);
        if (wrangleStep != null)
        {
            var wMc = wrangleStep.AddFor((int)Stat.MaterialCreation);
            var wTs = wrangleStep.AddFor((int)Stat.SpaceTime);
            usedNcu += wrangleStep.Ncu;
            _logger.LogInformation(
                $"PET DRYRUN:   + {wrangleStep.Name} +{wMc}/{wTs} [LAST - short summon-moment buff] -> peak {runMc + wMc}/{runTs + wTs}, " +
                $"NCU {usedNcu}/{maxNcu}; need {rmc}/{rts} -> " + (plan.CanSummon ? "CAN SUMMON. YES." : "cannot reach it. NO."));
        }
        else
        {
            _logger.LogInformation($"PET DRYRUN:   (no wrangle) peak {runMc}/{runTs} vs need {rmc}/{rts} -> " +
                (plan.CanSummon ? "CAN SUMMON. YES." : "cannot reach it. NO."));
        }

        if (plan.CanControl)
        {
            var learned = me.SpellList ?? Array.Empty<int>();

            // The buff bot's NAME comes from the one Buffs system, resolved by server (Chewysfix / Codedoc,
            // config override aside). No dimension fork here.
            var botName = _catalog.BotName;

            var actions = _catalog.RoutePlan(plan, "Engineer", botName, learned.Contains, StableStrains(me));
            var tells = actions.Where(a => a.Source == BuffCatalog.BuffSource.BuffBot).ToList();
            var selfCast = actions.Where(a => a.Source == BuffCatalog.BuffSource.SelfCast).Select(a => a.Name).ToList();
            var cantGet = actions.Where(a => a.Source == BuffCatalog.BuffSource.Unavailable).Select(a => a.Name).ToList();

            // THE TELLS, in the order he would send them. The NCU buff is FIRST and is TEAM-cast: he sends
            // that one tell, accepts the invite, waits for it to LAND (Max NCU expands, e.g. ->208) and the
            // auto-disband - and sends NOTHING else until it is up, because the rest only fit the bigger NCU.
            var ncuTell = plan.Ncu?.Tell;
            _logger.LogInformation($"PET DRYRUN: tells I'd send '{botName}' ({tells.Count}, in order):");
            if (tells.Count == 0)
            {
                _logger.LogInformation("PET DRYRUN:   (none route to the bot)");
            }

            for (var i = 0; i < tells.Count; i++)
            {
                var a = tells[i];
                var isNcuGate = !string.IsNullOrEmpty(ncuTell) && a.Tell == ncuTell;
                var kind = a.NeedsTeam ? "team: accept invite, they auto-disband" : "self: direct cast, no team";
                var gate = isNcuGate
                    ? "  <= SEND FIRST; wait for it to LAND before any other tell (it expands Max NCU)"
                    : (i > 0 && !string.IsNullOrEmpty(ncuTell) ? "  (only after the NCU buff is up)" : "");
                _logger.LogInformation($"PET DRYRUN:   {i + 1}. /tell {botName} {BuffCatalog.WireTell(a.Tell)}   [{kind}] - {a.Name}{gate}");
            }

            if (selfCast.Count > 0)
            {
                _logger.LogInformation("PET DRYRUN: self-cast (no tell): " + string.Join(", ", selfCast));
            }

            if (cantGet.Count > 0)
            {
                _logger.LogInformation($"PET DRYRUN: not in {botName}'s menu / not learned: " + string.Join(", ", cantGet));
            }

            // Survival once the wrangle lapses: what I can actually request into the real free NCU.
            var durableCtrlNcu = plan.Steps.Where(s => !s.Wrangle).Sum(s => s.Ncu);
            var survivalFree = Math.Max(0, plan.MaxNcu - durableCtrlNcu);
            var survival = _catalog.SurvivalFill(me, survivalFree, paid, "Engineer", botName,
                learned.Contains, StableStrains(me));
            var sTells = survival.Where(a => a.Source == BuffCatalog.BuffSource.BuffBot).ToList();
            _logger.LogInformation($"PET DRYRUN: survival tells (~{survivalFree} NCU free, after the pet is up): " +
                (sTells.Count > 0 ? string.Join(", ", sTells.Select(a => $"/tell {botName} {BuffCatalog.WireTell(a.Tell)} ({a.Name})")) : "none I can get yet"));

            _logger.LogInformation(
                "PET DRYRUN: => NCU tell -> wait for land -> the rest -> wrangle -> learn+summon -> survival -> WAITING.");
        }

        _logger.LogInformation("PET DRYRUN: no action (dry run).");
    }

    // The nano-skill stats other than MC(130)/TS(131). A ROBOT SUMMON requires ONLY Matter Creation +
    // Time & Space (at the same value) + Profession == 3 (engineer-pets.json: "Every summon needs Matter
    // Creation (130) + Time & Space (131) at the same value plus Profession(60) == 3"). A pet HEAL / BUFF
    // crystal is also an Engineer MC+TS item but gates on an EXTRA nano skill (e.g. Patchy Repairs needs
    // BiologicalMetamorphosis 147) - that extra skill is how we tell a heal/buff crystal from a summon.
    private static readonly int[] ForeignNanoSkills =
    {
        (int)Stat.MaterialMetamorphosis, (int)Stat.BiologicalMetamorphosis,
        (int)Stat.PsychologicalModification, (int)Stat.SensoryImprovement,
    };

    private static bool IsEngineerSummonCrystal(ItemBase ni)
    {
        if (ni?.Criteria == null || !ni.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            return false;
        }

        // Engineer gate: Profession(60) == 3 or VisualProfession(368) == 3 (the MC+TS reqs are confirmed by
        // the caller via PetReq).
        var engineer = use.Any(c => c.Operator == UseCriteriaOperator.EqualTo
                                    && (c.Param1 == (int)Stat.Profession || c.Param1 == 368) && c.Param2 == 3);
        if (!engineer)
        {
            return false;
        }

        // A summon gates on NO other nano skill. If it does (BioMet/MatMet/PsyMod/SensImp), it is a pet
        // heal or buff crystal (like 'Patchy Repairs'), not a robot summon - exclude it.
        return !use.Any(c => c.Operator == UseCriteriaOperator.GreaterThan && ForeignNanoSkills.Contains(c.Param1));
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
