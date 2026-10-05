// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PetBrain.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages; // PetCommand

namespace AOBuddy20.Brains;

/// <summary>
///     PET BRAIN BASE - the engine every pet class runs on (Engineer, MP, Bureaucrat, Trader,
///     Adventurer), with the policy seams a profession overrides. The charter is in
///     PETBRAIN-DESIGN.md; the reference is AOBuddy10 PetController/HuntController (ported, not
///     edited).
///     A pet class manages a ROSTER of slots. The SDK already carries the command channel, so
///     this base is almost pure policy-support:
///     - the pets are <see cref="LocalPlayer.Pets" /> (the NPCs the server says we own), and their
///       ROLE comes from the wire (<see cref="NpcChar.Role" /> = <see cref="PetType" />
///       Attack/Heal/Support/Social) - never guessed;
///     - commands go through <see cref="LocalPlayer.CommandPets(PetCommand)" />. All pets take the
///       same commands; the MP heal pet simply ignores attack (it just heals);
///     - a slot records its <see cref="PetSource" /> (Summoned vs Charmed) - the brain knows how
///       each pet was made, because re-acquiring differs.
///     The over-equip (OE) rule (owner, 2026-10-05): to SUMMON a pet you need 100% of its MC/TS
///     requirement; to KEEP CONTROLLING it you need only 80% (OE% <= 20). The engine exposes the
///     live margin so a policy can re-buff before a prop-buff lapse drops the pet below the floor.
///     Reference: AOBuddy20/GameData/profiles/reference/skill-buffs-mc-ts.md.
///     POLICY (summon selection, the buff-first sequence, role assignment, the charm flow) belongs
///     to the profession subclass; the base is dormant until one overrides <see cref="PolicyTick" />.
///     The tick runs on the update thread (BotLoop), the same one every packet handler runs on.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public abstract class PetBrain
{
    /// <summary>
    ///     The control floor: a pet whose buffed MC/TS is at or above 80% of its summon requirement
    ///     stays obedient (OE% &lt;= 20). Below it the pet stops obeying. OE% = 100 - (buffedSkill /
    ///     requiredSkill * 100); margin 1.0 = full skill, 0.80 = the obey cutoff.
    /// </summary>
    protected const double ControlFloor = 0.80;

    protected PetBrain(ILogger logger, ControlArbiter controlArbiter)
    {
        _logger = logger;
        _controlArbiter = controlArbiter;
    }

    protected readonly ILogger _logger;
    protected readonly ControlArbiter _controlArbiter;

    protected double _clock; // the brain's own clock, accumulated from dt
    private bool _holding;   // an episode is open: arbiter held at ControlPriority.Pet
    private double _lastTickErrorAt = double.NegativeInfinity; // throttle the recovery log

    /// <summary>True while a pet episode is open (BotLoop claims Tasks.Pet).</summary>
    public bool Tick(LocalPlayer me, double dt)
    {
        _clock += dt;
        if (me == null)
        {
            ReleaseControl(); // zone/login loss
            return false;
        }

        // A pet-brain decision must never wedge the bot: a throw here would otherwise skip every system
        // that ticks after the pet overlay this frame. Contain it, drop our control so nothing is held in
        // a broken state, report it (throttled, so a persistent bad-data case is visible but not spam),
        // and let the next tick retry. ALWAYS recover.
        try
        {
            var holding = PolicyTick(me, dt);
            if (!holding)
            {
                ReleaseControl();
            }

            return holding;
        }
        catch (Exception ex)
        {
            if (_clock - _lastTickErrorAt > 10.0)
            {
                _lastTickErrorAt = _clock;
                _logger.LogError(ex, "PET: brain tick threw - recovering (released control, retrying next tick).");
            }

            ReleaseControl();
            return false;
        }
    }

    /// <summary>
    ///     POLICY: this tick's decisions. Return true only while an episode holds the arbiter at
    ///     <see cref="ControlPriority.Pet" /> (call <see cref="TakeControl" /> on open,
    ///     <see cref="ReleaseControl" /> on close - the HealController pattern). The general brain
    ///     returns false (dormant).
    /// </summary>
    protected virtual bool PolicyTick(LocalPlayer me, double dt)
    {
        return false;
    }

    // ---- ENGINE: the roster (read from the wire) --------------------------------------------

    /// <summary>The pets the server says we own, each carrying its wire-read role.</summary>
    protected static IEnumerable<NpcChar> Pets(LocalPlayer me)
    {
        return me.Pets;
    }

    protected static NpcChar? AttackPet(LocalPlayer me)
    {
        return me.Pets.FirstOrDefault(p => p.Role == PetType.Attack);
    }

    protected static NpcChar? HealPet(LocalPlayer me)
    {
        return me.Pets.FirstOrDefault(p => p.Role == PetType.Heal);
    }

    protected static NpcChar? SupportPet(LocalPlayer me)
    {
        return me.Pets.FirstOrDefault(p => p.Role == PetType.Support);
    }

    // ---- ENGINE: commands (the SDK channel) -------------------------------------------------

    /// <summary>Command the whole roster. All pets take the same commands.</summary>
    protected static void Command(LocalPlayer me, PetCommand command)
    {
        me.CommandPets(command);
    }

    /// <summary>Command a subset (the PetCommandMessage carries the pet list).</summary>
    protected static void Command(LocalPlayer me, PetCommand command, IEnumerable<Identity> pets)
    {
        me.CommandPets(command, pets);
    }

    // Pet attack is TARGET-BASED (AOBuddy10 PetController.EngageTarget): set our target to the mob,
    // then CommandPets(Attack) naming the attack pets. Issue it ONCE PER MOB (re-asserting resets the
    // pet's swing timer), re-sent only when the target changes - or as a retry when a pet still isn't
    // on it a few seconds later (a pet can miss the first order). Stand down to Follow when there is
    // no target (and once on a freshly-summoned pet, to establish it).
    private const double AttackRetrySec = 3.0;
    private Identity? _attackTarget;
    private double _attackSentAt;
    private int _commandedInstance; // the attack-pet instance we last issued a command to (0 = none)

    /// <summary>
    ///     Drive the attack pet: onto <paramref name="target" /> (attack-once + retry), or Follow when
    ///     <paramref name="target" /> is null. <paramref name="attackPet" /> is the current attack pet
    ///     (its instance is tracked so a re-summoned pet is re-established).
    /// </summary>
    protected void DriveAttack(LocalPlayer me, NpcChar attackPet, SimpleChar? target)
    {
        var attackers = me.Pets.Where(p => p.Role == PetType.Attack).Select(p => p.Identity).ToList();

        if (target != null && attackers.Count > 0)
        {
            var changed = _attackTarget != target.Identity;
            var retry = !changed && _clock - _attackSentAt >= AttackRetrySec
                        && attackers.Any(a => !(DynelManager.Find(a, out NpcChar pet)
                                                && pet.FightingIdentity.HasValue
                                                && pet.FightingIdentity.Value == target.Identity));
            if (changed || retry)
            {
                Targeting.SetTarget(target.Identity);
                Command(me, PetCommand.Attack, attackers);
                _attackTarget = target.Identity;
                _attackSentAt = _clock;
                _commandedInstance = attackPet.Identity.Instance;
                _logger.LogInformation($"PET: attack '{target.Name}'{(retry ? " (again, not on it yet)" : "")}.");
            }

            return;
        }

        // No target: Follow, when we were just attacking (stand down) or this attack pet is new.
        var stoodDown = _attackTarget != null;
        var newPet = _commandedInstance != attackPet.Identity.Instance;
        if (stoodDown || newPet)
        {
            _attackTarget = null;
            _commandedInstance = attackPet.Identity.Instance;
            Command(me, PetCommand.Follow);
            _logger.LogInformation(stoodDown
                ? "PET: no target - pets on Follow."
                : $"PET: robot '{attackPet.Name}'#{attackPet.Identity.Instance} is up - on Follow.");
        }
    }

    // ---- ENGINE: the over-equip (OE) math ---------------------------------------------------

    /// <summary>
    ///     The live OE margin for a pet of the given MC/TS requirement:
    ///     min(buffedMC / reqMC, buffedTS / reqTS), read from the current (buffed) stats. A
    ///     requirement of 0 (or less) is treated as satisfied (margin 1.0 for that skill). &gt;= 1.0
    ///     means full skill; the obey cutoff is <see cref="ControlFloor" /> (0.80).
    /// </summary>
    protected static double OeMargin(LocalPlayer me, int reqMc, int reqTs)
    {
        var mc = reqMc <= 0 ? 1.0 : (me.TryGetStat(Stat.MaterialCreation, out var m) ? m : 0) / (double)reqMc;
        var ts = reqTs <= 0 ? 1.0 : (me.TryGetStat(Stat.SpaceTime, out var t) ? t : 0) / (double)reqTs;
        return Math.Min(mc, ts);
    }

    /// <summary>
    ///     Whether a pet of this MC/TS requirement is still controllable at the current buffed
    ///     skills (margin at or above the 80% floor). Note: SUMMONING needs margin &gt;= 1.0;
    ///     this is the weaker KEEP-CONTROL test.
    /// </summary>
    protected static bool CanControl(LocalPlayer me, int reqMc, int reqTs)
    {
        return OeMargin(me, reqMc, reqTs) >= ControlFloor;
    }

    /// <summary>Whether the character can SUMMON a pet of this requirement right now (margin &gt;= 1.0).</summary>
    protected static bool CanSummon(LocalPlayer me, int reqMc, int reqTs)
    {
        return OeMargin(me, reqMc, reqTs) >= 1.0;
    }

    // ---- ENGINE: the arbiter hold -----------------------------------------------------------

    /// <summary>Open the episode: hold the arbiter at ControlPriority.Pet (idempotent).</summary>
    protected void TakeControl()
    {
        if (!_holding)
        {
            _holding = true;
            _controlArbiter.TakeControl(ControlPriority.Pet);
        }
    }

    /// <summary>Close the episode: release the arbiter if we were holding it (idempotent).</summary>
    protected void ReleaseControl()
    {
        if (_holding)
        {
            _holding = false;
            _controlArbiter.ReleaseControl();
        }
    }

    // ---- ENGINE: the cross-brain handshake (the buff-first summon) --------------------------

    // One-shot: another brain asked for an immediate summon pass (consumed by the next tick).
    private bool _summonRequested;

    // While held, the policy summons nothing - the buff-first "stay petless until it resolves".
    private bool _summonHold;

    /// <summary>
    ///     CROSS-BRAIN: a buffing brain (the Selfbuffing / ExternalBuffing side, once the peak
    ///     skill stack is confirmed up - PETBRAIN-DESIGN.md, buff-first step 3) asks the pet
    ///     brain for an IMMEDIATE summon pass: the next tick runs the summon policy without the
    ///     ~1s decide cadence. One-shot, and a no-op while <see cref="SetSummonHold" /> is held
    ///     (release first, then request). Reachable as <c>BrainBank.Pet.RequestSummon()</c>.
    /// </summary>
    public void RequestSummon()
    {
        if (_summonHold)
        {
            return;
        }

        _summonRequested = true;
        _logger.LogInformation("PET: summon requested (buff-first handshake).");
    }

    /// <summary>
    ///     CROSS-BRAIN: while held, the policy summons NOTHING - not on its own cadence, not on
    ///     request - but keeps commanding pets already up. This is the buff-first gate ("stay
    ///     petless until it resolves", owner 2026-10-05): hold while the NCU/skill buffs are
    ///     being arranged, then release + <see cref="RequestSummon" /> at the peak. Releasing
    ///     without a request simply returns to the normal auto cadence.
    /// </summary>
    public void SetSummonHold(bool held)
    {
        if (_summonHold == held)
        {
            return;
        }

        _summonHold = held;
        _logger.LogInformation($"PET: summon hold {(held ? "ON - staying petless until released" : "off")}.");
    }

    /// <summary>Whether summoning is currently held (the policy checks before any cast).</summary>
    protected bool SummonHeld => _summonHold;

    /// <summary>Consume the pending summon request - true once, then it is gone.</summary>
    protected bool ConsumeSummonRequest()
    {
        if (!_summonRequested)
        {
            return false;
        }

        _summonRequested = false;
        return true;
    }

    /// <summary>
    ///     CROSS-BRAIN: /pet terminate - the WHOLE roster dies (server-side), its NCU frees and
    ///     every slot is empty again. The buff-first re-summon uses this to clear a pet summoned
    ///     below the peak before casting the better one. Pair it with <see cref="SetSummonHold" />
    ///     when the intent is a re-summon at higher skills: otherwise the normal cadence refills
    ///     the empty slots within a second. <see cref="OnRosterTerminated" /> lets the policy
    ///     drop its per-pet bookkeeping. Reachable as <c>BrainBank.Pet.TerminateAll(me)</c>.
    /// </summary>
    public void TerminateAll(LocalPlayer me)
    {
        if (me == null || !me.Pets.Any())
        {
            return;
        }

        var count = me.Pets.Count();
        Command(me, PetCommand.Terminate);
        _logger.LogInformation($"PET: terminating all {count} pet(s).");
        _attackTarget = null;    // the engine's attack bookkeeping is stale with the roster gone
        _commandedInstance = 0;
        OnRosterTerminated();
    }

    /// <summary>Hook: the roster was just terminated - the policy drops its per-pet bookkeeping.</summary>
    protected virtual void OnRosterTerminated()
    {
    }
}

/// <summary>
///     One pet slot in a brain's roster - the model PETBRAIN-DESIGN.md describes. Step 1 defines
///     the shape; the profession subclasses fill and maintain it (the base reads live pets, the
///     subclass records Source/Commandable as it summons or charms).
/// </summary>
public sealed class PetSlot
{
    /// <summary>What the pet is for, as the server states it (wire-read).</summary>
    public PetType Role;

    /// <summary>How it was acquired - the brain records this, it is not on the wire.</summary>
    public PetSource Source;

    /// <summary>The pet dynel, or null while the slot is empty / a summon is pending.</summary>
    public Identity? Identity;

    /// <summary>False for a vanity/social follower that is not ours to command.</summary>
    public bool Commandable = true;

    /// <summary>Engineer OE: a disobeying (over-equipped) pet is effectively lost.</summary>
    public bool Obeying = true;
}
