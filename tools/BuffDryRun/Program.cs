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

    private sealed class CharReq
    {
        public string name { get; set; } = "Testdummy";
        public string profession { get; set; } = "Engineer";
        public int level { get; set; } = 50;
        public int maxNcu { get; set; } = 210;
        public bool paid { get; set; } = true;
        public int specialization { get; set; } = 0; // highest spec book learned (0-4)
        public int expansion { get; set; } = 0;       // ExpansionFlags bitmask owned
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

    private static double Ratio(int cur, int req) => req <= 0 ? 1.0 : cur / (double)req;

    private static string OpSym(UseCriteriaOperator op) => op switch
    {
        UseCriteriaOperator.GreaterThan => ">",
        UseCriteriaOperator.LessThan => "<",
        UseCriteriaOperator.EqualTo => "==",
        _ => op.ToString()
    };

    private static string StatName(int id) => Enum.IsDefined(typeof(Stat), id) ? ((Stat)id).ToString() : id.ToString();

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
                        receiverLevel = pick?.ReceiverLevel ?? 0,
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
        me.SetStat(Stat.Expansion, req.expansion);

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
        return me;
    }

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
      <div><label>Paid (SL)</label><select id="paid"><option value="true">Yes</option><option value="false">No</option></select></div>
    </div>
    <div class="row">
      <div><label>Specialization (highest book)</label>
        <select id="spec"><option value="0">None</option><option value="1">1</option><option value="2">2</option><option value="3">3</option><option value="4">4</option></select>
      </div>
    </div>
    <label>Expansions owned</label>
    <div class="mini">A nano gated on an expansion/spec you don't own is NEVER castable.</div>
    <div id="exp" style="display:flex;gap:14px;flex-wrap:wrap;margin-top:6px;font-size:15px">
      <label style="display:inline-flex;gap:5px;align-items:center;margin:0"><input type="checkbox" data-bit="1" style="width:auto"> Notum Wars</label>
      <label style="display:inline-flex;gap:5px;align-items:center;margin:0"><input type="checkbox" data-bit="2" checked style="width:auto"> Shadowlands</label>
      <label style="display:inline-flex;gap:5px;align-items:center;margin:0"><input type="checkbox" data-bit="8" checked style="width:auto"> Alien Invasion</label>
      <label style="display:inline-flex;gap:5px;align-items:center;margin:0"><input type="checkbox" data-bit="32" checked style="width:auto"> Lost Eden</label>
      <label style="display:inline-flex;gap:5px;align-items:center;margin:0"><input type="checkbox" data-bit="128" checked style="width:auto"> Legacy of Xan</label>
    </div>

    <label>Skills (effective, no buffs)</label>
    <div class="mini">Stat name exactly as the game enum: MaterialCreation, SpaceTime, MatterMetamorphosis, BiologicalMetamorphosis, PsychologicalModifications, SensoryImprovement, TimeAndSpace...</div>
    <div id="stats"></div>
    <button class="sec" onclick="addStat('','')">+ add skill</button>

    <label style="margin-top:14px">Wants - pet/nano ids to attempt (comma separated)</label>
    <input id="wants" value="43325">
    <div class="mini">Summon nano ids (from AODB / itemnames). 43325 = Feeble Automaton (example).</div>

    <button onclick="plan()">Plan</button>
    <button class="sec" onclick="loadExample()">Load example</button>
  </section>

  <section class="card out">
    <div id="results"><p class="mini">Fill the form and press <b>Plan</b>. Results appear here.</p></div>
  </section>
</main>

<script>
function addStat(name, val){
  const wrap=document.getElementById('stats');
  const div=document.createElement('div'); div.className='statgrid';
  div.innerHTML=`<input placeholder="Stat name" value="${name}"><input type="number" placeholder="value" value="${val}"><button class="sec" onclick="this.parentElement.remove()">x</button>`;
  wrap.appendChild(div);
}
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
    expansion:[...document.querySelectorAll('#exp input:checked')].reduce((a,c)=>a+parseInt(c.dataset.bit,10),0),
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
