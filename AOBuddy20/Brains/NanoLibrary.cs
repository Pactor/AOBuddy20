// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: NanoLibrary.cs
//
// Last modified: 2026-10-04
// Created:       2026-10-04
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO.Compression;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The nano library: every nano formula in the game, read once at startup from
///     GameData/nanos.ocp (OmniCell's canonical content pack, written by omnicell's
///     Tools/Algorithman/Extractor Serializer out of the client's own nano records).
///     THE PACK, not the JSONs, not ItemData.bin: the per-profession profile JSONs lift only
///     a whitelisted handful of stats, and ItemData.bin - though it holds all 10965 nano
///     templates, with names, NCU, line, school, stacking order, cast/recharge times and the
///     use criteria - carries NO effect modifiers, so the OnUse heal amount (the "which is
///     higher, stim or nano" input) exists in neither. The pack's Stats dictionary holds
///     every stat the client record does, heal amounts included.
///     NAMES come from ItemData.bin (<see cref="NameOf" /> - the pack has none); the bin is
///     already loaded for every real bot run, so the two sources meet at query time.
///     FORMAT (OmniCellContentPack v3, the nano kind only - read-only, no OmniCell reference,
///     same house pattern as ItemValues.bin and Zoning.json): a GZip stream carrying
///     "OMNICELL-CONTENT", a version, the kind byte and a count, then per formula five
///     int32s (ID, instance, item type, type, flags), three stat dictionaries (attack,
///     defend, Stats), the cast actions with requirement quintuples, the effect events with
///     their functions (arguments tagged 1=int 2=float 3=string, function record flagged),
///     and (version 2+) the rest of the client record. Unknown versions are refused loudly -
///     omnicell owns the format; when it moves, this reader moves with it.
///     Loaded once before the session starts, immutable afterwards - pure reads from any
///     thread (the Zoning.Load posture). A missing or unreadable pack logs an error and
///     leaves the library EMPTY - brains asking an empty library get nothing, and the log
///     says why.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public static class NanoLibrary
{
    private const string Magic = "OMNICELL-CONTENT";
    private const byte KindNanos = 2;
    private const int OldestReadableVersion = 1;
    private const int NewestReadableVersion = 3;

    private static readonly List<NanoProfile> _nanos = new();
    private static readonly Dictionary<int, NanoProfile> _byId = new();
    private static readonly HashSet<int> _nameMisses = new(); // ids ItemData does not know - asked once, logged once

    /// <summary>The load attempt is done (successfully or as a definitive miss).</summary>
    public static bool Loaded { get; private set; }

    /// <summary>Every formula in the pack. Empty until loaded, or when the pack is missing/broken.</summary>
    public static IReadOnlyList<NanoProfile> Nanos => _nanos;

    public static NanoProfile? Find(int nanoId) => _byId.TryGetValue(nanoId, out var n) ? n : null;

    /// <summary>The nanos of one strain (stat 75) - the stacking groups: within a strain, the
    /// higher StackingOrder supersedes.</summary>
    public static IEnumerable<NanoProfile> InStrain(int strain) =>
        _nanos.Where(n => n.Stat(75) == strain);

    /// <summary>The display name, from ItemData.bin (the pack carries none). Mob formulas the
    /// item database does not know answer "(nano id)" - asked once, then remembered, so a
    /// fire line cannot spam the SDK's per-miss warnings.</summary>
    public static string NameOf(int nanoId)
    {
        if (_nameMisses.Contains(nanoId))
        {
            return $"(nano {nanoId})";
        }

        if (ItemData.Find<NanoItem>(nanoId, out var ni) && ni != null)
        {
            return ni.Name ?? $"(nano {nanoId})";
        }

        _nameMisses.Add(nanoId);
        return $"(nano {nanoId})";
    }

    /// <summary>
    ///     Called once from Program.Main, next to Zoning.Load, before the session starts.
    ///     A missing pack is a warning (the library stays empty); a pack that is there but
    ///     unreadable - wrong magic, kind or version - is an error. Never throws.
    /// </summary>
    public static void Load(string baseDir, Action<string> log)
    {
        var file = Path.Combine(baseDir, "GameData", "nanos.ocp");
        if (!File.Exists(file))
        {
            log($"NANOS: no nano pack at {file} - the library stays empty until it is dropped in.");
            return;
        }

        try
        {
            using var compressed = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);

            var magic = reader.ReadString();
            if (magic != Magic)
            {
                throw new InvalidDataException($"not an OmniCell content pack (magic '{magic}')");
            }

            var version = reader.ReadInt32();
            if (version is < OldestReadableVersion or > NewestReadableVersion)
            {
                throw new InvalidDataException($"pack format version {version} is outside what this reader knows " +
                                               $"({OldestReadableVersion}..{NewestReadableVersion}) - update the reader with the pack");
            }

            var kind = reader.ReadByte();
            if (kind != KindNanos)
            {
                throw new InvalidDataException($"pack holds kind {kind}, not nanos ({KindNanos})");
            }

            var count = reader.ReadInt32();
            var nanos = new List<NanoProfile>(count);
            var byId = new Dictionary<int, NanoProfile>(count);

            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadInt32();
                reader.ReadInt32(); // instance
                reader.ReadInt32(); // item type
                reader.ReadInt32(); // type
                reader.ReadInt32(); // flags

                ReadDictionary(reader); // attack
                ReadDictionary(reader); // defend
                var stats = ReadDictionary(reader);

                var actions = ReadActions(reader);
                SkipEvents(reader, version);
                if (version >= 2)
                {
                    SkipRecordData(reader, version);
                }

                var nano = new NanoProfile { NanoId = id, Stats = stats, Actions = actions };
                nanos.Add(nano);
                byId[id] = nano;
            }

            _nanos.Clear();
            _nanos.AddRange(nanos);
            _byId.Clear();
            foreach (var kv in byId)
            {
                _byId[kv.Key] = kv.Value;
            }

            Loaded = true;
            log($"NANOS: {_nanos.Count} formulas from {Path.GetFileName(file)} (pack v{version}).");
        }
        catch (Exception ex)
        {
            _nanos.Clear();
            _byId.Clear();
            Loaded = true;
            log($"NANOS: reading {file} failed - the library stays empty. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- The v3 nano-record grammar -----------------------------------------

    private static Dictionary<int, int> ReadDictionary(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var dict = new Dictionary<int, int>(count);
        for (var i = 0; i < count; i++)
        {
            dict[reader.ReadInt32()] = reader.ReadInt32();
        }

        return dict;
    }

    private static List<NanoAction> ReadActions(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var actions = new List<NanoAction>(count);
        for (var i = 0; i < count; i++)
        {
            var action = new NanoAction { ActionType = reader.ReadInt32() };
            var reqCount = reader.ReadInt32();
            var reqs = new List<NanoRequirement>(reqCount);
            for (var r = 0; r < reqCount; r++)
            {
                reqs.Add(new NanoRequirement
                {
                    ChildOperator = reader.ReadInt32(),
                    Operator = reader.ReadInt32(),
                    Stat = reader.ReadInt32(),
                    Target = reader.ReadInt32(),
                    Value = reader.ReadInt32(),
                });
            }

            action.Requirements = reqs;
            actions.Add(action);
        }

        return actions;
    }

    private static void SkipEvents(BinaryReader reader, int version)
    {
        var eventCount = reader.ReadInt32();
        for (var e = 0; e < eventCount; e++)
        {
            reader.ReadInt32(); // event type
            var functionCount = reader.ReadInt32();
            for (var f = 0; f < functionCount; f++)
            {
                SkipFunction(reader, version);
            }
        }
    }

    private static void SkipFunction(BinaryReader reader, int version)
    {
        reader.ReadInt32(); // function type
        reader.ReadInt32(); // target
        reader.ReadInt32(); // tick count
        reader.ReadInt32(); // tick interval
        reader.ReadBoolean(); // dolocalstats
        SkipRequirements(reader);

        var argCount = reader.ReadInt32();
        for (var a = 0; a < argCount; a++)
        {
            switch (reader.ReadByte())
            {
                case 1: reader.ReadInt32(); break;
                case 2: reader.ReadSingle(); break;
                case 3: reader.ReadString(); break;
                default:
                    throw new InvalidDataException("unsupported function argument tag");
            }
        }

        if (version >= 2 && reader.ReadBoolean())
        {
            reader.ReadInt32(); // leading zero words
            reader.ReadInt32(); // header 1
            reader.ReadInt32(); // header 2
            reader.ReadInt32(); // header 3
            SkipIntList(reader);
            SkipBytes(reader);
        }
    }

    private static void SkipRequirements(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            reader.ReadInt32(); // child operator
            reader.ReadInt32(); // operator
            reader.ReadInt32(); // stat
            reader.ReadInt32(); // target
            reader.ReadInt32(); // value
        }
    }

    private static void SkipIntList(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            reader.ReadInt32();
        }
    }

    private static void SkipBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        for (var i = 0; i < length; i++)
        {
            reader.ReadByte();
        }
    }

    private static void SkipRecordData(BinaryReader reader, int version)
    {
        if (!reader.ReadBoolean())
        {
            return;
        }

        reader.ReadInt32(); // header A
        reader.ReadInt32(); // header B
        reader.ReadInt32(); // header C
        reader.ReadString(); // description
        SkipIntList(reader); // block order

        var groups = reader.ReadInt32();
        for (var g = 0; g < groups; g++)
        {
            reader.ReadInt32(); // group key
            SkipIntList(reader);
        }

        SkipIntList(reader); // block 6 pairs

        var animSets = reader.ReadInt32();
        for (var s = 0; s < animSets; s++)
        {
            reader.ReadInt32(); // block key
            reader.ReadInt32(); // value
            var entries = reader.ReadInt32();
            for (var e = 0; e < entries; e++)
            {
                reader.ReadInt32(); // entry key
                SkipIntList(reader);
            }
        }

        var reqSets = reader.ReadInt32();
        for (var r = 0; r < reqSets; r++)
        {
            reader.ReadInt32(); // hook
            SkipIntList(reader); // triples
        }

        var shopBlocks = reader.ReadInt32();
        for (var b = 0; b < shopBlocks; b++)
        {
            reader.ReadInt32(); // event type
            var entries = reader.ReadInt32();
            for (var e = 0; e < entries; e++)
            {
                SkipBytes(reader);
            }
        }

        if (version >= 3)
        {
            var bareFunctions = reader.ReadInt32();
            for (var f = 0; f < bareFunctions; f++)
            {
                SkipFunction(reader, version);
            }
        }
    }
}
