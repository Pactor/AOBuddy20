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

using AOSharp.Clientless;
using AOSharp.Common.GameData;
using AOBuddy20.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog.Events;

namespace AOBuddy20.Brains;

/// <summary>
///     The nano library: the character's profession's castable nanos, from
///     GameData/Profiles/&lt;profession&gt;-nanos.json (tools/mp-nano-extractor over the client's
///     nanos.ocp - per entry id, name, nano line, level requirement, the cast requirements and
///     the effect summary, filed under the extractor's categories, see <see cref="NanoProfile" />).
///     Like the brains, it waits for the profession to come onto the wire: the load runs once,
///     post-login, from BotLoop's tick (<see cref="EnsureLoaded" />, the BrainBank pattern), and
///     the loaded list is immutable afterwards - pure reads from any thread.
///     The file is looked up by the profession enum name in lower case (Doctor ->
///     doctor-nanos.json, NanoTechnician -> nanotechnician-nanos.json). A profession without a
///     profile (Agent, Fixer, ... - the extractor has not covered them yet) loads as EMPTY, once,
///     with a warning: brains asking an empty library get nothing, which is the truthful answer
///     for "no known nanos", and the file can be dropped in later without code changes.
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class NanoLibrary
{
    private readonly string _profilesDir;
    private readonly ILogger<NanoLibrary> _logger;

    private bool _selected; // the load attempt ran - success, or a definitive miss (never retried)
    private bool _warnedBadProfession;
    private Profession _profession = Profession.Unknown;
    private readonly Dictionary<int, NanoProfile> _byId = new();

    public NanoLibrary(string baseDir, ILogger<NanoLibrary> logger)
    {
        _profilesDir = Path.Combine(baseDir, "GameData", "Profiles");
        _logger = logger;
    }

    /// <summary>The load attempt is done (successfully or as a definitive miss).</summary>
    public bool Loaded => _selected;

    public Profession Profession => _profession;

    /// <summary>The profession's nanos, all categories flattened. Empty until loaded, or when no
    /// profile exists for the profession.</summary>
    public IReadOnlyList<NanoProfile> Nanos { get; private set; } = Array.Empty<NanoProfile>();

    public NanoProfile? Find(int nanoId) => _byId.TryGetValue(nanoId, out var n) ? n : null;

    /// <summary>The nanos of one profile category ("Heals - Single Target", ...), file order kept.</summary>
    public IEnumerable<NanoProfile> InCategory(string category) =>
        Nanos.Where(n => string.Equals(n.Category, category, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     BotLoop calls this every tick; it loads once, on the first tick where the profession
    ///     is on the wire (Stat.Profession). Unreadable or Unknown stats just mean "not loaded
    ///     yet" - retried next tick, no noise.
    /// </summary>
    public void EnsureLoaded(LocalPlayer me)
    {
        if (_selected || me == null)
        {
            return;
        }

        if (!me.TryGetStat(Stat.Profession, out var raw))
        {
            return;
        }

        if (!Enum.IsDefined(typeof(Profession), (uint)raw))
        {
            // A profession value the enum does not know: warn once and keep waiting - the server
            // may still be filling the stats in (the brain bank's posture).
            if (!_warnedBadProfession)
            {
                _warnedBadProfession = true;
                _logger.LogWarning($"NANOS: profession stat reads {raw}, not a known profession - waiting.");
            }

            return;
        }

        var profession = (Profession)(uint)raw;
        if (profession == Profession.Unknown)
        {
            return;
        }

        _selected = true;
        _profession = profession;

        var file = Path.Combine(_profilesDir, profession.ToString().ToLowerInvariant() + "-nanos.json");
        if (!File.Exists(file))
        {
            _logger.LogWarning($"NANOS: no nano profile for {profession} ({Path.GetFileName(file)}) - " +
                               "the library stays empty until the file is dropped in.");
            return;
        }

        try
        {
            var dto = JsonConvert.DeserializeObject<NanoFileDto>(File.ReadAllText(file));
            if (dto?.Categories == null)
            {
                throw new InvalidDataException("no categories object in the profile");
            }

            var list = new List<NanoProfile>();
            foreach (var pair in dto.Categories)
            {
                foreach (var e in pair.Value ?? new List<NanoEntryDto>())
                {
                    var nano = new NanoProfile
                    {
                        NanoId = e.Id,
                        Name = e.Name ?? "",
                        Category = pair.Key,
                        NanoStrain = e.NanoStrain,
                        NanoLine = e.NanoLine ?? "",
                        MinLevel = e.MinLevel,
                        PetTargeted = e.PetTargeted,
                        CastReqs = e.CastReqs ?? new List<string>(),
                        Effects = e.EffectSummary ?? new List<string>(),
                    };
                    list.Add(nano);
                    _byId[nano.NanoId] = nano;
                }
            }

            Nanos = list;
            _logger.LogInformation($"NANOS: {profession} - loaded {list.Count} nanos in " +
                                   $"{dto.Categories.Count} categories from {Path.GetFileName(file)}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"NANOS: reading {file} failed - the library stays empty.");
        }
    }

    // ---- The profile file's shape (camelCase, tools/mp-nano-extractor) ---------------------

    private sealed class NanoFileDto
    {
        public int TotalCount;
        public Dictionary<string, List<NanoEntryDto>> Categories = new();
    }

    private sealed class NanoEntryDto
    {
        public int Id;
        public string? Name;
        public int NanoStrain;
        public string? NanoLine;
        public int MinLevel;
        public bool PetTargeted;
        public List<string>? CastReqs;
        public List<string>? EffectSummary;
    }
}
