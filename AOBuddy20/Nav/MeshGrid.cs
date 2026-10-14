// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: MeshGrid.cs
//
// Created:       2026-10-13
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using System.Globalization;
using System.IO.Compression;
using AOBuddy20.Components;
using AOSharp.Common.GameData;

namespace AOBuddy20.Nav;

/// <summary>
///     The overland walk grid straight from the source data: ground.bin's heightfield for the
///     terrain floor and meshes.bin's triangles (the unfiltered RDB 1000013 set) for everything
///     built on it - ramps, decks, towers, enclosure walls with their openings, tree canopies.
///     No precomputed lattice: the surfaces of a column are computed on demand by clipping the
///     column's triangles against it (the mission grids' ClipColumn, at query time), and the
///     query returns ALL floors - the caller (walker, planner) decides which one is right.
///
///     The only precomputation is the spatial index (cell -&gt; triangle ids), cached to disk per
///     playfield and keyed to the meshes.bin content - recalculated only when the extraction
///     changes (owner: "cache the spatial hash, no need to calculate it over and over again").
/// </summary>
public sealed class MeshGrid : IWalkGrid
{
    public int Pf { get; }
    public float Cell => 1f;
    public int CellsX { get; }
    public int CellsZ { get; }
    public int BlockedCells { get; private set; }
    public bool HasWalls => true;

    private const float BandTop = 0.6f;      // the calibrated collision band's top: standing needs this much air
    private const float MaxStepUp = 1.05f;   // the climb limit for level rises - Newland's terminal
                                             // terrace sits exactly 1.0 m over its street (0.8 severed
                                             // it and the whole island, owner 2026-10-13)
    private const float BuriedDepth = 20f;   // a run rooted this far below terrain is the invisible wall
    private const int MaxFloors = 15;
    private const float PriceyCellCost = 0.5f; // the soft toll for entering a priced cell (the yank
                                               // marks, the zone-line clearance band): half a metre
                                               // per cell - a run along a priced band doubles in
                                               // cost, a 4 m sidestep beats ~8 m of hugging it

    private readonly NavGround _ground;   // the terrain heightfield
    private float[] _meshVerts;           // 9 floats per triangle, world coords
    private int[] _cellStart;             // per cell: start index into _cellTris
    private int[] _cellTris;              // triangle ids, grouped by cell
    private readonly Dictionary<long, float[]> _columnCache = new();

    private MeshGrid(int pf, NavGround ground, int cellsX, int cellsZ)
    {
        Pf = pf;
        _ground = ground;
        CellsX = cellsX;
        CellsZ = cellsZ;
    }

    // ── loading ────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid from ground.bin + meshes.bin; null when the playfield has no meshes.</summary>
    public static MeshGrid Load(string pluginDir, int pf, Action<string> log)
    {
        try
        {
            return LoadInner(pluginDir, pf, log);
        }
        catch (Exception ex)
        {
            // dungeons and odd pfs: no usable ground/meshes - the caller falls back to the old path
            log?.Invoke($"MESHGRID: pf {pf} unavailable ({ex.GetType().Name}: {ex.Message}) - falling back.");
            return null;
        }
    }

    private static MeshGrid LoadInner(string pluginDir, int pf, Action<string> log)
    {
        var folder = AOBuddyNav.FolderFor(pluginDir, pf);
        var meshPath = Path.Combine(folder, "meshes.bin");
        if (!File.Exists(meshPath))
        {
            return null;
        }

        var ground = NavGround.Read(Path.Combine(folder, "ground.bin"));
        var cellsX = (int)((ground.SamplesX - 1) * ground.Cell);
        var cellsZ = (int)((ground.SamplesZ - 1) * ground.Cell);
        var grid = new MeshGrid(pf, ground, cellsX, cellsZ);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var src = new FileInfo(meshPath);
        var cachePath = Path.Combine(pluginDir, "GameData", "meshindex", pf + ".bin");
        var whompahPads = new List<Vector3>();
        try
        {
            Zoning.Load(pluginDir, log);
            foreach (var e in Zoning.ExitsFrom(pf))
            {
                if (e.Template == 96272)
                {
                    whompahPads.Add(e.A);
                }
            }

            if (whompahPads.Count > 0)
            {
                log?.Invoke($"MESHGRID: pf {pf}: filtering {whompahPads.Count} whompah tubes out of the index.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"MESHGRID: zoning unavailable ({ex.GetType().Name}: {ex.Message}) - no whompah filter.");
        }

        var (verts, start, tris) = LoadOrBuildIndex(cachePath, src, meshPath, pf, cellsX, cellsZ, ground, whompahPads, log);
        grid._meshVerts = verts;
        grid._cellStart = start;
        grid._cellTris = tris;
        log?.Invoke($"MESHGRID: pf {pf}: {cellsX}x{cellsZ} cells, {tris.Length:N0} triangle placements, " +
                    $"{src.Length:N0} B meshes.bin, {sw.ElapsedMilliseconds} ms");
        return grid;
    }

    private static (float[] verts, int[] start, int[] tris) LoadOrBuildIndex(
        string cachePath, FileInfo src, string meshPath, int pf, int cellsX, int cellsZ, NavGround ground, List<Vector3> whompahPads, Action<string> log)
    {
        if (File.Exists(cachePath))
        {
            try
            {
                using var fs = File.OpenRead(cachePath);
                using var br = new BinaryReader(fs);
                if (br.ReadBytes(4).SequenceEqual("MSI3"u8) // MSI3: whompah tubes filtered (was MSI2: walls back in the index)
                    && br.ReadInt32() == pf
                    && br.ReadInt64() == src.LastWriteTimeUtc.Ticks
                    && br.ReadInt64() == src.Length)
                {
                    var verts = new float[br.ReadInt32()];
                    for (var i = 0; i < verts.Length; i++)
                    {
                        verts[i] = br.ReadSingle();
                    }

                    var start = new int[br.ReadInt32() + 1];
                    for (var i = 0; i < start.Length; i++)
                    {
                        start[i] = br.ReadInt32();
                    }

                    var tris = new int[br.ReadInt32()];
                    for (var i = 0; i < tris.Length; i++)
                    {
                        tris[i] = br.ReadInt32();
                    }

                    return (verts, start, tris);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"MESHGRID: index cache unreadable ({ex.GetType().Name}) - rebuilding.");
            }
        }

        // build: read the triangles, bucket them into cells by their xz footprint
        var b = File.ReadAllBytes(meshPath);
        if (b[0] != 'A' || b[1] != 'O' || b[2] != 'C' || b[3] != 'L')
        {
            throw new InvalidDataException("meshes.bin: no AOCL magic");
        }

        var chunks = BitConverter.ToInt32(b, 8);
        var rawLen = BitConverter.ToInt32(b, 12);
        var zLen = BitConverter.ToInt32(b, 16);
        var body = new byte[rawLen];
        using (var msrc = new MemoryStream(b, 22, zLen - 2)) // zlib = 2-byte header + raw deflate + adler
        using (var inflate = new DeflateStream(msrc, CompressionMode.Decompress))
        {
            for (var got = 0; got < rawLen;)
            {
                got += inflate.Read(body, got, rawLen - got);
            }
        }

        var trisAll = new List<float[]>();
        var nearPad = false;
        var p = 0;

        // THE WHOMPAH TUBES STAY OUT OF THE INDEX (owner: "filter them out completely and try
        // again"): a whompah is a walk-through tube on flat plaza, but its rim/wall triangles
        // swallow the columns around the pad - only the rim's high level survives, the pad's
        // surroundings read as solid, and every approach stranded (Tir 2026-10-13). The tubes
        // carry no per-dynel identity in the surface records (the whole plaza shares one
        // instance), so the filter is geometric: no triangle whose centre lies within 7.5 m of
        // a known whompah pad enters the index at all. The terrain heightfield provides the
        // floor there - the plaza IS flat - and the tube is crossed as the open ground it is.
        for (var c = 0; c < chunks && p + 32 <= rawLen; c++)
        {
            p += 16; // instance, teleportDestPf, localizerType, localizerInstance - the router's later
            var ox = BitConverter.ToSingle(body, p);
            var oy = BitConverter.ToSingle(body, p + 4);
            var oz = BitConverter.ToSingle(body, p + 8);
            var cnt = BitConverter.ToInt32(body, p + 12);
            p += 16;
            if (p + cnt * 18 > body.Length)
            {
                throw new InvalidDataException($"meshes.bin chunk {c}: {cnt} tris at {p} overruns {body.Length}");
            }

            for (var t = 0; t < cnt; t++)
            {
                var v = new float[9];
                for (var k = 0; k < 9; k++)
                {
                    v[k] = (k % 3 == 0 ? ox : k % 3 == 1 ? oy : oz) + BitConverter.ToInt16(body, p + t * 18 + k * 2) / 100f;
                }

                // JUNK FILTER: only true far-out geometry (a single triangle kilometres tall,
                // or a vertex above y=5000) is dropped. The boundary curtains - AO's buried
                // impassables, the invisible walls - span hundreds of metres and MUST stay in
                // the index (AOBuddy10 walked out of geometry through exactly these). They are
                // neutralized as floors in Surfaces by the wall-face tag instead.
                var ySpan = Math.Abs(v[1] - v[4]) + Math.Abs(v[4] - v[7]);
                if (ySpan > 1000f || Math.Abs(v[1]) > 5000f)
                {
                    continue;
                }

                if (whompahPads.Count > 0)
                {
                    var mx = (v[0] + v[3] + v[6]) / 3f;
                    var mz = (v[2] + v[5] + v[8]) / 3f;
                    foreach (var wp in whompahPads)
                    {
                        var ddx = mx - wp.X;
                        var ddz = mz - wp.Z;
                        if (ddx * ddx + ddz * ddz <= 56.25f) // 7.5 m: the tube's full footprint
                        {
                            // THE 30 CM PLATFORM STAYS (owner: "it is always 30cm above the
                            // ground. always"): the pad's own slab - a thin triangle at terrain
                            // +0.3 - is the floor the pad sits on and is kept; everything else
                            // of the tube (walls, rim, cap) is dropped.
                            var top = MathF.Max(v[1], MathF.Max(v[4], v[7]));
                            var terr = (float)ground.HeightAt(mx, mz);
                            nearPad = float.IsNaN(terr) || top - terr > 0.45f;
                            break;
                        }
                    }
                }

                if (!nearPad)
                {
                    trisAll.Add(v);
                }

                nearPad = false;
            }

            p += cnt * 18;
        }

        var buckets = new SortedDictionary<long, List<int>>();
        for (var t = 0; t < trisAll.Count; t++)
        {
            var v = trisAll[t];
            var cx0 = Math.Max(0, (int)(Math.Min(v[0], Math.Min(v[3], v[6])) / CellF));
            var cx1 = Math.Min(cellsX - 1, (int)(Math.Max(v[0], Math.Max(v[3], v[6])) / CellF));
            var cz0 = Math.Max(0, (int)(Math.Min(v[2], Math.Min(v[5], v[8])) / CellF));
            var cz1 = Math.Min(cellsZ - 1, (int)(Math.Max(v[2], Math.Max(v[5], v[8])) / CellF));
            for (var cz = cz0; cz <= cz1; cz++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var key = cz * (long)cellsX + cx;
                    if (!buckets.TryGetValue(key, out var l))
                    {
                        buckets[key] = l = new List<int>();
                    }

                    l.Add(t);
                }
            }
        }

        var flatVerts = new float[trisAll.Count * 9];
        for (var t = 0; t < trisAll.Count; t++)
        {
            Array.Copy(trisAll[t], 0, flatVerts, t * 9, 9);
        }

        // DENSE over the full key space (void cells get a zero-width range): Surfaces indexes
        // with the raw cell key, so the start array must cover every key 0..cellsX*cellsZ
        var totalCells = (long)cellsX * cellsZ;
        var indexStart = new int[totalCells + 1];
        var indexTris = new int[buckets.Values.Sum(l => l.Count)];
        var pos = 0;
        for (long key = 0; key < totalCells; key++)
        {
            indexStart[key] = pos;
            if (buckets.TryGetValue(key, out var l))
            {
                foreach (var id in l)
                {
                    indexTris[pos] = id;
                    pos++;
                }
            }
        }

        indexStart[totalCells] = pos;

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
        var tmpPath = cachePath + ".tmp";
        using (var fs = File.Create(tmpPath))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write("MSI3"u8);
            bw.Write(pf);
            bw.Write(src.LastWriteTimeUtc.Ticks);
            bw.Write(src.Length);
            bw.Write(flatVerts.Length);
            foreach (var f in flatVerts)
            {
                bw.Write(f);
            }

            bw.Write(indexStart.Length - 1);
            for (var i = 0; i < indexStart.Length; i++)
            {
                bw.Write(indexStart[i]);
            }

            bw.Write(indexTris.Length);
            foreach (var id in indexTris)
            {
                bw.Write(id);
            }
        }

        File.Move(tmpPath, cachePath, overwrite: true);
        log?.Invoke($"MESHGRID: index built for pf {pf}: {buckets.Count} cells, {indexTris.Length:N0} placements (cached).");
        return (flatVerts, indexStart, indexTris);
    }

    private const float CellF = 1f;

    // ── the pure query: ALL surfaces of a column, ascending ────────────────────────────────

    /// <summary>Every floor at (x, z) ascending: the terrain height plus each mesh run top with
    /// BandTop of air above it. The caller picks the level - the grid never filters by "near y".</summary>
    public float[] Surfaces(float x, float z)
    {
        var cx = (int)x;
        var cz = (int)z;
        if (cx < 0 || cz < 0 || cx >= CellsX || cz >= CellsZ)
        {
            return Array.Empty<float>();
        }

        var key = cz * (long)CellsX + cx;
        if (_columnCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var runs = new List<(float y0, float y1, bool buried)>();
        var terr = (float)_ground.HeightAt(x, z);
        if (!float.IsNaN(terr))
        {
            runs.Add((terr - 0.5f, terr, false)); // the terrain column: solid for half a metre under its top
        }

        var s = _cellStart[key];
        var e = _cellStart[key + 1];
        for (var i = s; i < e; i++)
        {
            var b = _cellTris[i] * 9;
            var (r, hit) = ClipColumn(
                _meshVerts, b,
                cx * (double)Cell, cz * (double)Cell, Cell);
            if (hit)
            {
                if (r.y1 > 1000f || float.IsNaN(r.y1) || float.IsNaN(r.y0))
                {
                    Console.WriteLine("    MESHGRID DBG cell (" + cx + "," + cz + ") tri " + _cellTris[i] +
                        " run " + r.y0.ToString("F2", CultureInfo.InvariantCulture) + ".." + r.y1.ToString("F2", CultureInfo.InvariantCulture) +
                        "  verts y: " + _meshVerts[b + 1].ToString("F2", CultureInfo.InvariantCulture) + "," +
                        _meshVerts[b + 4].ToString("F2", CultureInfo.InvariantCulture) + "," + _meshVerts[b + 7].ToString("F2", CultureInfo.InvariantCulture));
                }

                // CELL STATES: Free (no mesh, terrain only) / Check (meshes present - the
                // per-step floor logic does the precise collision work at move time) /
                // Blocked (the buried impassables). Only the burial signature hard-blocks:
                // a run rooted >= BuriedDepth below the terrain is an invisible wall
                // (walls root at -140..-423, foundations stay above -90, ICC's interior
                // rooms sit within -20 of their terrain). Ordinary faces - steps, poles,
                // fences, curtain bands - keep their cells' levels: solid structures
                // swallow the terrain level in the merge and become unpathable that way,
                // without heuristics that once killed a whole road cell for a 1.37 m step.
                runs.Add((r.y0, r.y1, r.y0 < terr - BuriedDepth));
            }
        }

        runs.Sort((a, b) => a.y0.CompareTo(b.y0));
        var merged = new List<(float y0, float y1, bool buried)>();
        foreach (var (y0, y1, buried) in runs)
        {
            if (merged.Count > 0 && y0 <= merged[^1].y1 + 0.05f)
            {
                merged[^1] = (merged[^1].y0, Math.Max(merged[^1].y1, y1), merged[^1].buried || buried);
            }
            else
            {
                merged.Add((y0, y1, buried));
            }
        }

        // a level is a run top with BandTop of air above it (or the topmost run) - but a
        // run rooted deep below the terrain is the invisible wall: it never yields a floor
        var levels = new List<float>();
        for (var k = 0; k < merged.Count; k++)
        {
            if (merged[k].buried)
            {
                continue;
            }

            var air = k + 1 < merged.Count ? merged[k + 1].y0 - merged[k].y1 : float.PositiveInfinity;
            if (air >= BandTop)
            {
                levels.Add(merged[k].y1);
            }
        }

        var arr = levels.ToArray();
        _columnCache[key] = arr;
        return arr;
    }

    // ── the column clip (NorthboundGrid.ClipColumn, ported) ────────────────────────────────
    // Intersect a triangle with the cell's vertical column [bx,bx+c]x[bz,bz+c]; y is
    // interpolated along the clipped edges - a vertical wall survives as its line, exact at
    // any thinness. ONE PLANE PER PASS: a [lo,hi] strip clip in one pass drops edges that span
    // the whole strip (the 37.6% stripe bug); four half-plane passes always add the crossings.
    private static ((float y0, float y1), bool hit) ClipColumn(float[] v, int o, double bx, double bz, double c)
    {
        const double e = 1e-9;
        double[][] cur = [[v[o], v[o + 1], v[o + 2]], [v[o + 3], v[o + 4], v[o + 5]], [v[o + 6], v[o + 7], v[o + 8]]];
        double[][] tmp = [new double[3], new double[3], new double[3], new double[3], new double[3], new double[3], new double[3]];
        double[][] nxt = [new double[3], new double[3], new double[3], new double[3], new double[3], new double[3], new double[3]];

        var n = 3;
        n = ClipPlane(cur, tmp, n, 0, keepLo: true, plane: bx - e);
        n = ClipPlane(tmp, nxt, n, 0, keepLo: false, plane: bx + c + e);
        n = ClipPlane(nxt, tmp, n, 2, keepLo: true, plane: bz - e);
        n = ClipPlane(tmp, nxt, n, 2, keepLo: false, plane: bz + c + e);
        if (n == 0)
        {
            return ((0f, 0f), false);
        }

        float y0 = float.MaxValue, y1 = float.MinValue;
        for (var k = 0; k < n; k++)
        {
            y0 = Math.Min(y0, (float)nxt[k][1]);
            y1 = Math.Max(y1, (float)nxt[k][1]);
        }

        return ((y0, y1), true);
    }

    private static int ClipPlane(double[][] src, double[][] dst, int n, int axis, bool keepLo, double plane)
    {
        int m = 0;
        for (var i = 0; i < n; i++)
        {
            var p = src[i];
            var q = src[(i + 1) % n];
            var pin = keepLo ? p[axis] >= plane : p[axis] <= plane;
            var qin = keepLo ? q[axis] >= plane : q[axis] <= plane;
            if (pin)
            {
                p.CopyTo(dst[m]);
                m++;
                if (qin)
                {
                    continue;
                }
            }
            else if (!qin)
            {
                continue; // both outside this half-plane: the edge contributes nothing
            }

            // the edge crosses the plane: add the crossing point
            var t = (plane - p[axis]) / (q[axis] - p[axis]);
            dst[m][0] = p[0] + t * (q[0] - p[0]);
            dst[m][1] = p[1] + t * (q[1] - p[1]);
            dst[m][2] = p[2] + t * (q[2] - p[2]);
            m++;
        }

        return m;
    }

    // ── IWalkGrid ──────────────────────────────────────────────────────────────────────────

    public bool OpenAt(Vector3 p)
    {
        var lv = Surfaces(p.X, p.Z);
        if (lv.Length == 0)
        {
            return false;
        }

        if (float.IsNaN(p.Y))
        {
            return true;
        }

        foreach (var l in lv)
        {
            if (Math.Abs(l - p.Y) <= 3f)
            {
                return true;
            }
        }

        return false;
    }

    public HashSet<int> CellsAlong(Vector3 a, Vector3 b, float radius, HashSet<int> into = null)
    {
        into ??= new HashSet<int>();
        var dist = Movement.Flat(a, b);
        var steps = Math.Max(1, (int)(dist / Cell));
        var r = (int)MathF.Ceiling(radius / Cell);
        for (var s = 0; s <= steps; s++)
        {
            var t = s / (float)steps;
            var cx = (int)((a.X + (b.X - a.X) * t) / Cell);
            var cz = (int)((a.Z + (b.Z - a.Z) * t) / Cell);
            // THE RADIUS IS REAL (owner, 2026-10-13): a disc around every sample, OverlandGrid's
            // semantics - this used to stamp the segment line only, so every radius-based mark
            // collapsed on the mesh grid: the wompah pads' 5 m passage discs and the 6/9 m
            // clearance tolls were ONE cell (pads are point exits, A==B), and the ICC arrival
            // route walked 1 m past the arrival whompah and re-zoned through it.
            for (var dz = -r; dz <= r; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    var x = cx + dx;
                    var z = cz + dz;
                    if (x < 0 || z < 0 || x >= CellsX || z >= CellsZ || dx * dx + dz * dz > r * r)
                    {
                        continue;
                    }

                    into.Add(z * CellsX + x);
                }
            }
        }

        return into;
    }

    public Vector3? Escape(Vector3 from, Vector3 goal, out string why)
    {
        // the stuck escape: sample 16 directions, take the one whose 3 m probe stands on ground
        // that best improves the flat distance to the goal
        why = "";
        var bestD = Movement.Flat(from, goal);
        Vector3? best = null;
        for (var a = 0f; a < Math.PI * 2f; a += MathF.PI / 8f)
        {
            var px = from.X + MathF.Cos(a) * 3f;
            var pz = from.Z + MathF.Sin(a) * 3f;
            foreach (var l in Surfaces(px, pz))
            {
                if (Math.Abs(l - from.Y) > 4f)
                {
                    continue;
                }

                var d = Movement.Flat(new Vector3(px, from.Y, pz), goal);
                if (d < bestD)
                {
                    bestD = d;
                    best = new Vector3(px, l, pz);
                }
            }
        }

        if (best == null)
        {
            why = "every direction is walled in the mesh too";
        }

        return best;
    }

    /// <summary>
    ///     Dijkstra over (cell, floor): a cell's floors come from Surfaces, the step is 8-way,
    ///     rises limited to the 0.8 m climb (descents unlimited - outdoors there is no fall
    ///     damage, and the walker announces the landing). Waypoints are the cell centres at
    ///     their floor height.
    /// </summary>
    public List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why,
        HashSet<int> pricey = null)
        => FindPath(a, b, extra, snap, reach, out why, pricey, null);

    /// <param name="opened">cells the caller vouches walkable at the current level even where
    /// the mesh holds only structure levels - the whompah door cone (see PlanRoute)</param>
    public List<Vector3> FindPath(Vector3 a, Vector3 b, HashSet<int> extra, float snap, float reach, out string why,
        HashSet<int> pricey = null, HashSet<int> opened = null)
    {
        why = "";

        // THE PATH CACHE (owner, 2026-10-13): the Dijkstra over a zone's mesh is the most
        // expensive thing the walk thread does - the 19-million-node drain on pf 655 proved it -
        // and the same plan is asked for again: the travel tick re-setting the identical goal,
        // the next trip to the same mission entrance, the legs a multi-zone journey walks twice.
        // The memo lives per playfield in GameData/Nav/<pf>/paths.json, one zone in memory,
        // loaded on zone change. A hit is served only when the ends still match and the path
        // crosses none of this walk's hard blocks; the `opened` cone needs no key - its cells
        // are exempt from extra only while open, and the pad disc sits in extra otherwise, so a
        // stale path through it fails the overlap and re-plans.
        var cached = PathCache.Get(this, a, b, reach, float.NaN, extra);
        if (cached != null)
        {
            return cached;
        }

        var startCell = CellOf(a.X, a.Z);
        var goalCell = CellOf(b.X, b.Z);
        var gcx = goalCell % CellsX;
        var gcz = goalCell / CellsX;
        var goalFloors = Surfaces((gcx + 0.5f) * Cell, (gcz + 0.5f) * Cell);
        if (goalFloors.Length == 0)
        {
            why = "no floors at the goal";
            return null;
        }

        // THE BLOCKED GOAL (owner, 2026-10-13: "the bot doesn't move at all"): a goal inside the
        // caller's blocked set can never be popped, and Dijkstra drains the ENTIRE playfield
        // proving it - 19 million nodes and ~10 minutes on a 5 km playfield (pf 655), while the
        // movement thread hangs inside this call and nothing else (TravelTick, the pad Use
        // watchdog) gets a tick. Fail here instead: the caller's escape machinery answers fast.
        if (extra != null && extra.Contains(goalCell))
        {
            why = "the goal cell is blocked ground";
            return null;
        }

        var startFloors = Surfaces((startCell % CellsX + 0.5f) * Cell, (startCell / CellsX + 0.5f) * Cell);
        if (startFloors.Length == 0)
        {
            why = "no open ground within the snap radius";
            return null;
        }

        var startSlot = ClosestSlot(startFloors, a.Y);
        var startNode = Node(startCell, startSlot);

        // Dijkstra: node = (cell << 4) | floor slot; rises <= 0.8, descents unlimited
        var dist = new Dictionary<long, float> { [startNode] = 0 };
        var came = new Dictionary<long, long>();
        var open = new SortedDictionary<(float d, long node), bool> { [(0, startNode)] = true };

        long goalNode = -1;
        while (open.Count > 0)
        {
            var (du, node) = open.First().Key;
            open.Remove(open.First().Key);
            if (dist.TryGetValue(node, out var seen) && du > seen + 1e-4f)
            {
                continue;
            }

            var cell = (int)(node >> 4);
            var cx = cell % CellsX;
            var cz = cell / CellsX;
            if (cx == gcx && cz == gcz)
            {
                goalNode = node;
                break;
            }

            var floors = Surfaces((cx + 0.5f) * Cell, (cz + 0.5f) * Cell);
            var slot = (int)(node & 15);
            var here = slot < floors.Length ? floors[slot] : float.NaN;
            if (float.IsNaN(here))
            {
                continue;
            }

            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    var nx = cx + dx;
                    var nz = cz + dz;
                    if (nx < 0 || nz < 0 || nx >= CellsX || nz >= CellsZ)
                    {
                        continue;
                    }

                    var nkey = nz * (long)CellsX + nx;
                    if (extra != null && extra.Contains((int)nkey))
                    {
                        continue;
                    }

                    var nFloors = Surfaces((nx + 0.5f) * Cell, (nz + 0.5f) * Cell);
                    if (opened != null && opened.Contains((int)nkey) && nFloors.Length > 0)
                    {
                        // THE OPENED CONE (owner: "open the wompahs in exit direction, from mid
                        // zoneline in a 90° cone outwards"): cells the caller vouches for - the
                        // tube's rim-sealed approach, where the mesh holds only the rim's high
                        // level. Lend the search a floor at the level we're on: the walker claims
                        // its own level through them anyway (FloorY's roof guard keeps the 13 m
                        // rim from being claimed), and the terrain under the tube is flat.
                        var has = false;
                        foreach (var t0 in nFloors)
                        {
                            if (Math.Abs(t0 - here) <= 1.2f)
                            {
                                has = true;
                                break;
                            }
                        }

                        if (!has)
                        {
                            var widened = new float[nFloors.Length + 1];
                            Array.Copy(nFloors, widened, nFloors.Length);
                            widened[^1] = here; // a virtual node at the current level (unordered:
                                                // every slot is rise-checked against `here` alone)
                            nFloors = widened;
                        }
                    }

                    var stepCost = (dx != 0 && dz != 0 ? 1.4142f : 1f) * Cell;
                    // THE SOFT TOLL (owner, 2026-10-13): priced cells cost extra to ENTER - the
                    // route steers round them while a detour is cheaper and pays through where
                    // the priced ground is the only way. The parameter existed but was never
                    // consumed here: soft costs were dead on the mesh grid (OverlandGrid charges
                    // its own RefusedCellWeight for the same set).
                    if (pricey != null && pricey.Contains((int)nkey))
                    {
                        stepCost += PriceyCellCost;
                    }
                    for (var s = 0; s < nFloors.Length && s < MaxFloors; s++)
                    {
                        var rise = nFloors[s] - here;
                        if (rise > MaxStepUp)
                        {
                            continue; // the climb limit
                        }

                        var nn = Node(nkey, s);
                        var nd = du + stepCost;
                        if (!dist.TryGetValue(nn, out var old) || nd < old - 1e-4f)
                        {
                            dist[nn] = nd;
                            came[nn] = node;
                            open[(nd, nn)] = true;
                        }
                    }
                }
            }
        }

        if (goalNode < 0)
        {
            why = $"no path on the mesh (expanded {dist.Count} nodes, open {open.Count}, " +
                  $"start floors [{string.Join(",", startFloors)}], goal floors [{string.Join(",", goalFloors)}])";
            return null;
        }

        var path = new List<Vector3>();
        var w = goalNode;
        while (w != startNode)
        {
            var cell = (int)(w >> 4);
            var slot = (int)(w & 15);
            var cx = cell % CellsX;
            var cz = cell / CellsX;
            var floors = Surfaces((cx + 0.5f) * Cell, (cz + 0.5f) * Cell);
            var y = slot < floors.Length ? floors[slot] : 0f;
            path.Add(new Vector3((cx + 0.5f) * Cell, y, (cz + 0.5f) * Cell));
            if (!came.TryGetValue(w, out w))
            {
                break;
            }
        }

        var sx = startCell % CellsX;
        var sz = startCell / CellsX;
        var sFl = Surfaces((sx + 0.5f) * Cell, (sz + 0.5f) * Cell);
        var sy = startSlot < sFl.Length ? sFl[startSlot] : a.Y;
        path.Add(new Vector3((sx + 0.5f) * Cell, sy, (sz + 0.5f) * Cell));
        path.Reverse();
        PathCache.Put(Pf, a, b, reach, float.NaN, path);
        return path;

        int CellOf(float x, float z) => (int)(z / Cell) * CellsX + (int)(x / Cell);

        int ClosestSlot(float[] floors, float y)
        {
            var bestSlot = 0;
            var bestD = float.MaxValue;
            for (var i = 0; i < floors.Length && i < MaxFloors; i++)
            {
                var d = Math.Abs(floors[i] - y);
                if (d < bestD)
                {
                    bestD = d;
                    bestSlot = i;
                }
            }

            return bestSlot;
        }
    }

    private static long Node(long cell, int floor) => (cell << 4) | (long)Math.Min(floor, 15);

    private int ClosestSlot(List<float> floors, float y)
    {
        var bestSlot = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i < floors.Count && i < MaxFloors; i++)
        {
            var d = Math.Abs(floors[i] - y);
            if (d < bestD)
            {
                bestD = d;
                bestSlot = i;
            }
        }

        return bestSlot;
    }
    private List<float> FloorsNear(long cell, float y, float snap)
    {
        var cx = (int)(cell % CellsX);
        var cz = (int)(cell / CellsX);
        var r = Math.Max(1, (int)Math.Ceiling(snap));
        var best = new List<float>();
        var bestD = float.MaxValue;
        for (var dz = -r; dz <= r; dz++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                var x = (cx + dx + 0.5f) * Cell;
                var z = (cz + dz + 0.5f) * Cell;
                var lv = Surfaces(x, z);
                if (lv.Length == 0)
                {
                    continue;
                }

                var d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < bestD)
                {
                    bestD = d;
                    best = lv.ToList();
                }
            }
        }

        return best;
    }
}
