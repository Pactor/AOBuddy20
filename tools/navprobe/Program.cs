using System.Globalization;
using AOBuddy20.Components;
using AOBuddy20.Enums;
using AOBuddy20.Nav;
using AOSharp.Common.GameData;

// Walk-grid probe. With args <pf> <x1> <z1> <x2> <z2>: load the playfield's grid (Recast
// lattice first, RECAST-NAV-PLAN) and plan the route the way MovementController does.
//   navprobe plot <pf> <x0> <z0> <x1> <z1>  - the route with PlanRoute's own marks (blocks,
//                                             clearance toll, body unseal) as an ASCII map
var baseDir = @"F:\testcellao\AOBuddy20\AOBuddy20\Build";

if (args.Length >= 6 && args[0] == "plot")
{
    int pPf = int.Parse(args[1], CultureInfo.InvariantCulture);
    float F2(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    var fromP = new Vector3(F2(args[2]), float.NaN, F2(args[3]));
    var goalP = new Vector3(F2(args[4]), float.NaN, F2(args[5]));
    var pGrid = MeshGrid.Load(baseDir, pPf, s => Console.WriteLine(s));
    if (pGrid == null) { Console.WriteLine("no meshes.bin for pf " + pPf + " (plot needs the concrete mesh grid)"); return 1; }

    try { Zoning.Load(baseDir, _ => { }); } catch { }

    // PlanRoute's marks, replicated (MovementController.PlanRoute): hard blocks, the
    // clearance toll, the body unseal, the crossing re-add.
    var extra = new HashSet<int>();
    var pricey = new HashSet<int>();
    var crossing = new HashSet<int>();
    var pads = new List<Vector3>();
    foreach (var e in Zoning.ExitsFrom(pPf))
    {
        if (e.Kind == ExitKind.ZoneLine)
        {
            pGrid.CellsAlong(e.A, e.B, 2f, extra);
            pGrid.CellsAlong(e.A, e.B, 2f, crossing);
            pGrid.CellsAlong(e.A, e.B, 6f, pricey);
        }
        else if (e.Kind == ExitKind.Line || e.Kind == ExitKind.Proxy || e.Kind == ExitKind.Teleport)
        {
            // a pad the goal stands beside is the leg's own (PlanRoute's passLine): no disc
            if (Movement.Flat(e.A, goalP) > 3.5f && Movement.Flat(e.A, fromP) > 3.5f)
            {
                pGrid.CellsAlong(e.A, e.A, 5f, extra);
                pGrid.CellsAlong(e.A, e.A, 5f, crossing);
            }

            pGrid.CellsAlong(e.A, e.A, 9f, pricey);
            if (e.ObjType == 51016)
            {
                pads.Add(e.A);
            }
        }
    }

    var pUnseal = new HashSet<int>();
    pGrid.CellsAlong(fromP, fromP, 3f, pUnseal);
    extra.ExceptWith(pUnseal);
    pricey.ExceptWith(pUnseal);
    extra.UnionWith(crossing);

    var pRoute = pGrid.FindPath(fromP, goalP, extra, 8f, 3f, out var pWhy, pricey)
                 ?? pGrid.FindPath(fromP, goalP, extra, 8f, 8f, out pWhy, pricey);
    Console.WriteLine($"marks: extra={extra.Count}, pricey={pricey.Count}, unseal={pUnseal.Count}, crossing={crossing.Count}");
    foreach (var probe in new[] { (3173.5f, 866f), (3178f, 866f), (3183f, 866f), (3178f, 870f), (3160f, 866f), (3190f, 866f) })
    {
        var k = CellKey(pGrid, probe.Item1, probe.Item2);
        Console.WriteLine($"  cell ({probe.Item1:0},{probe.Item2:0}): extra={extra.Contains(k)}, pricey={pricey.Contains(k)}");
    }
    if (pRoute == null)
    {
        Console.WriteLine($"NO ROUTE with marks: {pWhy}");
    }
    else
    {
        Console.WriteLine($"route with marks: {pRoute.Count} pts, {pRoute[0]} -> {pRoute[^1]}");
        foreach (var pad in pads)
        {
            var bd = float.MaxValue;
            for (var i = 1; i < pRoute.Count; i++)
            {
                bd = Math.Min(bd, SegDist(pad, pRoute[i - 1], pRoute[i]));
            }

            Console.WriteLine($"  pad ({pad.X:0.0},{pad.Z:0.0}): closest route approach {bd:0.0} m" +
                              (bd < 2.5f ? "   <== THROUGH the trigger" : ""));
        }
    }

    var plain = pGrid.FindPath(fromP, goalP, null, 8f, 3f, out _, null)
                ?? pGrid.FindPath(fromP, goalP, null, 8f, 8f, out _, null);

    // the ASCII map, 2 m per char, x -> columns (east), z -> rows (north up)
    var xs = new[] { fromP.X, goalP.X };
    var zs = new[] { fromP.Z, goalP.Z };
    var x0 = Math.Min(xs[0], xs[1]) - 15;
    var x1 = Math.Max(xs[0], xs[1]) + 15;
    var z0 = Math.Min(zs[0], zs[1]) - 12;
    var z1 = Math.Max(zs[0], zs[1]) + 12;
    for (var z = z1; z >= z0; z -= 2f)
    {
        var row = new char[(int)((x1 - x0) / 2) + 1];
        for (var c = 0; c < row.Length; c++)
        {
            var wx = x0 + c * 2;
            row[c] = pricey.Contains(CellKey(pGrid, wx, z)) ? '.' : ' ';
            if (extra.Contains(CellKey(pGrid, wx, z)))
            {
                row[c] = '#';
            }
        }

        if (plain != null)
        {
            foreach (var p in plain)
            {
                if (Math.Abs(p.Z - z) < 1f)
                {
                    var c = (int)((p.X - x0) / 2);
                    if (c >= 0 && c < row.Length && row[c] != '#')
                    {
                        row[c] = ':'; // the plain (no-marks) route
                    }
                }
            }
        }

        if (pRoute != null)
        {
            foreach (var p in pRoute)
            {
                if (Math.Abs(p.Z - z) < 1f)
                {
                    var c = (int)((p.X - x0) / 2);
                    if (c >= 0 && c < row.Length)
                    {
                        row[c] = '*';
                    }
                }
            }
        }

        foreach (var pad in pads)
        {
            if (Math.Abs(pad.Z - z) < 1f)
            {
                var c = (int)((pad.X - x0) / 2);
                if (c >= 0 && c < row.Length)
                {
                    row[c] = 'W';
                }
            }
        }

        if (Math.Abs(fromP.Z - z) < 1f)
        {
            var c = (int)((fromP.X - x0) / 2);
            if (c >= 0 && c < row.Length)
            {
                row[c] = 'S';
            }
        }

        if (Math.Abs(goalP.Z - z) < 1f)
        {
            var c = (int)((goalP.X - x0) / 2);
            if (c >= 0 && c < row.Length)
            {
                row[c] = 'G';
            }
        }

        Console.WriteLine($"z={z,6:0} |{new string(row)}|");
    }

    Console.WriteLine($"        x {x0:0} -> {x1:0}   S start, G goal, W whompah pad, * route with marks, : plain route, # blocked, . priced");
    return 0;

    static int CellKey(MeshGrid g, float x, float z) =>
        (int)(z / g.Cell) * g.CellsX + (int)(x / g.Cell);

    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        var abx = b.X - a.X;
        var abz = b.Z - a.Z;
        var t = Math.Clamp(((p.X - a.X) * abx + (p.Z - a.Z) * abz) / (abx * abx + abz * abz + 1e-6f), 0f, 1f);
        var dx = p.X - (a.X + abx * t);
        var dz = p.Z - (a.Z + abz * t);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

if (args.Length >= 5 && args[0] == "floormap")
{
    int fPf = int.Parse(args[1], CultureInfo.InvariantCulture);
    float F3(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    var fcx = F3(args[2]);
    var fcz = F3(args[3]);
    var fspan = F3(args[4]);
    var refY = args.Length > 5 ? F3(args[5]) : float.NaN;
    var fGrid = MeshGrid.Load(baseDir, fPf, s => Console.WriteLine(s));
    if (fGrid == null) { Console.WriteLine("no meshes.bin for pf " + fPf); return 1; }

    var sealedCells = 0;
    var groundCells = 0;
    for (var z = fcz + fspan; z >= fcz - fspan; z -= 1f)
    {
        var row = "";
        for (var x = fcx - fspan; x <= fcx + fspan; x += 1f)
        {
            var lv = fGrid.Surfaces(x + 0.5f, z + 0.5f);
            if (lv.Length == 0)
            {
                row += ' ';
                continue;
            }

            if (float.IsNaN(refY))
            {
                row += lv.Length > 1 ? '2' : '1';
                continue;
            }

            var near = false;
            foreach (var f in lv)
            {
                if (Math.Abs(f - refY) <= 2.5f)
                {
                    near = true;
                    break;
                }
            }

            if (near) { row += '.'; groundCells++; }
            else { row += '#'; sealedCells++; }
        }

        Console.WriteLine($"z={z,6:0} {row}");
    }

    Console.WriteLine($"x {fcx - fspan:0} -> {fcx + fspan:0}  centre ({fcx:0},{fcz:0}); " +
                      $"'.' floor within 2.5 m of y={refY:0.0} ({groundCells} cells), '#' only other levels ({sealedCells} cells), ' ' void");
    return 0;
}

if (args.Length >= 6 && args[0] == "door")
{
    // replicate the pad-front staging: per direction, the ground run + the clearance veto
    int dPf = int.Parse(args[1], CultureInfo.InvariantCulture);
    float F4(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    var pad = new Vector3(F4(args[2]), 0, F4(args[3]));
    var dfrom = new Vector3(F4(args[4]), 8f, F4(args[5]));
    var dGrid = MeshGrid.Load(baseDir, dPf, s => Console.WriteLine(s));
    if (dGrid == null) { Console.WriteLine("no mesh"); return 1; }

    try { Zoning.Load(baseDir, _ => { }); } catch { }

    var toPad = pad - dfrom;
    toPad.Y = 0;
    var bd = toPad.Magnitude > 0.5f ? toPad.Normalize() : new Vector3(1, 0, 0);
    foreach (var deg in new[] { 0f, 180f, 90f, -90f, 45f, -45f, 135f, -135f })
    {
        var r = deg * MathF.PI / 180f;
        var dir = new Vector3(bd.X * MathF.Cos(r) + bd.Z * MathF.Sin(r), 0,
            -bd.X * MathF.Sin(r) + bd.Z * MathF.Cos(r));
        var run = 0f;
        for (var t = 0.5f; t <= 12f; t += 0.5f)
        {
            var p = pad + dir * t;
            var lv = dGrid.Surfaces(p.X, p.Z);
            var ok = false;
            foreach (var f in lv)
            {
                if (Math.Abs(f - dfrom.Y) <= 2.5f)
                {
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                break;
            }

            run = t;
        }

        var veto = "";
        foreach (var e in Zoning.ExitsFrom(dPf))
        {
            if (e.Kind == ExitKind.ZoneLine)
            {
                var s2 = pad + dir * 2;
                var abx = e.B.X - e.A.X;
                var abz = e.B.Z - e.A.Z;
                var tt = Math.Clamp(((s2.X - e.A.X) * abx + (s2.Z - e.A.Z) * abz) / (abx * abx + abz * abz + 1e-6f), 0f, 1f);
                var ddx = s2.X - (e.A.X + abx * tt);
                var ddz = s2.Z - (e.A.Z + abz * tt);
                if (MathF.Sqrt(ddx * ddx + ddz * ddz) <= 2f)
                {
                    veto = "zoneline->" + e.ToPf;
                }
            }
            else if (e.Kind is ExitKind.Line or ExitKind.Proxy or ExitKind.Teleport)
            {
                var d = Movement.Flat(e.A, pad + dir * 2);
                if (d > 1.5f && d <= 6f && Movement.Flat(e.A, dfrom) > 3.5f)
                {
                    veto = "disc->" + e.ToPf;
                }
            }
        }

        Console.WriteLine($"deg {deg,5:+0;-0;0} dir ({dir.X:+0.00;-0.00},{dir.Z:+0.00;-0.00}) standoff ({pad.X + dir.X * 2:0.0},{pad.Z + dir.Z * 2:0.0}) run {run,5:0.0} {(veto == "" ? "clear" : veto)}");
    }

    return 0;
}

if (args.Length >= 5)
{
    if (args[0] == "surf")
    {
        // surfaces along the line: where does the mesh column go empty or jump levels
        var pfS = int.Parse(args[1], CultureInfo.InvariantCulture);
        var ax = float.Parse(args[2], CultureInfo.InvariantCulture);
        var az = float.Parse(args[3], CultureInfo.InvariantCulture);
        var bx = float.Parse(args[4], CultureInfo.InvariantCulture);
        var bz = float.Parse(args[5], CultureInfo.InvariantCulture);
        var g = MeshGrid.Load(baseDir, pfS, s => Console.WriteLine(s));
        if (g == null) { Console.WriteLine("no mesh grid"); return 1; }
        var steps = (int)Math.Max(1, MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)));
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (float)steps;
            var x = ax + (bx - ax) * t;
            var z = az + (bz - az) * t;
            var lv = g.Surfaces(x, z);
            Console.WriteLine("  (" + x.ToString("F1", CultureInfo.InvariantCulture) + "," + z.ToString("F1", CultureInfo.InvariantCulture) + ")  " +
                              (lv.Length == 0 ? "EMPTY" : string.Join(", ", lv.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))));
        }
        return 0;
    }

    var pf = int.Parse(args[0], CultureInfo.InvariantCulture);
    var x1 = float.Parse(args[1], CultureInfo.InvariantCulture);
    var z1 = float.Parse(args[2], CultureInfo.InvariantCulture);
    var x2 = float.Parse(args[3], CultureInfo.InvariantCulture);
    var z2 = float.Parse(args[4], CultureInfo.InvariantCulture);

    var ground = NavGround.Read(Path.Combine(baseDir, "GameData", "Nav", pf.ToString(), "ground.bin"));
    Console.WriteLine($"water planes ({ground.WaterY.Length}): " +
                      string.Join(", ", ground.WaterY.Select(y => y.ToString("F2", CultureInfo.InvariantCulture))));
    Console.WriteLine("SwimY grid around the start (5 m step, '.' = dry):");
    for (var dz = -40; dz <= 40; dz += 5)
    {
        var row = "";
        for (var dx = -40; dx <= 40; dx += 5)
        {
            var s = ground.SwimY(x1 + dx, z1 + dz, 1.0);
            row += double.IsNaN(s) ? " ." : s.ToString("F0", CultureInfo.InvariantCulture).PadLeft(3);
        }
        Console.WriteLine("  z" + (z1 + dz).ToString(CultureInfo.InvariantCulture).PadLeft(5) + " " + row);
    }

    var grid = MeshGrid.Load(baseDir, pf, s => Console.WriteLine(s))
              ?? (IWalkGrid)OverlandGrid.FromLattice(baseDir, pf, s => Console.WriteLine(s));
    if (grid == null)
    {
        Console.WriteLine("no meshes.bin/lattice.bin for pf " + pf + " under " + baseDir);
        return 1;
    }

    Console.WriteLine($"grid loaded: {grid.GetType().Name}");
    Console.WriteLine($"OpenAt start ({x1},{z1}): {grid.OpenAt(new Vector3(x1, float.NaN, z1))};  OpenAt goal ({x2},{z2}): {grid.OpenAt(new Vector3(x2, float.NaN, z2))}");
    var a = new Vector3(x1, float.NaN, z1);
    var b = new Vector3(x2, float.NaN, z2);
    var route = grid.FindPath(a, b, null, 8f, 3f, out var why, null)
                ?? grid.FindPath(a, b, null, 8f, 8f, out why, null);
    if (route == null)
    {
        Console.WriteLine($"NO ROUTE: {why}");
        return 1;
    }

    Console.WriteLine($"route: {route.Count} points, {route[0]} -> {route[^1]}");
    float yMin = float.MaxValue, yMax = float.MinValue;
    foreach (var p in route)
    {
        yMin = Math.Min(yMin, p.Y);
        yMax = Math.Max(yMax, p.Y);
    }

    Console.WriteLine($"y profile: {yMin:0.0} .. {yMax:0.0}");
    for (var i = 0; i < route.Count; i += Math.Max(1, route.Count / 12))
    {
        Console.WriteLine($"  [{i:3}] {route[i]}");
    }

    return 0;
}

var nav = AOBuddyNav.Load(baseDir, 1187);
if (nav == null) { Console.WriteLine("no nav data for 1187"); return 1; }
var grid1187 = (IWalkGrid)FloorGrid.Build(baseDir, 1187, nav, s => Console.WriteLine(s));
if (grid1187 == null) { Console.WriteLine("no grid for 1187"); return 1; }

Console.WriteLine("=== line door -> vendor (205,121.8) -> (211,129):");
var aa = new Vector3(205f, 5f, 121.8f);
var bb = new Vector3(211f, 5f, 129f);
var len = Math.Sqrt((bb.X - aa.X) * (bb.X - aa.X) + (bb.Z - aa.Z) * (bb.Z - aa.Z));
var n = (int)(len / 0.5f);
var bad = 0;
for (var s = 0; s <= n; s++)
{
    var t = s / (float)n;
    var x = aa.X + (bb.X - aa.X) * t;
    var z = aa.Z + (bb.Z - aa.Z) * t;
    var ok = grid1187.OpenAt(new Vector3(x, 5f, z));
    if (!ok)
    {
        bad++;
    }

    Console.WriteLine($"  ({x:0.0},{z:0.0)} {(ok ? "open" : "BLOCKED")}".Replace("(0,0)", ")"));
}

Console.WriteLine(bad == 0 ? "  LINE CLEAN" : $"  {bad} blocked samples");
return 0;
