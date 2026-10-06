// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BrainBank.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The brain bank (AOBuddy10 BOTREWORK's "FightingBrainHolder - what login actually
///     injects"): holds the character's three brains - one per family - and forwards the
///     BotLoop ticks to them.
///     The DI container is built BEFORE login (Program.Main), but the profession is only on
///     the wire after the character's FullCharacter arrives - so the bank is the singleton,
///     and the brains inside it are chosen once, post-login, by EnsureSelected (BotLoop calls
///     it every tick; it selects on the first tick where the profession reads as a known one).
///     The choice never changes for the process lifetime (review.md #12): on a reconnect the
///     same brain instances keep ticking, and the bases tolerate me == null.
///     Brains are constructed through ActivatorUtilities, so a future profession brain can
///     take any registered service (MovementController, Awareness, NavController, ...) in its
///     constructor with no registry change. A brain that fails to construct falls back to its
///     family's general; a family with no brain and no general stays disabled (null - the tick
///     forwarders answer false, BotLoop never notices).
///     All of this runs on the update thread (the same argument every controller makes): no
///     locks.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BrainBank
{
    private readonly BrainRegistry _registry;
    private readonly IServiceProvider _provider;
    private readonly ILogger<BrainBank> _logger;

    private bool _selected;
    private bool _warnedBadProfession;
    private Profession _profession = Profession.Unknown;

    public CombatBrain? Combat { get; private set; }
    public SelfbuffingBrain? Selfbuff { get; private set; }
    public ExternalBuffingBrain? ExternalBuff { get; private set; }
    public PetBrain? Pet { get; private set; }

    /// <summary>This character fights THROUGH pets - a concrete profession pet brain is registered for it
    /// (not the dormant GeneralPetBrain). The data-driven "is a pet class" test (the brain registration IS
    /// the determination; no hardcoded profession list): a pet-class buddy stays back and lets its pet
    /// fight rather than meleeing the owner's target.</summary>
    public bool IsPetClass => Pet is not null and not GeneralPetBrain;

    public BrainBank(BrainRegistry registry, IServiceProvider provider, ILogger<BrainBank> logger)
    {
        _registry = registry;
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    ///     BotLoop calls this every tick; it selects once, on the first tick where the
    ///     profession is on the wire (Stat.Profession). Unreadable or Unknown stats just mean
    ///     "not loaded yet" - retried next tick, no noise.
    /// </summary>
    public void EnsureSelected(LocalPlayer me)
    {
        if (_selected || me == null)
        {
            return;
        }

        if (!me.TryGetStat(Stat.Profession, out var raw))
        {
            return;
        }

        var profession = (Profession)(uint)raw;
        if (profession == Profession.Unknown)
        {
            return;
        }

        // A profession value the enum does not know would explode Enum-free lookups downstream;
        // warn once and keep waiting - the server may still be filling the stats in.
        if (!Enum.IsDefined(typeof(Profession), (uint)raw))
        {
            if (!_warnedBadProfession)
            {
                _warnedBadProfession = true;
                _logger.LogWarning($"BRAIN: profession stat reads {raw}, not a known profession - waiting.");
            }

            return;
        }

        _selected = true;
        _profession = profession;
        Combat = Create<CombatBrain>(BrainKind.Combat, profession);
        Selfbuff = Create<SelfbuffingBrain>(BrainKind.Selfbuffing, profession);
        ExternalBuff = Create<ExternalBuffingBrain>(BrainKind.ExternalBuffing, profession);
        Pet = Create<PetBrain>(BrainKind.Pet, profession);
        _logger.LogInformation(
            $"BRAINS: profession {profession} - combat={Name(Combat)}, selfbuffing={Name(Selfbuff)}, " +
            $"externalbuff={Name(ExternalBuff)}, pet={Name(Pet)}.");
    }

    // ---- Tick forwarders (BotLoop's chain, descending ControlPriority) ----------------------

    public bool TickCombat(LocalPlayer me, double dt)
    {
        return Combat?.Tick(me, dt) ?? false;
    }

    public bool TickSelfbuff(LocalPlayer me, double dt)
    {
        return Selfbuff?.Tick(me, dt) ?? false;
    }

    public bool TickExternalBuff(LocalPlayer me, double dt)
    {
        return ExternalBuff?.Tick(me, dt) ?? false;
    }

    public bool TickPet(LocalPlayer me, double dt)
    {
        return Pet?.Tick(me, dt) ?? false;
    }

    /// <summary>The 'brain' owner command's answer.</summary>
    public string Describe()
    {
        if (!_selected)
        {
            return "Brains: not selected yet (waiting for the character to load).";
        }

        return $"Brains: profession {_profession} - combat: {Name(Combat)}{DormantNote(Combat)}, " +
               $"selfbuffing: {Name(Selfbuff)}{DormantNote(Selfbuff)}, " +
               $"externalbuffing: {Name(ExternalBuff)}{DormantNote(ExternalBuff)}, " +
               $"pet: {Name(Pet)}{DormantNote(Pet)}. " +
               "Dormant brains take no actions until their family is implemented.";
    }

    // ---- Selection ---------------------------------------------------------------------------

    private T? Create<T>(BrainKind kind, Profession profession) where T : class
    {
        var type = _registry.Resolve(kind, profession, out var isGeneral);
        if (type == null)
        {
            _logger.LogError($"BRAIN {kind}: no brain and no general registered - family disabled.");
            return null;
        }

        if (isGeneral)
        {
            _logger.LogInformation($"BRAIN {kind}: no {kind} brain for profession {profession} - using {type.Name}.");
        }

        try
        {
            var brain = (T?)ActivatorUtilities.CreateInstance(_provider, type);
            _logger.LogInformation($"BRAIN {kind}: {type.Name} active for profession {profession}.");
            return brain;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"BRAIN {kind}: construction of {type.Name} failed.");

            // The one fallback that survives a broken brain: the general. If THAT is broken too,
            // the family stays disabled for the session - and the log says so.
            if (!isGeneral && _registry.Resolve(kind, Profession.Unknown, out _) is { } general)
            {
                try
                {
                    var fallback = (T?)ActivatorUtilities.CreateInstance(_provider, general);
                    _logger.LogInformation($"BRAIN {kind}: fell back to {general.Name}.");
                    return fallback;
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, $"BRAIN {kind}: general {general.Name} failed too - family disabled.");
                }
            }

            return null;
        }
    }

    private static string Name(object? brain)
    {
        return brain == null ? "(none)" : brain.GetType().Name;
    }

    private static string DormantNote(object? brain)
    {
        return brain != null && brain.GetType().Name.StartsWith("General") ? " (dormant)" : "";
    }
}