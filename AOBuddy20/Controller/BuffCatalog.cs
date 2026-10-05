// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: BuffCatalog.cs
//
// Last modified: 2026-10-05
// Created:       2026-10-05
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

using System.IO;
using System.Text.RegularExpressions;
using AOBuddy20.Configuration;
using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace AOBuddy20.Controlling;

/// <summary>
///     BUFF CATALOG (step 4a.2, pet-focused) - the public buff bot's menu: each buff's tell code,
///     NCU and nano id, from GameData/ChewysBuffs.json (RubiKa). Ported from AOBuddy10. Dimension
///     aware: RubiKa2019 uses CodedocBuffs.json, whose selection model is level-locked and different
///     enough that its proper handling is 4a.3 - here it is loaded best-effort with the same shape.
///     This first pass serves ONLY the pet brain's buff-first summon: produce the tells to lift the
///     caster's Matter Creation / Time and Space enough to summon a better pet, highest NCU buff
///     FIRST (owner, 2026-10-05: it expands Max NCU so the rest fit), then one nano-skill buff.
///     SAFE SET: only buffs that raise BOTH MC and TS (the all-nano-skill composites and the
///     weapon/nano Skill Wrangler ladder) and that are receivable at any level (froob, Level &gt; 0)
///     - the level-gated Umbral wranglers and the multi-hour Mochams are left out until the full
///     optimizer adds proper receiver-requirement gating (TODO 4a.2-full).
/// </summary>
[MinLogLevel(LogEventLevel.Debug)]
public sealed class BuffCatalog
{
    public sealed class BuffEntry
    {
        public string Profession = "";
        public string Name = "";
        public string Effect = "";
        public int Ncu;
        public int? NanoId;
        public string Tell = "";
    }

    private static readonly Regex Plus = new(@"\+(\d+)", RegexOptions.Compiled);

    private readonly ILogger<BuffCatalog> _logger;
    private readonly List<BuffEntry> _buffs = new();

    public bool Loaded { get; private set; }
    public IReadOnlyList<BuffEntry> Buffs => _buffs;

    public BuffCatalog(AccountInfo config, ILogger<BuffCatalog> logger)
    {
        _logger = logger;
        var is2019 = (config.Dimension ?? "").Replace(" ", "").Equals("RubiKa2019", StringComparison.OrdinalIgnoreCase);
        var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData",
            is2019 ? "CodedocBuffs.json" : "ChewysBuffs.json");
        Load(file);
    }

    private void Load(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                _logger.LogWarning($"BUFFS: no buff catalog at {file} - computed buff plans are off (config tells still work).");
                return;
            }

            var doc = JObject.Parse(File.ReadAllText(file));
            foreach (var p in doc["professions"] ?? new JArray())
            {
                var prof = (string?)p["name"] ?? "";
                foreach (var b in p["buffs"] ?? new JArray())
                {
                    _buffs.Add(new BuffEntry
                    {
                        Profession = prof,
                        Name = (string?)b["name"] ?? "",
                        Effect = (string?)b["effect"] ?? "",
                        Ncu = (int?)b["ncu"] ?? 0,
                        NanoId = (int?)b["id"],
                        Tell = (string?)b["tell"] ?? "",
                    });
                }
            }

            Loaded = _buffs.Count > 0;
            _logger.LogInformation($"BUFFS: {_buffs.Count} catalog entries from {Path.GetFileName(file)}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"BUFFS: failed to read {Path.GetFileName(file)}.");
        }
    }

    /// <summary>The NCU buff's tell (the Fixer Max-NCU ladder), or null if the catalog has none.</summary>
    public string? NcuTell()
    {
        var ncu = _buffs.FirstOrDefault(b => b.Tell.Equals("ncu", StringComparison.OrdinalIgnoreCase)
                                             || b.Effect.Contains("NCU", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(ncu?.Tell) ? null : ncu.Tell;
    }

    /// <summary>
    ///     The safe MC/TS buffs (raise BOTH, receivable at any level), each with its parsed +N gain,
    ///     smallest first. The all-nano-skill composites and the weapon/nano Skill Wrangler ladder;
    ///     the level-gated Umbral and the multi-hour Mochams are excluded (TODO 4a.2-full: proper
    ///     receiver-requirement gating lets those back in).
    /// </summary>
    public List<(BuffEntry Entry, int Gain)> SafeNanoSkillBuffs()
    {
        var result = new List<(BuffEntry, int)>();
        foreach (var b in _buffs)
        {
            var eff = b.Effect;
            var raisesBoth = eff.Contains("Nano skills", StringComparison.OrdinalIgnoreCase)
                             || eff.Contains("Nano Skills", StringComparison.OrdinalIgnoreCase);
            if (!raisesBoth)
            {
                continue; // single-skill (MatCrea-only / SpaceTime-only) needs two tells - optimizer's job
            }

            if (eff.Contains("Umbral", StringComparison.OrdinalIgnoreCase)
                || eff.Contains("Hour", StringComparison.OrdinalIgnoreCase))
            {
                continue; // level-gated - excluded until receiver-req gating (TODO)
            }

            var m = Plus.Match(eff);
            if (!m.Success)
            {
                continue;
            }

            result.Add((b, int.Parse(m.Groups[1].Value)));
        }

        return result.OrderBy(x => x.Item2).ToList();
    }

    /// <summary>
    ///     The ordered tells to summon a pet needing <paramref name="reqMc" /> / <paramref name="reqTs" />
    ///     MC/TS: the NCU buff first, then the SMALLEST safe nano-skill buff that closes the gap from
    ///     our current (buffed) skills - or the biggest if none closes it (we over-buff for the summon
    ///     moment, then downshift; see PETBRAIN-DESIGN.md). Empty when the catalog is not loaded.
    /// </summary>
    public List<string> PlanForPetSummon(LocalPlayer me, int reqMc, int reqTs)
    {
        var tells = new List<string>();
        if (!Loaded || me == null)
        {
            return tells;
        }

        var ncu = NcuTell();
        if (ncu != null)
        {
            tells.Add(ncu);
        }

        var curMc = me.TryGetStat(Stat.MaterialCreation, out var mc) ? mc : 0;
        var curTs = me.TryGetStat(Stat.SpaceTime, out var ts) ? ts : 0;
        var need = Math.Max(Math.Max(0, reqMc - curMc), Math.Max(0, reqTs - curTs));

        var cands = SafeNanoSkillBuffs();
        if (cands.Count > 0)
        {
            var pick = cands.FirstOrDefault(x => x.Gain >= need);
            if (pick.Entry == null)
            {
                pick = cands[^1]; // none closes the gap: the biggest we safely can
            }

            tells.Add(pick.Entry.Tell);
        }

        return tells;
    }
}
