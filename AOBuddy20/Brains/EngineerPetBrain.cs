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

    private readonly HashSet<int> _robotSummonIds = new();
    private bool _warnedNoSummonData;

    private readonly HuntController _hunt;
    private readonly Awareness _awareness;
    private readonly BuffCatalog _catalog;
    private readonly BuffBotController _buffBot;
    private readonly AccountInfo _config;

    private const double BuffRetrySec = 300.0; // don't re-ask the buff bot more often than this
    private double _buffAskedAt = -1e9;
    private int _loggedBuffOppFor; // the want-pet id we last logged a buff opportunity for

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
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        var attack = AttackPet(me);
        if (attack != null)
        {
            // The robot is up: drive it onto the hunt target (hunt off / no target -> Follow).
            DriveAttack(me, attack, _hunt.Tick(me, _awareness, dt));
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
            return; // nothing castable this pass (skills/level/credits/expansion)
        }

        if (_summonAt.TryGetValue(best.Id, out var last) && _clock - last < SummonRecastSec)
        {
            return; // just cast this one - wait for its shell
        }

        _summonAt[best.Id] = _clock;
        me.Cast(best.Id);
        _logger.LogInformation($"PET: summon - casting '{best.Name}' ({best.Id}, ql {best.Ql}) to make a shell.");
    }

    /// <summary>
    ///     Buff-first: when a BETTER robot than <paramref name="castableBest" /> is learned but gated
    ///     only by MC/TS, ask the buff bot (PetAutoBuff on, bot configured, un-teamed, near it) and
    ///     wait, rather than summoning the weaker one. Returns true while holding for the buffs. Off,
    ///     or no better pet, or a non-MC/TS gap, returns false and the caller summons what it can.
    /// </summary>
    private bool BuffFirst(LocalPlayer me, NanoItem? castableBest)
    {
        var want = BestLearnedRobot(me);
        if (want == null || (castableBest != null && want.Ql <= castableBest.Ql))
        {
            return false; // no better pet to reach for
        }

        var (reqMc, reqTs) = PetReq(want);
        if (OeMargin(me, reqMc, reqTs) >= 1.0)
        {
            return false; // the gap is not MC/TS (level/credits/expansion) - buffs won't help
        }

        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            if (_loggedBuffOppFor != want.Id)
            {
                _loggedBuffOppFor = want.Id;
                var who = _config.BuffBotName.Length > 0 ? _config.BuffBotName : "the buff bot";
                _logger.LogInformation(
                    $"PET: '{want.Name}' (ql {want.Ql}) needs MC {reqMc}/TS {reqTs} - out of reach now; " +
                    $"buffs would unlock it ('buffs pet' near {who}, or set PetAutoBuff).");
            }

            return false; // the owner controls buffing; summon the best we can now
        }

        if (_buffBot.Active)
        {
            return true; // a buff session is running - wait for it
        }

        if (_clock - _buffAskedAt >= BuffRetrySec)
        {
            if (_buffBot.RequestBuffs(_catalog.PlanForPetSummon(me, reqMc, reqTs), $"pet-first for {want.Name}"))
            {
                _buffAskedAt = _clock;
                _logger.LogInformation($"PET: asking the buff bot for MC {reqMc}/TS {reqTs} to summon '{want.Name}'.");
                return true;
            }

            return false; // couldn't start (not near the bot / no plan) - summon what we can
        }

        return _clock - _buffAskedAt < _config.PetBuffWaitSeconds; // hold for the wait window
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

    // ---- Data -------------------------------------------------------------------------------

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
