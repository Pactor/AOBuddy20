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
using AOBuddy20.Brains;
using AOBuddy20.Configuration;
using AOBuddy20.Nav;
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

        /// <summary>
        ///     The nano-skill gains parsed from the effect text: stat id → +amount (127 MatMet,
        ///     128 BioMet, 130 MatCrea, 131 SpaceTime; the all-nano-skill composites and the
        ///     Weap/Nano wrangles carry all four). Empty for buffs that raise none.
        /// </summary>
        public Dictionary<int, int> Gains = new();

        /// <summary>
        ///     The formula's NanoLine - the server's stacking key (owner, 2026-10-06: same line
        ///     does not stack, only ONE of them runs and the rest is wasted). The id comes from
        ///     the menu JSON, the line itself from the PACK (NanoStrain, stat 75) - resolved
        ///     lazily in <see cref="Pool" />; null until then (nulls share one conservative
        ///     bucket in the planner).
        /// </summary>
        public int? NanoLine;
    }

    private static readonly Regex Plus = new(@"\+(\d+)", RegexOptions.Compiled);

    private readonly ILogger<BuffCatalog> _logger;
    private readonly List<BuffEntry> _buffs = new();

    public bool Loaded { get; private set; }
    public IReadOnlyList<BuffEntry> Buffs => _buffs;

    /// <summary>
    ///     Where the buff bots stand - the dance travels there before asking (the handshake's
    ///     deferred 4a.1b). The playfield is required to travel; the coordinates are the bot spot
    ///     itself and may be null (playfield-level travel only).
    /// </summary>
    public (int Playfield, float? X, float? Y, float? Z)? BotLocation { get; private set; }

    /// <summary>
    ///     WHO to tell - from the same location block as the spot ("botName"): Chewysfix on
    ///     RubiKa, Codedoc on RK2019. The tell target is the CATALOG's business (dimension-picked
    ///     with the buff list), not the conf's - BuffBotName stays the 'buffs' owner command's
    ///     target only.
    /// </summary>
    public string? BotName { get; private set; }

    /// <summary>
    ///     The invite FAMILY - from the same location block ("botFamily", e.g. "Chewys"): the
    ///     team invite may come from ANY toon whose name starts with it (the invite packet
    ///     carries the inviter's name, and the owner's bots answer as a Chewys* family). A
    ///     JSON without the field falls back to botName itself.
    /// </summary>
    public string? BotFamily { get; private set; }

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
                    if ((bool?)b["team"] == true)
                    {
                        // TEAM-cast entries never enter the list (owner, 2026-10-06: "let's not
                        // use the team wrangles - maybe they only work while in a team, and the
                        // bot kicks us right afterwards"): the kick races the cast, the buff
                        // never lands, and the stage's gate stays open. Plans use self-landed
                        // buffs only.
                        continue;
                    }

                    var effect = (string?)b["effect"] ?? "";
                    _buffs.Add(new BuffEntry
                    {
                        Profession = prof,
                        Name = (string?)b["name"] ?? "",
                        Effect = effect,
                        Ncu = (int?)b["ncu"] ?? 0,
                        NanoId = (int?)b["id"],
                        Tell = (string?)b["tell"] ?? "",
                        Gains = ParseGains(effect),
                    });
                }
            }

            Loaded = _buffs.Count > 0;

            var loc = doc["location"];
            var playfield = (int?)loc?["playfield"];
            if (loc != null && playfield is > 0)
            {
                BotLocation = (playfield.Value, (float?)loc["x"], (float?)loc["y"], (float?)loc["z"]);
                var botName = (string?)loc["botName"];
                if (!string.IsNullOrWhiteSpace(botName))
                {
                    BotName = botName;
                }

                var botFamily = (string?)loc["botFamily"];
                BotFamily = string.IsNullOrWhiteSpace(botFamily) ? BotName : botFamily;
            }

            _logger.LogInformation(
                $"BUFFS: {_buffs.Count} catalog entries from {Path.GetFileName(file)}" +
                (BotLocation.HasValue ? $", bots at {Zoning.Name(BotLocation.Value.Playfield)}." : "."));
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

    /// <summary>The menu entry behind a tell code, or null.</summary>
    public BuffEntry? Find(string tell)
    {
        return _buffs.FirstOrDefault(b => b.Tell.Equals(tell, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The menu entry for a nano id (the renewal watch maps kept ids to codes), or null.</summary>
    public BuffEntry? FindById(int nanoId)
    {
        return _buffs.FirstOrDefault(b => b.NanoId == nanoId);
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
    ///     The ordered tells to lift our nano skills by <paramref name="need" />: the NCU buff
    ///     first, then the SMALLEST safe nano-skill buff closing the gap - or the biggest if none
    ///     closes it (we over-buff for the summon moment, then downshift; see PETBRAIN-DESIGN.md).
    ///     The raw-gap entry point: the caller computes the need from whatever skill pair its
    ///     target gates on (the MP pet lines gap on BioMet (128) / MatMet (127) + TS, not only
    ///     MC/TS). Empty when the catalog is not loaded.
    /// </summary>
    public List<string> PlanForSkillGap(LocalPlayer me, int need)
    {
        var tells = new List<string>();
        if (!Loaded || me == null || need <= 0)
        {
            return tells;
        }

        var ncu = NcuTell();
        if (ncu != null)
        {
            tells.Add(ncu);
        }

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

    /// <summary>
    ///     The ordered tells to summon a pet needing <paramref name="reqMc" /> / <paramref name="reqTs" />
    ///     MC/TS: the gap is read from our current (buffed) MC and TS.
    /// </summary>
    public List<string> PlanForPetSummon(LocalPlayer me, int reqMc, int reqTs)
    {
        if (!Loaded || me == null)
        {
            return new List<string>();
        }

        var curMc = me.TryGetStat(Stat.MaterialCreation, out var mc) ? mc : 0;
        var curTs = me.TryGetStat(Stat.SpaceTime, out var ts) ? ts : 0;
        var need = Math.Max(Math.Max(0, reqMc - curMc), Math.Max(0, reqTs - curTs));
        return PlanForSkillGap(me, need);
    }

    // ---- The staged dance (the MP buff-first; owner's L50 froob sequence, 2026-10-05/06) ----

    /// <summary>The four nano skills the pet summon tiers gate on.</summary>
    private static readonly int[] NanoSkills = { 127, 128, 130, 131 };

    /// <summary>The pack's GreaterThan operator on cast requirements - stat &gt; value at runtime
    /// (every skill and level gate in the pack carries it; observed value 2 throughout).</summary>
    private const int OpGreaterThan = 2;

    private const int LongTermSeconds = 3600; // the obedience stage wants 1hr+ buffs, nothing shorter

    /// <summary>
    ///     Free NCU right now - the hard cap every plan runs under ("NCU is the hard cap, if it
    ///     doesn't fit, it won't work", owner 2026-10-06).
    /// </summary>
    public static int FreeNcu(LocalPlayer me)
    {
        return (me.TryGetStat(Stat.MaxNCU, out var max) ? max : 0)
             - (me.TryGetStat(Stat.CurrentNCU, out var cur) ? cur : 0);
    }

    /// <summary>
    ///     The NanoLines already running on us - the lines every plan must treat as OCCUPIED.
    ///     Owner, 2026-10-06: "group all buffs by their nanoline first, then start calculations" -
    ///     the kept Time&amp;Space carriers of the earlier peak stages (and anything else up) fence
    ///     their line off: an ask on an occupied line SUPERSEDES what is running instead of
    ///     stacking on it, so its +N must never be counted as new gain.
    /// </summary>
    public static HashSet<int> RunningLines(LocalPlayer me)
    {
        var lines = new HashSet<int>();
        foreach (var b in me.Buffs)
        {
            if (b?.NanoItem == null)
            {
                continue;
            }

            // The pack's NanoStrain (stat 75) is the stacking key - the id comes off the running
            // buff, everything else from the pack (same law as the pool entries). A buff the
            // pack does not know fences nothing - its group is unknowable.
            var strain = NanoLibrary.Find(b.Id)?.Stat(75);
            if (strain is > 0)
            {
                lines.Add(strain.Value);
            }
        }

        return lines;
    }

    /// <summary>
    ///     PEAK STAGE: the tells that reach the highest template branch of a summon the current
    ///     stats don't satisfy yet - "get the buffs which fit in your NCU and give you the highest
    ///     TimeSpace and MatCrea/TimeSpace and BioMet/TimeSpace and MatMet values" (owner). The
    ///     branches are evaluated top template down against the pack's per-branch gates; the
    ///     first branch with an NCU-feasible cover wins, and when the mochams don't fit the same
    ///     search simply lands on infuses and a lower wrangle. Short buffs are legitimate peak
    ///     tools here - the 3-minute wrangle exists to be cancelled after the pets are out.
    ///     Empty when the top branch is already reachable (just summon) or nothing fits.
    /// </summary>
    public List<string> PlanForPeak(LocalPlayer me, int freeNcu, IReadOnlyList<SummonBranch> branches)
    {
        return PlanForPeakEntries(me, freeNcu, branches).Select(c => c.Tell).ToList();
    }

    /// <summary>
    ///     <see cref="PlanForPeak" /> with the full entries - the dance needs the NANO IDS of what
    ///     it asked for, because the later stages cancel those buffs off again.
    /// </summary>
    public IReadOnlyList<BuffEntry> PlanForPeakEntries(LocalPlayer me, int freeNcu, IReadOnlyList<SummonBranch> branches)
    {
        var none = Array.Empty<BuffEntry>();
        LastPeakReachable = false;
        if (!Loaded || me == null || branches == null || branches.Count == 0)
        {
            return none;
        }

        foreach (var branch in branches.OrderByDescending(b => b.TemplateLevel))
        {
            var mins = new Dictionary<int, int>();
            foreach (var r in branch.Requirements)
            {
                if (r.Operator == OpGreaterThan && NanoSkills.Contains(r.Stat))
                {
                    mins[r.Stat] = Math.Max(mins.GetValueOrDefault(r.Stat), r.Value + 1);
                }
            }

            if (mins.Count == 0)
            {
                continue; // the bottom branch gates on nothing we can buff - casting it needs no asks
            }

            var gaps = SkillGaps(me, mins);
            if (gaps.Count == 0)
            {
                // The top branch is already reachable - nothing to ask for. "Empty" here means
                // SUCCESS: the swap can run on the running buffs.
                LastPeakReachable = true;
                return none;
            }

            // The lines already running fence their entries out BEFORE the calculation (owner,
            // 2026-10-06): the kept TS carriers of the earlier peak stages are in the current
            // stats already - another buff on their line would only supersede them.
            var occupied = RunningLines(me);
            var cover = Cover(freeNcu, gaps,
                Pool(me, new HashSet<int>(gaps.Keys), longTermOnly: false, occupied));
            if (cover == null)
            {
                continue; // this template is out of reach - try the one below it
            }

            LastPeakReachable = true;
            _logger.LogInformation(
                $"BUFFS: peak plan for template {branch.TemplateLevel} ({cover.Sum(c => c.Ncu)} NCU, " +
                $"lines occupied: {(occupied.Count == 0 ? "none" : string.Join(",", occupied.OrderBy(x => x)))}): " +
                $"{string.Join(" ", cover.Select(c => $"{c.Tell}(L{c.NanoLine})"))}.");
            return cover;
        }

        return none;
    }

    /// <summary>
    ///     What the LAST <see cref="PlanForPeakEntries" /> empty result meant: true = the gates
    ///     are met on the running buffs (empty is success - the swap can run); false = no branch
    ///     was coverable with the freed budget (the line stays as it is - no asks, no swap).
    /// </summary>
    public bool LastPeakReachable { get; private set; }

    /// <summary>
    ///     OBEDIENCE STAGE: the cheapest LONG-TERM tells (effects of an hour or more - the
    ///     3-minute wrangle is explicitly not wanted here) that lift the current stats to every
    ///     given total, spending as little NCU as possible: "use the buffs which cost least NCU
    ///     to satisfy the pet's obedience requirements. You want as much free NCU after that as
    ///     possible" (owner, 2026-10-05). <paramref name="minimumTotals" /> carries the absolute
    ///     per-skill totals - 80% of the summoned tier's requirements, caller-computed from the
    ///     cast snapshots. Empty when nothing is needed or nothing fits.
    /// </summary>
    public List<string> PlanForObedience(LocalPlayer me, int freeNcu, IReadOnlyDictionary<int, int> minimumTotals)
    {
        return PlanForObedienceEntries(me, freeNcu, minimumTotals).Select(c => c.Tell).ToList();
    }

    /// <summary><see cref="PlanForObedience" /> with the full entries (the dance keeps the ids).</summary>
    public IReadOnlyList<BuffEntry> PlanForObedienceEntries(LocalPlayer me, int freeNcu,
        IReadOnlyDictionary<int, int> minimumTotals)
    {
        var none = Array.Empty<BuffEntry>();
        if (!Loaded || me == null || minimumTotals == null || minimumTotals.Count == 0)
        {
            return none;
        }

        var gaps = SkillGaps(me, minimumTotals);
        if (gaps.Count == 0)
        {
            return none;
        }

        var occupied = RunningLines(me); // same law: a running line cannot take a second buff
        var cover = Cover(freeNcu, gaps, Pool(me, new HashSet<int>(gaps.Keys), longTermOnly: true, occupied));
        if (cover == null)
        {
            return none;
        }

        _logger.LogInformation(
            $"BUFFS: obedience plan ({cover.Sum(c => c.Ncu)} NCU, " +
            $"lines occupied: {(occupied.Count == 0 ? "none" : string.Join(",", occupied.OrderBy(x => x)))}): " +
            $"{string.Join(" ", cover.Select(c => $"{c.Tell}(L{c.NanoLine})"))}.");
        return cover;
    }

    // ---- The comfort plan -------------------------------------------------------------------

    /// <summary>
    ///     The SELF-DECIDED comfort tells (owner, 2026-10-06 - PetQoLTells is obsolete, and
    ///     "QoL is long term nanos ONLY"). LONG-TERM entries only (an hour or more - a wrangle
    ///     is a short buff and is out by definition), gated by level and receivability, and
    ///     picked BEST-PER-NANOLINE ("always check the nano lines"): the fixer long HoT and
    ///     the doc Health+HoT stack, the Essence stacks with the doc buff, the general-damage
    ///     lines stack - so every category tells one buff per LINE, spending the free-NCU
    ///     budget strongest-first. Priority: heal-over-time, run speed, max health, the skill
    ///     buffs by mode (weapon = matching the WIELDED weapon's own skills, nano = the
    ///     strongest long-term MatMet buff), then general damage, and finally damage SHIELDS
    ///     with whatever budget is left (owner: "like coruscating screen").
    /// </summary>
    public List<string> PlanForComfort(LocalPlayer me, bool attackNanoSkills,
        IReadOnlyCollection<string> weaponKeys)
    {
        var tells = new List<string>();
        if (!Loaded || me == null)
        {
            return tells;
        }

        var level = me.TryGetStat(Stat.Level, out var l) ? l : 0;
        var budget = FreeNcu(me);
        var taken = new HashSet<int>(RunningLines(me)); // running lines supersede - never re-planned
        _logger.LogInformation($"BUFFS: comfort - budget {budget} NCU, lines taken: {(taken.Count == 0 ? "none" : string.Join(",", taken.OrderBy(x => x)))}.");

        // The strongest entry of EACH matching nanoline (owner: "always check the nano
        // lines"), in score order, while the NCU budget pays. A line already running or
        // already planned is skipped; a pick too pricey for the left budget is logged.
        void AddCategory(string label, Func<BuffEntry, bool> match, Func<BuffEntry, int> score,
            bool single = false)
        {
            var bests = new Dictionary<int, (BuffEntry B, int S)>();
            foreach (var b in _buffs)
            {
                if (b.NanoId == null || !match(b) ||
                    PackDurationSeconds(b.NanoId.Value) < LongTermSeconds)
                {
                    continue;
                }

                var levelReq = PackLevelReq(b.NanoId.Value);
                if (levelReq.HasValue && level <= levelReq.Value)
                {
                    continue; // never ask for what answers "your level is too low"
                }

                if (!Receivable(me, b.NanoId.Value))
                {
                    continue;
                }

                var strain = NanoLibrary.Find(b.NanoId.Value)?.Stat(75) ?? 0;
                var s = score(b);
                if (!bests.TryGetValue(strain, out var cur) || s > cur.S)
                {
                    bests[strain] = (b, s);
                }
            }

            foreach (var kv in bests.OrderByDescending(kv => kv.Value.S))
            {
                if (single)
                {
                    bests = new Dictionary<int, (BuffEntry B, int S)> { [kv.Key] = kv.Value };
                    break;
                }
            }

            foreach (var kv in bests)
            {
                var b = kv.Value.B;
                var strain = NanoLibrary.Find(b.NanoId.Value)?.Stat(75) ?? 0;
                if (taken.Contains(strain))
                {
                    continue;
                }

                if (b.Ncu > budget)
                {
                    _logger.LogInformation(
                        $"BUFFS: comfort - {label}: {b.Name} needs {b.Ncu} NCU, {budget} left - skipped.");
                    continue;
                }

                budget -= b.Ncu;
                taken.Add(strain);
                tells.Add(b.Tell);
                _logger.LogInformation(
                    $"BUFFS: comfort - {label}: {b.Name} ({b.Tell}, {b.Ncu} NCU, line {strain}).");
            }
        }

        static int Amount(BuffEntry b)
        {
            var m = Plus.Match(b.Effect);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        static bool Has(BuffEntry b, string text)
        {
            return b.Effect.ToLowerInvariant().Contains(text) || b.Name.ToLowerInvariant().Contains(text);
        }

        var hotWord = new Regex(@"\bhot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // THE PRIORITY (owner, 2026-10-06): long HoT / Health+Long HoT first, then the Essence
        // (it stacks with the doc buff), then the damage buff (Horde), then run speed, then
        // damage SHIELDS with what is left, and finally the weapon/nano stat buffs with
        // whatever remains after all that.
        AddCategory("long heal over time",
            b => Has(b, "long hot") || (hotWord.IsMatch(b.Effect) && Has(b, "max health")), Amount);
        AddCategory("essence (health)", b => Has(b, "essence"), Amount);
        AddCategory("general damage",
            b =>
            {
                var e = b.Effect.ToLowerInvariant();
                return (e.Contains("dmg") || e.Contains("damage")) && !e.Contains("damage shield");
            }, Amount);
        AddCategory("run speed", b => Has(b, "run speed"), Amount);
        AddCategory("damage shields",
            b => Has(b, "damage shield") || Has(b, "shield ac"), Amount);

        if (attackNanoSkills)
        {
            AddCategory("attack-nano skills (MatMet)",
                b => b.Gains.ContainsKey(127), b => b.Gains.TryGetValue(127, out var g) ? g : 0, single: true);
        }
        else if (weaponKeys != null && weaponKeys.Count > 0)
        {
            AddCategory("weapon skills", b => weaponKeys.Any(k => Has(b, k)), Amount, single: true);
        }
        else
        {
            _logger.LogInformation("BUFFS: comfort - weapon skills: nothing wielded to buff.");
        }

        return tells.Distinct().ToList();
    }

    // ---- The dance's machinery --------------------------------------------------------------

    /// <summary>Per-skill shortfalls of the current (buffed) stats against the given totals.</summary>
    private static Dictionary<int, int> SkillGaps(LocalPlayer me, IReadOnlyDictionary<int, int> totals)
    {
        var gaps = new Dictionary<int, int>();
        foreach (var kv in totals)
        {
            var cur = me.TryGetStat((Stat)kv.Key, out var v) ? v : 0;
            if (kv.Value > cur)
            {
                gaps[kv.Key] = kv.Value - cur;
            }
        }

        return gaps;
    }

    /// <summary>
    ///     The menu entries that may enter a plan for the wanted skills: nano-skill gains that
    ///     overlap them, the character meets the formula's level gate (the pack's Level &gt; N on
    ///     the ToUse action - never ask for a code that answers "your level is too low"), - when
    ///     <paramref name="longTermOnly" /> - the effect runs an hour or more (the 3-minute
    ///     wrangles drop out here), and the entry's line is NOT already running
    ///     (<paramref name="occupied" /> - grouped out before any calculation). Ordered
    ///     cheapest-Ncu first.
    /// </summary>
    private List<BuffEntry> Pool(LocalPlayer me, HashSet<int> wantedSkills, bool longTermOnly,
        HashSet<int> occupied)
    {
        var level = me.TryGetStat(Stat.Level, out var l) ? l : 0;
        var pool = new List<BuffEntry>();
        foreach (var b in _buffs)
        {
            if (b.NanoId == null || b.Ncu <= 0 || b.Gains.Count == 0)
            {
                continue;
            }

            if (!b.Gains.Keys.Any(wantedSkills.Contains))
            {
                continue;
            }

            var levelReq = PackLevelReq(b.NanoId.Value);
            if (levelReq.HasValue && level <= levelReq.Value)
            {
                continue;
            }

            if (!Receivable(me, b.NanoId.Value))
            {
                continue;
            }

            if (longTermOnly && PackDurationSeconds(b.NanoId.Value) < LongTermSeconds)
            {
                continue;
            }

            // The stacking key: the id comes from the menu JSON, EVERYTHING else from the pack -
            // NanoStrain (stat 75) is the game's own stacking group, StackingOrder (551) says who
            // supersedes (NanoProfile's charter). A TEAM-cast formula (the "132" wrangle) never
            // lands as a buff itself: it TeamCastNanos a CHILD onto the team, and the CHILD
            // carries the strain the receiver actually gets - strain 220, the same line as the
            // solo wranglers, which is why the team wrangle supersedes them (owner, 2026-10-06:
            // "team wranglers cast 2 different nanos"). So a strainless wrapper resolves through
            // its TeamCastNano child. No ItemData here - a wrong source inside this family.
            if (b.NanoLine == null)
            {
                var packStrain = NanoLibrary.Find(b.NanoId.Value)?.Stat(75);
                if (!(packStrain is > 0))
                {
                    var child = NanoLibrary.Find(b.NanoId.Value)?.TeamCastChild ?? 0;
                    if (child > 0)
                    {
                        packStrain = NanoLibrary.Find(child)?.Stat(75);
                    }
                }

                if (packStrain is > 0)
                {
                    b.NanoLine = packStrain;
                }
            }

            if (b.NanoLine.HasValue && occupied.Contains(b.NanoLine.Value))
            {
                continue; // the line is already running - a second buff on it supersedes, never stacks
            }

            pool.Add(b);
        }

        return pool.OrderBy(b => b.Ncu).ToList();
    }

    /// <summary>
    ///     Can this entry's formula be cast ON US? The use-TARGET requirements (ToUse reqs with
    ///     Target == 18 - the composite masteries/infuses/mochams carry "recipient must have
    ///     Shadowlands" as BitAnd on Stat.Expansion, target 18; the froob composites and the
    ///     Mocham's Gifts carry none) decide receivability. A target gate we cannot evaluate
    ///     counts as FAILED - never ask for what we can't prove receivable (owner, 2026-10-06:
    ///     the bot asked for masteries on a froob).
    /// </summary>
    private static bool Receivable(LocalPlayer me, int nanoId)
    {
        var nano = NanoLibrary.Find(nanoId);
        if (nano == null)
        {
            return false;
        }

        foreach (var action in nano.Actions)
        {
            foreach (var r in action.Requirements)
            {
                if (r.Target != 18)
                {
                    continue; // caster-side reqs are the bot's problem, not ours
                }

                var has = me.TryGetStat((Stat)r.Stat, out var v);
                switch (r.Operator)
                {
                    case 22: // BitAnd - the expansion gates ("has Shadowlands")
                        if (!has || (v & r.Value) != r.Value)
                        {
                            return false;
                        }

                        break;
                    case 2: // GreaterThan
                        if (!has || v <= r.Value)
                        {
                            return false;
                        }

                        break;
                    case 0: // EqualTo
                        if (!has || v != r.Value)
                        {
                            return false;
                        }

                        break;
                    default:
                        return false; // unevaluable target gate - fail safe
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     The cheapest subset of the pool whose gains close every gap within
    ///     <paramref name="freeNcu" />, or null. The pools are a dozen entries, so the search is
    ///     exhaustive over subsets (capped at the 14 cheapest - beyond that nothing helps a
    ///     min-NCU cover). STACKING (owner, 2026-10-06: "only ONE is counting - they are all on
    ///     the same nanoline, so it doesn't help to cast a 7 wrangle and a 35 wrangle, only the
    ///     35 would count"): a set holds AT MOST ONE entry per NanoLine - the server keeps a
    ///     single buff of a line running, so same-line gains neither add up nor blend per stat,
    ///     and a stack of wrangles is wasted tells. A gap one line's best cannot close must be
    ///     closed by OTHER lines (composites, infuses, mochams), or not at all - then the peak
    ///     planner falls to the next template branch down.
    /// </summary>
    private static List<BuffEntry>? Cover(int freeNcu, Dictionary<int, int> gaps, List<BuffEntry> pool)
    {
        pool = TrimForSearch(pool, 14, 3);

        List<BuffEntry>? best = null;
        var bestNcu = int.MaxValue;
        var n = pool.Count;
        for (var mask = 1; mask < (1 << n); mask++)
        {
            var ncu = 0;
            var covered = new Dictionary<int, int>();
            var seenLines = new HashSet<int>();
            var feasible = true;
            for (var i = 0; i < n && feasible; i++)
            {
                if ((mask & (1 << i)) == 0)
                {
                    continue;
                }

                // One entry per NanoLine: the second one cast only supersedes the first - it
                // adds no gains, only NCU. Nulls (unresolvable line) share one conservative
                // bucket: assumed to clash with each other.
                if (!seenLines.Add(pool[i].NanoLine ?? 0))
                {
                    feasible = false;
                    break;
                }

                ncu += pool[i].Ncu;
                if (ncu > freeNcu)
                {
                    feasible = false;
                    break;
                }

                foreach (var g in pool[i].Gains)
                {
                    if (gaps.ContainsKey(g.Key))
                    {
                        covered[g.Key] = covered.GetValueOrDefault(g.Key) + g.Value;
                    }
                }
            }

            if (!feasible || gaps.Any(kv => covered.GetValueOrDefault(kv.Key) < kv.Value))
            {
                continue;
            }

            if (ncu < bestNcu)
            {
                bestNcu = ncu;
                best = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) != 0).Select(i => pool[i]).ToList();
            }
        }

        return best;
    }

    /// <summary>Trim the pool for the exhaustive search: at most <paramref name="perLine" /> per
    /// NanoLine - the strongest tiers of the line (the search may pick any ONE of them; a weaker
    /// same-line entry in a feasible set is always droppable) - then the cheapest
    /// <paramref name="total" /> overall, so a long wrangle ladder cannot crowd the other lines
    /// out of the cap.</summary>
    private static List<BuffEntry> TrimForSearch(List<BuffEntry> pool, int total, int perLine)
    {
        var trimmed = pool
            .GroupBy(b => b.NanoLine ?? 0)
            .SelectMany(g => g.OrderByDescending(b => b.Gains.Values.Sum()).Take(perLine))
            .OrderBy(b => b.Ncu)
            .ToList();
        return trimmed.Count > total ? trimmed.Take(total).ToList() : trimmed;
    }

    /// <summary>The formula's character-level gate (the ToUse action's Level &gt; N), or null.</summary>
    private static int? PackLevelReq(int nanoId)
    {
        var nano = NanoLibrary.Find(nanoId);
        if (nano == null)
        {
            return null;
        }

        int? req = null;
        foreach (var action in nano.Actions)
        {
            foreach (var r in action.Requirements)
            {
                if (r.Stat == (int)Stat.Level && r.Operator == OpGreaterThan)
                {
                    req = Math.Max(req ?? 0, r.Value);
                }
            }
        }

        return req;
    }

    /// <summary>The formula's effect run time in seconds (TimeExist, stat 8), 0 when unknown.</summary>
    private static int PackDurationSeconds(int nanoId)
    {
        return NanoLibrary.Find(nanoId)?.TimeExistSeconds ?? 0;
    }

    /// <summary>
    ///     The nano-skill gains an effect text promises: the per-skill lines by name (SpaceTime,
    ///     MatCrea/Creation, BioMet/Biological, MatMet/Metamorphose - Biological checked before
    ///     the shared "metamorph"), the all-nano composites and the Weap/Nano wrangles as all
    ///     four skills. No nano-skill wording, no gains.
    /// </summary>
    private static Dictionary<int, int> ParseGains(string effect)
    {
        var gains = new Dictionary<int, int>();
        var m = Plus.Match(effect);
        if (!m.Success)
        {
            return gains;
        }

        var amount = int.Parse(m.Groups[1].Value);
        var text = effect.ToLowerInvariant();
        int? skill = text.Contains("space") ? 131
            : text.Contains("matcrea") || text.Contains("creation") ? 130
            : text.Contains("biomet") || text.Contains("biological") ? 128
            : text.Contains("matmet") || text.Contains("metamorph") ? 127
            : null;

        if (skill.HasValue)
        {
            gains[skill.Value] = amount;
            return gains;
        }

        if (text.Contains("all nano") || text.Contains("nano skill") || text.Contains("weap/nano"))
        {
            foreach (var s in NanoSkills)
            {
                gains[s] = amount;
            }
        }

        return gains;
    }
}
