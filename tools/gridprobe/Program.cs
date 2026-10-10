using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AOBuddy20.Nav;
using AOSharp.Common.GameData;

// Nav-data probe for a dungeon playfield's walk grid and the monitor's plan rendering:
//   gridprobe <pluginDir> <pf>                          - FindPath across every rooms.json door connection
//   gridprobe <pluginDir> <pf> <cx> <cz> [span]         - OpenAt lattice around a world point
//   gridprobe <pluginDir> <pf> water <x> <z> [span]      - wet/dry lattice around a world point
//                                                          (SwimY: the liquid polygons, wade 1.0)
//   gridprobe <pluginDir> <pf> <x1> <z1> <x2> <z2> [any]- one FindPath with its points
//   gridprobe <pluginDir> <pf> plan <cx> <cz> [span]    - the monitor's floor-0 plan as ASCII
//                                                         ('.' room floor, 't' threshold, '#' wall, ' ' void)
//   gridprobe selftest                                  - synthetic checks of the exact line-wall test
//   gridprobe mission <poolPf> <layout.txt>             - rebuild a saved mission instance
//                                                         (Build/mission-layout-*.txt) and sweep it
//   gridprobe <pluginDir> missionx <poolPf> <layout.txt> path <x0> <z0> <y0> <x1> <z1> <y1>
//     [block|pricy <x> <z> <r>]...                       - replay a mission planner query on the
//                                                         northbound grid ('classic' opts out), with
//                                                         synthetic mark discs: block = hard (extra),
//                                                         pricy = soft cost (the yank marks' semantics)
//   gridprobe <pluginDir> missionx <poolPf> <layout.txt> cost <out.png>
//                                                      - the walk grid as PNG, 2x2 px/cell, red = cost
var pluginDir = args.Length > 0 ? args[0] : "";

if (args.Length > 0 && args[0] == "selftest")
{
    return LineSelfTest.Run();
}

if (args.Length > 2 && args[1] == "northbound")
{
    // gridprobe <pluginDir> northbound <poolPf> - precalculate the per-room navigation
    // lattices (20 cm, unrotated pool frame) from the pool's own bins + rooms.json, into
    // GameData/Nav/<pf>/poolgrid.northbound. The bot's mission grids hydrate from it.
    int nPf = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var nNav = AOBuddyNav.Load(args[0], nPf) ?? throw new Exception($"no pool data for pf {nPf}");
    var nPool = nNav.Dungeon ?? throw new Exception($"pf {nPf} has no dungeon rooms");
    var folder = AOBuddyNav.FolderFor(args[0], nPf);
    List<List<float[]>> ReadBin(string file)
    {
        var byRoom = new List<List<float[]>>();
        foreach (var c in NavCollision.Read(System.IO.Path.Combine(folder, file)).Chunks)
        {
            var idx = c.Instance & 0xFFFF;
            while (byRoom.Count <= idx) byRoom.Add(null);
            (byRoom[idx] ??= new List<float[]>()).Add(c.Verts);
        }
        return byRoom;
    }

    var collision = ReadBin("collision.bin");
    var walls = ReadBin("walls.bin");
    var pool = new NorthboundPool { PoolPf = nPf, Cell = 0.2f, SourceHash = $"{collision.Count}x{walls.Count}" };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var built = 0;
    foreach (var pr in nPool.Rooms)
    {
        var cols = pr.Index < collision.Count ? collision[pr.Index] : null;
        var wls = pr.Index < walls.Count ? walls[pr.Index] : null;
        if (cols == null && wls == null) continue;
        var room = NorthboundBuilder.Build(pr.Index, pr, cols ?? new List<float[]>(), wls ?? new List<float[]>(),
            AOBuddyNav.DoorwaysFromField(pr), nPool);
        if (room == null) { Console.WriteLine($"  room {pr.Index} {pr.Name}: no geometry - skipped"); continue; }
        pool.Rooms[pr.Index] = room;
        built++;
    }

    var outPath = System.IO.Path.Combine(folder, "poolgrid.northbound");
    pool.Save(outPath);
    Console.WriteLine($"northbound pf {nPf}: {built} room(s), cell {pool.Cell:0.0#} m, {sw.ElapsedMilliseconds} ms -> {outPath}");
    foreach (var r in pool.Rooms.Values)
    {
        var fl = 0;
        var bl = 0;
        for (var c = 0; c < r.Levels.Length; c++)
        {
            if (r.Levels[c] is { Count: > 0 }) fl++;
            if (r.Blocked[c]) bl++;
        }

        var hist = new Dictionary<float, int>();
        foreach (var l in r.Levels)
        {
            if (l == null) continue;
            foreach (var v in l)
            {
                var key = (float)Math.Round(v, 1);
                hist[key] = hist.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        var top = string.Join(" ", hist.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key:0.0}x{kv.Value}"));
        Console.WriteLine($"  room {r.RoomIndex,3} {nPool.Rooms.First(x => x.Index == r.RoomIndex).Name,-32} {r.W,3}x{r.H,-3} floored {fl,6} blocked {bl,5} portals {r.Portals.Count}  levels: {top}");
        if (r.RoomIndex == 99 || r.RoomIndex == 43)
        {
            var prC = nPool.Rooms.First(x => x.Index == r.RoomIndex);
            int ci = (int)Math.Floor((prC.Pos[0] - r.Ox) / 0.2f), cj = (int)Math.Floor((prC.Pos[2] - r.Oz) / 0.2f);
            var c = cj * r.W + ci;
            var lv = c < r.Levels.Length ? r.Levels[c] : null;
            Console.WriteLine($"    centre cell ({ci},{cj}) of {r.W}x{r.H}: {(lv == null ? "NO LEVELS" : string.Join(" ", lv))} blocked {(c < r.Blocked.Length && r.Blocked[c] ? "yes" : "no")} (pool centre {prC.Pos[0]:0.0},{prC.Pos[2]:0.0}, lattice {r.Ox:0.0},{r.Oz:0.0})");
            // every triangle whose XZ bbox touches the centre, with its ny - what does the mesh
            // actually carry at the room's middle?
            var src = collision;
            foreach (var cv in src)
            {
                foreach (var carr in cv ?? new List<float[]>())
                {
                    for (int o = 0; o + 8 < carr.Length; o += 9)
                    {
                        float mnx = Math.Min(carr[o], Math.Min(carr[o + 3], carr[o + 6])), mxx = Math.Max(carr[o], Math.Max(carr[o + 3], carr[o + 6]));
                        float mnz = Math.Min(carr[o + 2], Math.Min(carr[o + 5], carr[o + 8])), mxz = Math.Max(carr[o + 2], Math.Max(carr[o + 5], carr[o + 8]));
                        if (prC.Pos[0] < mnx || prC.Pos[0] > mxx || prC.Pos[2] < mnz || prC.Pos[2] > mxz) continue;
                        float ux = carr[o + 3] - carr[o], uy = carr[o + 4] - carr[o + 1], uz = carr[o + 5] - carr[o + 2];
                        float wx = carr[o + 6] - carr[o], wy = carr[o + 7] - carr[o + 1], wz = carr[o + 8] - carr[o + 2];
                        var ny = uz * wx - ux * wz;
                        var len = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz) * (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                        Console.WriteLine($"    tri at centre: ny/len {(ny / len):0.00} y [{Math.Min(carr[o + 1], Math.Min(carr[o + 4], carr[o + 7])):0.0}..{Math.Max(carr[o + 1], Math.Max(carr[o + 4], carr[o + 7])):0.0}] x [{mnx:0.0}..{mxx:0.0}] z [{mnz:0.0}..{mxz:0.0}]");
                    }
                }
            }
        }
    }

    return 0;
}

if (args.Length > 4 && args[1] == "trail")
{
    // gridprobe <pluginDir> trail <poolPf> <layout.txt> <trailfile> - replay a captured walk
    // (id x y z per line, server-accepted positions) against the northbound grid: every point
    // the grid refuses is a cell-level bug with server truth attached.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    int tPf = int.Parse(args[2], inv);
    var tLayout = MissionSweep.BuildLayout(args[0], tPf, args[3]);
    NorthboundPool tNorth = null;
    var tNorthPath = System.IO.Path.Combine(AOBuddyNav.FolderFor(args[0], tPf), "poolgrid.northbound");
    if (System.IO.File.Exists(tNorthPath)) tNorth = NorthboundPool.Load(tNorthPath);
    var tGrid = FloorGrid.Build(args[0], 14624428, AOBuddyNav.ComposeMission(args[0], tLayout), s => Console.WriteLine("  [grid] " + s), tNorth);
    if (tGrid == null) { Console.WriteLine("grid build failed"); return 1; }

    int tOk = 0, tBad = 0;
    foreach (var line in System.IO.File.ReadAllLines(args[4]))
    {
        var p = line.Split(' ');
        if (p.Length < 4) continue;
        float F(string s) => float.Parse(s, inv);
        var x = F(p[1]); var wy = F(p[2]); var z = F(p[3]);
        if (tGrid.WalkableAt(x, z, wy, 1.0f)) { tOk++; continue; }
        tBad++;
        var fl = tGrid.FloorsAt(x, z);
        var near = fl.Length == 0 ? "no floor in cell" : "levels [" + string.Join(" ", fl.Select(v => v.ToString("0.0#"))) + "]";
        if (tBad <= 25)
            Console.WriteLine($"  REFUSED ({x:0.0},{wy:0.0},{z:0.0}): {near}");
    }

    Console.WriteLine($"trail: {tOk} accepted, {tBad} refused by the grid");
    return 0;
}

if (args.Length > 1 && args[1] == "mission")
{
    return MissionSweep.Run(args[0], args[2], args[3]);
}

if (args.Length > 1 && args[1] == "missionx")
{
    // gridprobe <pluginDir> missionx <poolPf> <layout.txt> floors <cx> <cz> [span] [y] - the
    //   composed cell lattice: every floor level per 0.5 m cell, '*' on the level FloorAt
    //   would pick for y
    // gridprobe <pluginDir> missionx <poolPf> <layout.txt> path <x0> <z0> <y0> <x1> <z1> <y1>
    //   - replay one planner query and print the route's y profile
    return MissionX.Run(args);
}

if (args.Length > 0 && args[0] == "wallfit")
{
    // gridprobe wallfit <pluginDir> <poolPf> <layout.txt> - per placed room, how the monitor's
    // drawn layers line up: the wall-line span (nav.Walls, the outlines) against the tile
    // footprint span (CellOf inverse, the ground tiles), today's tiles and the pre-2026-10-02
    // (+0.5 cell) ones. Answers "outlines offset from ground tiles" with numbers.
    return WallFit.Run(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
}

if (args.Length > 0 && args[0] == "poolfit")
{
    // gridprobe poolfit <pluginDir> <poolPf> - the same two layers in the POOL's own frame
    // (rooms placed as the extractor wrote them): walls.bin span vs CellOf tile span per room.
    return WallFit.RunPool(args[1], args[2]);
}

if (args.Length > 0 && args[0] == "overland")
{
    return OverlandProbe.Run(args[1], args);
}

if (args.Length > 2 && args[2] == "water")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    int wPf = int.Parse(args[1], inv);
    float wx = float.Parse(args[3], inv), wz = float.Parse(args[4], inv);
    var span = args.Length > 5 ? float.Parse(args[5], inv) : 16f;
    var wNav = AOBuddyNav.Load(pluginDir, wPf);
    if (wNav?.Ground == null) { Console.WriteLine($"no ground data for pf {wPf} under {pluginDir}"); return 1; }
    var g = wNav.Ground;
    // The bot's own rule (OverlandGrid.WadeDepth 1.0) plus the polygon truth at any depth.
    double floor = g.HeightAt(wx, wz), swim = g.SwimY(wx, wz, 1.0), poly = g.SwimY(wx, wz, 0);
    Console.WriteLine($"pf {wPf} ({wx:0.0},{wz:0.0}): terrain {(double.IsNaN(floor) ? "none (off-map)" : floor.ToString("0.00") + " m")}" +
        $", liquid polygon: {(double.IsNaN(poly) ? "none" : "y " + poly.ToString("0.00") + $" ({(poly - floor).ToString("0.00")} m over terrain)")}" +
        $", verdict: {(double.IsNaN(swim) ? "DRY (walkable)" : $"WATER (swim, {((swim - floor).ToString("0.00"))} m deep)")}");
    for (float z = wz + span; z >= wz - span - 1e-3f; z -= span / 8)
    {
        var row = "";
        for (float x = wx - span; x <= wx + span + 1e-3f; x += span / 8)
            row += double.IsNaN(g.HeightAt(x, z)) ? ' ' : double.IsNaN(g.SwimY(x, z, 1.0)) ? '.' : 'W';
        Console.WriteLine($"z={z,7:0.0}  {row}");
    }
    Console.WriteLine($"           x {wx - span:0.0} -> {wx + span:0.0} (centre {wx}; '.' dry, 'W' water, ' ' off-map)");
    return 0;
}

if (args.Length < 2) { Console.WriteLine("usage: gridprobe <pluginDir> <pf> [...] | gridprobe selftest | gridprobe mission <poolPf> <layout.txt>"); return 2; }
var pf = int.Parse(args[1]);
var nav = AOBuddyNav.Load(pluginDir, pf);
if (nav?.Dungeon == null) { Console.WriteLine("no dungeon data"); return 1; }
var d = nav.Dungeon;
var grid = FloorGrid.Build(pluginDir, pf, nav, s => Console.WriteLine("  [grid] " + s));
if (grid == null) { Console.WriteLine("no floor grid built"); return 1; }
float y = d.Rooms.Count > 0 ? d.Rooms[0].Pos[1] : 0;

if (args.Length > 2 && args[2] == "parity")
{
    // placement test: for each room, which parity shift (0/1 m per axis) puts its tiled cells
    // on the real ground-level collision floor? Decisive for the stored-centre-vs-floor-centre
    // question (the AOBuddy10 mission offset, for static rooms).
    if (nav.Collision == null) { Console.WriteLine("no collision.bin"); return 1; }
    var ground = new HashSet<(int, int)>();
    foreach (var ch in nav.Collision.Chunks)
        for (int i = 0; i + 2 < ch.Verts.Length; i += 3)
            if (ch.Verts[i + 1] > 4.5f && ch.Verts[i + 1] < 5.5f)
                ground.Add(((int)Math.Round(ch.Verts[i] / d.Cell), (int)Math.Round(ch.Verts[i + 2] / d.Cell)));
    Console.WriteLine($"ground collision cells (2 m lattice): {ground.Count}");
    foreach (var rm in d.Rooms)
    {
        int x1 = rm.Rect[0], z1 = rm.Rect[1];
        double mx = (rm.Rect[0] + rm.Rect[2] + 1) / 2.0, mz = (rm.Rect[1] + rm.Rect[3] + 1) / 2.0;
        int W = rm.Rect[2] - rm.Rect[0] + 1, H = rm.Rect[3] - rm.Rect[1] + 1;
        var turns = ((-rm.Rot) % 4 + 4) % 4;
        Console.WriteLine($"  room {rm.Index} '{rm.Name}' ({W}x{H}, rot {rm.Rot}):");
        for (int sz = 0; sz <= 1; sz++)
            for (int sx = 0; sx <= 1; sx++)
            {
                int hit = 0, miss = 0;
                for (int r = 0; r < rm.Tile.Length; r++)
                    for (int c = 0; c < rm.Tile[r].Length; c++)
                    {
                        if (rm.Tile[r][c] == 0) continue;
                        double tx = (x1 + c - mx) * d.Cell + sx, tz = (z1 + r - mz) * d.Cell + sz;
                        for (var t2 = 0; t2 < turns; t2++) { var t3 = tx; tx = -tz; tz = t3; }
                        var k = ((int)Math.Round((rm.Pos[0] + tx) / d.Cell), (int)Math.Round((rm.Pos[2] + tz) / d.Cell));
                        if (ground.Contains(k)) hit++; else miss++;
                    }
                var parity = (sx == (W % 2 == 0 ? 1 : 0) && sz == (H % 2 == 0 ? 1 : 0)) ? "  <-- parity (floor centre)" : "";
                Console.WriteLine($"    shift ({sx},{sz}): {hit,3} on floor, {miss,3} off  ({hit * 100 / Math.Max(1, hit + miss)} %){parity}");
            }
    }
    return 0;
}

if (args.Length > 2 && args[2] == "plan")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var cx = float.Parse(args[3], inv); var cz = float.Parse(args[4], inv);
    var span = args.Length > 5 ? float.Parse(args[5], inv) : 4f;
    var render = new AOBuddyMonitor.MapRender(pluginDir);
    AOBuddyMonitor.MapRender.MissionPlan plan = null;
    for (var i = 0; i < 60 && plan == null; i++) { plan = render.GetDungeon(pf); System.Threading.Thread.Sleep(200); }
    if (plan == null) { Console.WriteLine("no plan built"); return 1; }
    Console.WriteLine($"plan: {plan.Name}, {plan.W}x{plan.H} px, cell {plan.Cell} m, px/cell {plan.PxPerCell}, floors [{string.Join(",", plan.Floors)}]");
    for (float z = cz + span; z >= cz - span - 1e-3f; z -= 0.5f)
    {
        var row = "";
        for (float x = cx - span; x <= cx + span + 1e-3f; x += 0.5f)
        {
            int px = (int)((x - plan.MinX) / plan.Cell * plan.PxPerCell);
            int py = (int)((z - plan.MinZ) / plan.Cell * plan.PxPerCell);
            if (px < 0 || py < 0 || px >= plan.W || py >= plan.H) { row += '?'; continue; }
            int i = (py * plan.W + px) * 4;
            var bgra = plan.FloorBgra[plan.Floors[0]];
            row += bgra[i + 3] == 0 ? ' '
                 : bgra[i + 2] >= 190 && bgra[i] <= 80 ? 'D'
                 : bgra[i + 2] >= 140 ? '#'
                 : bgra[i + 2] is >= 65 and <= 75 ? 't'
                 : '.';
        }
        Console.WriteLine($"z={z,7:0.0}  {row}");
    }
    Console.WriteLine($"           x {cx - span:0.0} -> {cx + span:0.0} (centre {cx})");
    return 0;
}

if (args.Length > 2 && args.Length < 5 && args[2] != "parity")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var cx = float.Parse(args[2], inv); var cz = float.Parse(args[3], inv);
    var span = args.Length > 4 ? float.Parse(args[4], inv) : 4f;
    for (float z = cz + span; z >= cz - span - 1e-3f; z -= 0.5f)
    {
        var row = "";
        for (float x = cx - span; x <= cx + span + 1e-3f; x += 0.5f)
            row += grid.OpenAt(new Vector3(x, y, z)) ? "." : " ";
        Console.WriteLine($"z={z,7:0.0}  {row}");
    }
    Console.WriteLine($"           x {cx - span:0.0} -> {cx + span:0.0} (centre {cx})");
    return 0;
}

if (args.Length >= 6)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var a = new Vector3(float.Parse(args[2], inv), y, float.Parse(args[3], inv));
    var b = new Vector3(float.Parse(args[4], inv), args.Length >= 7 && args[6] == "any" ? float.NaN : y,
        float.Parse(args[5], inv));
    var one = grid.FindPath(a, b, null, 3f, 2f, out var whyOne);
    Console.WriteLine($"({a.X:0},{a.Z:0}) -> ({b.X:0},{b.Z:0}): " +
        (one != null ? "PATH " + string.Join(" -> ", one.Select(p => $"({p.X:0.0},{p.Z:0.0})")) : "NO PATH - " + whyOne));
    return 0;
}

Vector3 Centre(NavDungeon.Room rm)
{
    if (rm?.Tile == null || rm.Rect == null || rm.Pos == null) return default;
    int a1 = rm.Rect[0], b1 = rm.Rect[1];
    double amx = (rm.Rect[0] + rm.Rect[2] + 1) / 2.0, amz = (rm.Rect[1] + rm.Rect[3] + 1) / 2.0;
    var tn = ((-rm.Rot) % 4 + 4) % 4;
    double sx = 0, sz = 0; int n = 0;
    for (int r = 0; r < rm.Tile.Length; r++)
    for (int c = 0; c < rm.Tile[r].Length; c++)
    {
        if (rm.Tile[r][c] == 0) continue;
        double tx = (a1 + c - amx) * d.Cell, tz = (b1 + r - amz) * d.Cell;
        for (int i = 0; i < tn; i++) (tx, tz) = (-tz, tx);
        sx += rm.Pos[0] + tx; sz += rm.Pos[2] + tz; n++;
    }
    return n == 0 ? default : new Vector3((float)(sx / n), rm.Pos[1], (float)(sz / n));
}

int ok = 0, fail = 0;
foreach (var rm in d.Rooms)
{
    if (rm?.Doors == null) continue;
    foreach (var door in rm.Doors)
    {
        if (door == null || door.Length < 2 || door[0] == 65535 || door[0] >= d.Rooms.Count) continue;
        var a = Centre(rm); var b = Centre(d.Rooms[door[0]]);
        if (a == default || b == default) continue;
        var pts = grid.FindPath(a, b, null, 3f, 2f, out var why);
        if (pts != null) ok++; else fail++;
        Console.WriteLine($"  room {rm.Index} ({a.X:0.0},{a.Z:0.0}) -> room {door[0]} ({b.X:0.0},{b.Z:0.0}): " +
            (pts != null ? $"PATH ({pts.Count} pts)" : "NO PATH - " + why));
    }
}
Console.WriteLine($"  {ok} of {ok + fail} door connections pathable");
return 0;
// Synthetic checks of the exact line-wall test (NavCollision.LineBlocked), 2026-10-03. The wall
// triangles here are hand-built, so every verdict is known: this is the regression net for the
// geometry core the smoothing, the edge test and the geometry-direct fallback all stand on.
internal static class LineSelfTest
{
    private const double Low = 0.3, High = 1.9;

    // A wall face on the plane x=wx: a quad split into two triangles (vertical end edges carry the
    // face's height span - how real wall meshes are built).
    private static float[] Quad(float wx, float z0, float z1, float y0, float y1) => new float[]
    {
        wx, y0, z0, wx, y1, z0, wx, y1, z1,
        wx, y0, z0, wx, y1, z1, wx, y0, z1,
    };

    private static bool Blocked(float[] tris, double x0, double z0, double x1, double z1, double floor = 0)
        => NavCollision.FromTriangles(tris).LineBlocked(x0, z0, x1, z1, _ => floor, Low, High);

    private static int _failed;

    private static void Check(string name, bool got, bool want)
    {
        if (got == want)
        {
            Console.WriteLine($"  ok   {name}: {(got ? "BLOCK" : "clear")}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}: got {(got ? "BLOCK" : "clear")}, want {(want ? "BLOCK" : "clear")}");
        }
    }

    public static int Run()
    {
        Console.WriteLine("line-wall selftest (floor 0, body band 0.3-1.9):");

        // A 3 m wall across the line - the EXACTLY VERTICAL case, whose ground projection is a
        // zero-width line and which the point-sampled test was blind to altogether.
        var wall = Quad(5, -2, 2, 0, 3);
        Check("vertical wall across the line", Blocked(wall, 0, 0, 10, 0), true);
        Check("vertical wall off to the side", Blocked(wall, 0, 5, 10, 5), false);
        Check("line parallel to the wall face", Blocked(wall, 4.9, -5, 4.9, 5), false);

        // A doorway: two jambs and a lintel over a 1.6 m gap. Under it the line passes; the same
        // line at lintel height does not (the band rides on the caller's floor).
        var door = new float[0].Concat(Quad(5, -2, -0.8f, 0, 3)).Concat(Quad(5, 0.8f, 2, 0, 3))
            .Concat(Quad(5, -0.8f, 0.8f, 2f, 3)).ToArray();
        Check("doorway: through the gap", Blocked(door, 0, 0, 10, 0), false);
        Check("doorway: the line at lintel height", Blocked(door, 0, 0, 10, 0, 1.5), true);

        // A waist-high wall (1.5 m): the band catches it at floor level, and its top edge blocks a
        // line walked one floor up.
        Check("waist-high wall at floor 0", Blocked(Quad(5, -2, 2, 0, 1.5f), 0, 0, 10, 0), true);
        Check("wall from 2 to 3.5 m, floor 0", Blocked(Quad(5, -2, 2, 2f, 3.5f), 0, 0, 10, 0), false);
        Check("wall from 2 to 3.5 m, floor 1.5", Blocked(Quad(5, -2, 2, 2f, 3.5f), 0, 0, 10, 0, 1.5), true);

        // A line that ENDS inside a surface's projection: no edge is left to cross - the
        // containment fallback must catch it.
        var slab = new float[] { 0, 1, 0, 10, 1, 0, 0, 1, 10 };
        Check("line ends inside a slab at band height", Blocked(slab, 2, 2, 3, 2), true);
        Check("line crossing a slab ABOVE the band", Blocked(new float[] { 0, 2.5f, 0, 10, 2.5f, 0, 0, 2.5f, 10 }, -2, 5, 12, 5), false);
        Check("the 2.5 m slab from one floor up", Blocked(new float[] { 0, 2.5f, 0, 10, 2.5f, 0, 0, 2.5f, 10 }, -2, 5, 12, 5, 2.0), true);

        Console.WriteLine(_failed == 0 ? "all line-wall checks pass" : $"{_failed} check(s) FAILED");
        return _failed == 0 ? 0 : 1;
    }
}

// Mission-instance sweep (2026-10-03): rebuild a saved mission from its layout dump
// (Build/mission-layout-<pf>.txt), compose it exactly as the zone-in would, build the walk
// grid through the REAL-3D path (the only place the exact edge test runs), then check what
// matters: every doorway's approach/exit standoff pair must be a clear straight line, and
// every room's doorway-to-doorway routes must path. Over-blocking walls shows up as fails.
internal static class MissionSweep
{
    // The layout dump → composed MissionLayout: parse (exact slots when the dump carries them,
    // else the centre inversion), solve Height, add the room table. Shared with the trail replay.
    public static AOBuddyNav.MissionLayout BuildLayout(string pluginDir, int poolPf, string layoutPath)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var pool = AOBuddyNav.Load(pluginDir, poolPf) ?? throw new Exception($"no pool data for pf {poolPf}");
        var byName = new Dictionary<string, NavDungeon.Room>();
        foreach (var r in pool.Dungeon.Rooms) byName[r.Name] = r;

        // parse the dump: room N <PoolName> fF centre (cx,cz) y Y rot R [slot (X,Z)]  (de-DE commas)
        var rooms = new List<(string name, double cx, double cz, int rot, int? slotX, int? slotZ, int floor)>();
        int? layoutW = null, layoutH = null, layoutWh = null;
        foreach (var line in System.IO.File.ReadAllLines(layoutPath))
        {
            var lt = System.Text.RegularExpressions.Regex.Match(line, @"^layout .+ w (\d+) h (\d+) wh (\d+)");
            if (lt.Success) { layoutW = int.Parse(lt.Groups[1].Value); layoutH = int.Parse(lt.Groups[2].Value); layoutWh = int.Parse(lt.Groups[3].Value); continue; }
            var mt = System.Text.RegularExpressions.Regex.Match(line,
                @"room (\d+) (\S+) f(-?\d+) centre \(([\d,]+)\) y ([\d,]+) rot (\d)(?: slot \((-?\d+),(-?\d+)\))?");
            if (!mt.Success) continue;
            double D(string s) => double.Parse(s.Replace(',', '.'), inv);
            var parts = mt.Groups[4].Value.Split(',');
            // "(11,0,64,0)" is Pos[0]=11.0, Pos[2]=64.0 in the dump's comma decimals
            double cx = parts.Length >= 4 ? D(parts[0] + "." + parts[1]) : D(parts[0]);
            double cz = parts.Length >= 4 ? D(parts[^2] + "." + parts[^1]) : D(parts[^1]);
            rooms.Add((mt.Groups[2].Value, cx, cz, int.Parse(mt.Groups[6].Value),
                mt.Groups[7].Success ? int.Parse(mt.Groups[7].Value) : (int?)null,
                mt.Groups[8].Success ? int.Parse(mt.Groups[8].Value) : (int?)null,
                int.Parse(mt.Groups[3].Value)));
        }

        if (rooms.Count == 0) throw new Exception("no rooms parsed from " + layoutPath);
        var exactSlots = rooms.All(r => r.slotX.HasValue && r.slotZ.HasValue);
        if (exactSlots) Console.WriteLine("exact slots from the dump - no centre inversion");

        // invert ComposeMission's placement: ox = cx - tx - tw + 1, oz = cz - tz - th - 1
        // (Cell 2 m, slot 10 m); Z = H - (oz + 2*th) / 10 - find the H that makes every slot land
        // on whole numbers. Dumps that carry the room table's own slot (X,Z) skip this entirely -
        // the inversion's rounding bent rooms up to half a slot in z.
        var tw = new int[rooms.Count];
        var th = new int[rooms.Count];
        var ox = new double[rooms.Count];
        var oz = new double[rooms.Count];
        for (var i = 0; i < rooms.Count; i++)
        {
            if (!byName.TryGetValue(rooms[i].name, out var pr)) throw new Exception($"pool has no room '{rooms[i].name}'");
            int w = pr.Rect[2] - pr.Rect[0] + 1, h = pr.Rect[3] - pr.Rect[1] + 1;
            tw[i] = rooms[i].rot % 2 == 0 ? w : h;
            th[i] = rooms[i].rot % 2 == 0 ? h : w;
            int turns = ((-rooms[i].rot) % 4 + 4) % 4;
            double tx = 1, tz = 1;
            for (var k = 0; k < turns; k++) (tx, tz) = (-tz, tx);
            ox[i] = rooms[i].cx - tx - tw[i] + 1;
            oz[i] = rooms[i].cz - tz - th[i] - 1;
        }

        int bestH;
        if (exactSlots && layoutH.HasValue && layoutWh.HasValue)
        {
            bestH = layoutH.Value;
        }
        else
        {
            bestH = -1;
            double bestErr = double.MaxValue;
            for (var H = 1; H <= 256; H++)
            {
                double err = 0;
                for (var i = 0; i < rooms.Count; i++)
                {
                    err += Math.Abs(ox[i] / 10 - Math.Round(ox[i] / 10));
                    err += Math.Abs(H - (oz[i] + 2 * th[i]) / 10 - Math.Round(H - (oz[i] + 2 * th[i]) / 10));
                }
                if (err < bestErr) { bestErr = err; bestH = H; }
            }

            if (bestErr > rooms.Count * 0.15) throw new Exception($"slot reconstruction failed (err {bestErr:0.00})");
        }

        var m = new AOBuddyNav.MissionLayout
        {
            Instance = 14624428, TemplatePlayfield = poolPf, Width = layoutW ?? 64, Height = bestH, WorldHeight = layoutWh ?? 12,
        };
        for (var i = 0; i < rooms.Count; i++)
        {
            m.Rooms.Add(new[] { byName[rooms[i].name].Index, rooms[i].floor,
                exactSlots ? rooms[i].slotX.Value : (int)Math.Round(ox[i] / 10),
                exactSlots ? rooms[i].slotZ.Value : (int)Math.Round(bestH - (oz[i] + 2 * th[i]) / 10), rooms[i].rot });
        }

        return m;
    }

    public static int Run(string pluginDir, string poolPfArg, string layoutPath)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int poolPf = int.Parse(poolPfArg, inv);
        var m = BuildLayout(pluginDir, poolPf, layoutPath);

        var nav = AOBuddyNav.ComposeMission(pluginDir, m);

        // The dump's server-door line, when it carries doors: the live CorrectWithServerDoors pass
        // (apply: true) that MissionController runs - re-solve each room's translation from its
        // server doors, re-place walls/surfaces/doorways, THEN build the grid. Answers "what the
        // bot would walk with the correction on" offline.
        var sdLine = System.Text.RegularExpressions.Regex.Match(
            string.Join("\n", System.IO.File.ReadAllLines(layoutPath)), @"^server doors: (.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (sdLine.Success && !sdLine.Groups[1].Value.StartsWith("none"))
        {
            var sdoors = new List<(short room, short adjoining, Vector3 pos)>();
            foreach (var part in sdLine.Groups[1].Value.Split('|'))
            {
                var dm = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"Room=(-?\d+) Adj=(-?\d+) \(([\d,]+)\)");
                if (!dm.Success) continue;
                double D(string s) => double.Parse(s.Replace(',', '.'), inv);
                var pp = dm.Groups[3].Value.Split(',');
                // (X,Y,Z) since 2026-10-08; older dumps carry (X,Z) only - the level abstains (NaN)
                var y = pp.Length >= 6 ? D(pp[2] + "." + pp[3]) : float.NaN;
                sdoors.Add(((short)int.Parse(dm.Groups[1].Value), (short)int.Parse(dm.Groups[2].Value),
                            new Vector3((float)D(pp[0] + "." + pp[1]), (float)y, (float)D(pp[^2] + "." + pp[^1]))));
            }

            if (sdoors.Count > 0)
            {
                Console.WriteLine($"server-door correction on {sdoors.Count} door(s):");
                foreach (var line in AOBuddyNav.CorrectWithServerDoors(pluginDir, nav, sdoors, out _, apply: true))
                {
                    Console.WriteLine("  " + line);
                }
            }
        }

        NorthboundPool north = null;
        var northPath = System.IO.Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "poolgrid.northbound");
        if (System.IO.File.Exists(northPath))
        {
            try
            {
                north = NorthboundPool.Load(northPath); // refuses a stale version - rebuild below
            }
            catch (Exception ex)
            {
                Console.WriteLine($"northbound pool grid unreadable ({ex.Message}) - rebuilding.");
            }

            north ??= NorthboundPool.For(pluginDir, poolPf, s => Console.WriteLine("  " + s));
            if (north != null)
            {
                Console.WriteLine($"northbound pool grid: {north.Rooms.Count} room(s) at {north.Cell:0.0#} m");
            }
        }

        var grid = FloorGrid.Build(pluginDir, m.Instance, nav, s => Console.WriteLine("  [grid] " + s), north);
        if (nav == null || grid == null) { Console.WriteLine("compose/build failed"); return 1; }
        foreach (var dw in nav.MissionDoorways)
        {
            Console.WriteLine($"  doorway ({dw.X,6:0.0},{dw.Y,5:0.0},{dw.Z,6:0.0}) n({dw.Nx:0.0},{dw.Nz:0.0})");
        }

        Console.WriteLine("doorway meeting: " + AOBuddyNav.DoorCheck);
        Console.WriteLine($"north blit: {FloorGrid.DebugNorthCells} cell(s)");
        foreach (var rm in nav.Dungeon.Rooms)
        {
            Console.WriteLine($"  centre {rm.PoolName,-32} {grid.DebugCell(rm.Pos[0], rm.Pos[2])}");
        }
        Console.WriteLine($"suppressor: {FloorGrid.DebugBuriedCells} tile floor(s) buried");

        // 1. every doorway: standoff-in -> standoff-out must be a clear geometry line
        int lineOk = 0, lineBad = 0;
        foreach (var dw in nav.MissionDoorways)
        {
            var a = new Vector3((float)(dw.X + dw.Nx * 1.5), (float)dw.Y, (float)(dw.Z + dw.Nz * 1.5));
            var b = new Vector3((float)(dw.X - dw.Nx * 1.5), (float)dw.Y, (float)(dw.Z - dw.Nz * 1.5));
            if (grid.GeometryLine(a, b, out var gwhy)) lineOk++;
            else { lineBad++; Console.WriteLine($"  doorway ({dw.X:0.0},{dw.Z:0.0}) f{dw.Floor} NOT clear: {gwhy}"); }
        }
        Console.WriteLine($"  doorways with a clear straight crossing: {lineOk} of {lineOk + lineBad}");

        // 2. per room, doorway -> doorway through the interior must path
        var at = new Dictionary<int, List<AOBuddyNav.Doorway>>(); // room index -> its doorways
        foreach (var dw in nav.MissionDoorways)
        {
            foreach (var side in new[] { 1.5, -1.5 })
            {
                double px = dw.X + dw.Nx * side, pz = dw.Z + dw.Nz * side;
                foreach (var rm in nav.Dungeon.Rooms)
                    if (!double.IsNaN(nav.Dungeon.FloorHeight(rm, px, pz)))
                    {
                        if (!at.TryGetValue(rm.Index, out var l)) at[rm.Index] = l = new List<AOBuddyNav.Doorway>();
                        l.Add(dw);
                    }
            }
        }

        int ok = 0, fail = 0;
        foreach (var (rmIdx, doorways) in at)
        {
            var rm = nav.Dungeon.Rooms[rmIdx];
            for (var a = 0; a < doorways.Count; a++)
            for (var b = a + 1; b < doorways.Count; b++)
            {
                if (doorways[a] == doorways[b]) continue;
                var pa = new Vector3((float)(doorways[a].X + doorways[a].Nx * 1.5), (float)doorways[a].Y, (float)(doorways[a].Z + doorways[a].Nz * 1.5));
                var pb = new Vector3((float)(doorways[b].X + doorways[b].Nx * 1.5), (float)doorways[b].Y, (float)(doorways[b].Z + doorways[b].Nz * 1.5));
                var pts = grid.FindPath(pa, pb, null, 3f, 2f, out var why);
                if (pts != null) ok++;
                else { fail++; Console.WriteLine($"  room {rm.PoolName} f{rm.Floor} door->door NO PATH - {why}"); }
            }
        }
        Console.WriteLine($"  {ok} of {ok + fail} in-room door->door routes pathable");
        return fail == 0 && lineBad == 0 ? 0 : 1;
    }
}

// Overland replay (2026-10-03, Varmint Woods): rebuild the zone's OverlandGrid and re-run the
// planner's own queries from a live session - the clean plan, then the same plan under a
// simulated yank band (the cells CellsAlong(pos, walked, 1f) would blacklist). The verdicts and
// their timings are the offline copy of what the log shows.
internal static class OverlandProbe
{
    public static int Run(string pluginDir, string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // overland <pluginDir> <pf> <x0> <z0> <x1> <z1> [yankToX yankToZ]
        int pf = int.Parse(args[2], inv);
        float F(string s) => float.Parse(s, inv);
        // args: overland <pluginDir> <pf> <x0> <z0> <x1> <z1> [yankToX yankToZ [yFrom yGoal]]
        var from = new Vector3(F(args[3]), args.Length > 9 ? F(args[9]) : 0, F(args[4]));
        var goal = new Vector3(F(args[5]), args.Length > 10 ? F(args[10]) : 0, F(args[6]));
        Console.WriteLine($"from ({from.X:0.0},{from.Y:0.0},{from.Z:0.0}) -> goal ({goal.X:0.0},{goal.Y:0.0},{goal.Z:0.0})");
        var nav = AOBuddyNav.Load(pluginDir, pf);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grid = OverlandGrid.Build(pluginDir, pf, nav, s => Console.WriteLine("  [grid] " + s));
        if (grid == null) { Console.WriteLine("no overland grid"); return 1; }
        Console.WriteLine($"grid built in {sw.ElapsedMilliseconds} ms");

        var clean = grid.FindPath(from, goal, null, 15f, 3f, out var whyClean);
        Console.WriteLine($"clean: {(clean != null ? clean.Count + " pts" : "NO PATH - " + whyClean)}");

        if (args.Length >= 9)
        {
            var walked = new Vector3(F(args[7]), 0, F(args[8]));
            // replicate PlanRoute's exact extra: zone-line corridors, exit discs (with its
            // exemptions), then the yank band, then the 3 m unseal
            try { Zoning.Load(pluginDir, s => { }); } catch { }
            var extra = new HashSet<int>();
            foreach (var e in Zoning.ExitsFrom(pf))
            {
                if (e.Kind == AOBuddy20.Enums.ExitKind.ZoneLine)
                {
                    grid.CellsAlong(e.A, e.B, 2f, extra);
                }
                else if (e.Kind != AOBuddy20.Enums.ExitKind.Line && AOBuddy20.Components.Movement.Flat(e.A, goal) > 1.5f && AOBuddy20.Components.Movement.Flat(e.A, from) > 3.5f)
                {
                    grid.CellsAlong(e.A, e.A, 3f, extra);
                }
            }

            var zoneBand = extra.Count;
            grid.CellsAlong(from, walked, 1f, extra); // the yank band: server pos -> walked-from
            var unseal = new HashSet<int>();
            grid.CellsAlong(from, from, 3f, unseal);
            extra.ExceptWith(unseal);
            Console.WriteLine($"extra: {extra.Count} blacklisted cell(s) (zoning {zoneBand}, yank band incl.)");
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var banded = grid.FindPath(from, goal, extra, 15f, 3f, out var whyBand);
            Console.WriteLine($"banded: {(banded != null ? banded.Count + " pts" : "NO PATH - " + whyBand)} in {sw2.ElapsedMilliseconds} ms");

            // the geometry escape: the grid's own stuck fan, judged from this exact spot
            var swE = System.Diagnostics.Stopwatch.StartNew();
            var esc = grid.Escape(from, goal, out var ewhy);
            swE.Stop();
            Console.WriteLine(esc != null
                ? $"geometry escape: clear line to ({esc.Value.X:0.0},{esc.Value.Z:0.0}) ({ewhy}) in {swE.ElapsedMilliseconds} ms"
                : $"geometry escape: NONE ({ewhy}) in {swE.ElapsedMilliseconds} ms");

            // the exact live re-plan `from` is unknown (the body slid after the SetPos): sweep a
            // lattice around the yank spot and report every start that fails, with its timing
            Console.WriteLine("from sweep (dx, dz in [-8..8] m, y 11.5 / 12.1):");
            var fails = 0;
            for (var dy = 11.5f; dy <= 12.11f; dy += 0.6f)
            for (var dz = -8f; dz <= 8.01f; dz += 2f)
            for (var dx = -8f; dx <= 8.01f; dx += 2f)
            {
                var p = new Vector3(from.X + dx, dy, from.Z + dz);
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                var r = grid.FindPath(p, goal, extra, 15f, 3f, out var whyS);
                sw3.Stop();
                if (r == null)
                {
                    fails++;
                    Console.WriteLine($"  FAIL ({p.X:0.0},{p.Y:0.0},{p.Z:0.0}): {whyS} ({sw3.ElapsedMilliseconds} ms)");
                }
            }
            Console.WriteLine(fails == 0 ? "  no failing start in the lattice" : $"  {fails} failing start(s)");
        }
        return 0;
    }
}

// Per placed room of a saved mission layout: how far apart the monitor's two drawn layers sit.
// The outlines are nav.Walls (PlaceBin: pool wall verts relative pr.Pos, turned pool->mission,
// + GeomPos); the ground tiles are the monitor Walk's painted blocks (today: CellOf's inverse
// footprint [x, x+cell]; before 2026-10-02: from (a+0.5-ccx), half a cell east/south). If the
// offsets are not ~wall thickness, the plan draws outlines off its own floor.
internal static class WallFit
{
    public static int Run(string pluginDir, string poolPfArg, string layoutPath, string detail = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int poolPf = int.Parse(poolPfArg, inv);
        var pool = AOBuddyNav.Load(pluginDir, poolPf) ?? throw new Exception($"no pool data for pf {poolPf}");
        var byName = new Dictionary<string, NavDungeon.Room>();
        foreach (var r in pool.Dungeon.Rooms) byName[r.Name] = r;

        // the dump MissionSweep reads: room N <PoolName> fF centre (cx,cz) y Y rot R (de-DE commas)
        var rooms = new List<(string name, double cx, double cz, int rot)>();
        foreach (var line in System.IO.File.ReadAllLines(layoutPath))
        {
            var mt = System.Text.RegularExpressions.Regex.Match(line,
                @"room (\d+) (\S+) f(-?\d+) centre \(([\d,]+)\) y ([\d,]+) rot (\d)");
            if (!mt.Success) continue;
            double D(string s) => double.Parse(s.Replace(',', '.'), inv);
            var parts = mt.Groups[4].Value.Split(',');
            double cx = parts.Length >= 4 ? D(parts[0] + "." + parts[1]) : D(parts[0]);
            double cz = parts.Length >= 4 ? D(parts[^2] + "." + parts[^1]) : D(parts[^1]);
            rooms.Add((mt.Groups[2].Value, cx, cz, int.Parse(mt.Groups[6].Value)));
        }

        if (rooms.Count == 0) { Console.WriteLine("no rooms parsed from " + layoutPath); return 1; }

        // invert ComposeMission's placement (MissionSweep's math): ox = cx - tx - tw + 1, oz = cz - tz - th - 1
        var tw = new int[rooms.Count];
        var th = new int[rooms.Count];
        var ox = new double[rooms.Count];
        var oz = new double[rooms.Count];
        for (var i = 0; i < rooms.Count; i++)
        {
            if (!byName.TryGetValue(rooms[i].name, out var pr)) { Console.WriteLine($"pool has no room '{rooms[i].name}'"); return 1; }
            int w = pr.Rect[2] - pr.Rect[0] + 1, h = pr.Rect[3] - pr.Rect[1] + 1;
            tw[i] = rooms[i].rot % 2 == 0 ? w : h;
            th[i] = rooms[i].rot % 2 == 0 ? h : w;
            int turns = ((-rooms[i].rot) % 4 + 4) % 4;
            double tx = 1, tz = 1;
            for (var k = 0; k < turns; k++) (tx, tz) = (-tz, tx);
            ox[i] = rooms[i].cx - tx - tw[i] + 1;
            oz[i] = rooms[i].cz - tz - th[i] - 1;
        }

        int bestH = -1;
        double bestErr = double.MaxValue;
        for (var H = 1; H <= 256; H++)
        {
            double err = 0;
            for (var i = 0; i < rooms.Count; i++)
            {
                err += Math.Abs(ox[i] / 10 - Math.Round(ox[i] / 10));
                err += Math.Abs(H - (oz[i] + 2 * th[i]) / 10 - Math.Round(H - (oz[i] + 2 * th[i]) / 10));
            }
            if (err < bestErr) { bestErr = err; bestH = H; }
        }

        if (bestErr > rooms.Count * 0.15) { Console.WriteLine($"slot reconstruction failed (err {bestErr:0.00})"); return 1; }
        var m = new AOBuddyNav.MissionLayout
        {
            Instance = 14624428, TemplatePlayfield = poolPf, Width = 64, Height = bestH, WorldHeight = 12,
        };
        for (var i = 0; i < rooms.Count; i++)
        {
            m.Rooms.Add(new[] { byName[rooms[i].name].Index, 0,
                (int)Math.Round(ox[i] / 10), (int)Math.Round(bestH - (oz[i] + 2 * th[i]) / 10), rooms[i].rot });
        }

        var nav = AOBuddyNav.ComposeMission(pluginDir, m);
        if (nav == null || nav.Dungeon?.Rooms == null) { Console.WriteLine("compose failed"); return 1; }

        string wp = System.IO.Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "walls.bin");
        if (!System.IO.File.Exists(wp)) { Console.WriteLine("pool has no walls.bin"); return 1; }
        var byRoom = new Dictionary<int, List<float[]>>();
        foreach (var c in NavCollision.Read(wp).Chunks)
        {
            int idx = c.Instance & 0xFFFF;
            if (!byRoom.TryGetValue(idx, out var l)) byRoom[idx] = l = new List<float[]>();
            l.Add(c.Verts);
        }

        float cell = nav.Dungeon.Cell;
        Console.WriteLine("pool " + poolPf + ", " + rooms.Count + " rooms, cell " + cell + " m; spans in world m (x / z), offsets = tile - wall (detail room: " + (detail ?? "-") + ")");
        Console.WriteLine($"{"room",-28} {"rot",3} {"w x h",7} | {"wall span",26} | {"tiles now",26} | off now           | off pre-eb4fbdd");
        foreach (var rm in nav.Dungeon.Rooms)
        {
            if (rm.PoolIndex < 0 || !byRoom.TryGetValue(rm.PoolIndex, out var chunks)) continue;
            var pr = pool.Dungeon.Rooms[rm.PoolIndex];

            // walls: PlaceBin's transform (GeomPos pivot)
            double wminx = double.MaxValue, wmaxx = double.MinValue, wminz = double.MaxValue, wmaxz = double.MinValue;
            int turns = ((((-pr.Rot) % 4 + 4) % 4) - ((((-rm.Rot) % 4 + 4) % 4)) + 4) % 4;
            foreach (var v in chunks)
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    double dx = v[i] - pr.Pos[0], dz = v[i + 2] - pr.Pos[2];
                    for (int t = 0; t < turns; t++) { double s = dx; dx = dz; dz = -s; }
                    double wx = rm.GeomPos[0] + dx, wz = rm.GeomPos[2] + dz;
                    wminx = Math.Min(wminx, wx); wmaxx = Math.Max(wmaxx, wx);
                    wminz = Math.Min(wminz, wz); wmaxz = Math.Max(wmaxz, wz);
                }

            // tiles: the monitor Walk, painted block [x, x+cell] - today (turned footprint's min
            // corner) and the pre-eb4fbdd (+0.5 cell, turned low corner) convention
            double ccx = (rm.Rect[0] + rm.Rect[2] + 1) / 2.0, ccz = (rm.Rect[1] + rm.Rect[3] + 1) / 2.0;
            int turnsT = ((-rm.Rot) % 4 + 4) % 4;
            double nminx = double.MaxValue, nmaxx = double.MinValue, nminz = double.MaxValue, nmaxz = double.MinValue;
            double ominx = double.MaxValue, omaxx = double.MinValue, ominz = double.MaxValue, omaxz = double.MinValue;
            for (int row = 0; row < rm.Tile.Length; row++)
                for (int col = 0; col < rm.Tile[row].Length; col++)
                {
                    if (rm.Tile[row][col] == 0) continue;
                    int a = rm.Rect[0] + col, b = rm.Rect[1] + row;
                    {
                        double dx0 = (a - ccx) * cell, dz0 = (b - ccz) * cell;
                        double dx1 = dx0 + cell, dz1 = dz0 + cell;
                        for (int t = 0; t < turnsT; t++)
                        {
                            double s = dx0; dx0 = -dz0; dz0 = s;
                            s = dx1; dx1 = -dz1; dz1 = s;
                        }
                        double x0 = rm.Pos[0] + Math.Min(dx0, dx1), z0 = rm.Pos[2] + Math.Min(dz0, dz1);
                        nminx = Math.Min(nminx, x0); nmaxx = Math.Max(nmaxx, x0 + cell);
                        nminz = Math.Min(nminz, z0); nmaxz = Math.Max(nmaxz, z0 + cell);
                    }
                    {
                        double dx = (a + 0.5 - ccx) * cell, dz = (b + 0.5 - ccz) * cell;
                        for (int t = 0; t < turnsT; t++) { double s = dx; dx = -dz; dz = s; }
                        double x0 = rm.Pos[0] + dx, z0 = rm.Pos[2] + dz;
                        ominx = Math.Min(ominx, x0); omaxx = Math.Max(omaxx, x0 + cell);
                        ominz = Math.Min(ominz, z0); omaxz = Math.Max(omaxz, z0 + cell);
                    }
                }

            int w = pr.Rect[2] - pr.Rect[0] + 1, h = pr.Rect[3] - pr.Rect[1] + 1;
            string span(double a, double b, double c, double d) => $"({a,6:0.0}..{b,6:0.0})/({c,6:0.0}..{d,6:0.0})";
            string off(double a, double b, double c, double d) => $"({a,+4:0.0},{b,+4:0.0}) ({c,+4:0.0},{d,+4:0.0})";
            Console.WriteLine($"{rm.PoolName,-28} {rm.Rot,3} {w,3}x{h,-3} | {span(wminx, wmaxx, wminz, wmaxz)} | {span(nminx, nmaxx, nminz, nmaxz)}" +
                $" | {off(nminx - wminx, nmaxx - wmaxx, nminz - wminz, nmaxz - wmaxz)} | {off(ominx - wminx, omaxx - wmaxx, ominz - wminz, omaxz - wmaxz)}");
            if (detail == rm.PoolName)
            {
                Console.WriteLine($"    detail: pr.Rot {pr.Rot}, pr.Pos ({pr.Pos[0]:0.##},{pr.Pos[2]:0.##}), rect [{rm.Rect[0]},{rm.Rect[1]},{rm.Rect[2]},{rm.Rect[3]}]," +
                    $" mr.Pos ({rm.Pos[0]:0.###},{rm.Pos[2]:0.###}), mr.GeomPos ({rm.GeomPos[0]:0.###},{rm.GeomPos[2]:0.###})," +
                    $" turnsT {turnsT}, wall turns {turns}");
                Console.WriteLine($"    wall span rel pool pr.Pos (placed world - pool pos): x [{wminx - pr.Pos[0]:0.##}..{wmaxx - pr.Pos[0]:0.##}], z [{wminz - pr.Pos[2]:0.##}..{wmaxz - pr.Pos[2]:0.##}]");
                int rmin = -1, rmax = -1, cmin = -1, cmax = -1;
                for (int row = 0; row < rm.Tile.Length; row++)
                    for (int col = 0; col < rm.Tile[row].Length; col++)
                        if (rm.Tile[row][col] != 0)
                        {
                            if (rmin < 0 || row < rmin) rmin = row;
                            if (row > rmax) rmax = row;
                            if (cmin < 0 || col < cmin) cmin = col;
                            if (col > cmax) cmax = col;
                        }
                Console.WriteLine($"    mask nonzero: rows {rmin}..{rmax}, cols {cmin}..{cmax} of {rm.Tile.Length}x{rm.Tile[0].Length}");
                for (int row = rmin; row <= rmax; row++)
                    for (int col = cmin; col <= cmax; col++)
                    {
                        if (rm.Tile[row][col] == 0) continue;
                        int a = rm.Rect[0] + col, b = rm.Rect[1] + row;
                        double dx = (a - ccx) * cell, dz = (b - ccz) * cell;
                        double tdx = dx, tdz = dz;
                        for (int t = 0; t < turnsT; t++) { double s = tdx; tdx = -tdz; tdz = s; }
                        Console.WriteLine($"      cell r{row} c{col} -> a {a} b {b}: local ({dx:0.#},{dz:0.#}) turned ({tdx:0.#},{tdz:0.#}) world ({rm.Pos[0] + tdx:0.##},{rm.Pos[2] + tdz:0.##})");
                    }
            }
        }

        return 0;
    }

    // The pool itself: walls.bin placed where the extractor wrote it vs the CellOf tile frame.
    // This is the ground truth the mission compose must preserve (walls and tiles of one room
    // keep their mutual offset through GeomPos/Pos - or they do not, and the monitor's two
    // layers split).
    public static int RunPool(string pluginDir, string poolPfArg)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int poolPf = int.Parse(poolPfArg, inv);
        var pool = AOBuddyNav.Load(pluginDir, poolPf) ?? throw new Exception($"no pool data for pf {poolPf}");
        var d = pool.Dungeon;
        string wp = System.IO.Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "walls.bin");
        if (!System.IO.File.Exists(wp)) { Console.WriteLine("pool has no walls.bin"); return 1; }
        var byRoom = new Dictionary<int, List<float[]>>();
        foreach (var c in NavCollision.Read(wp).Chunks)
        {
            int idx = c.Instance & 0xFFFF;
            if (!byRoom.TryGetValue(idx, out var l)) byRoom[idx] = l = new List<float[]>();
            l.Add(c.Verts);
        }

        float cell = d.Cell;
        Console.WriteLine($"pool {poolPf}, cell {cell:0.#} m; offsets relative to the full rect's CellOf footprint (pool frame, world m)");
        Console.WriteLine($"{"room",-30} {"rot",3} {"w x h",7} {"Pos",14} | wall span (x..x, z..z)   | tile span (x..x, z..z)   | mask");
        foreach (var rm in d.Rooms)
        {
            if (!byRoom.TryGetValue(rm.Index, out var chunks)) continue;
            double wminx = double.MaxValue, wmaxx = double.MinValue, wminz = double.MaxValue, wmaxz = double.MinValue;
            foreach (var v in chunks)
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    wminx = Math.Min(wminx, v[i]); wmaxx = Math.Max(wmaxx, v[i]);
                    wminz = Math.Min(wminz, v[i + 2]); wmaxz = Math.Max(wmaxz, v[i + 2]);
                }

            double ccx = (rm.Rect[0] + rm.Rect[2] + 1) / 2.0, ccz = (rm.Rect[1] + rm.Rect[3] + 1) / 2.0;
            int turns = ((-rm.Rot) % 4 + 4) % 4;
            double tminx = double.MaxValue, tmaxx = double.MinValue, tminz = double.MaxValue, tmaxz = double.MinValue;
            int n = 0, rows = rm.Tile.Length, cols = rm.Tile.Length > 0 ? rm.Tile[0].Length : 0;
            for (int row = 0; row < rm.Tile.Length; row++)
                for (int col = 0; col < rm.Tile[row].Length; col++)
                {
                    if (rm.Tile[row][col] == 0) continue;
                    n++;
                    int a = rm.Rect[0] + col, b = rm.Rect[1] + row;
                    double dx = (a - ccx) * cell, dz = (b - ccz) * cell;
                    for (int t = 0; t < turns; t++) { double s = dx; dx = -dz; dz = s; }
                    double x0 = rm.Pos[0] + dx, z0 = rm.Pos[2] + dz, x1 = x0 + cell, z1 = z0 + cell;
                    tminx = Math.Min(tminx, x0); tmaxx = Math.Max(tmaxx, x1);
                    tminz = Math.Min(tminz, z0); tmaxz = Math.Max(tmaxz, z1);
                }

            // both spans relative to the full rect's CellOf footprint (the slot frame the compose maps)
            double rminx = rm.Pos[0] + (rm.Rect[0] - ccx) * cell, rmaxx = rm.Pos[0] + (rm.Rect[2] + 1 - ccx) * cell;
            double rminz = rm.Pos[2] + (rm.Rect[1] - ccz) * cell, rmaxz = rm.Pos[2] + (rm.Rect[3] + 1 - ccz) * cell;
            string sp(double a, double b, double c, double e) => $"({a - rminx,+5:0.#}..{b - rmaxx,+5:0.#})({c - rminz,+5:0.#}..{e - rmaxz,+5:0.#})";
            Console.WriteLine($"{rm.Name,-30} {rm.Rot,3} {rm.Rect[2] - rm.Rect[0] + 1,3}x{rm.Rect[3] - rm.Rect[1] + 1,-3}" +
                $" ({rm.Pos[0],5:0.#},{rm.Pos[2],5:0.#})" +
                $" | {sp(wminx, wmaxx, wminz, wmaxz)} | {sp(tminx, tmaxx, tminz, tmaxz)} | {cols}x{rows}, {n} cells");
        }

        return 0;
    }
}

// Cell-level inspection of a composed mission grid (2026-10-04): MissionSweep answers "do the
// doors path"; this answers "what exactly did the grid record HERE" - the floor stack per 0.5 m
// cell and one replayed planner query with its y profile. Built for the Subway ramp room, where
// the route walked into the stairs instead of on top of them.
internal static class MissionX
{
    public static int Run(string[] args)
    {
        // missionx <pluginDir> <poolPf> <layout.txt> floors|path ...
        if (args.Length < 5) { Console.WriteLine("usage: missionx <pluginDir> <poolPf> <layout.txt> floors <cx> <cz> [span] [y] | path <x0> <z0> <y0> <x1> <z1> <y1>"); return 2; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int poolPf = int.Parse(args[2], inv);
        var pool = AOBuddyNav.Load(args[0], poolPf) ?? throw new Exception($"no pool data for pf {poolPf}");
        var byName = new Dictionary<string, NavDungeon.Room>();
        foreach (var r in pool.Dungeon.Rooms) byName[r.Name] = r;

        // the MissionSweep layout parse: room N <PoolName> fF centre (cx,cz) y Y rot R (de-DE commas)
        var rooms = new List<(string name, double cx, double cz, int rot)>();
        foreach (var line in System.IO.File.ReadAllLines(args[3]))
        {
            var mt = System.Text.RegularExpressions.Regex.Match(line,
                @"room (\d+) (\S+) f(-?\d+) centre \(([\d,]+)\) y ([\d,]+) rot (\d)");
            if (!mt.Success) continue;
            double D(string s) => double.Parse(s.Replace(',', '.'), inv);
            var parts = mt.Groups[4].Value.Split(',');
            double cx = parts.Length >= 4 ? D(parts[0] + "." + parts[1]) : D(parts[0]);
            double cz = parts.Length >= 4 ? D(parts[^2] + "." + parts[^1]) : D(parts[^1]);
            rooms.Add((mt.Groups[2].Value, cx, cz, int.Parse(mt.Groups[6].Value)));
        }

        if (rooms.Count == 0) { Console.WriteLine("no rooms parsed from " + args[3]); return 1; }

        var tw = new int[rooms.Count];
        var th = new int[rooms.Count];
        var ox = new double[rooms.Count];
        var oz = new double[rooms.Count];
        for (var i = 0; i < rooms.Count; i++)
        {
            if (!byName.TryGetValue(rooms[i].name, out var pr)) { Console.WriteLine($"pool has no room '{rooms[i].name}'"); return 1; }
            int w = pr.Rect[2] - pr.Rect[0] + 1, h = pr.Rect[3] - pr.Rect[1] + 1;
            tw[i] = rooms[i].rot % 2 == 0 ? w : h;
            th[i] = rooms[i].rot % 2 == 0 ? h : w;
            int turns = ((-rooms[i].rot) % 4 + 4) % 4;
            double tx = 1, tz = 1;
            for (var k = 0; k < turns; k++) (tx, tz) = (-tz, tx);
            ox[i] = rooms[i].cx - tx - tw[i] + 1;
            oz[i] = rooms[i].cz - tz - th[i] - 1;
        }

        int bestH = -1;
        double bestErr = double.MaxValue;
        for (var H = 1; H <= 256; H++)
        {
            double err = 0;
            for (var i = 0; i < rooms.Count; i++)
            {
                err += Math.Abs(ox[i] / 10 - Math.Round(ox[i] / 10));
                err += Math.Abs(H - (oz[i] + 2 * th[i]) / 10 - Math.Round(H - (oz[i] + 2 * th[i]) / 10));
            }
            if (err < bestErr) { bestErr = err; bestH = H; }
        }

        if (bestErr > rooms.Count * 0.15) { Console.WriteLine($"slot reconstruction failed (err {bestErr:0.00})"); return 1; }
        var m = new AOBuddyNav.MissionLayout
        {
            Instance = 14624516, TemplatePlayfield = poolPf, Width = 64, Height = bestH, WorldHeight = 12,
        };
        for (var i = 0; i < rooms.Count; i++)
        {
            m.Rooms.Add(new[] { byName[rooms[i].name].Index, 0,
                (int)Math.Round(ox[i] / 10), (int)Math.Round(bestH - (oz[i] + 2 * th[i]) / 10), rooms[i].rot });
        }

        var nav = AOBuddyNav.ComposeMission(args[0], m);

        // live parity: the dump's server-door rows hang the rooms exactly as MissionController
        // does, so the PNG answers for the building the bot walks - not the bare slot placement
        // (2026-10-10: the cost map used to render pre-chain rooms)
        var sdLine = System.Text.RegularExpressions.Regex.Match(
            string.Join("\n", System.IO.File.ReadAllLines(args[3])), @"^server doors: (.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var sdoors = new List<(short room, short adjoining, Vector3 pos)>();
        if (sdLine.Success && !sdLine.Groups[1].Value.StartsWith("none"))
        {
            foreach (var part in sdLine.Groups[1].Value.Split('|'))
            {
                var dm = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"Room=(-?\d+) Adj=(-?\d+) \(([\d,]+)\)");
                if (!dm.Success) continue;
                double D(string s) => double.Parse(s.Replace(',', '.'), inv);
                var pp = dm.Groups[3].Value.Split(',');
                var y = pp.Length >= 6 ? D(pp[2] + "." + pp[3]) : float.NaN;
                sdoors.Add(((short)int.Parse(dm.Groups[1].Value), (short)int.Parse(dm.Groups[2].Value),
                            new Vector3((float)D(pp[0] + "." + pp[1]), (float)y, (float)D(pp[^2] + "." + pp[^1]))));
            }

            if (sdoors.Count > 0)
            {
                Console.WriteLine($"server-door correction on {sdoors.Count} door(s):");
                foreach (var line in AOBuddyNav.CorrectWithServerDoors(args[0], nav, sdoors, out _, apply: true))
                {
                    Console.WriteLine("  " + line);
                }
            }
        }

        FloorGrid.DebugSkipRoomTiles = args.Any(a => a == "notiles");
        // live parity: NavGridCache hands every mission build the pool's precalculated lattices;
        // 'classic' opts out so the two grids answer the same query side by side
        var north = args.Any(a => a == "classic")
            ? null
            : NorthboundPool.For(args[0], poolPf, s => Console.WriteLine("  [nb] " + s));
        var grid = FloorGrid.Build(args[0], m.Instance, nav, s => Console.WriteLine("  [grid] " + s), north);
        if (nav == null || grid == null) { Console.WriteLine("compose/build failed"); return 1; }
        Console.WriteLine("doorway meeting: " + AOBuddyNav.DoorCheck);
        Console.WriteLine($"suppressor: {FloorGrid.DebugBuriedCells} tile floor(s) buried");

        if (args[4] == "leafs")
        {
            // LEAF-ROOM PATHABILITY (owner, 2026-10-10): every room with exactly ONE server-door
            // connection must be walkable from the entrance room's centre - the goal is the leaf
            // room's own centre, approach radius 1 m. The server rows name the tree; b.Y = NaN
            // accepts the centre at any floor level.
            var neighbours = new Dictionary<int, HashSet<int>>();
            foreach (var (room, adjoining, _) in sdoors)
            {
                if (room < 0 || adjoining < 0 || room >= nav.Dungeon.Rooms.Count || adjoining >= nav.Dungeon.Rooms.Count)
                {
                    continue;
                }

                if (!neighbours.TryGetValue(room, out var s1)) neighbours[room] = s1 = new HashSet<int>();
                if (!neighbours.TryGetValue(adjoining, out var s2)) neighbours[adjoining] = s2 = new HashSet<int>();
                s1.Add(adjoining);
                s2.Add(room);
            }

            var startRoom = nav.Dungeon.Rooms[0];
            var from = new Vector3(startRoom.Pos[0], startRoom.Pos[1], startRoom.Pos[2]);
            var leaves = nav.Dungeon.Rooms
                .Where(r => r.Index != 0 && neighbours.TryGetValue(r.Index, out var n) && n.Count == 1)
                .ToList();
            Console.WriteLine($"leaf rooms (1 connection): {leaves.Count} of {nav.Dungeon.Rooms.Count} room(s); start room 0 {startRoom.Name}");
            var reachable = 0;
            foreach (var r in leaves)
            {
                var goal = new Vector3(r.Pos[0], float.NaN, r.Pos[2]);
                var path = grid.FindPath(from, goal, null, 2f, 1.0f, out var why);
                if (path != null)
                {
                    reachable++;
                    double len = 0;
                    for (var i = 1; i < path.Count; i++)
                    {
                        var ddx = path[i].X - path[i - 1].X;
                        var ddz = path[i].Z - path[i - 1].Z;
                        len += Math.Sqrt(ddx * ddx + ddz * ddz);
                    }

                    var end = path[path.Count - 1];
                    Console.WriteLine($"  room {r.Index,2} {r.Name}: PATH, {path.Count} pts, {len:0.0} m, ends ({end.X:0.0},{end.Y:0.0},{end.Z:0.0})");
                }
                else
                {
                    Console.WriteLine($"  room {r.Index,2} {r.Name} centre ({r.Pos[0]:0.0},{r.Pos[2]:0.0}): NO PATH - {why}");
                    // TEMP: name the blockers around the leaf's centre - which room's blit owns
                    // each sealed cell, and what levels it carries
                    for (var wz = r.Pos[2] - 8f; wz <= r.Pos[2] + 8f; wz += 0.5f)
                    {
                        for (var wx = r.Pos[0] - 8f; wx <= r.Pos[0] + 8f; wx += 0.5f)
                        {
                            int ci = (int)((wx - grid.ProbeX0 * grid.Cell) / grid.Cell);
                            int cj = (int)((wz - grid.ProbeZ0 * grid.Cell) / grid.Cell); // the grid row - NOT the y-inverted image row
                            int key = cj * grid.ProbeW + ci;
                            var owner = grid.ProbeRoom.TryGetValue(key, out var o) ? o : -1;
                            if (grid.ProbeBlockedWalk(key))
                            {
                                var lv = string.Join(",", grid.ProbeLevels(key).Select(v => v.ToString("0.00")));
                                Console.WriteLine($"    BLOCKED ({wx:0.0},{wz:0.0}) owner room {owner} levels [{lv}]");
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"leaf pathability: {reachable}/{leaves.Count} reachable");
            return 0;
        }

        if (args[4] == "heightmap")
        {
            // HEIGHTMAP <out.png> <roomIndex> [span] - the floors around a room coloured by
            // height (blue low .. red high). Multi-level cells pick the LOWEST level with
            // 2 m of clear headroom above it (the walk-with-body-height floor - a bridge
            // shows over the water/ramp under it); void paints black, the 5 m grid grey.
            var outPath = args[5];
            var target = int.Parse(args[6], inv);
            var span = args.Length > 7 ? double.Parse(args[7], inv) : 20.0;
            var mr = nav.Dungeon.Rooms[target];
            var w = grid.ProbeW;
            var h = grid.ProbeH;
            var png = new CostPng(w * 2, h * 2);

            float? Pick(int key)
            {
                var lv = grid.ProbeLevels(key);
                if (lv.Length == 0)
                {
                    return null;
                }

                // 2 m of clear headroom above, and the level itself at or above y 0 - the
                // atlas ground plane and deep mine shafts sample below 0 and are not the
                // walker's floor. No qualifying level: the highest non-negative one.
                for (var f = 0; f + 1 < lv.Length; f++)
                {
                    if (lv[f] >= 0f && lv[f + 1] - lv[f] >= 2.0f)
                    {
                        return lv[f];
                    }
                }

                for (var f = lv.Length - 1; f >= 0; f--)
                {
                    if (lv[f] >= 0f)
                    {
                        return lv[f];
                    }
                }

                return lv[lv.Length - 1];
            }

            var cxw = mr.Pos[0];
            var czw = mr.Pos[2];
            var hmin = float.MaxValue;
            var hmax = float.MinValue;
            var picked = new Dictionary<int, float>();
            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    var wx = (grid.ProbeX0 + i + 0.5f) * grid.Cell;
                    var wz = (grid.ProbeZ0 + j + 0.5f) * grid.Cell;
                    if (Math.Abs(wx - cxw) > span || Math.Abs(wz - czw) > span)
                    {
                        continue;
                    }

                    var p = Pick(j * w + i);
                    if (p == null)
                    {
                        continue;
                    }

                    picked[j * w + i] = p.Value;
                    hmin = Math.Min(hmin, p.Value);
                    hmax = Math.Max(hmax, p.Value);
                }
            }

            (byte, byte, byte) Color(float t)
            {
                // blue (low) -> cyan -> green -> yellow -> red (high)
                if (t < 0.25f) return (0, (byte)(255 * t / 0.25f), 255);
                if (t < 0.5f) return (0, 255, (byte)(255 * (1 - (t - 0.25f) / 0.25f)));
                if (t < 0.75f) return ((byte)(255 * (t - 0.5f) / 0.25f), 255, 0);
                return (255, (byte)(255 * (1 - (t - 0.75f) / 0.25f)), 0);
            }

            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    var key = j * w + i;
                    byte r = 0, g = 0, b = 0;
                    if (picked.TryGetValue(key, out var v))
                    {
                        var t = (hmax - hmin) < 0.01f ? 0f : (v - hmin) / (hmax - hmin);
                        (r, g, b) = Color(t);
                    }
                    else if (i % 25 == 0 || j % 25 == 0) // the 5 m reference grid
                    {
                        r = g = b = 96;
                    }

                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            // y-inverted, matching PxY and the collision overlay: +z reads upward
                            png.Set(i * 2 + dx, (h - 1 - j) * 2 + dy, r, g, b);
                        }
                    }
                }
            }

            // THE PLACED COLLISION, vertex by vertex: the raw truth the lattices were sampled
            // from (collision.bin, PlaceBin-transformed to world coordinates). A vertex whose
            // colour disagrees with its cell's fill marks the blit diverging from the real
            // geometry - the bridge decks, ramps and pits show as vertex streams at their own
            // height, off-range depths clamp to the extreme colours.
            var vertsIn = 0;
            if (nav.Surfaces != null)
            {
                for (var i = 0; i + 2 < nav.Surfaces.Length; i += 3)
                {
                    var vx = nav.Surfaces[i];
                    var vz = nav.Surfaces[i + 2];
                    if (Math.Abs(vx - cxw) > span || Math.Abs(vz - czw) > span)
                    {
                        continue;
                    }

                    vertsIn++;
                    var vy = nav.Surfaces[i + 1];
                    var t = (hmax - hmin) < 0.01f ? 0f : Math.Clamp((float)((vy - hmin) / (hmax - hmin)), 0f, 1f);
                    var (r, g, b) = Color(t);
                    var pxi = (int)((vx - grid.ProbeX0 * grid.Cell) / grid.Cell) * 2;
                    var pyi = (int)(h - (vz - grid.ProbeZ0 * grid.Cell) / grid.Cell) * 2;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            png.Set(pxi + dx, pyi + dy, r, g, b);
                        }
                    }
                }
            }

            // the TARGET ROOM's own numbers, separate from the region: its picked floors and
            // the raw level stacks (shafts, ledges - everything ProbeLevels carries)
            var rmin = float.MaxValue;
            var rmax = float.MinValue;
            var rawMin = float.MaxValue;
            var rawMax = float.MinValue;
            var rcells = 0;
            foreach (var kv in picked)
            {
                if (!grid.ProbeRoom.TryGetValue(kv.Key, out var o) || o != target)
                {
                    continue;
                }

                rcells++;
                rmin = Math.Min(rmin, kv.Value);
                rmax = Math.Max(rmax, kv.Value);
                foreach (var v in grid.ProbeLevels(kv.Key))
                {
                    rawMin = Math.Min(rawMin, v);
                    rawMax = Math.Max(rawMax, v);
                }
            }

            png.Save(outPath);
            Console.WriteLine($"heightmap: room {target} {mr.Name} centre ({cxw:0.0},{czw:0.0}), span {span:0} m, {picked.Count} cell(s), blit heights {hmin:0.00}..{hmax:0.00}, {vertsIn} collision vertex(es) in region (blue low .. red high) -> {outPath}");
            Console.WriteLine($"  room {target} own cells: {rcells}, picked floors {rmin:0.00}..{rmax:0.00}, raw levels {rawMin:0.00}..{rawMax:0.00}");
            return 0;
        }

        if (args[4] == "cost")
        {
            // cost <out.png> - the walk grid as a PNG, 2x2 px per cell, Y-INVERTED so +z reads
            // upward. RED = the cell's walk cost at 64 per unit (plain floor 64, the max hug 3.5
            // lands at 255); a light-grey 5 m reference grid UNDERNEATH (it shows on the void);
            // doorways painted green across their leaf line.
            var w = grid.ProbeW;
            var h = grid.ProbeH;
            var cellPx = 2;
            var png = new CostPng(w * cellPx, h * cellPx);

            int PxX(float wx) => (int)((wx - grid.ProbeX0 * grid.Cell) / grid.Cell) * cellPx;
            int PxY(float wz) => (int)(h - (wz - grid.ProbeZ0 * grid.Cell) / grid.Cell) * cellPx;

            void Px(CostPng t, float wx, float wz, byte r, byte g, byte b)
            {
                var px = PxX(wx);
                var py = PxY(wz);
                for (var dy = 0; dy < cellPx; dy++)
                {
                    for (var dx = 0; dx < cellPx; dx++)
                    {
                        t.Set(px + dx, py + dy, r, g, b);
                    }
                }
            }

            // the paint passes, so the full map and every per-room map render identically
            void PaintGrid(CostPng t)
            {
                // the 5 m reference grid: 5 m / 0.2 m cell = every 25th cell - it stays visible
                // wherever the cost map has nothing to say (the void)
                for (var j = 0; j < h; j += 25)
                {
                    for (var i = 0; i < w; i++)
                    {
                        Px(t, grid.ProbeX0 * grid.Cell + (i + 0.5f) * grid.Cell,
                           grid.ProbeZ0 * grid.Cell + (j + 0.5f) * grid.Cell, 96, 96, 96);
                    }
                }

                for (var i = 0; i < w; i += 25)
                {
                    for (var j = 0; j < h; j++)
                    {
                        Px(t, grid.ProbeX0 * grid.Cell + (i + 0.5f) * grid.Cell,
                           grid.ProbeZ0 * grid.Cell + (j + 0.5f) * grid.Cell, 96, 96, 96);
                    }
                }
            }

            void PaintDoors(CostPng t)
            {
                // the doorways' STEL EXTENTS, green: the measured frame+leaf footprint of each
                // door (the same box the grid keeps open), cell by cell
                for (var di = 0; di < nav.MissionDoorways.Count && di < grid.ProbeDoorExtents.Count; di++)
                {
                    var (mnx, mxx, mnz, mxz, _, _) = grid.ProbeDoorExtents[di];
                    if (float.IsNaN(mnx))
                    {
                        continue;
                    }

                    for (var wx = mnx; wx <= mxx; wx += grid.Cell)
                    {
                        for (var wz = mnz; wz <= mxz; wz += grid.Cell)
                        {
                            Px(t, wx, wz, 0, 200, 0);
                        }
                    }
                }
            }

            void PaintLabels(CostPng t)
            {
                // the rotation label: each placed room's turn count at its slot centre (white
                // 3x5 glyph, scaled with the resolution)
                var gs = cellPx * 3; // font pixel scale
                foreach (var mr in nav.Dungeon.Rooms)
                {
                    var glyph = RotGlyphs.RotGlyph(mr.Rot);
                    if (glyph == null)
                    {
                        continue;
                    }

                    var px0 = PxX(mr.Pos[0]);
                    var py0 = PxY(mr.Pos[2]);
                    for (var rr = 0; rr < 5; rr++)
                    {
                        for (var cc = 0; cc < 3; cc++)
                        {
                            if (glyph[rr * 3 + cc] == 0)
                            {
                                continue;
                            }

                            for (var dy = 0; dy < gs; dy++)
                            {
                                for (var dx = 0; dx < gs; dx++)
                                {
                                    t.Set(px0 + cc * gs + dx - gs * 3 / 2, py0 + rr * gs + dy - gs * 5 / 2, 255, 255, 255);
                                }
                            }
                        }
                    }
                }
            }

            void PaintRoomRects(CostPng t)
            {
                // THE ROOM RECTANGLES, drawn LAST so nothing overwrites them: the placed tile
                // footprint per room (where floor tiles exist, walkable or not) as a thin cyan
                // outline - the slot box the compose puts each room's tiles in.
                const double slot = 10.0;
                const int line = 2;
                foreach (var mr in nav.Dungeon.Rooms)
                {
                    var src = pool.Dungeon.Rooms[mr.PoolIndex];
                    int tw = mr.Rot % 2 == 0 ? src.Rect[2] - src.Rect[0] + 1 : src.Rect[3] - src.Rect[1] + 1;
                    int th = mr.Rot % 2 == 0 ? src.Rect[3] - src.Rect[1] + 1 : src.Rect[2] - src.Rect[0] + 1;
                    double ox = mr.Slot[0] * slot, oz = (m.Height - mr.Slot[1]) * slot - th * pool.Dungeon.Cell;
                    var rx0 = PxX((float)ox);
                    var ry0 = PxY((float)(oz + th * pool.Dungeon.Cell));
                    var rx1 = PxX((float)(ox + tw * pool.Dungeon.Cell));
                    var ry1 = PxY((float)oz);
                    for (var x = rx0; x <= rx1; x++)
                    {
                        for (var d = 0; d < line; d++)
                        {
                            t.Set(x, ry0 + d, 0, 200, 255);
                            t.Set(x, ry1 - d, 0, 200, 255);
                        }
                    }

                    for (var y = ry0; y <= ry1; y++)
                    {
                        for (var d = 0; d < line; d++)
                        {
                            t.Set(rx0 + d, y, 0, 200, 255);
                            t.Set(rx1 - d, y, 0, 200, 255);
                        }
                    }
                }
            }

            // the cost map - every room's cells (the full map)
            var max = 0f;
            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    var c = grid.ProbeCost(j * w + i);
                    if (c <= 0f)
                    {
                        continue;
                    }

                    if (c > max)
                    {
                        max = c;
                    }

                    // unwalkable statels (a column blocking the walkable floor) paint BLUE
                    var blockedWalk = grid.ProbeBlockedWalk(j * w + i);
                    Px(png, grid.ProbeX0 * grid.Cell + (i + 0.5f) * grid.Cell,
                       grid.ProbeZ0 * grid.Cell + (j + 0.5f) * grid.Cell,
                       blockedWalk ? (byte)0 : (byte)Math.Clamp(c * 64f, 0f, 255f),
                       0, blockedWalk ? (byte)255 : (byte)0);
                }
            }

            PaintDoors(png);
            PaintLabels(png);
            PaintRoomRects(png); // last: nothing overwrites the room outlines
            png.Save(args[5]);
            Console.WriteLine($"cost map: {w}x{h} cells -> {w * 2}x{h * 2} px at {args[5]} (y-inverted, 5 m grid, doors green); cost 1 (plain floor) .. {max:0.##} (worst), red = cost x 64");

            // THE PER-ROOM MAPS (owner, 2026-10-09: "which room has a doorstep"): one full-canvas
            // image per room with ONLY that room's blit cells - red = cost, GREEN = the cell's
            // lowest floor over the room's dominant level (0.5 m -> full green): a doorstep at a
            // door shows as a yellow-green band, a flat room stays pure red.
            foreach (var mr in nav.Dungeon.Rooms)
            {
                var lowest = new Dictionary<int, float>();
                var freq = new Dictionary<int, int>();
                for (var kk = 0; kk < grid.ProbeW * grid.ProbeH; kk++)
                {
                    if (grid.ProbeRoom.TryGetValue(kk, out var ri) && ri != mr.Index)
                    {
                        continue;
                    }

                    var lv = grid.ProbeLevels(kk);
                    if (lv.Length == 0)
                    {
                        continue;
                    }

                    lowest[kk] = lv[0];
                    var key = (int)Math.Round(lv[0] / 0.1f);
                    freq[key] = freq.TryGetValue(key, out var n) ? n + 1 : 1;
                }

                if (lowest.Count == 0)
                {
                    continue;
                }

                var baseKey = freq.OrderByDescending(kv => kv.Value).First().Key;
                var baseLevel = baseKey * 0.1f;
                var rp = new CostPng(w * cellPx, h * cellPx);
                PaintGrid(rp);
                foreach (var kv in lowest)
                {
                    var i = kv.Key % w;
                    var j = kv.Key / w;
                    var c = grid.ProbeCost(kv.Key);
                    // the heightfield quantizes in heightScale steps (0.2 m) and gentle slopes
                    // flip between two quanta - in-game that renders as a smooth, flat floor
                    // (bilinear, owner-checked: "goes flat through the door"). A doorstep only
                    // counts when it exceeds the walker's step height (0.8 m).
                    var step = (byte)Math.Clamp((kv.Value - baseLevel - 0.8f) * 512f, 0f, 255f);
                    // BLUE = unwalkable statel: the walkable floor itself is blocked (a column)
                    var blockedWalk = grid.ProbeBlockedWalk(kv.Key);
                    Px(rp, grid.ProbeX0 * grid.Cell + (i + 0.5f) * grid.Cell,
                       grid.ProbeZ0 * grid.Cell + (j + 0.5f) * grid.Cell,
                       blockedWalk ? (byte)0 : (byte)Math.Clamp(c * 64f, 0f, 255f),
                       0, blockedWalk ? (byte)255 : (byte)0);
                }

                PaintDoors(rp);
                PaintLabels(rp);
                PaintRoomRects(rp); // last: nothing overwrites the room outlines
                var roomPath = args[5].Replace(".png", $"-r{mr.Index:00}rot{mr.Rot}.png");
                rp.Save(roomPath);
                Console.WriteLine($"  room map: {roomPath} (base level {baseLevel:0.0}; green = floor over base, 0.5 m = full)");
            }

            // THE DOOR TABLE: per doorway, how the placed floor lies along the door's normal -
            // floor presence at the centre and at ±1..4 m on both sides, plus the room's blitted
            // AABB. The placement error in numbers: a correct door has floor on BOTH sides at the
            // centre; an offset shows as one side starting a step or more late.
            foreach (var mr in nav.Dungeon.Rooms)
            {
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                var cells = 0;
                for (var kk = 0; kk < grid.ProbeW * grid.ProbeH; kk++)
                {
                    if (grid.ProbeCost(kk) <= 0)
                    {
                        continue;
                    }

                    // attribution is approximate (spill); the room whose GeomPos is nearest wins
                    int ci = kk % grid.ProbeW, cj = kk / grid.ProbeW;
                    float wx = (ci + grid.ProbeX0 + 0.5f) * grid.Cell, wz = (cj + grid.ProbeZ0 + 0.5f) * grid.Cell;
                    if (Math.Abs(wx - mr.Pos[0]) < 12f && Math.Abs(wz - mr.Pos[2]) < 12f)
                    {
                        minX = Math.Min(minX, wx); maxX = Math.Max(maxX, wx);
                        minZ = Math.Min(minZ, wz); maxZ = Math.Max(maxZ, wz);
                        cells++;
                    }
                }

                Console.WriteLine($"room {mr.Index} '{mr.PoolName}' rot {mr.Rot}: blit AABB ({minX:0.0}..{maxX:0.0}, {minZ:0.0}..{maxZ:0.0}) {cells} cells");
                foreach (var dw in nav.MissionDoorways.Where(d => d.Room == mr.Index))
                {
                    var along = "";
                    for (var d = -4; d <= 4; d++)
                    {
                        var fx = (float)(dw.X + dw.Nx * d);
                        var fz = (float)(dw.Z + dw.Nz * d);
                        along += grid.ProbeCostAt(fx, fz) > 0 ? (Math.Abs(d) < 0.5f ? "C" : d < 0 ? "-" : "+") : ".";
                    }

                    Console.WriteLine($"  door ({dw.X:0.0},{dw.Z:0.0}) n({dw.Nx:0.#},{dw.Nz:0.#}) floor along normal [-4m..+4m]: {along}");

                    // the statel's 3D extent, as the grid measured it for the keep-open
                    var di = nav.MissionDoorways.IndexOf(dw);
                    if (di >= 0 && di < grid.ProbeDoorExtents.Count)
                    {
                        var (mnx, mxx, mnz, mxz, mny, mxy) = grid.ProbeDoorExtents[di];
                        if (!float.IsNaN(mnx))
                        {
                            Console.WriteLine($"    statel extent x[{mnx:0.00}..{mxx:0.00}] y[{mny:0.00}..{mxy:0.00}] z[{mnz:0.00}..{mxz:0.00}] " +
                                              $"({mxx - mnx:0.00} x {mxy - mny:0.00} x {mxz - mnz:0.00} m); " +
                                              $"door point at ({(dw.X - mnx) / (mxx - mnx) * 100:0}% x, {(dw.Z - mnz) / (mxz - mnz) * 100:0}% z of the XZ box)");
                        }
                        else
                        {
                            Console.WriteLine("    statel extent: no geometry within 1.6 m (constant box fallback)");
                        }
                    }
                }
            }

            return 0;
        }

        if (args[4] == "floors")
        {
            float F(string s) => float.Parse(s, inv);
            var cx = F(args[5]); var cz = F(args[6]);
            var span = args.Length > 7 ? F(args[7]) : 12f;
            float y = args.Length > 8 ? F(args[8]) : 10f;
            Console.WriteLine($"floors lattice at ({cx:0.0},{cz:0.0}) span {span:0.0}, '*' = level picked for y {y:0.0}:");
            for (float z = cz + span; z >= cz - span - 1e-3f; z -= 0.5f)
            {
                var row = "";
                for (float x = cx - span; x <= cx + span + 1e-3f; x += 0.5f)
                {
                    var fl = grid.FloorsAt(x, z);
                    if (fl.Length == 0) { row += "  ."; continue; }
                    var pick = grid.FloorIndexAt(x, z, y, 0.8f);
                    row += "|";
                    for (int f = 0; f < fl.Length; f++)
                        row += f == pick ? $"*{fl[f]:0.##}" : $"{fl[f]:0.##}";
                }
                Console.WriteLine($"z={z,7:0.0}  {row}");
            }
            Console.WriteLine($"           x {cx - span:0.0} -> {cx + span:0.0} (centre {cx})");
            return 0;
        }

        if (args[4] == "path")
        {
            float F(string s) => float.Parse(s, inv);
            var a = new Vector3(F(args[5]), F(args[7]), F(args[6]));
            var b = new Vector3(F(args[8]), F(args[10]), F(args[9]));
            // synthetic marks after the coords, each 'block|pricy <x> <z> <r>' - a disc of hard
            // (extra) or soft (pricey) cells, replaying what a yank band did before 2026-10-09
            // and does since: 'block' should cut the corridor, 'pricy' must only toll it
            HashSet<int> extra = null, pricey = null;
            for (var i = 11; i + 3 < args.Length; i += 4)
            {
                var mk = new Vector3(F(args[i + 1]), 0, F(args[i + 2]));
                var set = args[i] == "block" ? (extra ??= new HashSet<int>()) : (pricey ??= new HashSet<int>());
                grid.CellsAlong(mk, mk, F(args[i + 3]), set);
                Console.WriteLine($"  band {args[i]} at ({mk.X:0.0},{mk.Z:0.0}) r {F(args[i + 3]):0.0}");
            }

            var pts = grid.FindPath(a, b, extra, 8f, 3f, out var why, pricey);
            Console.WriteLine($"({a.X:0.0},{a.Y:0.0},{a.Z:0.0}) -> ({b.X:0.0},{b.Y:0.0},{b.Z:0.0}): " +
                (pts != null ? $"{pts.Count} pts" : $"NO PATH - {why}"));
            if (pts != null)
                foreach (var p in pts)
                    Console.WriteLine($"  ({p.X,7:0.0},{p.Y,6:0.0},{p.Z,7:0.0})");
            return 0;
        }

        if (args[4] == "cells")
        {
            // missionx <pluginDir> <poolPf> <layout.txt> cells <x0> <z0> <x1> <z1> [y] - DebugCell
            // every 0.5 m along the segment: what the grid sees where the route is cut
            float F(string s) => float.Parse(s, inv);
            float x0 = F(args[5]), z0 = F(args[6]), x1 = F(args[7]), z1 = F(args[8]);
            float y = args.Length > 9 ? F(args[9]) : 5f;
            var len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            var n = Math.Max(1, (int)(len / 0.5f));
            Console.WriteLine($"cells along ({x0:0.0},{z0:0.0})->({x1:0.0},{z1:0.0}), floor idx for y {y:0.0}:");
            for (var s = 0; s <= n; s++)
            {
                var t = (float)s / n;
                var x = x0 + (x1 - x0) * t;
                var z = z0 + (z1 - z0) * t;
                Console.WriteLine($"  ({x,6:0.0},{z,6:0.0}) idx {grid.FloorIndexAt(x, z, y, 0.8f),3}  {grid.DebugCell(x, z)}");
            }

            return 0;
        }

        if (args[4] == "cell")
        {
            float F(string s) => float.Parse(s, inv);
            Console.WriteLine(grid.DebugCell(F(args[5]), F(args[6])));
            return 0;
        }

        if (args[4] == "line")
        {
            float F(string s) => float.Parse(s, inv);
            var a = new Vector3(F(args[5]), F(args[7]), F(args[6]));
            var b = new Vector3(F(args[8]), F(args[10]), F(args[9]));
            Console.WriteLine(grid.GeometryLine(a, b, out var gwhy)
                ? $"({a.X:0.0},{a.Y:0.0},{a.Z:0.0}) -> ({b.X:0.0},{b.Y:0.0},{b.Z:0.0}): CLEAR"
                : $"({a.X:0.0},{a.Y:0.0},{a.Z:0.0}) -> ({b.X:0.0},{b.Y:0.0},{b.Z:0.0}): BLOCKED - {gwhy}");
            return 0;
        }

        if (args[4] == "edges")
        {
            float F(string s) => float.Parse(s, inv);
            var p = new Vector3(F(args[5]), F(args[7]), F(args[6]));
            var reach = args.Length > 8 ? F(args[8]) : 1.5f;
            Console.WriteLine($"edge fan from ({p.X:0.0},{p.Y:0.0},{p.Z:0.0}), reach {reach:0.0} m:");
            foreach (var (lbl, dx, dz) in new[] { ("E ", 1f, 0f), ("NE", .71f, -.71f), ("N ", 0f, -1f), ("NW", -.71f, -.71f), ("W ", -1f, 0f), ("SW", -.71f, .71f), ("S ", 0f, 1f), ("SE", .71f, .71f) })
            {
                var q = new Vector3(p.X + dx * reach, p.Y, p.Z + dz * reach);
                Console.WriteLine("  " + lbl + ": " + (grid.GeometryLine(p, q, out var w) ? "clear" : "BLOCKED - " + w));
            }
            return 0;
        }

        if (args[4] == "map")
        {
            // map <roomIndex> - the placed room's blit cells as ASCII, one char per cell:
            // T = tile levels only, M = mesh levels only, B = both, H = none (a hole - hug seed),
            // lower-case = the cell also carries hug cost. The dual-frame seam map.
            var idx = int.Parse(args[5], inv);
            var mr = nav.Dungeon.Rooms[idx];
            var cx0 = (int)((mr.Pos[0] - grid.ProbeX0 * grid.Cell) / grid.Cell);
            var cy0 = (int)((mr.Pos[2] - grid.ProbeZ0 * grid.Cell) / grid.Cell);
            Console.WriteLine($"cells of room {idx} '{mr.PoolName}' rot {mr.Rot}: window center cell ({cx0},{cy0}) = world ({mr.Pos[0]:0.0},{mr.Pos[2]:0.0}); cell (px,py) = cell ({cx0},{cy0}) + ((px-622)/2, (py-693)/2) from the label position");
            Console.WriteLine($"window world: x {(grid.ProbeX0 + (cx0 - 40)) * grid.Cell:0.0}..{(grid.ProbeX0 + (cx0 + 40)) * grid.Cell:0.0}, z {(grid.ProbeZ0 + (cy0 - 40)) * grid.Cell:0.0}..{(grid.ProbeZ0 + (cy0 + 40)) * grid.Cell:0.0}");
            var x0 = (int)((mr.Pos[0] - grid.ProbeX0 * grid.Cell) / grid.Cell);
            var y0 = (int)((mr.Pos[2] - grid.ProbeZ0 * grid.Cell) / grid.Cell);
            for (var j = -40; j <= 40; j++)
            {
                var row = "";
                for (var i = -40; i <= 40; i++)
                {
                    var k = (y0 + j) * grid.ProbeW + (x0 + i);
                    var inGrid = x0 + i >= 0 && x0 + i < grid.ProbeW && y0 + j >= 0 && y0 + j < grid.ProbeH;
                    if (!inGrid || !grid.ProbeCellKind.TryGetValue(k, out var kind))
                    {
                        row += " ";
                        continue;
                    }

                    var hugged = grid.ProbeCost(k) > 1.01f;
                    var ch = kind switch { 1 => 'T', 2 => 'M', 3 => 'B', _ => 'H' };
                    row += hugged ? char.ToLowerInvariant(ch) : ch;
                }

                Console.WriteLine(row);
            }

            return 0;
        }

        if (args[4] == "probecell")
        {
            // probecell <x> <z> - one cell's full probe state: cost, levels, walk-blocked, layers
            float F(string s) => float.Parse(s, inv);
            var wx = F(args[5]);
            var wz = F(args[6]);
            var k = (int)((wz - grid.ProbeZ0 * grid.Cell) / grid.Cell) * grid.ProbeW
                  + (int)((wx - grid.ProbeX0 * grid.Cell) / grid.Cell);
            var lv = grid.ProbeLevels(k);
            var lvText = lv.Length == 0 ? "none" : string.Join(", ", lv.Select(v => v.ToString("0.00")));
            grid.ProbeCellKind.TryGetValue(k, out var kind);
            Console.WriteLine($"cell ({wx:0.00},{wz:0.00}): cost {grid.ProbeCost(k):0.00}, " +
                              $"blockedWalk {grid.ProbeBlockedWalk(k)}, layers {kind}, " +
                              $"levels [{lvText}]");
            return 0;
        }

        Console.WriteLine("unknown missionx verb: " + args[4]);
        return 2;
    }
}

// The rotation label's 3x5 glyphs, rows top-down (one byte per font pixel).
internal static class RotGlyphs
{
    public static byte[] RotGlyph(int rot)
    {
        return rot switch
        {
            0 => new byte[] { 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1 },
            1 => new byte[] { 0, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 1 },
            2 => new byte[] { 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1 },
            3 => new byte[] { 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1 },
            _ => null,
        };
    }
}

// A minimal PNG writer for the cost map: 8-bit truecolour, no per-scanline filter, zlib via the
// tree's Ionic.Zlib and a table-less CRC32 - no imaging dependency for one diagnostic render.
internal sealed class CostPng
{
    private readonly int _w;
    private readonly int _h;
    private readonly byte[] _raw;

    public CostPng(int w, int h)
    {
        _w = w;
        _h = h;
        _raw = new byte[h * (1 + w * 3)];
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h)
        {
            return; // clip: the rot-glyph labels reach 9-15 px past a room centre, and the
                    // canvas edge sits as little as 4 px out - an unchecked write lands in
                    // another row's filter byte or past the buffer
        }

        var o = y * (1 + _w * 3) + 1 + x * 3;
        _raw[o] = r;
        _raw[o + 1] = g;
        _raw[o + 2] = b;
    }

    /// <summary>A sub-rectangle of the image as a new PNG buffer (clamped reads skip out-of-range rows).</summary>
    public CostPng Crop(int x0, int y0, int w, int h)
    {
        var c = new CostPng(w, h);
        for (var y = 0; y < h; y++)
        {
            var sy = y0 + y;
            if (sy < 0 || sy >= _h)
            {
                continue;
            }

            for (var x = 0; x < w; x++)
            {
                var sx = x0 + x;
                if (sx < 0 || sx >= _w)
                {
                    continue;
                }

                var so = sy * (1 + _w * 3) + 1 + sx * 3;
                var to = y * (1 + w * 3) + 1 + x * 3;
                c._raw[to] = _raw[so];
                c._raw[to + 1] = _raw[so + 1];
                c._raw[to + 2] = _raw[so + 2];
            }
        }

        return c;
    }

    public void Save(string path)
    {
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        Be(ihdr, 0, _w);
        Be(ihdr, 4, _h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: truecolour RGB
        Chunk(f, "IHDR", ihdr);

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new Ionic.Zlib.ZlibStream(ms, Ionic.Zlib.CompressionMode.Compress, true))
            {
                z.Write(_raw, 0, _raw.Length);
            }

            idat = ms.ToArray();
        }

        Chunk(f, "IDAT", idat);
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream f, string type, byte[] data)
    {
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, body, 0);
        data.CopyTo(body, 4);
        var len = new byte[4];
        Be(len, 0, data.Length);
        f.Write(len);
        f.Write(body);
        var crc = new byte[4];
        Be(crc, 0, (int)Crc32(body));
        f.Write(crc);
    }

    private static void Be(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var t in data)
        {
            crc ^= t;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
