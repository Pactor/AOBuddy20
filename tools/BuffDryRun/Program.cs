// ---------------------------------------------------------------------------------------
// BuffDryRun - a standalone console+web tool that runs the REAL AOBuddy20 buff planner
// (BuffCatalog.BuildControlPlan) against a MADE-UP character, with no game client, no login
// and no network. You type a profession, level, NCU and skills (the values you'd read from
// GetStat), plus the pet/nano ids he wants to cast; the tool reports - from the live nano
// data - whether he can cast/summon it, what buffs (incl. a 131 wrangle) close the gap, the
// NCU those cost, and whether the pet stays OE-safe after the wrangle drops.
//
// It reuses the bot's own code, so "it works here" means "it works in the bot". Nothing in
// AOBuddy20 or the SDK is changed by running this.
//
// Run:  BuffDryRun.exe [path-to-GameData-parent]   (defaults to ..\Build)
// Open: http://localhost:9090
// ---------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using AOBuddy20.Brains;
using AOBuddy20.Configuration;
using AOBuddy20.Controlling;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace BuffDryRun;

internal static class Program
{
    private const int Port = 9090;
    private const int McStat = (int)Stat.MaterialCreation; // 130
    private const int TsStat = (int)Stat.SpaceTime;        // 131

    private static BuffCatalog _catalog;
    private static string _dataDir;

    private static void Main(string[] args)
    {
        // Where the GameData folder (nanos.ocp + ItemData.bin/.idx + the buff-bot JSONs) lives.
        _dataDir = ResolveDataDir(args.Length > 0 ? args[0] : null);
        Directory.SetCurrentDirectory(_dataDir); // ItemData reads "GameData\ItemData.bin" relative to CWD
        Console.WriteLine($"[BuffDryRun] data dir: {_dataDir}");

        if (!File.Exists(Path.Combine(_dataDir, "GameData", "nanos.ocp")))
        {
            Console.WriteLine("[BuffDryRun] ERROR: GameData\\nanos.ocp not found under the data dir.");
            Console.WriteLine("Pass the folder that contains GameData, e.g.  BuffDryRun.exe E:\\Funcom\\AOBuddy20\\Build");
            return;
        }

        // Load the real nano library (offline, same call the bot makes at startup).
        NanoLibrary.Load(_dataDir, s => Console.WriteLine("[nanolib] " + s));

        // Build the real planner once. Dimension empty => RubiKa profile (Chewy menu).
        try
        {
            _catalog = new BuffCatalog(new AccountInfo { Dimension = "" }, NullLogger<BuffCatalog>.Instance);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[BuffDryRun] BuffCatalog failed to load: " + ex);
            Console.WriteLine("(The planner needs the buff-bot JSON in GameData; the page will still load and report this.)");
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        Console.WriteLine($"[BuffDryRun] open  http://localhost:{Port}  (Ctrl+C to stop)");

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }

            try
            {
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/plan")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    var body = reader.ReadToEnd();
                    var result = Plan(body);
                    WriteJson(ctx, result);
                }
                else if (ctx.Request.Url.AbsolutePath == "/ncu")
                {
                    WriteJson(ctx, NcuTable());
                }
                else if (ctx.Request.Url.AbsolutePath == "/allgates")
                {
                    WriteJson(ctx, AllGates());
                }
                else if (ctx.Request.Url.AbsolutePath == "/statnames")
                {
                    WriteJson(ctx, StatNames());
                }
                else if (ctx.Request.Url.AbsolutePath == "/petnanos")
                {
                    WriteJson(ctx, PetNanos());
                }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/solve")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    WriteJson(ctx, Solve(reader.ReadToEnd()));
                }
                else if (ctx.Request.Url.AbsolutePath == "/item")
                {
                    WriteJson(ctx, DumpItems(ctx.Request.QueryString["ids"]));
                }
                else if (ctx.Request.Url.AbsolutePath == "/modifiers")
                {
                    WriteJson(ctx, ModifiersFor(ctx.Request.QueryString["stat"], ctx.Request.QueryString["prof"]));
                }
                else if (ctx.Request.Url.AbsolutePath == "/nanoinfo")
                {
                    WriteJson(ctx, NanoInfo(ctx.Request.QueryString["id"]));
                }
                else
                {
                    WriteHtml(ctx, Page);
                }
            }
            catch (Exception ex)
            {
                WriteJson(ctx, JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }
    }

    private static string ResolveDataDir(string arg)
    {
        if (!string.IsNullOrWhiteSpace(arg) && Directory.Exists(arg))
            return Path.GetFullPath(arg);

        var env = Environment.GetEnvironmentVariable("BUFFDRYRUN_DATA");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return Path.GetFullPath(env);

        // Default: the AOBuddy20 Build folder, found by walking up from the exe.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var build = Path.Combine(dir, "Build");
            if (File.Exists(Path.Combine(build, "GameData", "nanos.ocp")))
                return Path.GetFullPath(build);
            if (File.Exists(Path.Combine(dir, "GameData", "nanos.ocp")))
                return Path.GetFullPath(dir);
            dir = Directory.GetParent(dir)?.FullName;
        }

        return AppContext.BaseDirectory;
    }

    // ---- the plan ----------------------------------------------------------------------

    private class CharReq
    {
        public string name { get; set; } = "Testdummy";
        public string profession { get; set; } = "Engineer";
        public int level { get; set; } = 50;
        public int maxNcu { get; set; } = 210;
        public bool paid { get; set; } = true;         // paid = owns ALL expansions; false = froob (none)
        public int specialization { get; set; } = 0;   // highest spec book learned (0-4)
        public Dictionary<string, int> stats { get; set; } = new();
        public List<int> wants { get; set; } = new();
    }

    private static string Plan(string body)
    {
        var req = JsonSerializer.Deserialize<CharReq>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? new CharReq();

        if (_catalog == null)
            return JsonSerializer.Serialize(new { error = "BuffCatalog did not load - check the GameData buff-bot JSON. See console." });

        var me = BuildStub(req, out var professionId);

        int curMc = me.GetStat(Stat.MaterialCreation);
        int curTs = me.GetStat(Stat.SpaceTime);

        var rows = new List<object>();
        foreach (var id in req.wants.Distinct())
        {
            rows.Add(PlanOne(me, req, professionId, id, curMc, curTs));
        }

        var payload = new
        {
            character = new
            {
                req.name, req.profession, professionId, req.level, req.maxNcu, req.paid,
                materialCreation = curMc, spaceTime = curTs,
                otherStats = req.stats
            },
            results = rows
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    private static object PlanOne(LocalPlayer me, CharReq req, int professionId, int nanoId, int curMc, int curTs)
    {
        if (!ItemData.Find<NanoItem>(nanoId, out var nano) || nano == null)
            return new { nanoId, found = false, verdict = "Unknown nano id (not in ItemData).", reqs = (string)null };

        var (reqMc, reqTs, allReqs, profReq, levelReq, specReq, expReq) = ReadReqs(nano);

        // FIXED gates - no buff can fix these.
        if (profReq != 0 && profReq != professionId)
            return new { nanoId, name = nano.Name, found = true, verdict = $"Wrong profession - this needs profession {profReq}, you are {professionId}.", reqs = allReqs };
        if (levelReq != 0 && req.level < levelReq)
            return new { nanoId, name = nano.Name, found = true, verdict = $"Level too low - needs level {levelReq}, you are {req.level}.", reqs = allReqs };
        var meSpec = me.GetStat(Stat.Specialization);
        if (specReq != 0 && (meSpec & specReq) == 0)
            return new { nanoId, name = nano.Name, found = true, verdict = $"Requires specialization (book) flag {specReq} - not learned. No buff can grant this.", reqs = allReqs };
        var meExp = me.GetStat(Stat.Expansion);
        if (expReq != 0 && (meExp & expReq) == 0)
            return new { nanoId, name = nano.Name, found = true, verdict = $"Requires expansion flag {expReq} - not owned. No buff can grant this.", reqs = allReqs };

        if (reqMc == 0 && reqTs == 0)
            return new { nanoId, name = nano.Name, found = true, verdict = "No MC/TS summon gate - not an Engineer pet summon (nothing to plan).", reqs = allReqs, ncu = nano.NCU };

        // The REAL planner decision.
        var plan = _catalog.BuildControlPlan(me, curMc, curTs, reqMc, reqTs, req.paid, req.profession);

        bool castableNow = curMc >= reqMc && curTs >= reqTs;
        double oeNow = Math.Min(Ratio(curMc, reqMc), Ratio(curTs, reqTs));

        string verdict =
            castableNow ? "Castable NOW - no buffs needed." :
            !plan.CanSummon ? "CANNOT summon even fully buffed - base skill too low." :
            plan.CanControl ? "Summon with buffs, and it STAYS OE-safe after the wrangle drops." :
            "Summon with a wrangle, but it goes OE when the wrangle drops (would stop obeying).";

        var steps = plan.Steps.Select(s => new
        {
            name = s.Name,
            wrangle = s.Wrangle,
            ncu = s.Ncu,
            maxNcuAdded = s.MaxNcuAdded,
            addMc = s.AddFor(McStat),
            addTs = s.AddFor(TsStat)
        }).ToList();

        return new
        {
            nanoId,
            name = nano.Name,
            found = true,
            verdict,
            reqs = allReqs,
            reqMc, reqTs,
            curMc, curTs,
            oeNowPct = Math.Round(oeNow * 100, 1),
            castableNow,
            canSummon = plan.CanSummon,
            canControl = plan.CanControl,
            durableMc = plan.DurableMc, durableTs = plan.DurableTs,
            peakMc = plan.PeakMc, peakTs = plan.PeakTs,
            floorMc = plan.FloorMc, floorTs = plan.FloorTs,
            holdMarginPct = Math.Round(plan.MarginPct, 1),
            ncu = new { max = plan.MaxNcu, used = plan.NcuUsed, free = plan.NcuFree, alreadyUp = plan.NcuAlreadyUp },
            summonNcu = nano.NCU,
            steps
        };
    }

    // Read MC/TS summon gate + all use-criteria + fixed gates (profession/level/spec/expansion).
    private static (int mc, int ts, string all, int prof, int level, int spec, int exp) ReadReqs(NanoItem nano)
    {
        int mc = 0, ts = 0, prof = 0, level = 0, spec = 0, exp = 0;
        var parts = new List<string>();
        if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
        {
            foreach (var c in use)
            {
                if (c.Operator == UseCriteriaOperator.And || c.Operator == UseCriteriaOperator.Or)
                    continue; // logical separator

                if (c.Operator == UseCriteriaOperator.GreaterThan)
                {
                    if (c.Param1 == McStat) mc = Math.Max(mc, c.Param2 + 1);
                    else if (c.Param1 == TsStat) ts = Math.Max(ts, c.Param2 + 1);
                    if (c.Param1 == (int)Stat.Level) level = Math.Max(level, c.Param2 + 1);
                }
                if (c.Param1 == (int)Stat.Profession && c.Operator == UseCriteriaOperator.EqualTo)
                    prof = c.Param2;
                if (c.Param1 == (int)Stat.Specialization) spec |= c.Param2;
                if (c.Param1 == (int)Stat.Expansion) exp |= c.Param2;

                parts.Add($"{StatName(c.Param1)} {OpSym(c.Operator)} {c.Param2}");
            }
        }
        return (mc, ts, parts.Count == 0 ? "(no use reqs)" : string.Join(", ", parts), prof, level, spec, exp);
    }

    // The froob/expansion gate of a nano can live on its CRYSTAL, not its program: SL nanos of the
    // Umbral type carry only skill/level reqs on the program, while the uploadable crystal
    // ("Nano Crystal (<program name>)", a DummyItem) carries the real "Expansion BitAnd <bit>" use-crit
    // that blocks a froob (Expansion stat 0). The client refuses the upload; the program alone looks
    // froob-castable. Map program name -> OR of crystal Expansion bits, built once. (Verified over the
    // pack: 148 "hidden SL" nanos gated only on the crystal, e.g. Umbral Wrangler program 235058 /
    // crystal 235280; froob families like Skill Wrangler have no crystal Expansion crit.)
    private static Dictionary<string, int> _crystalExp;

    private static int CrystalExpansion(string programName)
    {
        if (_crystalExp == null)
        {
            _crystalExp = new Dictionary<string, int>(StringComparer.Ordinal);
            const string prefix = "Nano Crystal (";
            foreach (var id in ItemData.AllDummyItemIds())
            {
                if (!ItemData.Find<DummyItem>(id, out var d) || d?.Name == null)
                    continue;
                if (!d.Name.StartsWith(prefix, StringComparison.Ordinal) || !d.Name.EndsWith(")", StringComparison.Ordinal))
                    continue;
                var inner = d.Name.Substring(prefix.Length, d.Name.Length - prefix.Length - 1);
                if (d.Criteria == null || !d.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var uc))
                    continue;
                var exp = 0;
                foreach (var c in uc)
                    if (c.Param1 == (int)Stat.Expansion)
                        exp |= c.Param2; // any Expansion-BitAnd bit a froob lacks blocks the upload
                if (exp != 0)
                    _crystalExp[inner] = _crystalExp.TryGetValue(inner, out var had) ? had | exp : exp;
            }
        }

        return programName != null && _crystalExp.TryGetValue(programName, out var e) ? e : 0;
    }

    private static double Ratio(int cur, int req) => req <= 0 ? 1.0 : cur / (double)req;

    private static string OpSym(UseCriteriaOperator op) => op switch
    {
        UseCriteriaOperator.GreaterThan => ">",
        UseCriteriaOperator.LessThan => "<",
        UseCriteriaOperator.EqualTo => "==",
        _ => op.ToString()
    };

    private static string StatName(int id) => Enum.IsDefined(typeof(Stat), id) ? ((Stat)id).ToString() : id.ToString();

    // ---- the solver: target + character -> best buffs in NCU, and if short, +NCU to reach it ----
    private sealed class SolveReq : CharReq { public int itemId { get; set; } public int nanoId { get; set; } }

    private sealed class Cand
    {
        public int Id; public string Name; public int Strain; public int Ncu; public string Deliver;
        public Dictionary<int, int> Adds = new(); // required-stat -> +amount
        public int Total(IEnumerable<int> stats) => stats.Sum(s => Adds.TryGetValue(s, out var v) ? v : 0);
    }

    private static string Solve(string body)
    {
        var req = JsonSerializer.Deserialize<SolveReq>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SolveReq();
        var me = BuildStub(req, out var profId);

        // 1) target required skills + fixed gates (equip item uses action 8; a nano uses UseCriteria).
        var required = new Dictionary<int, int>();
        int reqProf = 0, reqLevel = 0, reqSpec = 0, reqExp = 0;
        string targetName = "?";
        if (req.itemId > 0)
        {
            if (!ItemData.Find<DummyItem>(req.itemId, out var it) || it == null)
                return JsonSerializer.Serialize(new { error = $"item {req.itemId} not in ItemData" });
            targetName = it.Name;
            // action 8 = wield/equip criteria
            if (it.Criteria != null)
                foreach (var kv in it.Criteria)
                    if ((int)kv.Key == 8)
                        foreach (var c in kv.Value)
                            Classify(c.Param1, c.Operator, c.Param2, required, ref reqProf, ref reqLevel, ref reqSpec, ref reqExp);
        }
        else if (req.nanoId > 0)
        {
            if (!ItemData.Find<NanoItem>(req.nanoId, out var nano) || nano == null)
                return JsonSerializer.Serialize(new { error = $"nano {req.nanoId} not in ItemData" });
            targetName = nano.Name;
            if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
                foreach (var c in use)
                    Classify(c.Param1, c.Operator, c.Param2, required, ref reqProf, ref reqLevel, ref reqSpec, ref reqExp);
            reqExp |= CrystalExpansion(nano.Name); // the SL gate may be on the crystal, not the program
        }
        else return JsonSerializer.Serialize(new { error = "pass itemId or nanoId" });

        // 2) fixed gates - no buff can satisfy these.
        var blockers = new List<string>();
        if (reqProf != 0 && reqProf != profId) blockers.Add($"profession {reqProf} required (you are {profId})");
        if (reqLevel != 0 && req.level < reqLevel) blockers.Add($"level {reqLevel} required");
        if (reqSpec != 0 && (me.GetStat(Stat.Specialization) & reqSpec) == 0) blockers.Add($"specialization flag {reqSpec} required");
        if (reqExp != 0 && (me.GetStat(Stat.Expansion) & reqExp) == 0) blockers.Add($"expansion flag {reqExp} required");

        // 3) per-skill gaps
        var gaps = new Dictionary<int, (int req, int cur, int gap)>();
        foreach (var kv in required)
        {
            int cur = me.GetStat((Stat)kv.Key);
            if (kv.Value - cur > 0) gaps[kv.Key] = (kv.Value, cur, kv.Value - cur);
        }

        var reqSkillsOut = required.ToDictionary(kv => StatName(kv.Key), kv => new { required = kv.Value, current = me.GetStat((Stat)kv.Key) });

        if (blockers.Count > 0)
            return JsonSerializer.Serialize(new { target = targetName, verdict = "BLOCKED (fixed gate - no buff helps)", blockers, requiredSkills = reqSkillsOut }, Pretty);
        if (gaps.Count == 0)
            return JsonSerializer.Serialize(new { target = targetName, verdict = "EQUIPPABLE NOW - no buffs needed", requiredSkills = reqSkillsOut }, Pretty);

        // 4) gather receivable candidate buffs for the gapped skills, best per strain.
        var gapStats = gaps.Keys.ToHashSet();
        var byStrain = new Dictionary<int, Cand>();
        foreach (var id in ItemData.AllNanoIds())
        {
            if (!ItemData.Find<NanoItem>(id, out var nano) || nano == null) continue;
            if (nano.Modifiers == null || !nano.Modifiers.TryGetValue(SpellListType.Use, out var mods)) continue;
            if (mods.Values.Any(v => v < 0)) continue; // has a debuff component - not a clean buff
            // Must be DIRECTLY CASTABLE: a real buff has a cast gate. The landed effect-nanos
            // (a wrangle's result, a Trader transfer's result like Divest 275032) carry the +skill
            // but have NO use-criteria - you never request those directly; you request the castable
            // nano whose gate names who can cast it. Exclude gateless effect-nanos.
            bool hasGate = nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var uc) && uc.Count > 0;
            if (!hasGate) continue;

            var adds = new Dictionary<int, int>();
            foreach (var s in gapStats) if (mods.TryGetValue((Stat)s, out var a) && a > 0) adds[s] = a;
            if (adds.Count == 0) continue;

            // The buff's OWN spec/expansion gate must be met by the character (a froob can't get an SL buff).
            int bSpec = 0, bExp = 0, bProfTmp = 0, bLvlTmp = 0; var bSkills = new Dictionary<int, int>();
            if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var bc))
                foreach (var cc in bc) Classify(cc.Param1, cc.Operator, cc.Param2, bSkills, ref bProfTmp, ref bLvlTmp, ref bSpec, ref bExp);
            bExp |= CrystalExpansion(nano.Name); // SL gate may live only on the crystal (the Umbral case)
            if (bSpec != 0 && (me.GetStat(Stat.Specialization) & bSpec) == 0) continue;
            if (bExp != 0 && (me.GetStat(Stat.Expansion) & bExp) == 0) continue;

            var np = NanoLibrary.Find(id);
            int strain = np?.Stat(75) ?? 0;
            int ownerProf = ReadReqsProf(nano);
            // deliver: others if the buff for a gapped skill lands on the cast target; else self.
            bool others = adds.Keys.Any(s => np?.ModifyTargetsOthers(s) ?? false);
            bool canSelf = (ownerProf == 0 || ownerProf == profId) && SafeMeets(nano, me);
            if (!others && !canSelf) continue; // not receivable: foreign self-only buff
            string deliver = others ? "others" : "self";

            var c = new Cand { Id = id, Name = nano.Name, Strain = strain, Ncu = nano.NCU, Deliver = deliver, Adds = adds };
            if (!byStrain.TryGetValue(strain, out var best) || c.Total(gapStats) > best.Total(gapStats)
                || (c.Total(gapStats) == best.Total(gapStats) && c.Ncu < best.Ncu))
                byStrain[strain] = c;
        }

        // cap to the most efficient strains to keep the subset search small
        var cands = byStrain.Values.Where(c => c.Total(gapStats) > 0)
            .OrderByDescending(c => c.Total(gapStats) / (double)Math.Max(1, c.Ncu)).Take(16).ToList();

        int budget = Math.Max(0, req.maxNcu);
        var bestInBudget = BestSubset(cands, gapStats, gaps, budget, requireAll: false);
        var goalSet = BestSubset(cands, gapStats, gaps, int.MaxValue, requireAll: true);

        object Report(List<Cand> set) => set == null ? null : new
        {
            ncu = set.Sum(c => c.Ncu),
            buffs = set.OrderByDescending(c => c.Total(gapStats)).Select(c => new { c.Name, c.Strain, c.Deliver, c.Ncu, adds = c.Adds.ToDictionary(kv => StatName(kv.Key), kv => kv.Value) }),
            resultingSkills = gaps.Keys.ToDictionary(StatName, s => gaps[s].cur + set.Sum(c => c.Adds.TryGetValue(s, out var v) ? v : 0))
        };

        bool goalFits = goalSet != null;
        bool nowWithBudget = bestInBudget != null && gaps.Keys.All(s => gaps[s].cur + bestInBudget.Sum(c => c.Adds.TryGetValue(s, out var v) ? v : 0) >= gaps[s].req);

        string verdict;
        object nextStep = null;
        if (nowWithBudget) verdict = "EQUIPPABLE WITH BUFFS (within current NCU)";
        else if (goalFits)
        {
            int need = goalSet.Sum(c => c.Ncu) - budget;
            verdict = "SHORT ON NCU - buffs exist but need more NCU to hold them";
            nextStep = new
            {
                extraNcuNeeded = Math.Max(0, need),
                thenHold = Report(goalSet),
                cheapRoute = $"buy ~{(int)Math.Ceiling(Math.Max(0, need) / 3.0)} low-QL NCU memory chip(s) (+3 NCU each) from a shop - bounded by free belt slots",
                lastResort = "implants/gear add the skill directly (no NCU) but cost credits - compare price to the NCU chips"
            };
        }
        else verdict = "NOT REACHABLE with available buffs - needs implants/gear or higher level";

        return JsonSerializer.Serialize(new
        {
            target = targetName,
            requiredSkills = reqSkillsOut,
            gaps = gaps.ToDictionary(kv => StatName(kv.Key), kv => new { kv.Value.req, kv.Value.cur, kv.Value.gap }),
            currentNcuBudget = budget,
            verdict,
            bestWithinNcu = Report(bestInBudget),
            nextStep
        }, Pretty);
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    // exact small knapsack over best-per-strain candidates (<=16): min-NCU subset, optionally requiring all gaps closed.
    private static List<Cand> BestSubset(List<Cand> cands, HashSet<int> stats, Dictionary<int, (int req, int cur, int gap)> gaps, int budget, bool requireAll)
    {
        List<Cand> best = null; int bestScore = -1, bestNcu = int.MaxValue;
        int n = cands.Count;
        for (int mask = 0; mask < (1 << n); mask++)
        {
            int ncu = 0; var set = new List<Cand>();
            for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) { ncu += cands[i].Ncu; set.Add(cands[i]); }
            if (ncu > budget) continue;
            bool all = stats.All(s => gaps[s].cur + set.Sum(c => c.Adds.TryGetValue(s, out var v) ? v : 0) >= gaps[s].req);
            if (requireAll)
            {
                if (all && ncu < bestNcu) { bestNcu = ncu; best = set; }
            }
            else
            {
                int closed = stats.Count(s => gaps[s].cur + set.Sum(c => c.Adds.TryGetValue(s, out var v) ? v : 0) >= gaps[s].req);
                int score = closed * 100000 + stats.Sum(s => Math.Min(gaps[s].req, gaps[s].cur + set.Sum(c => c.Adds.TryGetValue(s, out var v) ? v : 0)));
                if (score > bestScore || (score == bestScore && ncu < bestNcu)) { bestScore = score; bestNcu = ncu; best = set; }
            }
        }
        return best;
    }

    private static void Classify(int stat, UseCriteriaOperator op, int val, Dictionary<int, int> skills, ref int prof, ref int level, ref int spec, ref int exp)
    {
        if (op == UseCriteriaOperator.And || op == UseCriteriaOperator.Or) return;
        int plus1 = op == UseCriteriaOperator.GreaterThan ? val + 1 : val;
        switch ((Stat)stat)
        {
            case Stat.Profession: prof = val; break;
            case Stat.Level: level = Math.Max(level, plus1); break;
            case Stat.Specialization: spec |= val; break;
            case Stat.Expansion: exp |= val; break;
            case Stat.Cash: break; // money, not a buffable skill
            default:
                if (op == UseCriteriaOperator.GreaterThan) skills[stat] = Math.Max(skills.TryGetValue(stat, out var h) ? h : 0, plus1);
                break;
        }
    }

    private static int ReadReqsProf(NanoItem nano)
    {
        if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
            foreach (var c in use) if (c.Param1 == (int)Stat.Profession && c.Operator == UseCriteriaOperator.EqualTo) return c.Param2;
        return 0;
    }

    private static bool SafeMeets(NanoItem nano, LocalPlayer me)
    {
        try { return nano.MeetsUseReqs(me, true, false); } catch { return false; } // ignoreTargetReqs: self-cast check
    }

    // ---- probe: FULL nano record from nanos.ocp vs ItemData's criteria (extraction audit) --
    private static string NanoInfo(string idStr)
    {
        if (!int.TryParse(idStr, out var id)) return JsonSerializer.Serialize(new { error = "bad id" });

        // nanos.ocp (NanoLibrary)
        var np = NanoLibrary.Find(id);
        object pack = null;
        if (np != null)
        {
            pack = new
            {
                np.TargetsOthers,
                stats = np.Stats.ToDictionary(kv => StatName(kv.Key), kv => kv.Value),
                actions = np.Actions.Select(a => new
                {
                    actionType = a.ActionType,
                    requirements = a.Requirements.Select(r => $"{StatName(r.Stat)} op{r.Operator} {r.Value} (target {r.Target})").ToList()
                }).ToList(),
                modifies = np.Modifies.ToDictionary(kv => StatName(kv.Key), kv => kv.Value),
                modifyTargets = np.ModifyTargets.ToDictionary(kv => StatName(kv.Key), kv => kv.Value + " (" + (kv.Value == 3 ? "others" : "self") + ")")
            };
        }

        // ItemData.bin (what the other probes read)
        object itemData = null;
        if (ItemData.Find<NanoItem>(id, out var nano) && nano != null)
        {
            var crits = new List<string>();
            if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
                foreach (var c in use) crits.Add($"{StatName(c.Param1)} {OpSym(c.Operator)} {c.Param2}");
            itemData = new { name = nano.Name, nano.NCU, useCriteria = crits };
        }

        return JsonSerializer.Serialize(new { id, fromNanosOcp = pack, fromItemDataBin = itemData },
            new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- probe: every nano that BUFFS a given skill, optionally gated to a profession ---
    private static string ModifiersFor(string statName, string profStr)
    {
        if (!Enum.TryParse<Stat>(statName, true, out var stat))
            return JsonSerializer.Serialize(new { error = $"unknown stat '{statName}'" });
        int prof = int.TryParse(profStr, out var p) ? p : 0;

        var rows = new List<object>();
        foreach (var id in ItemData.AllNanoIds())
        {
            if (!ItemData.Find<NanoItem>(id, out var nano) || nano == null) continue;
            if (nano.Modifiers == null || !nano.Modifiers.TryGetValue(SpellListType.Use, out var mods)) continue;
            if (!mods.TryGetValue(stat, out var amount) || amount == 0) continue;

            var (_, _, allReqs, profReq, levelReq, _, _) = ReadReqs(nano);
            // keep: generic (no prof gate) or gated to the requested profession
            if (prof != 0 && profReq != 0 && profReq != prof) continue;

            var np = NanoLibrary.Find(id);
            int strain = np?.Stat(75) ?? 0;        // NanoStrain = the stacking/overwrite group
            int stackingOrder = np?.Stat(551) ?? 0; // who supersedes within the strain
            // self/other for THIS skill: the target of its own ModifyStat effect (not the formula-wide flag)
            bool skillOnOthers = np?.ModifyTargetsOthers((int)stat) ?? false;
            string cast = skillOnOthers ? "others" : "self";
            string owner = profReq != 0 ? ProfName(profReq) : "(no lock)";

            rows.Add(new { id, name = nano.Name, amount, ncu = nano.NCU, strain, stackingOrder,
                profession = owner, profId = profReq, cast,
                nanoLine = nano.NanoLine.ToString(), levelReq, reqs = allReqs });
        }
        return JsonSerializer.Serialize(new
        {
            stat = stat.ToString(), prof,
            count = rows.Count,
            buffs = rows.OrderByDescending(r => (int)r.GetType().GetProperty("amount").GetValue(r))
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- probe: what does ItemData actually hold for a non-nano item (weapon/gear)? -----
    private static string DumpItems(string idsCsv)
    {
        var ids = (idsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToList();

        var outp = new List<object>();
        foreach (var id in ids)
        {
            ItemBase it = null;
            try
            {
                if (ItemData.Find<DummyItem>(id, out var dummy) && dummy != null) it = dummy;
                else if (ItemData.Find<NanoItem>(id, out var nano) && nano != null) it = nano;
            }
            catch (Exception ex)
            {
                outp.Add(new { id, found = false, error = ex.GetType().Name });
                continue;
            }

            if (it == null) { outp.Add(new { id, found = false }); continue; }

            var actions = new List<object>();
            if (it.Criteria != null)
                foreach (var kv in it.Criteria)
                    actions.Add(new
                    {
                        action = kv.Key.ToString(),
                        criteria = kv.Value.Select(c => $"{StatName(c.Param1)} {OpSym(c.Operator)} {c.Param2}").ToList()
                    });

            outp.Add(new { id, name = it.Name, type = it.GetType().Name, actions });
        }
        return JsonSerializer.Serialize(outp, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- dump EVERY nano's real UseCriteria (cast gates) from ItemData ------------------
    private static string AllGates()
    {
        var ids = ItemData.AllNanoIds();
        var path = Path.Combine(_dataDir, "nano-gates.csv");
        int n = 0, withGates = 0;
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            // Split, cleaned columns. GreaterThan reqs are shown as the real threshold (value+1,
            // AO's "> N" means need N+1). Specialization/Expansion are BitAnd flag requirements -
            // without the specialization BOOK learned, an SL nano can never be cast. The And/Or
            // logical separators ("Flags And 0") are dropped.
            w.WriteLine("id,name,nanoLine,ncu,minLevel,profession,visualProfession,specialization,expansion,cash,mc,ts,otherReqs");
            foreach (var id in ids)
            {
                if (!ItemData.Find<NanoItem>(id, out var nano) || nano == null)
                    continue;

                int minLevel = 0, profession = 0, visualProf = 0, spec = 0, expansion = 0, cash = 0, mc = 0, ts = 0;
                var other = new List<string>();

                if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
                {
                    withGates++;
                    foreach (var c in use)
                    {
                        if (c.Operator == UseCriteriaOperator.And || c.Operator == UseCriteriaOperator.Or)
                            continue; // logical separator, not a requirement

                        int plus1 = c.Operator == UseCriteriaOperator.GreaterThan ? c.Param2 + 1 : c.Param2;
                        switch ((Stat)c.Param1)
                        {
                            case Stat.Level: minLevel = Math.Max(minLevel, plus1); break;
                            case Stat.Profession: profession = c.Param2; break;
                            case Stat.VisualProfession: visualProf = c.Param2; break;
                            case Stat.Specialization: spec = c.Param2; break;   // BitAnd flag: book required
                            case Stat.Expansion: expansion = c.Param2; break;   // BitAnd flag: 2 = Shadowlands
                            case Stat.Cash: cash = plus1; break;
                            case Stat.MaterialCreation: mc = plus1; break;
                            case Stat.SpaceTime: ts = plus1; break;
                            default: other.Add($"{StatName(c.Param1)} {OpSym(c.Operator)} {c.Param2}"); break;
                        }
                    }
                }

                var name = (nano.Name ?? "").Replace("\"", "\"\"");
                var otherStr = string.Join("; ", other).Replace("\"", "\"\"");
                w.WriteLine($"{id},\"{name}\",{nano.NanoLine},{nano.NCU},{minLevel},{profession},{visualProf},{spec},{expansion},{cash},{mc},{ts},\"{otherStr}\"");
                n++;
            }
        }

        return JsonSerializer.Serialize(new { nanos = n, withGates, file = path });
    }

    // Every Stat enum name, for the skill-name dropdown on the page (so a skill can't be mistyped).
    private static string StatNames()
    {
        var names = Enum.GetNames(typeof(Stat))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JsonSerializer.Serialize(names);
    }

    // The pet-summon nanos, for the "wants" dropdown: every formula that actually summons a pet
    // (SummonPets captured from its effect functions - the precise "summons a pet" test), id + name.
    private static string PetNanos()
    {
        var list = NanoLibrary.Nanos
            .Where(n => n.SummonPets != null && n.SummonPets.Count > 0)
            .Select(n => new { id = n.NanoId, name = NanoLibrary.NameOf(n.NanoId) })
            .Where(x => !string.IsNullOrEmpty(x.name) && !x.name.StartsWith("(nano "))
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JsonSerializer.Serialize(list);
    }

    // ---- NCU tier probe: what BestNcuBuff actually returns at each level ----------------
    private static string NcuTable()
    {
        if (_catalog == null)
            return JsonSerializer.Serialize(new { error = "BuffCatalog did not load." });

        var rows = new List<object>();
        foreach (var paid in new[] { true, false })
        {
            string prevKey = null;
            for (int lvl = 1; lvl <= 220; lvl++)
            {
                var me = BuildStub(new CharReq { profession = "Engineer", level = lvl }, out _);
                var pick = _catalog.BestNcuBuff(me, paid);
                var key = pick == null ? "none" : $"{pick.Name}|{pick.MaxNcuAdded}|{pick.ReceiverLevel}";
                if (key != prevKey)
                {
                    rows.Add(new
                    {
                        paid,
                        fromLevel = lvl,
                        tier = pick?.Name,
                        maxNcuAdded = pick?.MaxNcuAdded ?? 0,
                        casterLevel = pick?.ReceiverLevel ?? 0,
                        needsTeam = pick?.NeedsTeam ?? false,
                        landId = pick?.LandId ?? 0
                    });
                    prevKey = key;
                }
            }
        }
        // For every distinct landId the planner chose, dump the nano's REAL cast gates from ItemData.
        var ids = rows.Select(r => (int)r.GetType().GetProperty("landId").GetValue(r))
                      .Where(i => i > 0).Distinct().OrderBy(i => i).ToList();
        var nanos = new List<object>();
        foreach (var id in ids)
        {
            if (!ItemData.Find<NanoItem>(id, out var nano) || nano == null)
            {
                nanos.Add(new { id, found = false });
                continue;
            }

            var crits = new List<string>();
            if (nano.Criteria != null && nano.Criteria.TryGetValue(ItemActionInfo.UseCriteria, out var use))
                foreach (var c in use)
                    crits.Add($"{StatName(c.Param1)} {OpSym(c.Operator)} {c.Param2}");

            nanos.Add(new { id, name = nano.Name, ncu = nano.NCU, criteria = crits });
        }

        return JsonSerializer.Serialize(new
        {
            note = "BestNcuBuff() output swept by level; each row = the tier it starts returning at that level. 'nanos' = each chosen nano's REAL UseCriteria from ItemData.",
            rows,
            nanos
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- the simulated character -------------------------------------------------------

    // Build a LocalPlayer with no game behind it. We inject the typed values directly as the
    // EFFECTIVE (no-buff) skills: abilities are left at 0 so the SDK's trickle adds nothing on
    // top (the typed skill is treated as already including gear/implant/trickle). Buffs the
    // planner applies then stack on via GetStat, exactly as in the live bot.
    private static LocalPlayer BuildStub(CharReq req, out int professionId)
    {
        var msg = new SimpleCharFullUpdateMessage
        {
            Identity = new Identity(IdentityType.SimpleChar, 1),
            Position = new Vector3(),
            Heading = new Quaternion(),
            Appearance = new SmokeLounge.AOtomation.Messaging.GameData.Appearance { Breed = Breed.Solitus, Side = Side.Neutral },
            Name = string.IsNullOrWhiteSpace(req.name) ? "Testdummy" : req.name,
            Level = (short)req.level,
            Health = 1,
            HealthDamage = 0,
            CharacterInfo = null,
            HeadMesh = null,
            ActiveNanos = Array.Empty<SimpleCharInfo.ActiveNano>()
        };

        var me = new LocalPlayer(msg);

        professionId = ProfessionId(req.profession);
        me.SetStat(Stat.Level, req.level);
        me.SetStat(Stat.Profession, professionId);
        me.SetStat(Stat.MaxNCU, req.maxNcu);
        me.SetStat(Stat.CurrentNCU, 0);

        // Specialization is a bitmask: owning book L means bits 0..L-1 set (you earn 1..L in order).
        // "Specialization BitAnd 2" (spec 2) then passes only when L >= 2.
        me.SetStat(Stat.Specialization, req.specialization <= 0 ? 0 : (1 << req.specialization) - 1);

        // Expansion is froob-vs-paid, nothing in between: every paid account owns EVERY expansion
        // (owner + Alg, 2026-10-14 - no live account has one expansion and not another). So paid =
        // all bits set, which satisfies any Expansion-BitAnd gate whatever bit it names (incl. 16,
        // which the old per-expansion checkboxes did not even offer); froob = 0, which fails them all.
        me.SetStat(Stat.Expansion, req.paid ? ~0 : 0);

        // Typed skills (by Stat name). Always ensure MC/TS exist even if the form omitted one.
        me.SetStat(Stat.MaterialCreation, 0);
        me.SetStat(Stat.SpaceTime, 0);
        foreach (var kv in req.stats)
        {
            if (Enum.TryParse<Stat>(kv.Key, true, out var stat))
                me.SetStat(stat, kv.Value);
        }

        // The "wants" are treated as learned so the planner considers them castable.
        me.SpellList = req.wants?.ToArray() ?? Array.Empty<int>();

        // Register the stub as the local player so ReqChecker (which evaluates Self/User criteria
        // against DynelManager.LocalPlayer) tests OUR character, not a null live player. Without this
        // every self-cast requirement (profession/PM/SI) fails and self-buffs never become candidates.
        LocalPlayerProxy.LocalPlayer = me;
        return me;
    }

    private static string ProfName(int id) => id switch
    {
        1 => "Soldier", 2 => "MartialArtist", 3 => "Engineer", 4 => "Fixer", 5 => "Agent",
        6 => "Adventurer", 7 => "Trader", 8 => "Bureaucrat", 9 => "Enforcer", 10 => "Doctor",
        11 => "NanoTechnician", 12 => "Metaphysicist", 14 => "Keeper", 15 => "Shade",
        _ => $"prof{id}"
    };

    private static int ProfessionId(string name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "soldier" => 1,
        "martialartist" or "ma" => 2,
        "engineer" or "engi" => 3,
        "fixer" => 4,
        "agent" => 5,
        "adventurer" or "adv" => 6,
        "trader" => 7,
        "bureaucrat" or "crat" => 8,
        "enforcer" => 9,
        "doctor" or "doc" => 10,
        "nanotechnician" or "nt" => 11,
        "metaphysicist" or "mp" => 12,
        "keeper" => 14,
        "shade" => 15,
        _ => 3 // default Engineer for the first cut
    };

    // ---- http helpers ------------------------------------------------------------------

    private static void WriteJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    // ---- the page (big, high-contrast; one file, no external deps) ----------------------

    private const string Page = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Buff Dry-Run</title>
<style>
  :root { color-scheme: dark; }
  body { background:#12151b; color:#e8edf2; font:17px/1.5 system-ui,Segoe UI,Arial,sans-serif; margin:0; }
  header { padding:14px 20px; background:#1b2230; border-bottom:1px solid #2b3446; }
  header h1 { margin:0; font-size:22px; }
  header p { margin:4px 0 0; color:#9fb0c3; font-size:14px; }
  main { display:flex; gap:18px; padding:18px; flex-wrap:wrap; }
  .card { background:#1b2230; border:1px solid #2b3446; border-radius:10px; padding:16px; }
  .form { flex:1 1 360px; min-width:320px; }
  .out  { flex:2 1 520px; min-width:340px; }
  label { display:block; margin:10px 0 4px; color:#b9c6d6; font-size:14px; }
  input, select { width:100%; box-sizing:border-box; padding:10px; font-size:17px; background:#0e1117;
                  color:#e8edf2; border:1px solid #39465c; border-radius:7px; }
  .row { display:flex; gap:10px; } .row > div { flex:1; }
  .statgrid { display:grid; grid-template-columns:1fr 110px auto; gap:8px; align-items:center; margin-top:6px; }
  .statgrid input { padding:8px; }
  button { margin-top:14px; padding:12px 18px; font-size:18px; font-weight:600; border:0; border-radius:8px;
           background:#3b82f6; color:#fff; cursor:pointer; }
  button.sec { background:#39465c; font-weight:500; padding:8px 12px; margin:0; font-size:14px; }
  .mini { font-size:13px; color:#8b9bb0; }
  .result { border:1px solid #2b3446; border-radius:9px; padding:14px; margin-bottom:12px; }
  .result h3 { margin:0 0 6px; font-size:18px; }
  .verdict { font-weight:700; padding:6px 10px; border-radius:6px; display:inline-block; margin:4px 0 10px; }
  .ok { background:#14532d; color:#c7f9d8; } .warn { background:#633b02; color:#ffe2b0; }
  .bad { background:#5b1620; color:#ffc9d1; } .neutral { background:#2b3446; color:#cfe0f0; }
  table { width:100%; border-collapse:collapse; font-size:15px; margin-top:6px; }
  th,td { text-align:left; padding:6px 8px; border-bottom:1px solid #263047; }
  th { color:#9fb0c3; font-weight:600; }
  code { color:#9fd0ff; }
  .pill { font-size:12px; background:#0e1117; border:1px solid #39465c; border-radius:999px; padding:2px 8px; }
</style>
</head>
<body>
<header>
  <h1>Buff Dry-Run</h1>
  <p>Simulated character &rarr; the real AOBuddy20 planner. No client, no login. Enter effective (no-buff) skills.</p>
</header>
<main>
  <section class="card form">
    <div class="row">
      <div><label>Name</label><input id="name" value="Testdummy"></div>
      <div><label>Profession</label>
        <select id="prof">
          <option>Engineer</option>
          <option>Metaphysicist</option><option>Bureaucrat</option><option>Trader</option>
          <option>Adventurer</option><option>Soldier</option><option>Doctor</option>
          <option>NanoTechnician</option><option>Agent</option><option>Enforcer</option>
          <option>Fixer</option><option>MartialArtist</option><option>Keeper</option><option>Shade</option>
        </select>
      </div>
    </div>
    <div class="row">
      <div><label>Level</label><input id="level" type="number" value="50"></div>
      <div><label>Max NCU</label><input id="ncu" type="number" value="210"></div>
      <div><label>Account</label><select id="paid"><option value="true">Paid (all expansions)</option><option value="false">Froob</option></select></div>
    </div>
    <div class="row">
      <div><label>Specialization (highest book)</label>
        <select id="spec"><option value="0">None</option><option value="1">1</option><option value="2">2</option><option value="3">3</option><option value="4">4</option></select>
      </div>
    </div>
    <div class="mini">Account is froob-vs-paid only: every paid account owns all expansions. A froob is blocked from any nano with an expansion gate (e.g. Shadowlands).</div>

    <label>Skills (effective, no buffs)</label>
    <div class="mini">Stat name exactly as the game enum: MaterialCreation, SpaceTime, MatterMetamorphosis, BiologicalMetamorphosis, PsychologicalModifications, SensoryImprovement, TimeAndSpace...</div>
    <div id="stats"></div>
    <button class="sec" onclick="addStat('','')">+ add skill</button>

    <label style="margin-top:14px">Wants - pet/nano ids to attempt (comma separated)</label>
    <div class="row" style="margin-top:4px">
      <div><label class="mini" style="margin:0">Add a pet nano</label>
        <select id="petpick" onchange="addWant(this.value); this.selectedIndex=0"><option value="">- pick a pet summon -</option></select>
      </div>
    </div>
    <input id="wants" value="43325">
    <div class="mini">Pick from the dropdown (every pet-summon nano in the data) or type ids directly. 43325 = Feeble Automaton (example).</div>

    <button onclick="plan()">Plan</button>
    <button class="sec" onclick="loadExample()">Load example</button>
  </section>

  <section class="card out">
    <div id="results"><p class="mini">Fill the form and press <b>Plan</b>. Results appear here.</p></div>
  </section>
</main>

<script>
let STATS=[];
function statOptions(sel){
  return ['<option value="">- stat -</option>']
    .concat(STATS.map(s=>`<option value="${s}"${s===sel?' selected':''}>${s}</option>`)).join('');
}
function addStat(name, val){
  const wrap=document.getElementById('stats');
  const div=document.createElement('div'); div.className='statgrid';
  div.innerHTML=`<select>${statOptions(name)}</select><input type="number" placeholder="value" value="${val}"><button class="sec" onclick="this.parentElement.remove()">x</button>`;
  wrap.appendChild(div);
}
function addWant(id){
  if(!id) return;
  const w=document.getElementById('wants');
  const have=w.value.split(',').map(s=>s.trim()).filter(Boolean);
  if(!have.includes(id)){ have.push(id); w.value=have.join(','); }
}
// Account froob => no specialization (the spec dropdown is disabled and forced to None).
function syncSpec(){
  const paid=document.getElementById('paid').value==='true';
  const spec=document.getElementById('spec');
  spec.disabled=!paid;
  if(!paid) spec.value='0';
}
async function loadLists(){
  try{ STATS=await (await fetch('/statnames')).json(); }catch(e){ STATS=[]; }
  document.querySelectorAll('#stats .statgrid select').forEach(s=>{const v=s.value;s.innerHTML=statOptions(v);});
  try{
    const pets=await (await fetch('/petnanos')).json();
    const sel=document.getElementById('petpick');
    pets.forEach(p=>{const o=document.createElement('option');o.value=p.id;o.textContent=`${p.name} (${p.id})`;sel.appendChild(o);});
  }catch(e){}
}
window.addEventListener('DOMContentLoaded',()=>{ syncSpec(); loadLists();
  document.getElementById('paid').addEventListener('change',syncSpec); });
function loadExample(){
  document.getElementById('stats').innerHTML='';
  addStat('MaterialCreation','336'); addStat('SpaceTime','336');
  document.getElementById('level').value='50';
  document.getElementById('ncu').value='210';
  document.getElementById('wants').value='43325';
}
function collectStats(){
  const o={};
  document.querySelectorAll('#stats .statgrid').forEach(r=>{
    const n=r.children[0].value.trim(); const v=parseInt(r.children[1].value,10);
    if(n && !isNaN(v)) o[n]=v;
  });
  return o;
}
async function plan(){
  const body={
    name:document.getElementById('name').value,
    profession:document.getElementById('prof').value,
    level:parseInt(document.getElementById('level').value,10)||1,
    maxNcu:parseInt(document.getElementById('ncu').value,10)||0,
    paid:document.getElementById('paid').value==='true',
    specialization:parseInt(document.getElementById('spec').value,10)||0,
    stats:collectStats(),
    wants:document.getElementById('wants').value.split(',').map(s=>parseInt(s.trim(),10)).filter(n=>!isNaN(n))
  };
  const res=document.getElementById('results');
  res.innerHTML='<p class="mini">Planning...</p>';
  try{
    const r=await fetch('/plan',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const d=await r.json();
    render(d);
  }catch(e){ res.innerHTML='<p class="bad verdict">Error: '+e+'</p>'; }
}
function cls(d){
  if(d.castableNow) return 'ok';
  if(!d.canSummon) return 'bad';
  if(d.canControl) return 'ok';
  return 'warn';
}
function render(d){
  const res=document.getElementById('results');
  if(d.error){ res.innerHTML='<p class="bad verdict">'+d.error+'</p>'; return; }
  const c=d.character;
  let h=`<p class="mini">${c.name} &mdash; ${c.profession} (id ${c.professionId}), level ${c.level}, Max NCU ${c.maxNcu}, ${c.paid?'paid':'froob'} &mdash; MC ${c.materialCreation} / TS ${c.spaceTime}</p>`;
  for(const d2 of d.results){
    if(!d2.found){ h+=`<div class="result"><h3>${d2.nanoId}</h3><span class="verdict bad">${d2.verdict}</span></div>`; continue; }
    const k=cls(d2);
    h+=`<div class="result"><h3>${d2.name} <span class="pill">${d2.nanoId}</span></h3>`;
    h+=`<span class="verdict ${k}">${d2.verdict}</span>`;
    h+=`<div class="mini">reqs: <code>${d2.reqs}</code></div>`;
    if(d2.reqMc!==undefined){
      h+=`<table><tr><th></th><th>MC</th><th>TS</th></tr>`;
      h+=`<tr><td>Required</td><td>${d2.reqMc}</td><td>${d2.reqTs}</td></tr>`;
      h+=`<tr><td>Current (no buff)</td><td>${d2.curMc}</td><td>${d2.curTs}</td></tr>`;
      h+=`<tr><td>80% OE floor</td><td>${d2.floorMc}</td><td>${d2.floorTs}</td></tr>`;
      h+=`<tr><td>Durable (held, wrangle dropped)</td><td>${d2.durableMc}</td><td>${d2.durableTs}</td></tr>`;
      h+=`<tr><td>Peak (summon moment, +wrangle)</td><td>${d2.peakMc}</td><td>${d2.peakTs}</td></tr></table>`;
      h+=`<div class="mini" style="margin-top:6px">OE now: ${d2.oeNowPct}% &middot; hold margin: ${d2.holdMarginPct}% &middot; NCU: used ${d2.ncu.used}/${d2.ncu.max} (free ${d2.ncu.free})${d2.ncu.alreadyUp?' &middot; NCU buff already up':''}</div>`;
      if(d2.steps && d2.steps.length){
        // Capacity before any NCU buff lands = final max minus everything the plan's NCU buffs add.
        let cap = d2.ncu.max - d2.steps.reduce((a,s)=>a+(s.maxNcuAdded||0),0);
        let used = 0;
        h+=`<table style="margin-top:8px"><tr><th>Buff to cast</th><th>+MC</th><th>+TS</th><th>NCU</th><th>NCU left</th><th></th></tr>`;
        for(const s of d2.steps){
          let cell;
          if(s.maxNcuAdded>0){ cap += s.maxNcuAdded; cell = '+'+s.maxNcuAdded+' cap'; }
          else { used += (s.ncu||0); cell = s.ncu? ('-'+s.ncu) : '0'; }
          const left = cap - used;
          h+=`<tr><td>${s.name}</td><td>${s.addMc||''}</td><td>${s.addTs||''}</td><td>${cell}</td><td>${left}</td><td>${s.wrangle?'<span class="pill">wrangle</span>':''}</td></tr>`;
        }
        h+=`<tr><td colspan="4" style="text-align:right"><b>NCU left at end</b></td><td colspan="2"><b>${d2.ncu.free}</b> of ${d2.ncu.max}</td></tr>`;
        h+=`</table>`;
        if(d2.ncu.free < 0){ h+=`<div class="verdict bad" style="margin-top:6px">Over NCU budget by ${-d2.ncu.free} - this buff set does NOT fit.</div>`; }
      } else {
        h+=`<div class="mini" style="margin-top:6px">No buffs needed.</div>`;
      }
    }
    h+=`</div>`;
  }
  res.innerHTML=h;
}
loadExample();
</script>
</body>
</html>
""";
}
