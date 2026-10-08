// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: FloorGrid.cs
//
// Last modified: 2026-10-03
// Created:       2026-10-01 (ported from AOBuddy10 FloorGrid.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOSharp.Common.GameData;

namespace AOBuddy20.Nav;

/// <summary>
///     Walkable floors of a playfield WITHOUT a ground heightfield (dungeons, the Grid), for overland routing:
///     layered 0.5 m cells built from the client's floor triangles (collision.bin), with the wall triangles
///     (walls.bin) blocking at body height. A cell can hold several floors, one above the other; a step goes to
///     the floor in the next cell within MaxStep of the one we are on, so the path stays on the level it
///     started on and on the platform it is walking - the Grid's middle level is a web of walkways a metre or
///     two wide over a 33 m drop (2026-09-24). Changing level UP is not walking: that is a lift beam, which
///     the route planner treats as an exit. Dropping off an edge IS a route like any other - no fall damage
///     anywhere in AO (owner, 2026-10-04): the fall lands on the topmost floor below the edge.
///     Nothing here moves the body. IMMUTABLE after Build/Read: any thread may query.
/// </summary>
public sealed class FloorGrid : IWalkGrid
{
    public const float DefaultCell = 0.5f;

    /// <summary>The cell size: 0.5 m for overland and static dungeons, 0.2 m for missions off a
    /// northbound pool grid (fine enough for table legs and chair gaps, cheap because the
    /// lattices are precalculated per pool room type offline - see NorthboundPool).</summary>
    public float Cell = DefaultCell;

    private const float Merge = 0.6f; // surfaces this close are one floor (a deck's top and underside)
    private const float StaticStep = 0.4f; // rise between neighbouring cells (0.5 m): ~40 degrees
    private const float MissionStep = 0.8f; // missions: the composed rock slopes step up to this and the
                                            // walk claims climbs of +0.8 (StepFloor), so the planner may
                                            // too - the wall-edge test still refuses every real wall
                                            // (owner, 2026-10-03: the item stood on Grey Caves' rock at
                                            // y 7.2 and the route died at the 5.0 floor world's rim)
    private const float BodyLow = 0.3f, BodyHigh = 1.9f;
    private const int MaxFloors = 8;
    private const int MaxExpand = 1_000_000;
    private float _maxStep = StaticStep; // missions raise this in UseComposedGeometry

    private readonly Dictionary<int, float[]> _floors = new Dictionary<int, float[]>(); // cell -> floor heights, ascending
    private readonly HashSet<(int k, float h)> _tileStamp = new HashSet<(int, float)>(); // mission grids: floors the
    // room-tile stamp laid down, so SuppressBuriedTiles can tell them from the placed mesh's own floors
    private readonly HashSet<(int k, float h)> _meshStamp = new HashSet<(int, float)>(); // mission grids: floors the
    // placed mesh sampled in (SampleSurface -> AddLevel)
    private readonly HashSet<long> _blocked = new HashSet<long>(); // cell * 8 + floor index
    private readonly int _x0, _z0, _w, _h;
    private NavCollision _walls; // static dungeons: the zone's walls.bin, for GeometryLine (missions use _wallTris)
    private string _pluginDir; // set by Build only: where walls.bin lives, for GeometryLine's lazy load

    private FloorGrid(int pf, int x0, int z0, int w, int h)
    {
        Pf = pf;
        _x0 = x0;
        _z0 = z0;
        _w = w;
        _h = h;
    }

    public int Pf { get; }

    // ---- GridCache persistence (deterministic from the zone's files; see GridCache) ----------------------

    internal void Write(BinaryWriter bw)
    {
        bw.Write(_x0);
        bw.Write(_z0);
        bw.Write(_w);
        bw.Write(_h);
        bw.Write(_floors.Count);
        foreach (var kv in _floors)
        {
            bw.Write(kv.Key);
            bw.Write((byte)kv.Value.Length);
            foreach (float f in kv.Value)
            {
                bw.Write(f);
            }
        }

        bw.Write(_blocked.Count);
        foreach (long b in _blocked)
        {
            bw.Write(b);
        }

        bw.Write(_noEdge.Count);
        foreach (long e in _noEdge)
        {
            bw.Write(e);
        }

        bw.Write(_doorways.Count);
        foreach (int d in _doorways)
        {
            bw.Write(d);
        }

        bw.Write(_doorwayList.Count);
        foreach (var dw in _doorwayList)
        {
            bw.Write(dw.X); bw.Write(dw.Y); bw.Write(dw.Z);
            bw.Write(dw.Nx); bw.Write(dw.Nz);
        }
    }

    internal static FloorGrid Read(BinaryReader br, int pf)
    {
        int x0 = br.ReadInt32(), z0 = br.ReadInt32(), w = br.ReadInt32(), h = br.ReadInt32();
        var g = new FloorGrid(pf, x0, z0, w, h);
        int n = br.ReadInt32();
        for (var i = 0; i < n; i++)
        {
            int key = br.ReadInt32();
            int c = br.ReadByte();
            var fl = new float[c];
            for (var f = 0; f < c; f++)
            {
                fl[f] = br.ReadSingle();
            }

            g._floors[key] = fl;
        }

        int m = br.ReadInt32();
        for (var i = 0; i < m; i++)
        {
            g._blocked.Add(br.ReadInt64());
        }

        int ne = br.ReadInt32();
        for (var i = 0; i < ne; i++)
        {
            g._noEdge.Add(br.ReadInt64());
        }

        int nd = br.ReadInt32();
        for (var i = 0; i < nd; i++)
        {
            g._doorways.Add(br.ReadInt32());
        }

        int ndl = br.ReadInt32();
        for (var i = 0; i < ndl; i++)
        {
            var dw = new AOBuddyNav.Doorway
            {
                X = br.ReadDouble(), Y = br.ReadDouble(), Z = br.ReadDouble(),
                Nx = br.ReadDouble(), Nz = br.ReadDouble(),
            };
            g._doorwayList.Add(dw);
        }

        return g;
    }

    // Probe-only switch (gridprobe missionx): build the mission grid WITHOUT the room-tile stamp,
    // to measure how much of the floor map the placed mesh alone carries.
    public static bool DebugSkipRoomTiles;

    // Probe-only: how many cells SuppressBuriedTiles stripped of their tile floor - missionx
    // prints it after Build.
    public static int DebugBuriedCells;

    public static FloorGrid Build(string pluginDir, int pf, AOBuddyNav nav, Action<string> log, NorthboundPool north = null)
    {
        if (nav == null || nav.Ground != null)
        {
            return null;
        }

        // MISSION INSTANCE (a mission is composed from its zone-in packet - NavData.ComposeMission):
        // no disk files, no collision.bin. Floors come from the placed rooms' tiles, walls from the
        // pool's walls.bin carried alongside the room placement (nav.Walls), and the passages through
        // the door leaves are the mission's own doorway list. Nothing is cached to disk (GridCache) -
        // instanced, tiny, built per mission.
        if (nav.Layout != null)
        {
            // The northbound pool grid, when it carries every placed room's type: 20 cm lattices
            // precalculated offline per pool room, rotated/translated here as an index remap -
            // no triangle sampling, milliseconds. Rotation comes from the live placement, never
            // from the file, so OmniCell-style compositions work without a recache.
            var northReady = north != null && nav.Dungeon.Rooms.All(r => r.PoolIndex < 0 || north.Rooms.ContainsKey(r.PoolIndex));
            var mgrid = BoundsFromRooms(nav.Dungeon, pf, northReady ? 0.2f : DefaultCell, out var mx0, out var mz0, out var mw, out var mh);
            if (mgrid == null)
            {
                log?.Invoke($"FLOORGRID: mission pf {pf} has no rooms to walk");
                return null;
            }

            mgrid._pluginDir = pluginDir;
            var msw = System.Diagnostics.Stopwatch.StartNew();
            if (northReady)
            {
                mgrid.Cell = 0.2f;
                // The slot seams between rooms carry no pool geometry (it ended at the atlas
                // neighbours) - the doorway stamp fills them at the doors' own mesh-sampled
                // level, and the doors are walkable by default (a locked door is runtime state).
                mgrid.StampDoorways(nav.MissionDoorways);
                mgrid.UseNorthbound(north, nav);
            }
            else
            {
                mgrid.StampDoorways(nav.MissionDoorways);
                if (nav.Walls != null && nav.Walls.Length >= 9)
                {
                    // REAL 3D (owner, 2026-10-03: "the whole geometry, not flattened stuff - rooms
                    // double/triple height with multiple ramps"): the composed triangles are the truth,
                    // BOTH files. collision.bin carries the walkable surfaces - stairs, ramps, bridges,
                    // mezzanines - and a ramp sub-cell gets its true height, not its 2 m tile's.
                    // walls.bin (steep-only, checked: 57,116 triangles and not one flat) is bucketed and
                    // only ever forbids the EDGE a wall physically crosses - no cell is poisoned
                    // wholesale, so no sealed pockets and no keep-open patches. Furniture is render
                    // data and was never exported: no version of this knows a crate is there.
                    // THE MESH SAMPLES BEFORE THE TILES (Subway ramp room, 2026-10-04): the tile fold
                    // keeps the FIRST height, and with the tiles first the ramp's low footing folded
                    // into the tile's flat floor - both doors of the ramp room then tested as walled
                    // (the ramp's rising body crossed a floor-level band) and the phantom floor tunnel
                    // ran under the whole stairway. Mesh first: the ramp keeps its own heights, the
                    // tiles fold into them, and where the mesh has nothing the tiles still lay the
                    // room floor (the mesh does NOT carry it - a no-tile build collapses to NO PATH).
                    mgrid.UseComposedGeometry(nav.Walls, nav.Surfaces, nav.Dungeon);
                }
                else if (!DebugSkipRoomTiles)
                {
                    mgrid.StampRoomFloors(nav.Dungeon);
                }
            }

            log?.Invoke($"FLOORGRID: mission grid for pf {pf} ({nav.Name}): {mw}x{mh} cells of {mgrid.Cell:0.0#} m" +
                        (northReady ? " northbound" : "") + ", " +
                        $"{mgrid._floors.Count} with floor, {mgrid._noEdge.Count} edges walled, " +
                        $"{mgrid._blocked.Count} blocked, " +
                        $"{mgrid._doorways.Count} doorway cells bridged, {msw.ElapsedMilliseconds} ms");
            return mgrid;
        }

        if (nav.Collision == null)
        {
            return null;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var c in nav.Collision.Chunks)
        {
            for (int i = 0; i + 2 < c.Verts.Length; i += 3)
            {
                minX = Math.Min(minX, c.Verts[i]);
                maxX = Math.Max(maxX, c.Verts[i]);
                minZ = Math.Min(minZ, c.Verts[i + 2]);
                maxZ = Math.Max(maxZ, c.Verts[i + 2]);
            }
        }

        if (minX > maxX)
        {
            return null;
        }

        int x0 = (int)Math.Floor(minX / DefaultCell) - 2, z0 = (int)Math.Floor(minZ / DefaultCell) - 2;
        int w = (int)Math.Ceiling(maxX / DefaultCell) + 2 - x0, h = (int)Math.Ceiling(maxZ / DefaultCell) + 2 - z0;
        if ((long)w * h > 16_000_000)
        {
            log?.Invoke($"FLOORGRID: pf {pf} is too big for a floor grid ({w}x{h})");
            return null;
        }

        var grid = new FloorGrid(pf, x0, z0, w, h);
        grid._pluginDir = pluginDir;
        grid.StampFloors(nav.Collision);
        grid.StampRoomFloors(nav.Dungeon);
        string wp = Path.Combine(AOBuddyNav.FolderFor(pluginDir, pf), "walls.bin");
        var walls = File.Exists(wp);
        if (walls)
        {
            // The room doors go first: walls.bin carries the door leaves and frames as solid, and
            // nothing in the data says a doorway is a passage - without the keep-open cells the
            // shop's section doors walled the bot into the entrance room (Neutral Supermarket 1187,
            // 2026-10-02: half the room-to-room connections had no path across their doors).
            grid.StampDoorways(AOBuddyNav.StaticDoorways(nav.Dungeon));
            grid._walls = NavCollision.Read(wp); // retained: GeometryLine judges straight lines against it
            grid.StampWalls(grid._walls);
        }

        grid.StampHeadroom();
        log?.Invoke($"FLOORGRID: floor grid for pf {pf}: {w}x{h} cells of {grid.Cell:0.0#} m, {grid._floors.Count} with floor, {grid._blocked.Count} floor cells blocked, walls {(walls ? "yes" : "NONE")} ({grid._doorways.Count} doorway cells kept open), {sw.ElapsedMilliseconds} ms");
        return grid;
    }

    // A mission grid's bounds, from the placed rooms alone (there is no collision.bin to measure):
    // each room's tile area spans at most (rect size x cell) in both axes about its Pos, whichever way
    // it is turned, so the generous box around that covers every floor cell. Null when there is nothing.
    private static FloorGrid BoundsFromRooms(NavDungeon d, int pf, float cell, out int x0, out int z0, out int w, out int h)
    {
        x0 = z0 = w = h = 0;
        if (d?.Rooms == null || d.Rooms.Count == 0)
        {
            return null;
        }

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var rm in d.Rooms)
        {
            if (rm?.Rect == null || rm.Pos == null)
            {
                continue;
            }

            float ex = (rm.Rect[2] - rm.Rect[0] + 1) * d.Cell, ez = (rm.Rect[3] - rm.Rect[1] + 1) * d.Cell;
            minX = Math.Min(minX, rm.Pos[0] - ex);
            maxX = Math.Max(maxX, rm.Pos[0] + ex);
            minZ = Math.Min(minZ, rm.Pos[2] - ez);
            maxZ = Math.Max(maxZ, rm.Pos[2] + ez);
        }

        if (minX > maxX)
        {
            return null;
        }

        x0 = (int)Math.Floor(minX / cell) - 2;
        z0 = (int)Math.Floor(minZ / cell) - 2;
        w = (int)Math.Ceiling(maxX / cell) + 2 - x0;
        h = (int)Math.Ceiling(maxZ / cell) + 2 - z0;
        if ((long)w * h > 16_000_000)
        {
            return null;
        }

        return new FloorGrid(pf, x0, z0, w, h);
    }

    private void StampFloors(NavCollision col)
    {
        // Dense surface sampling, NOT the cell-centre test: shop floors are tiled from sub-metre
        // triangles on 0.5 m cells, and most cell centres fall in the seams between triangles - the
        // floor map came out Swiss-cheese, routes threaded the holes and the walk crossed real
        // walls (Neutral Supermarket 1187, 2026-10-02: three yanks inside the shop door). Sampling
        // the surface at 0.25 m stamps every cell the triangle touches, seams included.
        var raw = new Dictionary<int, List<float>>();
        foreach (var ch in col.Chunks)
        {
            float[] v = ch.Verts;
            for (int o = 0; o + 8 < v.Length; o += 9)
            {
                float ax = v[o], ay = v[o + 1], az = v[o + 2], bx = v[o + 3], by = v[o + 4], bz = v[o + 5], cx = v[o + 6], cy = v[o + 7], cz = v[o + 8];
                var longest = Math.Max(Len(bx - ax, by - ay, bz - az),
                    Math.Max(Len(cx - ax, cy - ay, cz - az), Len(cx - bx, cy - by, cz - bz)));
                int n = Math.Min(400, Math.Max(1, (int)Math.Ceiling(longest / 0.25f)));
                for (int i = 0; i <= n; i++)
                {
                    for (int j = 0; j <= n - i; j++)
                    {
                        float s = i / (float)n, t = j / (float)n;
                        float px = ax + (bx - ax) * s + (cx - ax) * t;
                        float py = ay + (by - ay) * s + (cy - ay) * t;
                        float pz = az + (bz - az) * s + (cz - az) * t;
                        int ci = CellX(px), cj = CellZ(pz);
                        if (!In(ci, cj))
                        {
                            continue;
                        }

                        int k = cj * _w + ci;
                        if (!raw.TryGetValue(k, out var l))
                        {
                            raw[k] = l = new List<float>();
                        }

                        l.Add(py);
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

            if (merged.Count > MaxFloors)
            {
                merged.RemoveRange(0, merged.Count - MaxFloors);
            }

            _floors[kv.Key] = merged.ToArray();
        }
    }

    // The room-tile floors (rooms.json): an interior's own floor lives in the room data, and
    // collision.bin only carries the sparse extras around it (stairs, props, an upper gallery).
    // Without the room tiles the indoor grid is mostly void and routes walk through real walls
    // (Neutral Supermarket 1187, 2026-10-02: three yanks inside the shop door - the landing and
    // the whole line to the vendor had no floor at the y the server had just put us on).
    private void StampRoomFloors(NavDungeon dungeon)
    {
        if (dungeon?.Rooms == null)
        {
            return;
        }

        foreach (var rm in dungeon.Rooms)
        {
            if (rm?.Tile == null || rm.Rect == null || rm.Pos == null || rm.Height == null)
            {
                continue;
            }

            int x1 = rm.Rect[0], z1 = rm.Rect[1], x2 = rm.Rect[2], z2 = rm.Rect[3];
            double mx = (x1 + x2 + 1) / 2.0, mz = (z1 + z2 + 1) / 2.0;
            var turns = ((-rm.Rot) % 4 + 4) % 4;
            for (int r = 0; r < rm.Tile.Length; r++)
            {
                for (int c = 0; c < rm.Tile[r].Length; c++)
                {
                    if (rm.Tile[r][c] == 0)
                    {
                        continue; // no tile: no floor
                    }

                    // tile (c, r) centre in world coordinates - the inverse of CellOf's rotation
                    double dx = (x1 + c - mx) * dungeon.Cell;
                    double dz = (z1 + r - mz) * dungeon.Cell;
                    for (int i = 0; i < turns; i++)
                    {
                        (dx, dz) = (-dz, dx);
                    }

                    double wx = rm.Pos[0] + dx, wz = rm.Pos[2] + dz;
                    float h = rm.Pos[1] + (rm.Height[r][c] - rm.HeightBase) * dungeon.HeightScale;

                    // the tile is dungeon.Cell (2 m) square centred there: stamp its sub-cells
                    int i0 = CellX((float)(wx - dungeon.Cell / 2)), i1 = CellX((float)(wx + dungeon.Cell / 2));
                    int j0 = CellZ((float)(wz - dungeon.Cell / 2)), j1 = CellZ((float)(wz + dungeon.Cell / 2));
                    for (int j = j0; j <= j1; j++)
                    {
                        for (int i = i0; i <= i1; i++)
                        {
                            if (!In(i, j))
                            {
                                continue;
                            }

                            int k = j * _w + i;
                            _tileStamp.Add((k, h));
                            if (!_floors.TryGetValue(k, out var fl))
                            {
                                _floors[k] = new[] { h };
                                continue;
                            }

                            var folded = false;
                            foreach (var f in fl)
                            {
                                if (Math.Abs(f - h) <= Merge)
                                {
                                    folded = true;
                                    break;
                                }
                            }

                            if (!folded && fl.Length < MaxFloors)
                            {
                                var merged = new float[fl.Length + 1];
                                Array.Copy(fl, merged, fl.Length);
                                merged[fl.Length] = h;
                                Array.Sort(merged);
                                _floors[k] = merged;
                            }
                        }
                    }
                }
            }
        }
    }

    // Cells a room doorway covers: walls there are door leaves and frames, not walls - StampWalls
    // leaves them open. Across the door: the ~2 m passage (the frame lands the centre line true, the
    // jambs at ±1.25 m stay blocked, so the opening threads like a doorway, not a hole in a wall).
    // ALONG the door's normal: the threshold strip is untiled in BOTH rooms' data (it belongs to the
    // wall - 1187's section doors had a ~2.5 m void where the floor should be), so the doorway also
    // BRIDGES floor at the door's height across that strip; the long reach along the normal never
    // approaches the jambs. Verified cell-by-cell against 1187 Neutral Supermarket Advanced.
    private const float DoorwayAcross = 1.25f;
    private const float DoorwayAlong = 3.5f;

    private readonly HashSet<int> _doorways = new HashSet<int>();

    // The doorway world centres and normals (static rooms): the pathfinder walks a crossing route
    // through the doorway's exact centre (InsertDoorWaypoints).
    private List<AOBuddyNav.Doorway> _doorwayList = new List<AOBuddyNav.Doorway>();

    private void StampDoorways(List<AOBuddyNav.Doorway> doorways)
    {
        _doorwayList = doorways;
        foreach (var dw in doorways)
        {
            double ax = Math.Abs(dw.Nx) > 0.5 ? DoorwayAlong : DoorwayAcross;
            double az = Math.Abs(dw.Nz) > 0.5 ? DoorwayAlong : DoorwayAcross;
            int i0 = CellX((float)(dw.X - ax)), i1 = CellX((float)(dw.X + ax));
            int j0 = CellZ((float)(dw.Z - az)), j1 = CellZ((float)(dw.Z + az));
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    if (!In(i, j))
                    {
                        continue;
                    }

                    int k = j * _w + i;
                    _doorways.Add(k);

                    // the threshold bridge: floor at the door's height where neither room has tiles
                    if (!_floors.TryGetValue(k, out var fl))
                    {
                        _floors[k] = new[] { (float)dw.Y };
                    }
                    else
                    {
                        var folded = false;
                        foreach (var f in fl)
                        {
                            if (Math.Abs(f - dw.Y) <= Merge)
                            {
                                folded = true;
                                break;
                            }
                        }

                        if (!folded && fl.Length < MaxFloors)
                        {
                            var merged = new float[fl.Length + 1];
                            Array.Copy(fl, merged, fl.Length);
                            merged[fl.Length] = (float)dw.Y;
                            Array.Sort(merged);
                            _floors[k] = merged;
                        }
                    }
                }
            }
        }
    }

    // ---- REAL 3D (composed missions): levels and walls from the placed triangle soup --------------
    //
    // The pool's walls.bin + collision triangles are the instance's structural truth: shells, door
    // frames, stairs, ramps, mezzanines (furniture is render data and was never exported - no
    // version of this data knows a crate is there). Instead of stamping steep triangles onto cells
    // (which sealed pockets and fought the doorways), every walkable-slope triangle CONTRIBUTES a
    // floor level sampled at each 0.5 m sub-cell it passes over - a ramp cell gets its true height
    // - and every steep triangle only ever forbids the specific EDGE it physically crosses: the
    // body band between the two step heights, sampled along the step. Nothing is blocked
    // wholesale, so there are no pockets to keep open and double/triple height rooms are simply
    // several levels, each with its own honest edges.

    private const float WalkSlopeNy = 0.75f; // flatter than ~41 degrees is a surface, steeper a wall
    private const float WallBucket = 4f;

    private readonly HashSet<long> _noEdge = new HashSet<long>(); // (cell*8+floor)*32 + dir*8 + floor
    private readonly Dictionary<long, float> _wallHug = new Dictionary<long, float>(); // missions: cells within a step of a wall cost extra
    private readonly Dictionary<long, List<float[]>> _wallTris = new Dictionary<long, List<float[]>>();

    // THE NORTHBOUND HYDRATE (the precalculated pool grids): each placed room's lattice is
    // rotated/translated into world cells as a pure index remap - every lattice cell centre goes
    // through the same transform the geometry does (PlaceBin's), so the blit is exact by
    // construction. Blocked cells mark all their levels; the headroom rule applies; doorway
    // cells stay open (doors are walkable by default - a LOCKED door, from DoorFullUpdate, must
    // gate its portal at runtime). No triangle sampling, no tiles, no burial: the mesh was
    // pre-sampled offline at 20 cm.
    public static int DebugNorthCells;

    public void UseNorthbound(NorthboundPool north, AOBuddyNav nav)
    {
        _maxStep = MissionStep;
        var blitted = 0;
        var pf = nav.Layout.TemplatePlayfield;
        NavDungeon pool;
        try
        {
            pool = NavDungeon.Read(Path.Combine(AOBuddyNav.FolderFor(_pluginDir, pf), "rooms.json"));
        }
        catch
        {
            return;
        }

        if (pool?.Rooms == null)
        {
            return;
        }

        var cellBlocked = new List<(int k, int count)>();

        // ONE mesh shift for the whole pool: the atlas mesh is a single coherent frame (all its
        // rooms sit in it together), so every room's mesh moves by the same vector - the average
        // of its rooms' (door Y − portal mesh level). A per-room shift would tear the mesh
        // structures apart at the seams.
        var shiftSamples = new List<double>();
        foreach (var mr in nav.Dungeon.Rooms)
        {
            if (mr.PoolIndex < 0 || !north.Rooms.TryGetValue(mr.PoolIndex, out var lat0))
            {
                continue;
            }

            var myDoors = nav.MissionDoorways.Where(d => d.Room == mr.Index).ToList();
            var anchors = lat0.Portals.Where(p => !float.IsNaN(p.MeshLevel)).ToList();
            if (myDoors.Count > 0 && anchors.Count > 0)
            {
                shiftSamples.Add(myDoors.Average(d => d.Y) - anchors.Average(p => (double)p.MeshLevel));
            }
        }

        var poolMeshShift = shiftSamples.Count > 0 ? shiftSamples.Average() : 0.0;

        foreach (var mr in nav.Dungeon.Rooms)
        {
            if (mr.PoolIndex < 0 || mr.Pos == null || mr.PoolIndex >= pool.Rooms.Count)
            {
                continue;
            }

            var pr = pool.Rooms[mr.PoolIndex];
            if (pr?.Pos == null || !north.Rooms.TryGetValue(mr.PoolIndex, out var lat))
            {
                continue;
            }

            var g = mr.GeomPos ?? mr.Pos;

            // Tile levels absolute; mesh levels carry the POOL-WIDE door-anchored shift.
            var meshShift = poolMeshShift;

            int turns = ((((-pr.Rot) % 4 + 4) % 4) - (((-mr.Rot) % 4 + 4) % 4) + 4) % 4;
            void Turn(ref double x, ref double z)
            {
                for (int t = 0; t < turns; t++)
                {
                    (x, z) = (z, -x);
                }
            }

            // the room's chunks carry atlas spill far beyond the room itself; a lattice cell
            // belongs to this room where the pool's own tile data says floor - portal cells
            // excepted (they reach into the doorway seam)
            var portalCells = new HashSet<int>();
            foreach (var p in lat.Portals)
            {
                foreach (var (pcx, pcy, _) in p.Cells)
                {
                    portalCells.Add(pcy * lat.W + pcx);
                }
            }

            for (int j = 0; j < lat.H; j++)
            {
                for (int i = 0; i < lat.W; i++)
                {
                    var levels = lat.Levels[j * lat.W + i];
                    if (levels == null || levels.Count == 0)
                    {
                        continue;
                    }

                    double cx = lat.Ox + (i + 0.5f) * north.Cell, cz = lat.Oz + (j + 0.5f) * north.Cell;
                    var isPortal = portalCells.Contains(j * lat.W + i);
                    if (!isPortal && double.IsNaN(pool.FloorHeight(pr, cx, cz)))
                    {
                        continue; // atlas spill: not this room's floor
                    }

                    double dx = cx - pr.Pos[0], dz = cz - pr.Pos[2];
                    Turn(ref dx, ref dz);
                    var k = Key((float)(g[0] + dx), (float)(g[2] + dz));
                    if (k < 0)
                    {
                        continue;
                    }

                    // MERGE into what earlier rooms left - spill must not clobber a neighbour.
                    // Tile levels stay absolute; mesh levels carry the door-anchored shift.
                    var incoming = new List<float>(levels.Count + (lat.MeshLevels[j * lat.W + i]?.Count ?? 0));
                    foreach (var v in levels)
                    {
                        incoming.Add(v);
                    }

                    var ml = lat.MeshLevels[j * lat.W + i];
                    if (ml != null)
                    {
                        foreach (var v in ml)
                        {
                            incoming.Add((float)(v + meshShift));
                        }
                    }

                    incoming.Sort();
                    if (!_floors.TryGetValue(k, out var fl))
                    {
                        fl = incoming.ToArray();
                    }
                    else
                    {
                        var mergedList = new List<float>(fl);
                        mergedList.AddRange(incoming);
                        mergedList.Sort();
                        for (var x = mergedList.Count - 1; x > 0; x--)
                        {
                            if (mergedList[x] - mergedList[x - 1] < 0.25f)
                            {
                                mergedList.RemoveAt(x);
                            }
                        }

                        if (mergedList.Count > MaxFloors)
                        {
                            mergedList.RemoveRange(0, mergedList.Count - MaxFloors);
                        }

                        fl = mergedList.ToArray();
                    }

                    _floors[k] = fl;
                    blitted++;
                    if (lat.Hug is { Length: > 0 } && i < lat.W && j < lat.H)
                    {
                        // graded wall-hug: the closer the cell sits to a wall/column, the more a
                        // step through it costs - routes tend to the centres of rooms/hallways
                        var penalty = WallHugPenalty * lat.Hug[j * lat.W + i] / 255f;
                        for (int f = 0; f < fl.Length; f++)
                        {
                            _wallHug[(long)k * 8 + f] = penalty;
                        }
                    }

                    if (lat.Blocked[j * lat.W + i])
                    {
                        cellBlocked.Add((k, fl.Length));
                    }
                }
            }
        }

        StampHeadroom();
        DebugNorthCells = blitted;
        foreach (var (k, count) in cellBlocked)
        {
            for (int f = 0; f < count && f < MaxFloors; f++)
            {
                _blocked.Add((long)k * 8 + f);
            }
        }

        foreach (var k in _doorways)
        {
            if (!_floors.TryGetValue(k, out var fl))
            {
                continue;
            }

            for (int f = 0; f < fl.Length; f++)
            {
                _blocked.Remove((long)k * 8 + f);
            }
        }
    }

    public void UseComposedGeometry(float[] walls, float[] surfaces, NavDungeon rooms)
    {
        // collision.bin first: it carries the walkable truth (54,400 flat + 1,549 ramp triangles in
        // pool 320 alone); walls.bin is steep-only and would sample nothing. Every triangle is still
        // classified by its own normal, so a misfiled one lands where it belongs either way. The
        // room tiles fold INTO the mesh's heights (the mesh's ramp footing must survive the fold),
        // then the walls are bucketed, the buried tile floors go, and the edge verdicts run on the
        // final floor arrays.
        _maxStep = MissionStep;
        Classify(surfaces);
        if (!DebugSkipRoomTiles)
        {
            StampRoomFloors(rooms);
        }

        Classify(walls);
        SuppressBuriedTiles();
        BuildEdges();
        BuildWallHugCost();
    }

    // The room-tile stamp lays its flat floor into EVERY tiled cell - including the cells the placed
    // mesh built a stairway on (the Subway ramp room, 2026-10-04: the tile's flat floor under every
    // tread, and a floor-10 line straight under the whole stairway reading "clear" - the A* walked
    // the bot INTO the stairs and the server yanked it back out). So after the mesh is in, every
    // tile-stamped floor that has the mesh's own structure - its steep triangles: risers, soffits,
    // stringers - crossing the body band right above it is buried: not a floor at all. The tread
    // ladder stays, the ramp is the way up, and its low end sits within MissionStep of the room
    // floor, so the climb starts where the stairs start. A floor under open air keeps standing -
    // the deck 8 m over the Subway floor never touches the band, and the server has confirmed
    // under-deck walking (2026-10-04 01:00 run: SetPos held the body at floor level beneath it).
    // The test is the tight half-cell cross through the cell centre, so a floor beside a wall
    // survives, and only tile-stamped floors can die - mesh floors and doorway bridges never.
    private void SuppressBuriedTiles()
    {
        if (_tileStamp.Count == 0)
        {
            return;
        }

        var buried = new List<(int k, float h)>();
        foreach (var kv in _floors)
        {
            int k = kv.Key;
            if (_doorways.Contains(k))
            {
                continue; // a doorway cell: the frame triangles over its floor are the door, not a wall
            }

            float cx = (k % _w + _x0 + 0.5f) * Cell, cz = (k / _w + _z0 + 0.5f) * Cell;
            foreach (float f in kv.Value)
            {
                if (!_tileStamp.Contains((k, f)))
                {
                    continue;
                }

                if (LineHitsWall(cx - 0.25f, cz, cx + 0.25f, cz, _ => f) ||
                    LineHitsWall(cx, cz - 0.25f, cx, cz + 0.25f, _ => f))
                {
                    // Bury only where the mesh offers its own walkway at this height - the stairway's
                    // treads, whose low end sits within MissionStep of the tile (that is the climb's
                    // start). A floor with nothing mesh-built within a step of it is the room's floor
                    // under a ceiling, a lintel or an upper storey's rim: the edge tests already
                    // forbid the crossings, and burying the floor instead cuts the room in two (Grey
                    // Caves 2026-10-08: Startroom_Medium8_2's whole midriff buried under its own upper
                    // deck, all 8 of its doorways sealed off one another, 61 of 274 door->door routes
                    // dead).
                    if (_meshStamp.Any(t => t.k == k && Math.Abs(t.h - f) <= MissionStep))
                    {
                        buried.Add((k, f));
                    }
                }
            }
        }

        DebugBuriedCells = buried.Count;
        foreach (var (k, f) in buried)
        {
            _tileStamp.Remove((k, f));
            var kept = _floors[k].Where(v => v != f).ToArray(); // exact float: the stored value
            if (kept.Length == 0)
            {
                _floors.Remove(k);
            }
            else
            {
                _floors[k] = kept;
            }
        }
    }

    // Routes through the middle of the room: cells whose body band stands within a step of a
    // wall cost extra per step, so the A* only hugs walls when the geometry leaves no choice.
    // Missions only - the wall buckets exist only where UseComposedGeometry ran.
    private const float WallHugPenalty = 1.5f;

    private void BuildWallHugCost()
    {
        foreach (var kv in _floors)
        {
            int k = kv.Key;
            float x = (k % _w + _x0 + 0.5f) * Cell, z = (k / _w + _z0 + 0.5f) * Cell;
            foreach (var probe in new[] { (0f, 0f), (0.6f, 0f), (-0.6f, 0f), (0f, 0.6f), (0f, -0.6f) })
            {
                for (int f = 0; f < kv.Value.Length; f++)
                {
                    var h = kv.Value[f];
                    if (WallHits(x + probe.Item1, z + probe.Item2, h + 0.3f, h + 1.9f))
                    {
                        _wallHug[(long)k * 8 + f] = WallHugPenalty;
                    }
                }
            }
        }
    }

    private void Classify(float[] v)
    {
        if (v == null)
        {
            return;
        }

        for (int o = 0; o + 8 < v.Length; o += 9)
        {
            float ux = v[o + 3] - v[o], uy = v[o + 4] - v[o + 1], uz = v[o + 5] - v[o + 2];
            float wx = v[o + 6] - v[o], wy = v[o + 7] - v[o + 1], wz = v[o + 8] - v[o + 2];
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9f)
            {
                continue;
            }

            if (ny / len >= WalkSlopeNy)
            {
                SampleSurface(v, o);
            }
            else
            {
                BucketWall(v, o);
            }
        }
    }

    // The triangle is walkable ground: EVERY 0.5 m cell its projection touches gets a level at the
    // triangle's own height there - that is how a ramp becomes a slope instead of one 2 m tile step.
    // The old 0.4 m point lattice sampled the triangle too: a sliver between sample points left its
    // cells empty, and 1-2 cell cracks shattered continuous rock into small islands the A* dies at
    // (Grey Caves' item rock, owner 2026-10-03: y 7.2 unreachable from the 5.0 floor world,
    // "explored 11535 cells" - and ~100 from above).
    private void SampleSurface(float[] v, int o)
    {
        float x0 = Math.Min(v[o], Math.Min(v[o + 3], v[o + 6])), x1 = Math.Max(v[o], Math.Max(v[o + 3], v[o + 6]));
        float z0 = Math.Min(v[o + 2], Math.Min(v[o + 5], v[o + 8])), z1 = Math.Max(v[o + 2], Math.Max(v[o + 5], v[o + 8]));
        for (int j = CellZ(z0); j <= CellZ(z1); j++)
        {
            for (int i = CellX(x0); i <= CellX(x1); i++)
            {
                if (!In(i, j))
                {
                    continue;
                }

                float cx = (i + _x0 + 0.5f) * Cell, cz = (j + _z0 + 0.5f) * Cell;
                if (!TriClosest(v, o, cx, cz, out var px, out var pz) ||
                    px < (i + _x0) * Cell || px >= (i + 1 + _x0) * Cell ||
                    pz < (j + _z0) * Cell || pz >= (j + 1 + _z0) * Cell)
                {
                    continue; // the triangle never reaches this cell
                }

                if (TriContains(v, o, px, pz, out var h))
                {
                    AddLevel(j * _w + i, h);
                }
            }
        }
    }

    // The closest point of the triangle's projection to (px, pz); false for a degenerate triangle.
    private static bool TriClosest(float[] v, int o, float px, float pz, out float qx, out float qz)
    {
        if (TriContains(v, o, px, pz, out _))
        {
            qx = px;
            qz = pz;
            return true;
        }

        float ax = v[o], az = v[o + 2], bx = v[o + 3], bz = v[o + 5], cx = v[o + 6], cz = v[o + 8];
        qx = qz = 0f;
        var best = float.MaxValue;
        ClosestOnEdge(ax, az, bx, bz, px, pz, ref qx, ref qz, ref best);
        ClosestOnEdge(bx, bz, cx, cz, px, pz, ref qx, ref qz, ref best);
        ClosestOnEdge(cx, cz, ax, az, px, pz, ref qx, ref qz, ref best);
        return best < float.MaxValue;
    }

    private static void ClosestOnEdge(float sx, float sz, float ex, float ez, float px, float pz,
        ref float qx, ref float qz, ref float best)
    {
        float dx = ex - sx, dz = ez - sz, len2 = dx * dx + dz * dz;
        float t = len2 < 1e-12f ? 0f : Math.Clamp(((px - sx) * dx + (pz - sz) * dz) / len2, 0f, 1f);
        float x = sx + dx * t, z = sz + dz * t;
        float d = (px - x) * (px - x) + (pz - z) * (pz - z);
        if (d < best)
        {
            best = d;
            qx = x;
            qz = z;
        }
    }

    private void BucketWall(float[] v, int o)
    {
        var tri = new float[9];
        Array.Copy(v, o, tri, 0, 9);
        float x0 = Math.Min(tri[0], Math.Min(tri[3], tri[6])), x1 = Math.Max(tri[0], Math.Max(tri[3], tri[6]));
        float z0 = Math.Min(tri[2], Math.Min(tri[5], tri[8])), z1 = Math.Max(tri[2], Math.Max(tri[5], tri[8]));
        int bx0 = (int)Math.Floor(x0 / WallBucket), bx1 = (int)Math.Floor(x1 / WallBucket);
        int bz0 = (int)Math.Floor(z0 / WallBucket), bz1 = (int)Math.Floor(z1 / WallBucket);
        for (int bz = bz0; bz <= bz1; bz++)
        {
            for (int bx = bx0; bx <= bx1; bx++)
            {
                long key = ((long)(bx + 4096) << 16) | (uint)(bz + 4096);
                if (!_wallTris.TryGetValue(key, out var list))
                {
                    _wallTris[key] = list = new List<float[]>();
                }

                list.Add(tri);
            }
        }
    }

    private void AddLevel(int k, float h)
    {
        _meshStamp.Add((k, h));
        if (!_floors.TryGetValue(k, out var fl))
        {
            _floors[k] = new[] { h };
            return;
        }

        foreach (var f in fl)
        {
            if (Math.Abs(f - h) <= Merge)
            {
                return;
            }
        }

        if (fl.Length >= MaxFloors)
        {
            return;
        }

        var merged = new float[fl.Length + 1];
        Array.Copy(fl, merged, fl.Length);
        merged[fl.Length] = h;
        Array.Sort(merged);
        _floors[k] = merged;
    }

    // Judge every step the tiles and surfaces make possible, once: the body band between the two
    // step heights, sampled along the step, against the steep triangles.
    private void BuildEdges()
    {
        int[][] dirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
        foreach (var kv in _floors)
        {
            int k = kv.Key, i = k % _w, j = k / _w;
            for (var d = 0; d < 4; d++)
            {
                int ni = i + dirs[d][0], nj = j + dirs[d][1];
                if (!In(ni, nj) || !_floors.TryGetValue(nj * _w + ni, out var nfl))
                {
                    continue;
                }

                int nk = nj * _w + ni;
                for (int f = 0; f < kv.Value.Length; f++)
                {
                    for (int nf = 0; nf < nfl.Length; nf++)
                    {
                        if (Math.Abs(nfl[nf] - kv.Value[f]) > _maxStep)
                        {
                            continue;
                        }

                        if (!EdgeClear(i, j, kv.Value[f], ni, nj, nfl[nf]))
                        {
                            _noEdge.Add(((long)k * 8 + f) * 32 + d * 8 + nf);
                        }
                    }
                }
            }
        }
    }

    private bool EdgeClear(int i, int j, float ha, int ni, int nj, float hb)
    {
        double x0 = (i + 0.5f + _x0) * Cell, z0 = (j + 0.5f + _z0) * Cell;
        double x1 = (ni + 0.5f + _x0) * Cell, z1 = (nj + 0.5f + _z0) * Cell;
        return !LineHitsWall(x0, z0, x1, z1, t => ha + (hb - ha) * t);
    }

    // EXACT wall clearance over a walk line (owner, 2026-10-03: "pathfinds but runs at/through
    // walls"): the point-sampled WallHits was blind to a perfectly vertical wall - its ground
    // projection is a zero-width sliver no sample ever lands inside (TriContains needs area). The
    // line is tested against the wall triangles NEAR it by segment-edge crossings instead
    // (NavCollision.TriBlocksLine): a crossing IS the wall, wherever the wall stands. h(t) is the
    // floor height along the line; the body band rides on it.
    private bool LineHitsWall(double x0, double z0, double x1, double z1, Func<double, double> h)
    {
        if (_wallTris.Count == 0)
        {
            return false;
        }

        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        if (len < 1e-6)
        {
            return false;
        }

        var seen = new HashSet<float[]>();
        int steps = Math.Max(1, (int)Math.Ceiling(len / (WallBucket / 2)));
        for (var s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            int bx = (int)Math.Floor((x0 + (x1 - x0) * t) / WallBucket), bz = (int)Math.Floor((z0 + (z1 - z0) * t) / WallBucket);
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_wallTris.TryGetValue(((long)(bx + dx + 4096) << 16) | (uint)(bz + dz + 4096), out var list))
                    {
                        continue;
                    }

                    foreach (var tri in list)
                    {
                        if (seen.Add(tri) && NavCollision.TriBlocksLine(tri, 0, x0, z0, x1, z1, h, BodyLow, BodyHigh))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
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

    private bool WallHits(float x, float z, float low, float high)
    {
        int bx = (int)Math.Floor(x / WallBucket), bz = (int)Math.Floor(z / WallBucket);
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_wallTris.TryGetValue(((long)(bx + dx + 4096) << 16) | (uint)(bz + dz + 4096), out var list))
                {
                    continue;
                }

                foreach (var tri in list)
                {
                    if (TriContains(tri, 0, x, z, out var hT) && hT >= low && hT <= high)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TriContains(float[] v, int o, float x, float z, out float h)
    {
        h = 0f;
        float ax = v[o], az = v[o + 2], bx = v[o + 3], bz = v[o + 5], cx = v[o + 6], cz = v[o + 8];
        float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(d) < 1e-9f)
        {
            return false;
        }

        float l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
        float l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
        float l3 = 1 - l1 - l2;
        if (l1 < -0.001f || l2 < -0.001f || l3 < -0.001f)
        {
            return false;
        }

        h = l1 * v[o + 1] + l2 * v[o + 4] + l3 * v[o + 7];
        return true;
    }

    private bool EdgeBlocked(int k, int f, int di, int dj, int nf)
    {
        int d = dj == 0 ? (di > 0 ? 0 : 1) : (dj > 0 ? 2 : 3);
        return _noEdge.Contains(((long)k * 8 + f) * 32 + d * 8 + nf);
    }

    private void StampWalls(NavCollision walls)
    {
        foreach (var ch in walls.Chunks)
        {
            float[] v = ch.Verts;
            for (int o = 0; o + 8 < v.Length; o += 9)
            {
                float ax = v[o], ay = v[o + 1], az = v[o + 2];
                float ux = v[o + 3] - ax, uy = v[o + 4] - ay, uz = v[o + 5] - az;
                float wx = v[o + 6] - ax, wy = v[o + 7] - ay, wz = v[o + 8] - az;
                float longest = Math.Max(Len(ux, uy, uz), Math.Max(Len(wx, wy, wz), Len(wx - ux, wy - uy, wz - uz)));
                int n = Math.Min(400, Math.Max(1, (int)Math.Ceiling(longest / 0.25f)));
                for (int i = 0; i <= n; i++)
                {
                    for (int j = 0; j <= n - i; j++)
                    {
                        float s = i / (float)n, t = j / (float)n;
                        float px = ax + ux * s + wx * t, py = ay + uy * s + wy * t, pz = az + uz * s + wz * t;
                        int k = Key(px, pz);
                        if (k < 0 || !_floors.TryGetValue(k, out var fl) || _doorways.Contains(k))
                        {
                            continue; // a doorway cell: walls here are the door, not a wall
                        }

                        for (int f = 0; f < fl.Length; f++)
                        {
                            float above = py - fl[f];
                            if (above >= BodyLow && above <= BodyHigh)
                            {
                                _blocked.Add((long)k * 8 + f);
                            }
                        }
                    }
                }
            }
        }
    }

    // A floor with another one just above it (under a deck, inside a slab) is no place to stand.
    private void StampHeadroom()
    {
        foreach (var kv in _floors)
        {
            for (int f = 0; f + 1 < kv.Value.Length; f++)
            {
                if (kv.Value[f + 1] - kv.Value[f] < BodyHigh)
                {
                    _blocked.Add((long)kv.Key * 8 + f);
                }
            }
        }
    }

    private static float Len(float x, float y, float z) => (float)Math.Sqrt(x * x + y * y + z * z);

    // ---- queries ------------------------------------------------------------------------------------------

    private int CellX(float x) => (int)Math.Floor(x / Cell) - _x0;
    private int CellZ(float z) => (int)Math.Floor(z / Cell) - _z0;
    private bool In(int i, int j) => i >= 0 && j >= 0 && i < _w && j < _h;
    private int Key(float x, float z)
    {
        int i = CellX(x), j = CellZ(z);
        return In(i, j) ? j * _w + i : -1;
    }

    private Vector3 Centre(int k, float y) => new((k % _w + _x0 + 0.5f) * Cell, y, (k / _w + _z0 + 0.5f) * Cell);

    // The standable floor in cell k nearest height y, within tol; -1 when none.
    private int FloorAt(int k, float y, float tol, HashSet<int> extra)
    {
        if (k < 0 || (extra != null && extra.Contains(k)) || !_floors.TryGetValue(k, out var fl))
        {
            return -1;
        }

        int best = -1;
        float bd = tol;
        for (int f = 0; f < fl.Length; f++)
        {
            float d = Math.Abs(fl[f] - y);
            if (d <= bd && !_blocked.Contains((long)k * 8 + f))
            {
                bd = d;
                best = f;
            }
        }

        return best;
    }

    private float Height(long node) => _floors[(int)(node / 8)][(int)(node % 8)];

    /// <summary>Standable ground at p (a floor within 3 m of p.Y) — the front-ray test for doorway exits.</summary>
    public bool OpenAt(Vector3 p) => FloorAt(Key(p.X, p.Z), p.Y, 3f, null) >= 0;

    // NO FALL DAMAGE anywhere in AO (owner, 2026-10-04): a floor to LAND on below the current one -
    // the topmost unblocked floor of the cell strictly under the step band (floors ascend, so the
    // scan from the top finds the first surface a fall would hit). -1 when there is nothing to
    // stand on beneath (open air, or only the level we are leaving).
    private int LandingFloor(int k, float y, HashSet<int> extra)
    {
        if (k < 0 || (extra != null && extra.Contains(k)) || !_floors.TryGetValue(k, out var fl))
        {
            return -1;
        }

        for (int f = fl.Length - 1; f >= 0; f--)
        {
            if (fl[f] < y - _maxStep && !_blocked.Contains((long)k * 8 + f))
            {
                return f;
            }
        }

        return -1;
    }

    // The floor a walker takes in cell k from height y: the nearest step, or, none being within
    // reach, the landing below the edge (see LandingFloor). Climbs stay out of reach.
    private int StandFloor(int k, float y, HashSet<int> extra)
    {
        int f = FloorAt(k, y, _maxStep, extra);
        return f >= 0 ? f : LandingFloor(k, y, extra);
    }

    /// <summary>
    ///     Walkable ground within tol of y at (x, z)? The outside view of the grid for renderers and
    ///     tools: the same FloorAt verdict the pathfinder walks by (a floor exists there AND no wall,
    ///     no headroom rule, no doorway exception pending - the doorway keep-open is already baked in).
    /// </summary>
    public bool WalkableAt(float x, float z, float y, float tol) => FloorAt(Key(x, z), y, tol, null) >= 0;

    /// <summary>
    ///     The raw floor levels recorded for the cell at (x, z) - what SampleSurface, the room tiles
    ///     and the doorway bridges stacked there - for probes and renderers. Empty when the cell is
    ///     void. The per-level blocked verdict is not included; WalkableAt answers that for a height.
    /// </summary>
    public float[] FloorsAt(float x, float z)
    {
        int k = Key(x, z);
        return k >= 0 && _floors.TryGetValue(k, out var fl) ? fl : Array.Empty<float>();
    }

    // Probe-only provenance of one cell's floor stack: for every floor height, where it came from
    // and whether the grounding pass marked it. 'T' tile stamp, 'M' placed mesh, 'D' doorway
    // bridge (neither). missionx "cell" prints it.
    public string DebugCell(float x, float z)
    {
        int k = Key(x, z);
        if (k < 0 || !_floors.TryGetValue(k, out var fl))
        {
            return "void";
        }

        var parts = new List<string>();
        foreach (float f in fl)
        {
            var kind = _tileStamp.Contains((k, f)) ? 'T' : _meshStamp.Contains((k, f)) ? 'M' : _doorways.Contains(k) ? 'D' : '?';
            parts.Add($"{f:0.###}{kind}");
        }

        var tiles = _tileStamp.Where(t => t.k == k).Select(t => t.h.ToString("0.###")).ToList();
        var mesh = _meshStamp.Where(t => t.k == k).Select(t => t.h.ToString("0.###")).ToList();
        return $"cell ({(k % _w + _x0) * Cell:0.##},{(k / _w + _z0) * Cell:0.##}): floors [{string.Join(" ", parts)}]" +
               $", tile stamps [{string.Join(" ", tiles)}], mesh stamps [{string.Join(" ", mesh)}]";
    }

    /// <summary>The floor index FloorAt would pick at (x, z) for height y within tol; -1 when none.</summary>
    public int FloorIndexAt(float x, float z, float y, float tol)
    {
        int k = Key(x, z);
        return k < 0 ? -1 : FloorAt(k, y, tol, null);
    }

    /// <summary>
    ///     Can the body walk the STRAIGHT line a->b in the real geometry, the grid's verdict aside?
    ///     The blocked-verdict fallback (owner, 2026-10-03): a plan that found no route may still be
    ///     a straight walk the cells never saw. The line walks when there is floor to stand on every
    ///     step (each within MaxStep of the one before, or a landing below it - no fall damage, so
    ///     the line may drop off an edge - but no leaving the platform sideways and no climbing)
    ///     and no wall crosses the body band - EXACT segment-edge crossings, so a vertical wall is
    ///     caught. Missions judge by their composed wall triangles; static dungeons by their
    ///     walls.bin (loaded for the grid at build, or from the folder on first need); anything else
    ///     answers false - nothing in memory can vouch for the line.
    /// </summary>
    public bool GeometryLine(Vector3 a, Vector3 b, out string why)
    {
        why = "";
        if (_wallTris.Count == 0)
        {
            _walls ??= LazyWalls(); // a cache-loaded grid loads its walls.bin on first need
            if (_walls == null)
            {
                why = "no wall triangles in memory - the line cannot be vouched for";
                return false;
            }
        }

        int startK = Key(a.X, a.Z);
        int start = FloorAt(startK, a.Y, 2.5f, null);
        if (start < 0)
        {
            why = $"no floor within 2.5 m of me at height {a.Y:0}";
            return false;
        }

        double dx = b.X - a.X, dz = b.Z - a.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.01)
        {
            return true;
        }

        int n = Math.Max(1, (int)Math.Ceiling(len / 0.25));
        var floors = new List<float>(n + 1) { _floors[startK][start] };
        float y = floors[0];
        for (var s = 1; s <= n; s++)
        {
            double t = (double)s / n;
            int k = Key((float)(a.X + dx * t), (float)(a.Z + dz * t));
            int f = StandFloor(k, y, null);
            if (f < 0)
            {
                why = $"no floor {t * len:0.0} m along the line (a hole or a level change)";
                return false;
            }

            y = _floors[k][f];
            floors.Add(y);
        }

        // missions: the composed buckets; static dungeons: the whole walls.bin through NavCollision's
        bool hit = _wallTris.Count > 0
            ? LineHitsWall(a.X, a.Z, b.X, b.Z, t => Profile(floors, t))
            : _walls.LineBlocked(a.X, a.Z, b.X, b.Z, t => Profile(floors, t), BodyLow, BodyHigh);
        if (hit)
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

    // THE STUCK ESCAPE, indoor form: the same fan of GeometryLine probes (see OverlandGrid). In a
    // building most directions hit a wall within 20 m and the fan returns null - right, because
    // indoors the doorway-splitting A* is the finer instrument and the escape is only for when it
    // said no.
    private const float EscapeReach = 20f;

    public Vector3? Escape(Vector3 from, Vector3 goal, out string why)
    {
        why = "";
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
            double dx = p.X - goal.X, dz = p.Z - goal.Z;
            var score = (float)Math.Sqrt(dx * dx + dz * dz);
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

    public HashSet<int> CellsAlong(Vector3 a, Vector3 b, float radius, HashSet<int> into = null)
    {
        into ??= new HashSet<int>();
        float len = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
        int n = Math.Max(1, (int)Math.Ceiling(len / (Cell / 2)));
        int r = (int)Math.Ceiling(radius / Cell);
        for (int s = 0; s <= n; s++)
        {
            float t = s / (float)n;
            int ci = CellX(a.X + (b.X - a.X) * t), cj = CellZ(a.Z + (b.Z - a.Z) * t);
            for (int dj = -r; dj <= r; dj++)
            {
                for (int di = -r; di <= r; di++)
                {
                    if (In(ci + di, cj + dj) && di * di + dj * dj <= r * r)
                    {
                        into.Add((cj + dj) * _w + ci + di);
                    }
                }
            }
        }

        return into;
    }

    /// <summary>
    ///     A path over the floors from a (on the floor nearest a.Y) to within reach metres of b. b.Y NaN: any
    ///     level; otherwise the end must be on b's level (within 1.5 m). Points carry their floor height.
    ///     A crossed room doorway splits the route (see FindPathCore): approach standoff, the door's
    ///     exact centre, exit standoff, then a FRESH search for the rest - so the walk through a door
    ///     never re-joins whatever line the through-route happened to have.
    /// </summary>
    public List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why)
    {
        return FindPathCore(a, b, extra, snap, reach, null, 0, out why);
    }

    private List<Vector3> FindPathCore(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach,
        HashSet<AOBuddyNav.Doorway> skip, int depth, out string why)
    {
        // DOOR PRECISION: the first crossed doorway (both endpoints on opposite sides of its plane,
        // the straight line passing within 3 m of its centre) splits the route into two freshly
        // planned legs - up to the approach standoff, through the door's exact centre, and from the
        // exit standoff on. Deeper routes handle their remaining doorways the same way (depth cap
        // 4; the skip set keeps a handled doorway from splitting again).
        if (depth < 4 && _doorwayList.Count > 0 && a != default)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var candidates = new List<(AOBuddyNav.Doorway dw, Vector3 before, Vector3 after, double fromA)>();
            foreach (var dw in _doorwayList)
            {
                if (skip != null && skip.Contains(dw))
                {
                    continue;
                }

                double s0 = (a.X - dw.X) * dw.Nx + (a.Z - dw.Z) * dw.Nz;
                double s1 = (b.X - dw.X) * dw.Nx + (b.Z - dw.Z) * dw.Nz;
                if ((s0 > 0) == (s1 > 0) || Math.Abs(s0 - s1) < 1e-9)
                {
                    continue;
                }

                double t = s0 / (s0 - s1);
                double cx = a.X + (b.X - a.X) * t, cz = a.Z + (b.Z - a.Z) * t;
                double dist = Math.Sqrt((cx - dw.X) * (cx - dw.X) + (cz - dw.Z) * (cz - dw.Z));
                if (dist > 3.0)
                {
                    continue;
                }

                var sign = s0 > 0 ? 1 : -1;
                var before = new Vector3((float)(dw.X + dw.Nx * 1.5 * sign), (float)dw.Y, (float)(dw.Z + dw.Nz * 1.5 * sign));
                var after = new Vector3((float)(dw.X - dw.Nx * 1.5 * sign), (float)dw.Y, (float)(dw.Z - dw.Nz * 1.5 * sign));
                candidates.Add((dw, before, after, Math.Sqrt((a.X - dw.X) * (a.X - dw.X) + (a.Z - dw.Z) * (a.Z - dw.Z))));
            }

            foreach (var (dw, before, after, _) in candidates.OrderBy(c => c.fromA))
            {
                skip ??= new HashSet<AOBuddyNav.Doorway>();
                skip.Add(dw);
                var leg1 = FindPathCore(a, before, extra, snap, reach, skip, depth + 1, out _);
                if (leg1 == null)
                {
                    continue; // this doorway does not work for the approach - try the next crossed one
                }

                var leg2 = FindPathCore(after, b, extra, snap, reach, skip, depth + 1, out _);
                if (leg2 == null)
                {
                    continue;
                }

                why = "";
                var full = new List<Vector3>(leg1) { new Vector3((float)dw.X, (float)dw.Y, (float)dw.Z) };
                full.AddRange(leg2.Skip(1));
                return full;
            }
            // no doorway split worked: the plain through-route below still walks
        }

        why = "";
        long start = NearestNode(a, snap, extra);
        if (start < 0)
        {
            why = $"no floor within {snap:0} m of me at height {a.Y:0}";
            return null;
        }

        int bi = CellX(b.X), bj = CellZ(b.Z);
        float rc = Math.Max(reach, Cell) / Cell;
        bool anyLevel = float.IsNaN(b.Y);
        bool AtGoal(long node)
        {
            int k = (int)(node / 8);
            float di = k % _w - bi, dj = k / _w - bj;
            return di * di + dj * dj <= rc * rc && (anyLevel || Math.Abs(Height(node) - b.Y) <= 1.5f);
        }

        var gScore = new Dictionary<long, float> { [start] = 0 };
        var parent = new Dictionary<long, long>();
        var closed = new HashSet<long>();
        var open = new PriorityQueue<long, float>();
        float H(int k)
        {
            int di = Math.Abs(k % _w - bi), dj = Math.Abs(k / _w - bj);
            return 1.2f * Math.Max(0f, Math.Max(di, dj) + 0.4142f * Math.Min(di, dj) - rc);
        }

        open.Enqueue(start, H((int)(start / 8)));
        long goal = -1;
        while (open.TryDequeue(out long cur, out _))
        {
            if (!closed.Add(cur))
            {
                continue;
            }

            if (AtGoal(cur))
            {
                goal = cur;
                break;
            }

            if (closed.Count > MaxExpand)
            {
                why = $"searched {MaxExpand} floor cells without reaching it";
                return null;
            }

            int k = (int)(cur / 8), i = k % _w, j = k / _w;
            float y = Height(cur), gc = gScore[cur];
            int ff = (int)(cur % 8);
            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0 || !In(i + di, j + dj))
                    {
                        continue;
                    }

                    int nk = (j + dj) * _w + i + di;
                    int f = StandFloor(nk, y, extra);
                    if (f < 0)
                    {
                        continue;
                    }

                    if (di != 0 && dj != 0)
                    {
                        // no squeezing past a corner: both orthogonal steps must exist AND be clear
                        int kx = j * _w + i + di, kz = (j + dj) * _w + i;
                        int fx = FloorAt(kx, y, _maxStep, extra), fz = FloorAt(kz, y, _maxStep, extra);
                        if (fx < 0 || fz < 0 ||
                            EdgeBlocked(k, ff, di, 0, fx) || EdgeBlocked(k, ff, 0, dj, fz))
                        {
                            continue;
                        }
                    }
                    else if (EdgeBlocked(k, ff, di, dj, f))
                    {
                        continue; // a wall physically crosses this step (real 3D edge test)
                    }

                    long nn = (long)nk * 8 + f;
                    if (closed.Contains(nn))
                    {
                        continue;
                    }

                    float hug = _wallHug.Count > 0 && _wallHug.TryGetValue((long)nk * 8 + f, out var hc) ? hc : 0f;
                    float ng = gc + (di != 0 && dj != 0 ? 1.4142f : 1f) + hug;
                    if (gScore.TryGetValue(nn, out float old) && old <= ng)
                    {
                        continue;
                    }

                    gScore[nn] = ng;
                    parent[nn] = cur;
                    open.Enqueue(nn, ng + H(nk));
                }
            }
        }

        if (goal < 0)
        {
            // the explored count is the diagnosis: a few hundred cells = the START is sealed; hundreds
            // of thousands = the goal region is what is closed
            why = $"no walkway (explored {closed.Count} cells) from my level to within {reach:0.0} m of ({b.X:0},{(anyLevel ? "?" : b.Y.ToString("0"))},{b.Z:0})";
            return null;
        }

        var nodes = new List<long>();
        for (long c = goal; ; c = parent[c])
        {
            nodes.Add(c);
            if (c == start)
            {
                break;
            }
        }

        nodes.Reverse();

        var pts = new List<Vector3> { a };
        int i0 = 0;
        while (i0 < nodes.Count - 1)
        {
            int j = nodes.Count - 1;
            while (j > i0 + 1 && !Clear(nodes[i0], nodes[j], extra))
            {
                j--;
            }

            pts.Add(Centre((int)(nodes[j] / 8), Height(nodes[j])));
            i0 = j;
        }

        int bk = Key(b.X, b.Z);
        if (bk >= 0 && goal / 8 == bk)
        {
            pts[pts.Count - 1] = new Vector3(b.X, Height(goal), b.Z);
        }

        return pts;
    }

    private long NearestNode(Vector3 p, float maxR, HashSet<int> extra)
    {
        int ci = CellX(p.X), cj = CellZ(p.Z);
        int R = (int)Math.Ceiling(maxR / Cell);
        for (int r = 0; r <= R; r++)
        {
            long best = -1;
            float bd = float.MaxValue;
            for (int dj = -r; dj <= r; dj++)
            {
                for (int di = -r; di <= r; di++)
                {
                    if (Math.Max(Math.Abs(di), Math.Abs(dj)) != r || !In(ci + di, cj + dj))
                    {
                        continue;
                    }

                    int k = (cj + dj) * _w + ci + di;
                    int f = FloorAt(k, p.Y, 2.5f, extra);
                    if (f < 0)
                    {
                        continue;
                    }

                    float d = di * di + dj * dj + Math.Abs(_floors[k][f] - p.Y);
                    if (d < bd)
                    {
                        bd = d;
                        best = (long)k * 8 + f;
                    }
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return -1;
    }

    // Walkable in a straight line: every sample (and a body's width either side) has a floor within
    // MaxStep of the one before it, or a landing below it (no fall damage - the line may drop off
    // an edge) - so the line neither leaves the platform nor climbs.
    private bool Clear(long n0, long n1, HashSet<int> extra)
    {
        int k0 = (int)(n0 / 8), k1 = (int)(n1 / 8);
        float x0 = k0 % _w + 0.5f, z0 = k0 / _w + 0.5f, x1 = k1 % _w + 0.5f, z1 = k1 / _w + 0.5f;
        float len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        if (len < 1e-3f)
        {
            return true;
        }

        float px = -(z1 - z0) / len * 0.6f, pz = (x1 - x0) / len * 0.6f; // 0.3 m either side, in cells
        int n = Math.Max(1, (int)Math.Ceiling(len * 3));
        float y = Height(n0);
        var floors = new List<float>(n + 1) { y }; // the floor profile, for the exact wall test below
        for (int s = 1; s <= n; s++)
        {
            float t = s / (float)n, x = x0 + (x1 - x0) * t, z = z0 + (z1 - z0) * t;
            int f = StandFloor(Idx(x, z), y, extra);
            if (f < 0)
            {
                return false;
            }

            y = _floors[Idx(x, z)][f];
            floors.Add(y);
            if (StandFloor(Idx(x + px, z + pz), y, extra) < 0 || StandFloor(Idx(x - px, z - pz), y, extra) < 0)
            {
                return false;
            }
        }

        // A clear FLOOR line is not a clear LINE: a wall can cross it while the tiles run on - the
        // A* respected the wall (the edge it blocks), and the smoothing must too, or the walk cuts
        // the corner the route deliberately went around. The test is EXACT segment-edge crossings,
        // not point samples: a perfectly vertical wall is a zero-width projection no sample lands
        // inside, and the sampled line walked straight through it (owner, 2026-10-03: "pathfinds
        // but runs at/through walls").
        if (_wallTris.Count > 0 && LineHitsWall(x0, z0, x1, z1, t => Profile(floors, t)))
        {
            return false;
        }

        return true;
    }

    private int Idx(float ci, float cj)
    {
        int i = (int)Math.Floor(ci), j = (int)Math.Floor(cj);
        return In(i, j) ? j * _w + i : -1;
    }
}