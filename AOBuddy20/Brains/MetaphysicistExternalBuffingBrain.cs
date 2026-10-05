// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MetaphysicistExternalBuffingBrain.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     METAPHYSICIST EXTERNAL BUFFING BRAIN - the buff-first summon, driven from the buffing
///     side (PETBRAIN-DESIGN.md port order 4: "the buff step depends on the ExternalBuffingBrain
///     family... PetBrain waits on whichever applies"). The MP pet lines gap on THREE skill
///     pairs - attack MC(130)+TS(131), heal BioMet(128)+TS(131), mezz MatMet(127)+TS(131) - and
///     the buff bots' safe all-nano-skill composites lift every one of them. So this brain,
///     once a second, looks for a MISSING pet line whose best learned summon is one nano-skill
///     gap away (level/expansion/credit gaps are NOT buffable - no ask), and then:
///       1. holds the pet brain (SetSummonHold - "stay petless until it resolves"),
///       2. opens a BuffBotController session with the computed plan (NCU tell first, then the
///          smallest safe composite closing the gap),
///       3. when the session ends - buffs landed OR the window closed - releases and fires
///          RequestSummon(): the pet brain casts the best pet it can NOW, at the new skills.
///     Coordination runs even with PetAutoBuff off (an owner-started 'buffs pet' session gets
///     the same hold/release/request treatment around it); ASKING is what PetAutoBuff gates.
///     This is the Engineer's internal BuffFirst re-homed per the family split (the Engineer
///     keeps its in-policy variant); v1 only UNLOCKS missing pets - swapping an up pet for a
///     stronger one (terminate + re-summon at the peak) needs combat awareness and is a later
///     step. Runs on the update thread (BotLoop), like every brain.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.ExternalBuffing, Profession.Metaphysicist)]
public sealed class MetaphysicistExternalBuffingBrain : ExternalBuffingBrain
{
    /// <summary>The MP's three pet summon lines.</summary>
    private enum PetLine
    {
        Attack,
        Heal,
        Mezz
    }

    // One row per line: the summon line (strain), the wire role it fills, the PRIMARY skill
    // beside SpaceTime, and the log label. Order = the pet brain's fill priority.
    private static readonly (PetLine Line, PetType Role, NanoLine Strain, Stat Primary, string Label)[] Lines =
    {
        (PetLine.Attack, PetType.Attack, NanoLine.AttackPets, Stat.MaterialCreation, "attack"),
        (PetLine.Heal, PetType.Heal, NanoLine.HealPets, Stat.BiologicalMetamorphosis, "heal"),
        (PetLine.Mezz, PetType.Support, NanoLine.SupportPets, Stat.MaterialMetamorphosis, "mezz"),
    };

    // Decide roughly once a second, not every frame.
    private const double DecideEverySec = 1.0;

    // Don't re-ask after a closed session more often than this (Engineer BuffRetrySec parity):
    // a landed buff clears the want; an unlanded one waits out the cooldown.
    private const double AskRetrySec = 300.0;

    private readonly BrainBank _bank;
    private readonly BuffBotController _buffBot;
    private readonly BuffCatalog _catalog;
    private readonly AccountInfo _config;

    private readonly Dictionary<PetLine, HashSet<int>> _summonIds = new();
    private double _sinceDecide;
    private double _askedAt = -1e9;
    private int _opportunityLoggedFor; // the want id we last logged a buff opportunity for
    private bool _sessionSeen;         // a buff session ran last tick - its end releases the hold
    private bool _claimed;             // the arbiter is ours at ControlPriority.ExternalBuffing

    public MetaphysicistExternalBuffingBrain(ILogger<MetaphysicistExternalBuffingBrain> logger,
        ControlArbiter controlArbiter, BrainBank bank, BuffBotController buffBot, BuffCatalog catalog,
        AccountInfo config)
        : base(logger, controlArbiter)
    {
        _bank = bank;
        _buffBot = buffBot;
        _catalog = catalog;
        _config = config;
        ScanSummonLines();
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        var pet = _bank.Pet;
        if (pet == null)
        {
            return false; // no pet brain this session - nothing to hand the buffs to
        }

        // A buff session is running (ours, or the owner's 'buffs pet'): the pet brain stays
        // petless until it resolves.
        if (_buffBot.Active)
        {
            _sessionSeen = true;
            pet.SetSummonHold(true);
            return Claim();
        }

        // The session just ended - buffs landed, or the window closed without them. Either way
        // the pet brain may cast again, and NOW (not on its cadence): the whole point of the
        // dance is summoning at the new skills.
        if (_sessionSeen)
        {
            _sessionSeen = false;
            pet.SetSummonHold(false);
            pet.RequestSummon();
            Release();
            return false;
        }

        // Watching: once a second, is a missing line one nano-skill gap from a better pet?
        _sinceDecide += dt;
        if (_sinceDecide < DecideEverySec)
        {
            return false;
        }

        _sinceDecide = 0;

        var want = FindWant(me);
        if (want == null)
        {
            return false;
        }

        if (!_config.PetAutoBuff || string.IsNullOrWhiteSpace(_config.BuffBotName))
        {
            if (_opportunityLoggedFor != want.Value.Pet.Id)
            {
                _opportunityLoggedFor = want.Value.Pet.Id;
                _logger.LogInformation(
                    $"EXTBUFF: '{want.Value.Pet.Name}' (ql {want.Value.Pet.Ql}) needs +{want.Value.Need} nano skills - " +
                    "buffs would unlock it ('buffs pet' near the buff bot, or set PetAutoBuff).");
            }

            return false; // the owner controls buffing; the pet brain runs its own cadence
        }

        if (Team.IsInTeam)
        {
            return false; // the handshake needs us un-teamed - the bot must be the one to team us
        }

        if (_clock - _askedAt < AskRetrySec)
        {
            return false; // asked recently - give the last session's outcome time to show
        }

        var plan = _catalog.PlanForSkillGap(me, want.Value.Need);
        if (!_buffBot.RequestBuffs(plan, $"{want.Value.Label}-first for {want.Value.Pet.Name}"))
        {
            return false; // cannot start (no bot/plan) - the pet brain summons what it can
        }

        _askedAt = _clock;
        pet.SetSummonHold(true);
        _logger.LogInformation(
            $"EXTBUFF: asking the buff bot for +{want.Value.Need} nano skills to summon " +
            $"'{want.Value.Pet.Name}' ({want.Value.Label} pet).");
        return Claim();
    }

    // ---- The want ---------------------------------------------------------------------------

    /// <summary>
    ///     The first MISSING line (pet-brain fill order: attack, heal, mezz) whose best learned
    ///     summon is blocked by its skill pair alone. v1 ignores lines whose pet is already up -
    ///     upgrading a live roster is terminate + re-summon at the peak, a later step.
    /// </summary>
    private (NanoItem Pet, int Need, string Label)? FindWant(LocalPlayer me)
    {
        foreach (var (line, role, _, primary, label) in Lines)
        {
            if (me.Pets.Any(p => p.Role == role))
            {
                continue;
            }

            var (pet, need) = BestLearned(me, line, primary);
            if (pet != null && need > 0)
            {
                return (pet, need, label);
            }
        }

        return null;
    }

    /// <summary>
    ///     The best learned summon of the line (highest QL - the Engineer's wrangle parity) and
    ///     how much nano-skill lift it still needs. A non-skill gap (level, expansion, credits,
    ///     NCU) reads as need 0 only when the skills are already met - buffs fix exactly the
    ///     skill part, so that is all this brain asks for.
    /// </summary>
    private (NanoItem?, int) BestLearned(LocalPlayer me, PetLine line, Stat primary)
    {
        if (!_summonIds.TryGetValue(line, out var ids) || ids.Count == 0)
        {
            return (null, 0);
        }

        NanoItem? best = null;
        var need = 0;
        foreach (var nanoId in me.SpellList ?? Array.Empty<int>())
        {
            if (!ids.Contains(nanoId) || !ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            if (best != null && ni.Ql <= best.Ql)
            {
                continue;
            }

            var curPrimary = me.TryGetStat(primary, out var p) ? p : 0;
            var curTs = me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0;
            need = Math.Max(Math.Max(0, SkillReq(ni, primary) - curPrimary),
                Math.Max(0, SkillReq(ni, Stat.SpaceTime) - curTs));
            best = ni;
        }

        return (best, need);
    }

    // ---- Data -------------------------------------------------------------------------------

    /// <summary>The summon formulas per line, scanned from the whole nano library by strain.</summary>
    private void ScanSummonLines()
    {
        foreach (var (line, _, _, _, _) in Lines)
        {
            _summonIds[line] = new HashSet<int>();
        }

        if (!NanoLibrary.Loaded || NanoLibrary.Nanos.Count == 0)
        {
            _logger.LogWarning("EXTBUFF: nano library empty - the MP buff-first watcher stays off.");
            return;
        }

        foreach (var (line, _, strain, _, _) in Lines)
        {
            foreach (var nano in NanoLibrary.InStrain((int)strain))
            {
                _summonIds[line].Add(nano.NanoId);
            }
        }
    }

    // A summon's requirement on one stat, from its cast criteria (GreaterThan means stat > N,
    // so the requirement is N + 1). 0 when the nano has no such criterion. (The Engineer's
    // PetReq, generalized to any stat.)
    private static int SkillReq(NanoItem nano, Stat stat)
    {
        var req = 0;
        if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            foreach (var c in use)
            {
                if (c.Operator == UseCriteriaOperator.GreaterThan && c.Param1 == (int)stat)
                {
                    req = Math.Max(req, c.Param2 + 1);
                }
            }
        }

        return req;
    }

    // ---- The arbiter ------------------------------------------------------------------------

    /// <summary>Open the episode: hold the arbiter at ControlPriority.ExternalBuffing (idempotent).</summary>
    private bool Claim()
    {
        if (!_claimed)
        {
            _claimed = true;
            _controlArbiter.TakeControl(ControlPriority.ExternalBuffing);
        }

        return true;
    }

    /// <summary>Close the episode: release the arbiter if we were holding it (idempotent).</summary>
    private void Release()
    {
        if (_claimed)
        {
            _claimed = false;
            _controlArbiter.ReleaseControl();
        }
    }
}