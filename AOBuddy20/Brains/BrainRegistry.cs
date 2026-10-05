// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BrainRegistry.cs
//
// Last modified: 2026-10-02
// Created:       2026-10-02
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.Reflection;
using AOBuddy20.Enums;
using AOBuddy20.Utils;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The brain registry: scans the bot's own assembly ONCE at startup for [Brain] classes
///     (the same reflection pass shape the MinLogLevel scan in Program.cs uses) and answers
///     "which type runs Combat/Selfbuffing/ExternalBuffing for profession X". Profession.Unknown
///     is the general fallback of a family; a profession without its own brain resolves to it.
///     The registry only knows TYPES - instances come from the BrainBank (brains are built after
///     login, when the profession is on the wire, too late for the DI container to pre-build).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BrainRegistry
{
    private readonly Dictionary<(BrainKind, Profession), Type> _brains = new();
    private readonly ILogger<BrainRegistry> _logger;

    public BrainRegistry(ILogger<BrainRegistry> logger)
    {
        _logger = logger;

        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            var attr = type.GetCustomAttribute<BrainAttribute>();
            if (attr == null || type.IsAbstract)
            {
                continue;
            }

            // The attribute's kind must agree with what the type IS: a [Brain(BrainKind.Combat)]
            // on something that is not a CombatBrain would explode later, at construction.
            if (!BaseFor(attr.Kind).IsAssignableFrom(type))
            {
                _logger.LogError(
                    $"BRAIN: {type.Name} is tagged {attr.Kind} but does not derive from {BaseFor(attr.Kind).Name} - skipped.");
                continue;
            }

            var key = (attr.Kind, attr.Profession);
            if (_brains.ContainsKey(key))
            {
                _logger.LogWarning($"BRAIN: duplicate {attr.Kind} brain for {attr.Profession} ({type.Name}) - first one wins.");
                continue;
            }

            _brains[key] = type;
        }

        // A family without its general cannot fall back: log it loud now, not as a surprise later.
        foreach (BrainKind kind in Enum.GetValues(typeof(BrainKind)))
        {
            if (!_brains.ContainsKey((kind, Profession.Unknown)))
            {
                _logger.LogError($"BRAIN: no general {kind} brain registered - the family stays disabled.");
            }
        }

        _logger.LogInformation(
            $"Brain registry: {_brains.Count} brains - combat: {Describe(BrainKind.Combat)}, " +
            $"selfbuffing: {Describe(BrainKind.Selfbuffing)}, externalbuffing: {Describe(BrainKind.ExternalBuffing)}, " +
            $"pet: {Describe(BrainKind.Pet)}.");
    }

    /// <summary>
    ///     The brain type for a family and profession: the exact hit, else the family's general
    ///     (isGeneral tells which). Null when neither exists - the family is disabled.
    /// </summary>
    public Type? Resolve(BrainKind kind, Profession profession, out bool isGeneral)
    {
        isGeneral = false;
        if (_brains.TryGetValue((kind, profession), out var exact))
        {
            return exact;
        }

        isGeneral = true;
        return _brains.TryGetValue((kind, Profession.Unknown), out var general) ? general : null;
    }

    private static Type BaseFor(BrainKind kind)
    {
        return kind switch
        {
            BrainKind.Combat => typeof(CombatBrain),
            BrainKind.Selfbuffing => typeof(SelfbuffingBrain),
            BrainKind.ExternalBuffing => typeof(ExternalBuffingBrain),
            BrainKind.Pet => typeof(PetBrain),
            _ => typeof(object),
        };
    }

    private string Describe(BrainKind kind)
    {
        var names = _brains.Where(kv => kv.Key.Item1 == kind && kv.Key.Item2 != Profession.Unknown)
            .Select(kv => kv.Key.Item2.ToString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return names.Count > 0 ? $"General + {string.Join(", ", names)}" : "General";
    }
}