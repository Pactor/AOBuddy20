// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: OverlandGrid.cs
//
// Last modified: 2026-10-03
// Created:       2026-10-01 (ported from AOBuddy10 OverlandGrid.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Components;
using AOBuddy20.Enums;
using AOSharp.Common.GameData;

namespace AOBuddy20.Nav;

/// <summary>What overland routing needs from a playfield's walkable cells (OverlandGrid outdoors, FloorGrid indoors).</summary>
public interface IWalkGrid
{
    int Pf { get; }

    HashSet<int> CellsAlong(Vector3 a, Vector3 b, float radius, HashSet<int> into = null);

    /// <summary>extra blocks hard (zone lines, contact exits, real obstacles); pricey only costs
    /// RefusedCellWeight per cell - ground the server once refused is steered round, never sealed.</summary>
    List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why,
        HashSet<int> pricey = null);

    /// <summary>Standable ground at p — the front-ray test that finds a doorway exit's open side.</summary>
    bool OpenAt(Vector3 p);

    /// <summary>
    ///     The STUCK ESCAPE: the grid's verdict set aside, the geometry answers — the clear straight
    ///     line from `from` that best improves the distance to `goal` (null with why when every
    ///     direction is walled in the real geometry too). Implemented per grid; see OverlandGrid.
    /// </summary>
    Vector3? Escape(Vector3 from, Vector3 goal, out string why);
}

/// <summary>
///     Walkable cells of an OUTDOOR playfield, for overland routing: the ground heightfield (ground.bin) with
///     the zone's wall triangles (walls.bin, tools/AONavExtractor) stamped in. A cell is blocked when a wall
///     triangle passes through it at body height (0.3-1.9 m over the ground there), or it is off the map.
///     STRUCTURE-TOP SUPPRESSION (owner, 2026-10-01): the heightfield carries the TOP surface - its value
///     over a building or city wall is the roof/wall walk, and interpolation paints vertical faces as gentle
///     grades. Where collision.bin knows a floor well below the heightfield value, the heightfield level is
///     suppressed as a routing floor of that cell and the collision floors (the street) are what remains -
///     ground routing stays on the ground; elevated walkways connect only where real triangles carry them.
///     SLOPES ARE DIRECTIONAL (owner, 2026-09-25): a slope too steep to climb is still walkable going down,
///     and there is NO fall damage (owner, 2026-10-04, anywhere in AO) — a drop is a route like any
///     other, taken wherever it is shorter; LearnedGround's recorded jumps only still price the
///     climb back UP through them as a last resort.
///     THE LEARNED LAYERS (AOBuddy10, ported 2026-10-03): the owner's recorded roads (nav/&lt;pf&gt;.json)
///     cost RoadFactor of normal and the server's remembered pull-back spots (snapbacks.json) add up to
///     SnapWeight x hits, so the route is planned around yesterday's refusals instead of collecting them
///     again; hostile-mob spots (MobDanger) cost by aggro circle - never a block, the owner's roads exempt.
///     Both are RUNTIME layers, rebuilt lazily when their Version changes - never part of the GridCache
///     file - and only the walk thread's searches rebuild or read them, so they need no lock of their own.
///     Nothing here moves the body. The stamped geometry is IMMUTABLE after Build/Read: any thread may query.
/// </summary>
public sealed class OverlandGrid : IWalkGrid
{
    private const float Merge = 0.6f; // surfaces this close are one floor (a slab's top and underside)
    private const int MaxExtra = 15; // + terrain = 16 floors per cell (4-bit floor index). The ICC wompa
                                     // tower stacks a deck every ~13 m to 152 m: 7 slots kept only the TOP decks and
                                     // threw away the door-sill and mezzanine floors near the ground (2026-09-25).
    private const int FloorShift = 4, FloorMask = 15;
    private const float BodyLow = 0.3f, BodyHigh = 1.9f;
    private const float MaxRise = 4.0f; // metres of rise per metre of run: ~76 degrees — the climb limit, derived from
                                        // MissionStep (a 0.2 m cell at 0.8 m rise; owner, 2026-10-10: the arch bridges'
                                        // decks walked live at 60-63 deg = rise 2.0, comfortably inside). Downhill is
                                        // unlimited: no fall damage outdoors

    // STRUCTURE-TOP SUPPRESSION (Newland City, 2026-10-01: the bot routed along the city wall tops and
    // out of bounds, and the wall faces clipped as phantom ramps). The outdoor heightfield carries the
    // TOP surface - wall walks, roofs, plateaus - and bilinear interpolation turns vertical faces into
    // gentle grades the climb limit accepts. Where collision.bin knows a floor WELL below the heightfield
    // value, that heightfield level is a structure top, not a street: it stops being a routing floor of
    // the cell, and the collision floors (the street) are what remains.
    private const float TerrainStructureDrop = 2.5f;
    private const int MaxCells = 6_000_000; // cell size grows on the biggest maps to stay under this. 6 M (2026-09-25,
                                            // ICC): Andromeda's wompa tower needs 2 m cells — at 4 m the doorway is one
                                            // cell and its door-sill wall samples seal the tower shut.
    private const int MaxExpand = 1_500_000;

    // WALL CLEARANCE (owner, 2026-09-26: "the fastest route is hugging the walls, the safest route is to run the
    // middle, as most times that is where the road is"). _clear = cells to the nearest blocked cell, capped;
    // a step near a wall costs more (fading to nothing at ClearMetres), and the string-pull may not bring the
    // route closer to a wall than the search had it.
    private const float ClearMetres = 5f, ClearWeight = 2f, ClearKeep = 2f;

    // WATER (Algorithman, 2026-09-26): swimming is no slower, but the server corrects the bot far more there.
    // Water deeper than WadeDepth costs WaterWeight extra per step, so he swims only when it saves a lot.
    private const float WaterWeight = 2f, WadeDepth = 1.0f;

    // LEARNED (LearnedGround, ported 2026-10-03): the owner's recorded roads cost RoadFactor of normal,
    // the server's remembered snap-back spots add up to SnapWeight x hits within SnapRadius, and a slope
    // costs SlopeWeight per unit of grade over SlopeFree (downhill half) - the hill loses to the longer
    // road. A recorded stretch dropping faster than DropGrade is a jump off something (the owner jumps
    // off ledges by habit): it is not road, and climbing through it costs DropClimbCost extra.
    private const float RoadFactor = 0.5f, SnapWeight = 2f, SnapRadius = 6f;
    private const float DropGrade = MaxRise, DropClimbCost = 10f; // = MaxRise: only what can't be walked up is a jump

    // Per cell of ground the server once REFUSED us (PlanRoute's pricey set, the yank marks): a
    // detour of a cell length per unit beats it, but a corridor with no way round is paid and
    // crossed - the marks must never seal the only way back (owner, 2026-10-09, Grey Caves-Mines:
    // yank bands across the narrow tunnels left the exit door unplannable and the run looped).
    private const float RefusedCellWeight = 8f;

    // HOSTILE MOBS (MobDanger, ported 2026-10-03): crossing a spot's aggro circle through its middle
    // costs DetourPerMob x W metres per counted mob, fading to nothing at R - never a block. A cell's
    // cost is capped at DangerCap so a nest of spots stays a cost the search can still weigh.
    private const float DangerCap = 100f;

    private readonly bool[] _blocked;
    private readonly float[] _ch;
    private readonly NavGround _ground;
    private readonly HashSet<long> _blockedFl = new HashSet<long>(); // (cell << FloorShift) | floor
    private HashSet<int> _terrainTop; // cells whose heightfield level is a structure top: no terrain floor
    private readonly int _w, _h;

    // The learned cost layers (runtime only - see the class note): rebuilt when LearnedGround.Version
    // or MobDanger's keys change, by the walk thread's own searches. Never written to the GridCache.
    private bool[] _road, _drop, _ownerRoad;
    private float[] _learn;
    private bool _anyRoad;
    private int _learnVer = -1;
    private float[] _danger;
    private int _dangerKey = int.MinValue, _dangerLearnVer = -1, _dangerLiveVer = -1, _dangerSpots, _dangerLive;
    private readonly List<(int cell, float add)> _liveStamp = new();

    // Built by StampClearance at the end of Build/Read (construction only - the grid is immutable
    // once published).
    private byte[] _clear;
    private bool[] _water;

    // MULTI-LEVEL (owner, 2026-09-25): collision.bin's walkable surfaces — ramps, wall-tops, platforms,
    // the Stret West Bank wall you can walk up AND under — as extra floors per cell on top of the terrain.
    // Sparse: a cell with no structure on it has only the terrain floor and costs nothing extra.
    private Dictionary<int, float[]> _extra;

    // The zone's walls.bin, retained for GeometryLine's exact line verdicts (Build keeps the instance
    // it stamped from; a cache-loaded grid lazy-loads it from the folder on first need).
    private NavCollision _walls;
    private string _pluginDir; // set by Build only: where walls.bin lives, for GeometryLine's lazy load

    private OverlandGrid(int pf, float cell, int w, int h, NavGround g)
    {
        Pf = pf;
        Cell = cell;
        _w = w;
        _h = h;
        _ground = g;
        _blocked = new bool[w * h];
        _ch = new float[w * h]; // centre heights, for the directional edge checks
    }

    public int Pf { get; }
    public float Cell { get; }
    public int BlockedCells { get; private set; }
    public bool HasWalls { get; private set; }

    /// <summary>The grid for an outdoor playfield; null when it has no ground data (dungeons, instances).</summary>
    public static OverlandGrid Build(string pluginDir, int pf, AOBuddyNav nav, Action<string> log)
    {
        var g = nav?.Ground;
        if (g == null)
        {
            return null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float sizeX = (g.SamplesX - 1) * g.Cell, sizeZ = (g.SamplesZ - 1) * g.Cell;
        float cell = 1f;
        while ((sizeX / cell) * (sizeZ / cell) > MaxCells)
        {
            cell *= 2;
        }

        var grid = new OverlandGrid(pf, cell, (int)Math.Ceiling(sizeX / cell), (int)Math.Ceiling(sizeZ / cell), g);
        grid._pluginDir = pluginDir;
        grid.StampGround();
        grid.FillCentreHeights();
        grid.StampFloors(nav?.Collision); // structures first: the walls below block per floor
        string wp = Path.Combine(AOBuddyNav.FolderFor(pluginDir, pf), "walls.bin");
        if (File.Exists(wp))
        {
            grid._walls = NavCollision.Read(wp); // retained: GeometryLine judges straight lines against it
            grid.StampWalls(grid._walls);
            grid.HasWalls = true;
        }

        grid.StampHeadroom();
        grid.StampClearance();
        // The doorways stay passages: walls.bin carries the whole door/shop-front assembly as
        // solid, and nothing in the data says where the openings are - but Zoning does (the
        // contact exits: proxy doors, pads, teleporters). Keep a disc of cells open at each one,
        // LAST so no earlier stamping re-seals it - a proxy landing is ground the server PLACES
        // the bot on, and a sealed pocket there killed the trip home where it stood (Newland
        // Desert's Fair Trade front, 2026-10-02: zoned out onto a wall-stamped plateau at
        // y 21.2 - "every floor under me is blocked").
        grid.KeepExitsOpen(pf);
        for (var i = 0; i < grid._blocked.Length; i++)
        {
            if (grid._blocked[i])
            {
                grid.BlockedCells++;
            }
        }

        log?.Invoke($"OVERLAND: grid for pf {pf}: {grid._w}x{grid._h} cells of {cell:0} m, {grid.BlockedCells} blocked, {grid._extra?.Count ?? 0} multi-floor, walls {(grid.HasWalls ? "yes" : "NONE (walls.bin missing: routing sees only cliffs)")}, {sw.ElapsedMilliseconds} ms");
        return grid;
    }

    // Steepness does NOT block a cell (owner, 2026-09-25: downhill is always walkable outdoors, no fall
    // damage); only off-map ground does. The climb limit lives on the edges (the floor loop in Search).
    private void StampGround()
    {
        for (int z = 0; z < _h; z++)
        {
            for (int x = 0; x < _w; x++)
            {
                double x0 = x * Cell, z0 = z * Cell, x1 = x0 + Cell, z1 = z0 + Cell;
                double a = _ground.HeightAt(x0, z0), b = _ground.HeightAt(x1, z0), c = _ground.HeightAt(x0, z1), d = _ground.HeightAt(x1, z1);
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c) || double.IsNaN(d))
                {
                    _blocked[z * _w + x] = true;
                }
            }
        }
    }

    private void FillCentreHeights()
    {
        for (int z = 0; z < _h; z++)
        {
            for (int x = 0; x < _w; x++)
            {
                double h = _ground.HeightAt((x + 0.5) * Cell, (z + 0.5) * Cell);
                int k = z * _w + x;
                if (double.IsNaN(h))
                {
                    _blocked[k] = true;
                    _ch[k] = 0f;
                }
                else
                {
                    _ch[k] = (float)h;
                }
            }
        }
    }

    // ---- GridCache persistence (deterministic from the zone's files; see GridCache) ----------------------

    internal void Write(BinaryWriter bw)
    {
        bw.Write(Cell);
        bw.Write(_w);
        bw.Write(_h);
        bw.Write(HasWalls);
        // blocked as runs: terrain grids are mostly long stretches of open cells
        var i = 0;
        while (i < _blocked.Length)
        {
            bool v = _blocked[i];
            int run = 1;
            while (i + run < _blocked.Length && _blocked[i + run] == v)
            {
                run++;
            }

            bw.Write(v);
            bw.Write(run);
            i += run;
        }

        foreach (float h in _ch)
        {
            bw.Write(h);
        }

        // structure floors (sparse) and the per-floor blocks
        int ne = _extra?.Count ?? 0;
        bw.Write(ne);
        if (ne > 0)
        {
            foreach (var kv in _extra)
            {
                bw.Write(kv.Key);
                bw.Write((byte)kv.Value.Length);
                foreach (float f in kv.Value)
                {
                    bw.Write(f);
                }
            }
        }

        bw.Write(_blockedFl.Count);
        foreach (long b in _blockedFl)
        {
            bw.Write(b);
        }

        bw.Write(_terrainTop?.Count ?? 0);
        if (_terrainTop != null)
        {
            foreach (int c in _terrainTop)
            {
                bw.Write(c);
            }
        }
    }

    internal static OverlandGrid Read(BinaryReader br, NavGround g, int pf, string pluginDir)
    {
        float cell = br.ReadSingle();
        int w = br.ReadInt32(), h = br.ReadInt32();
        var grid = new OverlandGrid(pf, cell, w, h, g);
        grid._pluginDir = pluginDir; // GeometryLine/Escape lazy-load walls.bin with it - without the dir
                                     // a cache-loaded grid refused every geometry line ("no walls.bin")
                                     // and the blocked-verdict fallbacks were dead on every cache hit
        grid.HasWalls = br.ReadBoolean();
        int i = 0;
        while (i < grid._blocked.Length)
        {
            bool v = br.ReadBoolean();
            int run = br.ReadInt32();
            for (int k = 0; k < run && i < grid._blocked.Length; k++, i++)
            {
                grid._blocked[i] = v;
            }

            if (v)
            {
                grid.BlockedCells += run;
            }
        }

        for (int k = 0; k < grid._ch.Length; k++)
        {
            grid._ch[k] = br.ReadSingle();
        }

        int ne = br.ReadInt32();
        if (ne > 0)
        {
            foreach (var _ in Enumerable.Range(0, ne))
            {
                int key = br.ReadInt32();
                int c = br.ReadByte();
                var fl = new float[c];
                for (int f = 0; f < c; f++)
                {
                    fl[f] = br.ReadSingle();
                }

                grid._extra ??= new Dictionary<int, float[]>(ne);
                grid._extra[key] = fl;
            }
        }

        int nb = br.ReadInt32();
        for (var b = 0; b < nb; b++)
        {
            grid._blockedFl.Add(br.ReadInt64());
        }

        int nt = br.ReadInt32();
        if (nt > 0)
        {
            grid._terrainTop = new HashSet<int>(nt);
            for (var t = 0; t < nt; t++)
            {
                grid._terrainTop.Add(br.ReadInt32());
            }
        }

        grid.StampClearance();
        return grid;
    }

    // The zone's STRUCTURE floors from collision.bin (the near-horizontal surfaces; walls.bin holds the
    // steep ones): each triangle stamps the height under the cells its footprint covers — FloorGrid's
    // sampling, on the terrain grid. Floors within Merge of the terrain (or of each other) fold together,
    // so a prop hugging the ground adds nothing; what is left is genuinely another level to walk on.
    private void StampFloors(NavCollision col)
    {
        if (col == null)
        {
            return;
        }

        var raw = new Dictionary<int, List<float>>();
        foreach (var ch in col.Chunks)
        {
            float[] v = ch.Verts;
            for (int o = 0; o + 8 < v.Length; o += 9)
            {
                float ax = v[o], ay = v[o + 1], az = v[o + 2], bx = v[o + 3], by = v[o + 4], bz = v[o + 5], cx2 = v[o + 6], cy2 = v[o + 7], cz2 = v[o + 8];
                float d = (bx - ax) * (cz2 - az) - (cx2 - ax) * (bz - az);
                if (Math.Abs(d) < 1e-6f)
                {
                    continue;
                }

                int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx2)) / Cell)), i1 = Math.Min(_w - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx2)) / Cell));
                int j0 = Math.Max(0, (int)Math.Floor(Math.Min(az, Math.Min(bz, cz2)) / Cell)), j1 = Math.Min(_h - 1, (int)Math.Floor(Math.Max(az, Math.Max(bz, cz2)) / Cell));
                for (int j = j0; j <= j1; j++)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        float px = (i + 0.5f) * Cell, pz = (j + 0.5f) * Cell;
                        float u = ((px - ax) * (cz2 - az) - (cx2 - ax) * (pz - az)) / d;
                        float t = ((bx - ax) * (pz - az) - (px - ax) * (bz - az)) / d;
                        const float slack = -0.05f;
                        if (u < slack || t < slack || u + t > 1 - slack)
                        {
                            continue;
                        }

                        int k = j * _w + i;
                        if (_blocked[k])
                        {
                            continue; // off-map cell: no floor there either
                        }

                        if (!raw.TryGetValue(k, out var l))
                        {
                            raw[k] = l = new List<float>();
                        }

                        l.Add(ay + u * (by - ay) + t * (cy2 - ay));
                    }
                }
            }
        }

        foreach (var kv in raw)
        {
            kv.Value.Sort();
            var merged = new List<float>();
            foreach (float y in kv.Value)
            {
                if (merged.Count > 0 && y - merged[merged.Count - 1] <= Merge)
                {
                    merged[merged.Count - 1] = y; // keep the top of a slab
                }
                else
                {
                    merged.Add(y);
                }
            }

            merged.RemoveAll(y => Math.Abs(y - _ch[kv.Key]) <= Merge); // the terrain already is this floor
            if (merged.Count > MaxExtra)
            {
                merged.RemoveRange(MaxExtra, merged.Count - MaxExtra); // over cap: keep the floors nearest the ground
            }

            if (merged.Count > 0)
            {
                _extra ??= new Dictionary<int, float[]>();
                _extra[kv.Key] = merged.ToArray();
                if (merged[0] < _ch[kv.Key] - TerrainStructureDrop)
                {
                    // The heightfield here is a wall walk / roof / plateau well above the known street:
                    // not a routing floor. The street itself (merged[0]) is what the cell offers.
                    (_terrainTop ??= new HashSet<int>()).Add(kv.Key);
                }
            }
        }
    }

    private void StampWalls(NavCollision walls)
    {
        float step = Math.Min(0.5f, Cell / 2);
        foreach (var ch in walls.Chunks)
        {
            float[] v = ch.Verts;
            for (int o = 0; o + 8 < v.Length; o += 9)
            {
                // Sample the triangle's surface; a sample at body height over a FLOOR blocks that floor —
                // so a doorway's lintel blocks nothing under it while the wall's face blocks the floor it
                // stands on, at every level (ground beside it, walkway on top of it).
                float ax = v[o], ay = v[o + 1], az = v[o + 2];
                float ux = v[o + 3] - ax, uy = v[o + 4] - ay, uz = v[o + 5] - az;
                float wx = v[o + 6] - ax, wy = v[o + 7] - ay, wz = v[o + 8] - az;
                float longest = Math.Max(Len(ux, uy, uz), Math.Max(Len(wx, wy, wz), Len(wx - ux, wy - uy, wz - uz)));
                int n = Math.Min(400, Math.Max(1, (int)Math.Ceiling(longest / step)));
                for (int i = 0; i <= n; i++)
                {
                    for (int j = 0; j <= n - i; j++)
                    {
                        float s = i / (float)n, t = j / (float)n;
                        float px = ax + ux * s + wx * t, py = ay + uy * s + wy * t, pz = az + uz * s + wz * t;
                        int cx = (int)Math.Floor(px / Cell), cz = (int)Math.Floor(pz / Cell);
                        if (cx < 0 || cz < 0 || cx >= _w || cz >= _h)
                        {
                            continue;
                        }

                        int k = cz * _w + cx;
                        for (int f = 0; f < FloorCount(k); f++)
                        {
                            float above = py - FloorH(k, f);
                            if (above >= BodyLow && above <= BodyHigh)
                            {
                                _blockedFl.Add(((long)k << FloorShift) | (long)f);
                            }
                        }
                    }
                }
            }
        }
    }

    // Two-pass chamfer distance (3 straight, 4 diagonal) from every blocked cell, in cells, capped.
    private void StampClearance()
    {
        int cap = Math.Min(250, (int)Math.Ceiling(ClearMetres / Cell) + 1);
        var d = new int[_w * _h];
        int inf = cap * 3;
        for (var i = 0; i < d.Length; i++)
        {
            d[i] = _blocked[i] ? 0 : inf;
        }

        for (int z = 0; z < _h; z++)
        {
            for (int x = 0; x < _w; x++)
            {
                int i = z * _w + x, v = d[i];
                if (v == 0)
                {
                    continue;
                }

                if (x > 0)
                {
                    v = Math.Min(v, d[i - 1] + 3);
                }

                if (z > 0)
                {
                    v = Math.Min(v, d[i - _w] + 3);
                    if (x > 0)
                    {
                        v = Math.Min(v, d[i - _w - 1] + 4);
                    }

                    if (x < _w - 1)
                    {
                        v = Math.Min(v, d[i - _w + 1] + 4);
                    }
                }

                d[i] = v;
            }
        }

        for (int z = _h - 1; z >= 0; z--)
        {
            for (int x = _w - 1; x >= 0; x--)
            {
                int i = z * _w + x, v = d[i];
                if (v == 0)
                {
                    continue;
                }

                if (x < _w - 1)
                {
                    v = Math.Min(v, d[i + 1] + 3);
                }

                if (z < _h - 1)
                {
                    v = Math.Min(v, d[i + _w] + 3);
                    if (x < _w - 1)
                    {
                        v = Math.Min(v, d[i + _w + 1] + 4);
                    }

                    if (x > 0)
                    {
                        v = Math.Min(v, d[i + _w - 1] + 4);
                    }
                }

                d[i] = v;
            }
        }

        _clear = new byte[d.Length];
        for (var i = 0; i < d.Length; i++)
        {
            _clear[i] = (byte)Math.Min(cap, d[i] / 3);
        }

        if (_ground != null)
        {
            _water = new bool[_w * _h];
            for (int z = 0; z < _h; z++)
            {
                for (int x = 0; x < _w; x++)
                {
                    if (!_blocked[z * _w + x])
                    {
                        _water[z * _w + x] = !double.IsNaN(_ground.SwimY((x + 0.5) * Cell, (z + 0.5) * Cell, WadeDepth));
                    }
                }
            }

            // AOBuddy10 also published the wet map to Zoning (zone lines are crossed on dry land); Zoning
            // is not ported yet, so the wet map stays local (owner, 2026-10-01).
        }
    }

    /// <summary>Swimming water at (x, z) (the client's liquid polygons, deeper than wading).</summary>
    public bool Wet(float x, float z)
    {
        int cx = CellX(x), cz = CellZ(z);
        return _water != null && cx >= 0 && cz >= 0 && cx < _w && cz < _h && _water[cz * _w + cx];
    }

    // ---- the learned cost layers (walk thread only; see the class note) -------------------

    // Stamp the ROADS, DROPS and SNAP-BACK costs from LearnedGround whenever its Version moved. The
    // owner's recorded roads (nav/<pf>.json) and the bot's own clean walks (walked.json) mark their
    // cells: road for the planner, the owner's also trusted for a recorded drop. The remembered
    // pull-back spots add cost round themselves - the hill the server refused twice is not "walkable",
    // it is "expensive", and the longer way round wins (AOBuddy10 EnsureLearned).
    private void EnsureLearned()
    {
        LearnedGround.RefreshRoads();
        if (_learnVer == LearnedGround.Version)
        {
            return;
        }

        _learnVer = LearnedGround.Version;
        _road = new bool[_w * _h];
        _drop = new bool[_w * _h];
        _learn = new float[_w * _h];
        _anyRoad = false;
        bool OnFloor(Vector3 p)
        {
            var cx = CellX(p.X);
            var cz = CellZ(p.Z);
            if (!In(cx, cz))
            {
                return false;
            }

            var c = cz * _w + cx;
            for (var f = 0; f < FloorCount(c); f++)
            {
                if (Math.Abs(FloorH(c, f) - p.Y) < 3f)
                {
                    return true;
                }
            }

            return false;
        }

        var cells = new HashSet<int>();
        var drops = new HashSet<int>();
        var owner = new HashSet<int>();
        var ownerRoads = LearnedGround.RoadsIn(Pf);
        var ownerSet = new HashSet<List<Vector3>>(ownerRoads);
        foreach (var road in ownerRoads.Concat(LearnedGround.WalksIn(Pf)))
        {
            for (var i = 0; i + 1 < road.Count; i++)
            {
                var a = road[i];
                var b = road[i + 1];
                if (Vector3.Distance(a, b) > 40f || !OnFloor(a) || !OnFloor(b))
                {
                    continue; // a zone jump, or another zone's walk
                }

                var flat = Movement.Flat(a, b);
                // A recorded stretch dropping faster than DropGrade is a jump off something (the owner
                // jumps off ledges by habit): it is not road - the drop itself is free everywhere now
                // (no fall damage, owner 2026-10-04) - but the cells are marked so the climb back UP
                // through them is priced as a last resort (DropClimbCost).
                if (flat > 0.1f && Math.Abs(b.Y - a.Y) / flat > DropGrade)
                {
                    if (ownerSet.Contains(road))
                    {
                        CellsAlong(a, b, 2.5f, drops);
                    }

                    continue;
                }

                CellsAlong(a, b, 1.5f, cells);
                if (ownerSet.Contains(road))
                {
                    CellsAlong(a, b, 1.5f, owner);
                }
            }
        }

        foreach (var c in drops)
        {
            cells.Remove(c);
        }

        foreach (var c in cells)
        {
            _road[c] = true;
            _anyRoad = true;
        }

        _ownerRoad = new bool[_w * _h];
        foreach (var c in owner)
        {
            _ownerRoad[c] = true;
        }

        foreach (var c in drops)
        {
            _drop[c] = true;
        }

        var R = (int)Math.Ceiling(SnapRadius / Cell);
        foreach (var sp in LearnedGround.SnapsIn(Pf))
        {
            var sx = CellX(sp.X);
            var sz = CellZ(sp.Z);
            for (var dz = -R; dz <= R; dz++)
            {
                for (var dx = -R; dx <= R; dx++)
                {
                    if (!In(sx + dx, sz + dz))
                    {
                        continue;
                    }

                    var dist = (float)Math.Sqrt(dx * dx + dz * dz) * Cell;
                    if (dist > SnapRadius)
                    {
                        continue;
                    }

                    _learn[(sz + dz) * _w + sx + dx] += SnapWeight * Math.Min(sp.N, 6) * (1f - dist / SnapRadius);
                }
            }
        }
    }

    /// <summary>The hostile-mob cost layer, current for the atlas, his level, the owner's roads and the live mobs
    /// (AOBuddy10 EnsureDanger, ported 2026-10-03).</summary>
    private void EnsureDanger()
    {
        var key = MobDanger.Key(Pf);
        if (key != _dangerKey || _learnVer != _dangerLearnVer)
        {
            _dangerKey = key;
            _dangerLearnVer = _learnVer;
            _liveStamp.Clear();
            _dangerLiveVer = -1;
            var spots = MobDanger.AtlasSpots(Pf);
            _dangerSpots = spots.Count;
            if (spots.Count == 0)
            {
                _danger = null;
            }
            else
            {
                if (_danger == null)
                {
                    _danger = new float[_w * _h];
                }
                else
                {
                    Array.Clear(_danger, 0, _danger.Length);
                }

                foreach (var s in spots)
                {
                    StampSpot(s, null);
                }
            }
        }

        if (MobDanger.LiveVersion != _dangerLiveVer)
        {
            _dangerLiveVer = MobDanger.LiveVersion;
            if (_danger != null)
            {
                foreach (var (c, add) in _liveStamp)
                {
                    _danger[c] = Math.Max(0f, _danger[c] - add);
                }
            }

            _liveStamp.Clear();
            var live = MobDanger.LiveSpots(Pf);
            _dangerLive = live.Count;
            if (live.Count > 0 && _danger == null)
            {
                _danger = new float[_w * _h];
            }

            foreach (var s in live)
            {
                StampSpot(s, _liveStamp);
            }
        }
    }

    // Crossing the spot's aggro circle through its middle costs DetourPerMob x W metres; its cost fades
    // to nothing at R. The owner's recorded roads get none: they keep their priority.
    private void StampSpot(MobDanger.Spot s, List<(int, float)> undo)
    {
        if (s.R <= 0 || s.W <= 0)
        {
            return;
        }

        var k = MobDanger.DetourPerMob / s.R * s.W;
        var sx = CellX(s.X);
        var sz = CellZ(s.Z);
        var R = (int)Math.Ceiling(s.R / Cell) + 1;
        for (var dz = -R; dz <= R; dz++)
        {
            for (var dx = -R; dx <= R; dx++)
            {
                var x = sx + dx;
                var z = sz + dz;
                if (!In(x, z))
                {
                    continue;
                }

                float ex = (x + 0.5f) * Cell - s.X, ez = (z + 0.5f) * Cell - s.Z;
                var d = (float)Math.Sqrt(ex * ex + ez * ez);
                if (d >= s.R)
                {
                    continue;
                }

                var c = z * _w + x;
                if (_blocked[c] || (_ownerRoad != null && _ownerRoad[c]))
                {
                    continue;
                }

                var add = k * (1f - d / s.R);
                _danger[c] += add;
                undo?.Add((c, add));
            }
        }
    }

    private float DangerAt(int cell) => _danger == null ? 0f : Math.Min(DangerCap, _danger[cell]);

    // A floor with another one just above it (under a deck, inside a slab — FloorGrid's rule) is no
    // place to stand. Considers the terrain floor as one of the stack - except on structure-top cells,
    // where the heightfield level is not a floor at all.
    private void StampHeadroom()
    {
        if (_extra == null)
        {
            return;
        }

        foreach (var kv in _extra)
        {
            float[] all;
            if (TerrainFloor(kv.Key))
            {
                all = new float[1 + kv.Value.Length];
                all[0] = _ch[kv.Key];
                Array.Copy(kv.Value, 0, all, 1, kv.Value.Length);
                Array.Sort(all);
            }
            else
            {
                all = (float[])kv.Value.Clone(); // already sorted ascending (StampFloors merged)
            }

            for (int f = 0; f + 1 < all.Length; f++)
            {
                if (all[f + 1] - all[f] < BodyHigh)
                {
                    var fi = FloorIndex(kv.Key, all[f]);
                    if (fi >= 0)
                    {
                        _blockedFl.Add(((long)kv.Key << FloorShift) | (long)fi);
                    }
                }
            }
        }
    }

    // ---- floors (terrain = 0, structures above/below = 1..; terrain dropped on structure-top cells) ----

    private bool TerrainFloor(int cell) => _terrainTop == null || !_terrainTop.Contains(cell);

    private int FloorCount(int cell)
    {
        if (_extra == null || !_extra.TryGetValue(cell, out var f))
        {
            return 1; // terrain only
        }

        return TerrainFloor(cell) ? 1 + f.Length : f.Length;
    }

    private float FloorH(int cell, int i)
    {
        var f = _extra != null && _extra.TryGetValue(cell, out var fl) ? fl : null;
        if (f == null || TerrainFloor(cell))
        {
            return i == 0 ? _ch[cell] : f![i - 1];
        }

        return f[i]; // structure-top cell: the heightfield level is not a floor here
    }

    private int FloorIndex(int cell, float h)
    {
        var f = _extra != null && _extra.TryGetValue(cell, out var fl) ? fl : null;
        if (f == null || TerrainFloor(cell))
        {
            if (Math.Abs(h - _ch[cell]) < 0.01f)
            {
                return 0;
            }

            for (int i = 0; i < f!.Length; i++)
            {
                if (Math.Abs(h - f[i]) < 0.01f)
                {
                    return i + 1;
                }
            }

            return 0;
        }

        for (int i = 0; i < f.Length; i++)
        {
            if (Math.Abs(h - f[i]) < 0.01f)
            {
                return i;
            }
        }

        return -1;
    }

    private bool FloorOpen(int cell, int i) => !_blockedFl.Contains(((long)cell << FloorShift) | (long)i);

    /// <summary>Diagnostic: every floor of the cell at (x, z) with its open/blocked state.</summary>
    public string CellInfo(float x, float z)
    {
        int cx = CellX(x), cz = CellZ(z);
        if (!In(cx, cz))
        {
            return $"({x:0},{z:0}): off-grid";
        }

        int cell = cz * _w + cx;
        var sb = new System.Text.StringBuilder($"cell ({cx},{cz}) [{cx * Cell:0}-{cx * Cell + Cell:0} x {cz * Cell:0}-{cz * Cell + Cell:0}]: {(_blocked[cell] ? "CELL BLOCKED (off-map)" : "cell open")}");
        for (int i = 0; i < FloorCount(cell); i++)
        {
            sb.Append($"  floor {FloorH(cell, i):0.0} {(FloorOpen(cell, i) ? "open" : "BLOCKED")}");
        }

        return sb.ToString();
    }

    // The open floor nearest height y (0 when y is unknown); -1 when every floor is blocked.
    private int NearestFloor(int cell, float y)
    {
        int best = -1;
        float bd = float.NaN;
        for (int i = 0; i < FloorCount(cell); i++)
        {
            if (!FloorOpen(cell, i))
            {
                continue;
            }

            float d = Math.Abs(FloorH(cell, i) - y);
            if (best < 0 || d < bd)
            {
                bd = d;
                best = i;
            }
        }

        return best;
    }

    private static float Len(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);

    // Reopen the cells under and around every contact exit of the playfield (proxy door, booth/
    // grid/lift pad, teleporter): the cell-level block, every wall-stamped floor and the
    // structure-top override go, in a ~3 m disc. The server walks these spots - it PLACES the bot
    // there on every proxy crossing - and the walk's own route planning still keeps its distance
    // from an exit that is not its goal (the per-search exit discs in PlanRoute). Zone lines stay
    // blocked (their band IS the corridor the walk crosses on purpose) and Scotty tells have no
    // ground presence.
    // The structure-top override matters: at a shop front the collision's stair/shell triangles
    // make the cell a structure top and DROP the terrain level as a floor - but the terrain is
    // exactly where the bot lands (Newland Desert's Fair Trade plateau, 2026-10-02: the bot zoned
    // out onto y 21.2 and the grid had no floor under it at any level - "every floor under me is
    // blocked"). No height band either: the stored exit Y can sit above the real floor (the Desert
    // door reads y 22.7, the landing is 21.2), and a door is a passage at every level the building
    // has. A cell whose centre height is unknown (NaN centres read 0) gets no terrain floor.
    internal void KeepExitsOpen(int pf)
    {
        foreach (var e in Zoning.ExitsFrom(pf))
        {
            if (e.Kind == ExitKind.ZoneLine || e.Kind == ExitKind.Scotty)
            {
                continue;
            }

            int cx = CellX(e.A.X), cz = CellZ(e.A.Z);
            int r = (int)Math.Ceiling(3f / Cell);
            for (var dz = -r; dz <= r; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (!In(x, z) || dx * dx + dz * dz > r * r)
                    {
                        continue;
                    }

                    int k = z * _w + x;
                    _blocked[k] = false;
                    if (_ch[k] != 0f)
                    {
                        _terrainTop?.Remove(k);
                    }

                    for (int f = 0; f < FloorCount(k); f++)
                    {
                        _blockedFl.Remove(((long)k << FloorShift) | (long)f);
                    }
                }
            }
        }
    }

    // A runtime version of the exit discs, for a spot the server PUTS the body on without the
    // zoning data knowing it: the mission door. The bot lands there on every mission exit and can
    // relog standing at it - walls.bin stamps the whole door assembly solid and a mission entrance
    // is no zoning exit, so nothing kept its cells open (owner, 2026-10-03: back out of a mission,
    // every route out of the entrance pocket failed "walled off" - the pocket reached 8 m out).
    public void Reopen(Vector3 at, float radius)
    {
        int cx = CellX(at.X), cz = CellZ(at.Z);
        int r = (int)Math.Ceiling(radius / Cell);
        for (var dz = -r; dz <= r; dz++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (!In(x, z) || dx * dx + dz * dz > r * r)
                {
                    continue;
                }

                int k = z * _w + x;
                _blocked[k] = false;
                if (_ch[k] != 0f)
                {
                    _terrainTop?.Remove(k);
                }

                for (int f = 0; f < FloorCount(k); f++)
                {
                    _blockedFl.Remove(((long)k << FloorShift) | (long)f);
                }
            }
        }
    }

    // ---- queries ------------------------------------------------------------------------------------------

    private int CellX(float x) => (int)Math.Floor(x / Cell);
    private int CellZ(float z) => (int)Math.Floor(z / Cell);
    private bool In(int x, int z) => x >= 0 && z >= 0 && x < _w && z < _h;
    private bool Open(int x, int z, HashSet<int> extra) => In(x, z) && !_blocked[z * _w + x] && (extra == null || !extra.Contains(z * _w + x));

    private Vector3 Centre(int x, int z, float h) => new((x + 0.5f) * Cell, h, (z + 0.5f) * Cell);

    public bool IsOpen(Vector3 p, HashSet<int> extra = null) => Open(CellX(p.X), CellZ(p.Z), extra);

    /// <summary>Open ground at p at ANY of its levels: the cell is open and one of its floors sits within 3 m of p.Y.</summary>
    public bool OpenAt(Vector3 p)
    {
        int x = CellX(p.X), z = CellZ(p.Z);
        if (!Open(x, z, null))
        {
            return false;
        }

        if (float.IsNaN(p.Y))
        {
            return true;
        }

        int cell = z * _w + x;
        for (int i = 0; i < FloorCount(cell); i++)
        {
            if (Math.Abs(FloorH(cell, i) - p.Y) <= 3f && FloorOpen(cell, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Cells within radius of the segment a-b, for the caller's per-search blocked set.</summary>
    public HashSet<int> CellsAlong(Vector3 a, Vector3 b, float radius, HashSet<int> into = null)
    {
        into ??= new HashSet<int>();
        float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        int n = Math.Max(1, (int)Math.Ceiling(len / (Cell / 2)));
        int r = (int)Math.Ceiling(radius / Cell);
        for (var i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            int cx = CellX(a.X + (b.X - a.X) * t), cz = CellZ(a.Z + (b.Z - a.Z) * t);
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (In(cx + dx, cz + dz) && dx * dx + dz * dz <= r * r)
                    {
                        into.Add((cz + dz) * _w + cx + dx);
                    }
                }
            }
        }

        return into;
    }

    /// <summary>The nearest open cell to p within maxR metres, ring by ring; null when there is none.</summary>
    private (int, int)? NearestOpen(Vector3 p, float maxR, HashSet<int> extra)
    {
        int cx = CellX(p.X), cz = CellZ(p.Z);
        if (Open(cx, cz, extra))
        {
            return (cx, cz);
        }

        int R = (int)Math.Ceiling(maxR / Cell);
        for (var r = 1; r <= R; r++)
        {
            (int, int)? best = null;
            float bd = float.MaxValue;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || !Open(cx + dx, cz + dz, extra))
                    {
                        continue;
                    }

                    float d = dx * dx + dz * dz;
                    if (d < bd)
                    {
                        bd = d;
                        best = (cx + dx, cz + dz);
                    }
                }
            }

            if (best.HasValue)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>
    ///     The nearest (floor, cell) that can be stood on, ring by ring out to maxR metres: the open cell's open
    ///     floor closest to p's height, preferring the nearer ring, then the smaller height gap. For a body
    ///     standing on cells whose floors are all stamped blocked (a mission-entrance disc) - the walk has to
    ///     start somewhere, and the server's SetPos takes over from the first step. Null when there is none.
    /// </summary>
    private (int floor, int cell)? NearestOpenFloor(Vector3 p, float maxR, HashSet<int> extra)
    {
        int cx = CellX(p.X), cz = CellZ(p.Z);
        int R = (int)Math.Ceiling(maxR / Cell);
        for (var r = 0; r <= R; r++)
        {
            (int floor, int cell)? best = null;
            float bd = float.MaxValue;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || x < 0 || z < 0 || x >= _w || z >= _h ||
                        !Open(x, z, extra))
                    {
                        continue;
                    }

                    int cell = z * _w + x;
                    for (int i = 0; i < FloorCount(cell); i++)
                    {
                        if (!FloorOpen(cell, i))
                        {
                            continue;
                        }

                        float d = Math.Abs(FloorH(cell, i) - p.Y);
                        if (best == null || d < bd)
                        {
                            bd = d;
                            best = (i, cell);
                        }
                    }
                }
            }

            if (best.HasValue)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>
    ///     A walkable path from a to within reach metres of b (ground heights on the points), smoothed to its
    ///     corners. The start snaps to the nearest open cell within snap metres. The path ends at the first open
    ///     cell within reach of b. The first point is a itself; the last is b when b's cell is open and
    ///     reachable. Null (with why) when there is none.
    /// </summary>
    public List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why,
        HashSet<int> pricey = null)
        => FindPath(a, b, extra, snap, reach, out why, float.NaN, pricey);

    /// <summary>Same, with a goal HEIGHT: the path must arrive on a floor within 2.5 m of goalY (a wall-top, a walkway) instead of on whichever level touches the goal cell first. NaN goalY = any level.</summary>
    public List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why, float goalY,
        HashSet<int> pricey = null)
    {
        // Between two fixed objects (whompahs, grids, doors) the route is planned once and kept
        // (RouteCache, ported 2026-10-03). A saved route was planned without today's mobs: through a
        // hostile spot, plan it afresh.
        why = "";
        var saved = RouteCache.Get(this, a, b, reach, goalY, extra,
            (p, q) => Search(p, q, extra, snap, 1.5f, false, out _, float.NaN, pricey));
        if (saved != null)
        {
            var (sHit, sAll, sNear) = MobDanger.Along(Pf, saved);
            if (sHit == 0)
            {
                return saved;
            }

            MobDanger.Log($"OVERLAND: the saved route passes {sHit} of {sAll} hostile-mob spot(s) (closest {sNear:0} m); planning afresh.");
        }

        // In sight of b first; if that walks nowhere (b's pocket is closed off), on distance alone.
        var route = Search(a, b, extra, snap, reach, true, out why, goalY, pricey)
                    ?? Search(a, b, extra, snap, reach, false, out _, goalY, pricey);
        if (route != null)
        {
            RouteCache.Put(this, a, b, reach, goalY, route);
        }

        if (route != null && _danger != null)
        {
            var (hit, all, near) = MobDanger.Along(Pf, route);
            MobDanger.Log($"OVERLAND: routing round {_dangerSpots} hostile-mob spot(s){(_dangerLive > 0 ? $" and {_dangerLive} live hostile(s)" : "")} in {Zoning.Name(Pf)}: {route.Count} points; passes {hit} of {all} within aggro range, closest {near:0} m.");
        }

        return route;
    }

    private List<Vector3> Search(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, bool sight, out string why, float goalY,
        HashSet<int> pricey = null)
    {
        why = "";
        var s = NearestOpen(a, snap, extra);
        if (s == null)
        {
            why = $"no open ground within {snap:0} m of me";
            return null;
        }

        int startCell = s.Value.Item2 * _w + s.Value.Item1;
        int startFloor = NearestFloor(startCell, a.Y);
        if (startFloor < 0)
        {
            // The body can stand where the data has nothing open to stand on: a mission entrance
            // is no zoning exit, so KeepExitsOpen reopens no disc for it and walls.bin stamps the
            // whole door assembly solid (owner, 2026-10-03: relogged standing 1.3 m from the
            // mission door, "every floor under me is blocked" for every goal). The body IS there
            // and the server walks it - seed from the nearest open floor there is, and the
            // server's SetPos takes over from the first step.
            var f = NearestOpenFloor(a, snap, extra);
            if (f == null)
            {
                why = "every floor under me is blocked";
                return null;
            }

            startFloor = f.Value.floor;
            startCell = f.Value.cell;
        }

        long start = ((long)startCell << FloorShift) | (long)startFloor;
        int bx = CellX(b.X), bz = CellZ(b.Z);
        float reachCells = Math.Max(reach, Cell) / Cell;
        // Close enough AND in plain sight of b: "4 m from the terminal" was otherwise the far side of its kiosk
        // wall. When b stands in a closed box of walls nothing sees it, and then distance alone has to do.
        int bCell = bz * _w + bx;
        var needSight = sight && SeenFromOutside(bx, bz, reachCells);
        bool AtGoal(long node)
        {
            int c = (int)(node >> FloorShift);
            float dx = c % _w - bx, dz = c / _w - bz;
            if (dx * dx + dz * dz > reachCells * reachCells)
            {
                return false;
            }

            // A known goal height means THE level matters (the wall-top, a walkway): arrive on a floor
            // near it, not on whatever floor touches the goal cell first. NaN = any level, as before.
            if (!float.IsNaN(goalY) && Math.Abs(FloorH(c, (int)(node & FloorMask)) - goalY) > 2.5f)
            {
                return false;
            }

            return !needSight || Sight(c, bCell);
        }

        long goal = -1;

        var gScore = new Dictionary<long, float> { [start] = 0 };
        var parent = new Dictionary<long, long>();
        var closed = new HashSet<long>();
        var open = new PriorityQueue<long, float>();
        EnsureLearned();
        EnsureDanger();
        float hScale = _anyRoad ? RoadFactor : 1.2f; // the estimate stays under a road step's cost
        float H(int x, int z)
        {
            int dx = Math.Abs(x - bx), dz = Math.Abs(z - bz);
            return hScale * Math.Max(0f, Math.Max(dx, dz) + 0.4142f * Math.Min(dx, dz) - reachCells);
        }

        open.Enqueue(start, H(s.Value.Item1, s.Value.Item2));
        var found = false;
        while (open.TryDequeue(out long cur, out _))
        {
            if (!closed.Add(cur))
            {
                continue;
            }

            if (AtGoal(cur))
            {
                goal = cur;
                found = true;
                break;
            }

            if (closed.Count > MaxExpand)
            {
                why = $"searched {MaxExpand} cells without reaching it";
                return null;
            }

            int curCell = (int)(cur >> FloorShift);
            int x = curCell % _w, z = curCell / _w;
            int curFloor = (int)(cur & FloorMask);
            float fh = FloorH(curCell, curFloor);
            float gc = gScore[cur];
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    int nx = x + dx, nz = z + dz;
                    if (!Open(nx, nz, extra))
                    {
                        continue;
                    }

                    if (dx != 0 && dz != 0 && (!Open(x + dx, z, extra) || !Open(x, z + dz, extra)))
                    {
                        continue; // no squeezing past a corner
                    }

                    int ncell = nz * _w + nx;
                    float d = (dx != 0 && dz != 0 ? 1.4142f : 1f) * Cell;
                    float len1 = dx != 0 && dz != 0 ? 1.4142f : 1f;
                    float baseMul = 1f + WallCost(ncell) + (_water != null && _water[ncell] ? WaterWeight : 0f)
                                    + (_learn != null ? _learn[ncell] : 0f) + DangerAt(ncell)
                                    // refused ground is PRICED, not blocked (owner, 2026-10-09): the
                                    // route steers round it where a detour is cheaper, and pays
                                    // straight through a corridor with no way round
                                    + (pricey != null && pricey.Contains(ncell) ? RefusedCellWeight : 0f);
                    float roadMul = _road != null && _road[ncell] ? RoadFactor : 1f;
                    for (int j = 0; j < FloorCount(ncell); j++)
                    {
                        // The one-way rule per floor pair: climbing onto the next floor must stay under
                        // MaxRise; dropping onto it is free wherever it is shorter - NO FALL DAMAGE in
                        // AO, anywhere (owner, 2026-10-04: the old "only where the OWNER walked it"
                        // gate kept the planner off every ledge with no recorded jump). LearnedGround's
                        // recorded jumps only still price the climb back UP through them.
                        float rise = FloorH(ncell, j) - fh;
                        if (rise > MaxRise * d)
                        {
                            continue;
                        }

                        float grade = Math.Abs(rise) / d;
                        const float slopeWeight = 4f, slopeFree = 0.15f;
                        float slope = grade > slopeFree ? slopeWeight * (grade - slopeFree) * (rise < 0 ? 0.5f : 1f) : 0f;
                        if (rise > 0.2f && _drop != null && _drop[ncell])
                        {
                            slope += DropClimbCost; // climbing back through a recorded jump is a last resort
                        }

                        float stepCost = gc + len1 * (baseMul + slope) * roadMul;
                        long nn = ((long)ncell << FloorShift) | (long)j;
                        if (closed.Contains(nn) || !FloorOpen(ncell, j))
                        {
                            continue;
                        }

                        if (gScore.TryGetValue(nn, out float old) && old <= stepCost)
                        {
                            continue;
                        }

                        gScore[nn] = stepCost;
                        parent[nn] = cur;
                        open.Enqueue(nn, stepCost + H(nx, nz));
                    }
                }
            }
        }

        if (!found)
        {
            // the explored count is the diagnosis: a few hundred cells = the START is sealed (the
            // search died where it stood); hundreds of thousands = the goal region is what's closed
            why = $"walled off (explored {closed.Count} cells): no open ground within {reach:0} m of ({b.X:0},{b.Z:0}) that I can walk to";
            return null;
        }

        var nodes = new List<long>();
        for (long n = goal; ; n = parent[n])
        {
            nodes.Add(n);
            if (n == start)
            {
                break;
            }
        }

        nodes.Reverse();

        // String-pull: from each kept node, jump to the farthest one on a clear line.
        var pts = new List<Vector3> { a };
        int i0 = 0;
        while (i0 < nodes.Count - 1)
        {
            int j = nodes.Count - 1;
            while (j > i0 + 1 && !Clear(nodes[i0], nodes[j], extra))
            {
                j--;
            }

            int c = (int)(nodes[j] >> FloorShift);
            pts.Add(Centre(c % _w, c / _w, FloorH(c, (int)(nodes[j] & FloorMask))));
            i0 = j;
        }

        if (pts.Count == 1)
        {
            int c = (int)(goal >> FloorShift);
            pts.Add(Centre(c % _w, c / _w, FloorH(c, (int)(goal & FloorMask))));
        }

        int goalCell = (int)(goal >> FloorShift);
        if (goalCell != bCell)
        {
            int bf = NearestFloor(bCell, b.Y);
            if (bf >= 0 && IsOpen(b, extra) && Clear(goal, ((long)bCell << FloorShift) | (long)bf, extra))
            {
                pts[pts.Count - 1] = b;
            }
        }
        else
        {
            pts[pts.Count - 1] = b;
        }

        return pts;
    }

    /// <summary>
    ///     Can the body walk the STRAIGHT line a->b in the real geometry, the grid's verdict aside?
    ///     The blocked-verdict fallback (owner, 2026-10-03): a pocket sealed by the stamps - a one-cell
    ///     doorway at a big-map cell size, a landing on stamped ground, a yank band's own cells - is
    ///     still open in the triangles. The line walks when the terrain is under it end to end and no
    ///     walls.bin triangle crosses the body band (EXACT segment-edge crossings, so a vertical wall
    ///     is caught). False when the zone has no walls.bin: without it nothing can vouch for a line.
    /// </summary>
    public bool GeometryLine(Vector3 a, Vector3 b, out string why)
    {
        why = "";
        if (_ground == null)
        {
            why = "no heightfield in this grid - the line cannot be vouched for";
            return false;
        }

        _walls ??= LazyWalls();
        if (_walls == null)
        {
            why = "no walls.bin for this zone - the line cannot be vouched for";
            return false;
        }

        double dx = b.X - a.X, dz = b.Z - a.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.01)
        {
            return true;
        }

        int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
        var floors = new List<float>(n + 1);
        for (var s = 0; s <= n; s++)
        {
            double t = (double)s / n;
            double h = _ground.HeightAt(a.X + dx * t, a.Z + dz * t);
            if (double.IsNaN(h))
            {
                why = $"no terrain {t * len:0.0} m along the line (off the map)";
                return false;
            }

            floors.Add((float)h);
        }

        if (_walls.LineBlocked(a.X, a.Z, b.X, b.Z, t => Profile(floors, t), BodyLow, BodyHigh))
        {
            why = "a wall crosses the line at body height";
            return false;
        }

        return true;
    }

    // walls.bin of a cache-loaded grid (no plugin dir at Build): loaded once on first need from
    // where the playfield's folder is. Benign race - both loaders read the same immutable content.
    private NavCollision LazyWalls()
    {
        if (_pluginDir == null)
        {
            return null;
        }

        var wp = Path.Combine(AOBuddyNav.FolderFor(_pluginDir, Pf), "walls.bin");
        return File.Exists(wp) ? NavCollision.Read(wp) : null;
    }

    // THE STUCK ESCAPE (owner, 2026-10-03: "we need an exacter way to pathfind if he's stuck" -
    // the same session left the body pinned while every re-plan died in 30 ms, state the log
    // could not name). The grid's verdict is set aside and the GEOMETRY answers: a fan of
    // straight lines from the body, each judged by GeometryLine (terrain end to end, no wall in
    // the band), the reachable point that best improves the distance to the goal wins. Known
    // water costs EscapeWetPenalty extra, so a dry way round beats a straight swim when both
    // exist. Null when every direction is walled in the real geometry too - the body is then
    // genuinely trapped and the honest give-up stands.
    private const float EscapeReach = 20f;
    private const float EscapeWetPenalty = 15f;

    public Vector3? Escape(Vector3 from, Vector3 goal, out string why)
    {
        why = "";
        _walls ??= LazyWalls();
        if (_walls == null)
        {
            why = "no walls.bin - no geometry to escape by";
            return null;
        }

        double baseDist = Math.Sqrt((goal.X - from.X) * (goal.X - from.X) + (goal.Z - from.Z) * (goal.Z - from.Z));
        Vector3? best = null;
        var bestScore = float.MaxValue;
        var clear = 0;
        for (var deg = 0; deg < 360; deg += 15)
        {
            var rad = deg * Math.PI / 180.0;
            var p = new Vector3(from.X + (float)Math.Cos(rad) * EscapeReach, from.Y,
                from.Z + (float)Math.Sin(rad) * EscapeReach);
            if (!GeometryLine(from, p, out _))
            {
                continue;
            }

            clear++;
            var wet = _ground != null && !double.IsNaN(_ground.SwimY((from.X + p.X) / 2, (from.Z + p.Z) / 2, WadeDepth));
            double dx = p.X - goal.X, dz = p.Z - goal.Z;
            var score = (float)(Math.Sqrt(dx * dx + dz * dz) + (wet ? EscapeWetPenalty : 0));
            if (score < bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        if (best == null)
        {
            why = "no clear line in any of 24 directions - trapped in the geometry too";
            return null;
        }

        why = clear == 1 ? "one clear line" : $"{clear} clear lines";
        return best;
    }

    // Piecewise-linear floor height at line parameter t over the caller's even samples.
    private static double Profile(List<float> ys, double t)
    {
        double s = t * (ys.Count - 1);
        int i = (int)s;
        if (i >= ys.Count - 1)
        {
            return ys[ys.Count - 1];
        }

        double f = s - i;
        return ys[i] * (1 - f) + ys[i + 1] * f;
    }

    // A thin line of cells with no wall or cliff on it (zone-line cells don't hide anything).
    private bool Sight(int c0, int c1)
    {
        float x0 = c0 % _w + 0.5f, z0 = c0 / _w + 0.5f, x1 = c1 % _w + 0.5f, z1 = c1 / _w + 0.5f;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) * 3));
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            int x = (int)Math.Floor(x0 + (x1 - x0) * t), z = (int)Math.Floor(z0 + (z1 - z0) * t);
            if (!In(x, z) || _blocked[z * _w + x])
            {
                return false;
            }
        }

        return true;
    }

    // Whether any open cell within r cells has sight of (bx, bz).
    private bool SeenFromOutside(int bx, int bz, float r)
    {
        int R = (int)Math.Ceiling(r);
        for (int dz = -R; dz <= R; dz++)
        {
            for (int dx = -R; dx <= R; dx++)
            {
                int x = bx + dx, z = bz + dz;
                if (dx * dx + dz * dz > r * r || !In(x, z) || _blocked[z * _w + x])
                {
                    continue;
                }

                if ((dx != 0 || dz != 0) && Sight(z * _w + x, bz * _w + bx))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A thin line of cells with no wall or cliff on it, that a body's width fits on and that TRACKS A
    // FLOOR it can be walked on: at each sample, some OPEN floor of the cell lies within 1.5 m of the
    // chord's height. No closer to a wall than the ends are (up to ClearKeep).
    private bool Clear(long n0, long n1, HashSet<int> extra)
    {
        int c0 = (int)(n0 >> FloorShift), c1 = (int)(n1 >> FloorShift);
        float h0 = FloorH(c0, (int)(n0 & FloorMask)), h1 = FloorH(c1, (int)(n1 & FloorMask));
        float x0 = c0 % _w + 0.5f, z0 = c0 / _w + 0.5f, x1 = c1 % _w + 0.5f, z1 = c1 / _w + 0.5f;
        float len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        int n = Math.Max(1, (int)Math.Ceiling(len * 3));
        // Test a body's width, not a line: a third of a cell either side, across the direction of travel.
        float px = len > 0 ? -(z1 - z0) / len * 0.35f : 0, pz = len > 0 ? (x1 - x0) / len * 0.35f : 0;
        // No closer to a wall than the ends are (up to ClearKeep): the pull would otherwise lay the line back
        // along the wall the search's wall cost kept it off. A narrow gate passes, its ends are narrow too.
        float keep = Math.Min(ClearKeep, Math.Min(ClearAt(c0), ClearAt(c1)));
        // Nor off the road it was planned on, nor across a remembered bad spot, nor through a hostile spot
        // the search went round (the pull would cut the corner off the road over the spot the server resets,
        // AOBuddy10 2026-09-26).
        bool onRoad = _road != null && _road[c0] && _road[c1];
        float learnEnds = _learn == null ? 0f : Math.Max(_learn[c0], _learn[c1]);
        float dangerEnds = Math.Max(DangerAt(c0), DangerAt(c1));
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            int cx = (int)Math.Floor(x0 + (x1 - x0) * t), cz = (int)Math.Floor(z0 + (z1 - z0) * t);
            if (!In(cx, cz))
            {
                return false;
            }

            int cell = cz * _w + cx;
            double want = h0 + (h1 - h0) * t;
            var near = false;
            for (int f = 0; f < FloorCount(cell); f++)
            {
                if (Math.Abs(FloorH(cell, f) - want) <= 1.5 && FloorOpen(cell, f))
                {
                    near = true;
                    break;
                }
            }

            if (!near)
            {
                return false;
            }
        }

        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float x = x0 + (x1 - x0) * t, z = z0 + (z1 - z0) * t;
            if (!Open((int)Math.Floor(x), (int)Math.Floor(z), extra))
            {
                return false;
            }

            int sc = (int)Math.Floor(z) * _w + (int)Math.Floor(x);
            if (ClearAt(sc) < keep)
            {
                return false;
            }

            if (onRoad && !_road[sc])
            {
                return false;
            }

            if (_learn != null && _learn[sc] > learnEnds + 1f)
            {
                return false;
            }

            if (DangerAt(sc) > dangerEnds + 1f)
            {
                return false;
            }

            if (!Open((int)Math.Floor(x + px), (int)Math.Floor(z + pz), extra) || !Open((int)Math.Floor(x - px), (int)Math.Floor(z - pz), extra))
            {
                return false;
            }
        }

        return true;
    }

    private float ClearAt(int cell) => _clear == null ? ClearMetres : _clear[cell] * Cell;

    // 1 + ClearWeight right against a wall, falling off to 1 at ClearMetres.
    private float WallCost(int cell)
    {
        float d = ClearAt(cell);
        if (d >= ClearMetres)
        {
            return 0f;
        }

        float t = 1f - d / ClearMetres;
        return ClearWeight * t * t;
    }
}