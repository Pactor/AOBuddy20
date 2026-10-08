// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MetaphysicistBrain.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Controlling; // BuffBotController (the external-buff session the pet gate holds on)
using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages; // PetCommand

namespace AOBuddy20.Brains;

/// <summary>
///     METAPHYSICIST PET BRAIN - step 2 for the MP (summon + maintain + command, the simplest
///     path: skills already high enough, no buff routing - see PETBRAIN-DESIGN.md, port order 6).
///     Keeps the MP's three role-typed manifestations up: ONE attack pet (MC + TS), ONE heal pet
///     (BioMet + TS), ONE mezz pet (MatMeta + TS) - the only class that fields role-specialised
///     pets simultaneously. A pure overlay like the EngineerPetBrain: it casts and commands but
///     never moves the body, so it holds no movement arbiter.
///     THE MP SUMMON IS DIRECT - unlike the Engineer's two-step shells, the MP's SummonPet
///     (FunctionType 53167) makes the pet itself: cast and the manifestation appears. So there is
///     no shell inventory work here; casting a same-line nano while one is up would REPLACE it,
///     which the per-role slot check prevents.
///     The summon-nano id sets are scanned from the WHOLE nano library (NanoLibrary, OmniCell's
///     nanos.ocp) by pet line - the MP pet formulas carry their line as the strain (stat 75):
///     AttackPets 1015, HealPets 1016, SupportPets 1017. No curated id list, no profile file:
///     the scan catches every formula in the line, INCLUDING the multi-tier self-scaling summons
///     (e.g. Summon Anger Manifestation makes the best of its templates the caster's MC/TS allow)
///     whose pet level follows the skills held AT CAST TIME - which is exactly where the later
///     buff-first step plugs in. The charm-recovery nanos ("Pet Steal Back", line 1022) are a
///     different line and stay out by construction. Among the line's formulas the character has
///     actually learned (SpellList) and can cast (MeetsUseReqs), the highest StackingOrder one is
///     chosen per line (the tier rank within the strain - NOT QL: pet formulas all read ql 1 in
///     ItemData) - no hardcoded ids. One cast at a time (gated on IsCasting) with a per-nano recast
///     cooldown so a pet gets time to appear.
///     Roles are read from the WIRE (<see cref="NpcChar.Role" />): attack/heal/support - the mezz
///     pet is PetType.Support, never guessed. A new pet is put on Follow once (conservative, no
///     target assignment yet); the MP heal pet heals on its own and the mezz pet simply waits for
///     a target, so an idle roster is harmless.
///     The wrangle/OE sustain gate (80% control math) and the pet buffs (Instill 216, Chant 217,
///     Evocation/Anima 225, the heal/defensive lines) are LATER steps; this one assumes the
///     character can already cast its chosen pets.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
[Brain(BrainKind.Pet, Profession.Metaphysicist)]
public sealed class MetaphysicistBrain : PetBrain
{
    /// <summary>The MP's three summon lines - the roster is one pet per line.</summary>
    private enum PetLine
    {
        Attack,
        Heal,
        Mezz
    }

    // One slot per line, in summon priority: the attack pet is the MP's main weapon, the heal
    // pet changes gameplay the moment it is up, the mezz pet is the least urgent (it does
    // nothing until a target is assigned - a later step). The mezz pet is SUPPORT on the wire.
    private static readonly (PetLine Line, PetType Role)[] Slots =
    {
        (PetLine.Attack, PetType.Attack),
        (PetLine.Heal, PetType.Heal),
        (PetLine.Mezz, PetType.Support),
    };

    // A summon needs a few seconds to become a pet; re-casting before then only burns nano
    // (AOBuddy10 SummonRecastSec, same window the Engineer brain uses for its shells).
    private const double SummonRecastSec = 12.0;

    // Decide roughly once a second, not every frame.
    private const double DecideEverySec = 1.0;

    private double _sinceDecide;
    private readonly Dictionary<int, double> _summonAt = new(); // nanoId -> last cast (_clock)
    private readonly HashSet<int> _commanded = new(); // pet instances we last sent Follow to

    private readonly Dictionary<PetLine, HashSet<int>> _summonIds = new();
    private bool _warnedNoSummonData;
    private readonly HashSet<PetLine> _warnedNoLineFormula = new(); // "no formula learned" - once per line
    private readonly Dictionary<PetLine, int> _lastSummonNano = new(); // line -> the formula last cast

    /// <summary>The formula the bot last cast for this wire role - the floor phase's snapshot key
    /// (the pet's own requirements follow the formula's gates and the cast-time stats).</summary>
    public override int? LastSummonNanoFor(PetType role)
    {
        return _lastSummonNano.TryGetValue(Slots.FirstOrDefault(s => s.Role == role).Line, out var id)
            ? id
            : null;
    }

    public MetaphysicistBrain(ILogger<MetaphysicistBrain> logger, ControlArbiter controlArbiter,
        BuffBotController buffBot)
        : base(logger, controlArbiter, buffBot)
    {
        ScanNanoLibrary();
    }

    protected override bool PolicyTick(LocalPlayer me, double dt)
    {
        // Keep every manifestation we own commanded (Follow on acquire). The roster command is
        // idempotent on the server, so re-sending it when a new pet lands is safe.
        EnsureCommanded(me);

        // A buffing brain's request jumps the queue - AND punches the summon hold (RequestSummonNow,
        // the buff cycle's per-line cast: the hold stays on, this ONE cast goes out). IsCasting still
        // gates - the wire takes one cast at a time; the request stays pending until a tick can act.
        if (!me.IsCasting && ConsumeSummonRequest())
        {
            TrySummon(me);
            return true;
        }

        // The buff-first hold wins over everything below: stay petless until the buffing side
        // resolves (pets already up stay commanded).
        if (SummonHeld)
        {
            return true;
        }

        // The roster is complete: let the chain continue (overlay, returns true - the body is
        // never held; ControlPriority.Pet stays reserved for a later step).
        if (RosterComplete(me))
        {
            return true;
        }

        // A line is missing: work on summoning it, roughly once a second - but not in the login
        // dormancy (the punch above is exempt: a session's per-line cast must never wait it out).
        if (!CadenceAllowed())
        {
            return true;
        }

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

        TrySummon(me);
        return true;
    }

    /// <summary>After /pet terminate every instance is gone - the re-summon gets Followed afresh.</summary>
    protected override void OnRosterTerminated()
    {
        _commanded.Clear();
    }

    // ---- Roster -----------------------------------------------------------------------------

    private bool RosterComplete(LocalPlayer me)
    {
        foreach (var (line, role) in Slots)
        {
            if (!RolePresent(me, role))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Command ----------------------------------------------------------------------------

    /// <summary>
    ///     On newly acquiring a manifestation, put the roster on Follow once (established and
    ///     commanded). Target assignment / hunting is a later step; a conservative Follow keeps
    ///     the pets from pulling trains on their own ([[outside-never-fight]]). The heal pet heals
    ///     on its own and the mezz pet waits for a target, so an idle Follow roster is harmless.
    /// </summary>
    private void EnsureCommanded(LocalPlayer me)
    {
        foreach (var pet in me.Pets)
        {
            if (!_commanded.Add(pet.Identity.Instance))
            {
                continue;
            }

            Command(me, PetCommand.Follow);
            _logger.LogInformation($"PET: {Describe(pet.Role)} pet '{pet.Name}'#{pet.Identity.Instance} is up - on Follow.");
        }
    }

    // ---- Summon -----------------------------------------------------------------------------

    /// <summary>
    ///     Fill the first missing line (priority order in <see cref="Slots" />): the best summon
    ///     for that line we know and can cast. One cast per decide window.
    /// </summary>
    private void TrySummon(LocalPlayer me)
    {
        foreach (var (line, role) in Slots)
        {
            if (RolePresent(me, role))
            {
                continue; // filled - or merely out of sight (the grace, not the re-summon path)
            }

            CastBest(me, line);
            return; // one cast at a time - the next line waits for its decide window
        }
    }

    private void CastBest(LocalPlayer me, PetLine line)
    {
        if (!_summonIds.TryGetValue(line, out var ids) || ids.Count == 0)
        {
            if (!_warnedNoSummonData)
            {
                _warnedNoSummonData = true;
                _logger.LogWarning("PET: no MP summon ids scanned (nano library empty?) - summoning is disabled.");
            }

            return;
        }

        var learned = me.SpellList ?? Array.Empty<int>();
        NanoItem? best = null;
        var bestOrder = -1;
        foreach (var nanoId in learned)
        {
            if (!ids.Contains(nanoId))
            {
                continue;
            }

            if (!ItemData.Find(nanoId, out NanoItem ni) || ni == null)
            {
                continue;
            }

            // THE PICK (owner, 2026-10-07): the highest StackingOrder formula the bot has LEARNED -
            // no castability filter (the buff cycle's wait-for-all guarantees the stack is in
            // before the cast; MeetsUseReqs's gate was silently skipping the whole line when its
            // evaluation disagreed with the server). StackingOrder is the tier rank within the
            // line - NOT QL: pet formulas all read ql 1 in ItemData.
            var order = NanoLibrary.Find(nanoId)?.Stat((int)Stat.StackingOrder) ?? 0;
            if (best == null || order > bestOrder)
            {
                best = ni;
                bestOrder = order;
            }
        }

        if (best == null)
        {
            if (!_warnedNoLineFormula.Contains(line))
            {
                _warnedNoLineFormula.Add(line);
                _logger.LogWarning(
                    $"PET: no {LineName(line)} formula learned - the {LineName(line)} pet stays down until one is uploaded.");
            }

            return;
        }

        if (_summonAt.TryGetValue(best.Id, out var last) && _clock - last < SummonRecastSec)
        {
            return; // just cast this one - wait for the manifestation to appear
        }

        _summonAt[best.Id] = _clock;
        _lastSummonNano[line] = best.Id;
        me.Cast(best.Id);
        _logger.LogInformation($"PET: summon - casting '{best.Name}' ({best.Id}, tier {bestOrder}) for the {LineName(line)} pet; " +
                               $"buffs running at cast: {RunningBuffs(me)}");
    }

    // ---- Data -------------------------------------------------------------------------------

    /// <summary>
    ///     The MP's summon nano ids per line, scanned out of the whole nano library (NanoLibrary,
    ///     OmniCell's nanos.ocp - loaded once before the session starts, so it is there by the
    ///     time brains are built after login). The MP pet formulas carry their pet line as the
    ///     strain (stat 75): <see cref="NanoLine.AttackPets" /> 1015, <see cref="NanoLine.HealPets" />
    ///     1016, <see cref="NanoLine.SupportPets" /> 1017 - the whole game's formulas are swept,
    ///     and only these three lines match. Best-effort: an empty library leaves the sets empty
    ///     and the brain only keeps pets already up commanded.
    /// </summary>
    private void ScanNanoLibrary()
    {
        foreach (var (line, _) in Slots)
        {
            _summonIds[line] = new HashSet<int>();
        }

        if (!NanoLibrary.Loaded || NanoLibrary.Nanos.Count == 0)
        {
            _logger.LogWarning("PET: nano library empty (nanos.ocp missing or unreadable) - MP summoning is disabled.");
            return;
        }

        Collect(NanoLine.AttackPets, PetLine.Attack);
        Collect(NanoLine.HealPets, PetLine.Heal);
        Collect(NanoLine.SupportPets, PetLine.Mezz);

        _logger.LogInformation(
            $"PET: MP summon lines scanned - " +
            $"{_summonIds[PetLine.Attack].Count} attack, {_summonIds[PetLine.Heal].Count} heal, {_summonIds[PetLine.Mezz].Count} mezz formulas.");
    }

    private void Collect(NanoLine line, PetLine petLine)
    {
        // Descending StackingOrder (0 when a formula does not carry it - the Stats dictionary
        // is sparse; the raw indexer would throw). The sets are membership only: selection
        // ranks by StackingOrder over the learned list at cast time.
        foreach (var nano in NanoLibrary.InStrain((int)line).OrderByDescending(x => x.Stat((int)Stat.StackingOrder)))
        {
            _summonIds[petLine].Add(nano.NanoId);
        }
    }

    // ---- Naming -----------------------------------------------------------------------------

    private static string Describe(PetType role)
    {
        return role switch
        {
            PetType.Attack => "attack",
            PetType.Heal => "heal",
            PetType.Support => "mezz",
            _ => role.ToString(),
        };
    }

    /// <summary>
    ///     The buff cycle's per-line cast: summon the pet of THIS wire role (its buffs just landed),
    ///     never an earlier-priority line. No-op while the role's slot is already filled or a cast is
    ///     in flight (the pending summon request retries on a later tick).
    /// </summary>
    public override void CastLineRequest(LocalPlayer me, PetType role)
    {
        if (me.Pets.Count(p => p.Role == role) >= 1)
        {
            return; // already up - nothing to cast
        }

        if (me.IsCasting)
        {
            RequestSummonNow(); // a cast is in flight - the pending request retries once it clears
            return;
        }

        var line = Slots.FirstOrDefault(s => s.Role == role).Line;
        _logger.LogInformation($"PET: buff cycle cast - the {Describe(role)} pet's buffs are up.");
        CastBest(me, line);
    }

    private static string LineName(PetLine line)
    {
        return line switch
        {
            PetLine.Attack => "attack",
            PetLine.Heal => "heal",
            _ => "mezz",
        };
    }
}