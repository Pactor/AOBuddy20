using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ionic.Zlib;

namespace AOBuddy20.Nav;

/// <summary>
///     'northbound': a mission pool's per-room navigation lattices, precalculated OFFLINE and
///     stored UNROTATED - north-aligned, in the room's own pool frame. One lattice per pool room
///     type at 20 cm resolution: floor levels sampled from the pool's collision.bin (its walkable
///     truth), wall-band blockers from its steep triangles, headroom applied, and the door
///     portals marked (doors are walkable by default; a LOCKED door - state from
///     DoorFullUpdate/mission packets, see MissionController - must gate its portal at runtime).
///     The bot loads the file at mission compose and rotates/translates each placed room's
///     lattice into the world grid as an index remap - the rotation comes from the live
///     placement (slot rot + server-door chain), never from this file, so OmniCell-style
///     compositions work without a recache.
/// </summary>
public sealed class NorthboundPool
{
    public const string Magic = "NBGR";
    public const int Version = 2;

    public float Cell = 0.2f;
    public int PoolPf;
    public string SourceHash = "";
    public Dictionary<int, NorthboundRoom> Rooms = new();

    /// <summary>
    ///     The pool grid for a playfield: load the cached file, or build it on first encounter
    ///     (a second or two, once per pool) and save it next to the bins. Null on any failure -
    ///     the caller falls back to the per-mission triangle path.
    /// </summary>
    public static NorthboundPool For(string pluginDir, int poolPf, Action<string> log)
    {
        var path = Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "poolgrid.northbound");
        if (File.Exists(path))
        {
            try
            {
                return Load(path);
            }
            catch (Exception ex)
            {
                log?.Invoke($"NORTHBOUND: {path} unreadable ({ex.Message}) - rebuilding.");
            }
        }

        try
        {
            var pool = NavDungeon.Read(Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "rooms.json"));
            List<List<float[]>> ReadBin(string file)
            {
                var byRoom = new List<List<float[]>>();
                var bp = Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), file);
                if (!File.Exists(bp))
                {
                    return byRoom;
                }

                foreach (var c in NavCollision.Read(bp).Chunks)
                {
                    var idx = c.Instance & 0xFFFF;
                    while (byRoom.Count <= idx)
                    {
                        byRoom.Add(null);
                    }

                    (byRoom[idx] ??= new List<float[]>()).Add(c.Verts);
                }

                return byRoom;
            }

            var collision = ReadBin("collision.bin");
            var walls = ReadBin("walls.bin");
            var built = new NorthboundPool { PoolPf = poolPf, Cell = 0.2f, SourceHash = $"{collision.Count}x{walls.Count}" };
            var count = 0;
            foreach (var pr in pool.Rooms)
            {
                var cols = pr.Index < collision.Count ? collision[pr.Index] : null;
                var wls = pr.Index < walls.Count ? walls[pr.Index] : null;
                if (cols == null && wls == null)
                {
                    continue;
                }

                var room = NorthboundBuilder.Build(pr.Index, pr, cols ?? new List<float[]>(), wls ?? new List<float[]>(),
                    AOBuddyNav.DoorwaysFromField(pr), pool);
                if (room == null)
                {
                    continue;
                }

                built.Rooms[pr.Index] = room;
                count++;
            }

            if (count == 0)
            {
                return null;
            }

            built.Save(path);
            log?.Invoke($"NORTHBOUND: built {count} room lattice(s) for pf {poolPf} -> {path}");
            return built;
        }
        catch (Exception ex)
        {
            log?.Invoke($"NORTHBOUND: pf {poolPf} build failed ({ex.Message}) - triangle path this session.");
            return null;
        }
    }

    public static NorthboundPool Load(string path)
    {
        using var f = File.OpenRead(path);
        using var z = new ZlibStream(f, CompressionMode.Decompress);
        using var br = new BinaryReader(z);
        if (new string(br.ReadChars(4)) != Magic || br.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{path}: not a northbound v{Version} pool grid");
        }

        var pool = new NorthboundPool
        {
            PoolPf = br.ReadInt32(),
            Cell = br.ReadSingle(),
            SourceHash = br.ReadString(),
        };
        var n = br.ReadInt32();
        for (var ri = 0; ri < n; ri++)
        {
            var r = new NorthboundRoom
            {
                RoomIndex = br.ReadInt32(),
                Ox = br.ReadSingle(),
                Oz = br.ReadSingle(),
                W = br.ReadInt32(),
                H = br.ReadInt32(),
            };
            var cells = r.W * r.H;
            r.Levels = new List<float>[cells];
            for (var c = 0; c < cells; c++)
            {
                var count = br.ReadInt32();
                if (count > 0)
                {
                    var l = new List<float>(count);
                    for (var k = 0; k < count; k++)
                    {
                        l.Add(br.ReadSingle());
                    }

                    r.Levels[c] = l;
                }
            }

            r.MeshLevels = new List<float>[cells];
            for (var c = 0; c < cells; c++)
            {
                var count = br.ReadInt32();
                if (count > 0)
                {
                    var l = new List<float>(count);
                    for (var k = 0; k < count; k++)
                    {
                        l.Add(br.ReadSingle());
                    }

                    r.MeshLevels[c] = l;
                }
            }

            var blockedBytes = br.ReadInt32();
            r.Blocked = new bool[cells];
            for (var b = 0; b < blockedBytes; b++)
            {
                var bits = br.ReadByte();
                for (var bit = 0; bit < 8 && b * 8 + bit < cells; bit++)
                {
                    if ((bits & (1 << bit)) != 0)
                    {
                        r.Blocked[b * 8 + bit] = true;
                    }
                }
            }

            var portals = br.ReadInt32();
            for (var p = 0; p < portals; p++)
            {
                var portal = new NorthboundPortal
                {
                    X = br.ReadDouble(),
                    Z = br.ReadDouble(),
                    Nx = br.ReadDouble(),
                    Nz = br.ReadDouble(),
                    MeshLevel = br.ReadSingle(),
                };
                var pc = br.ReadInt32();
                for (var q = 0; q < pc; q++)
                {
                    portal.Cells.Add((br.ReadInt32(), br.ReadInt32(), br.ReadSingle()));
                }

                r.Portals.Add(portal);
            }

            pool.Rooms[r.RoomIndex] = r;
        }

        return pool;
    }

    public void Save(string path)
    {
        using var f = File.Create(path);
        using var z = new ZlibStream(f, CompressionMode.Compress, CompressionLevel.BestSpeed);
        using var bw = new BinaryWriter(z);
        bw.Write(Magic.ToCharArray());
        bw.Write(Version);
        bw.Write(PoolPf);
        bw.Write(Cell);
        bw.Write(SourceHash ?? "");
        bw.Write(Rooms.Count);
        foreach (var r in Rooms.Values)
        {
            bw.Write(r.RoomIndex);
            bw.Write(r.Ox);
            bw.Write(r.Oz);
            bw.Write(r.W);
            bw.Write(r.H);
            var cells = r.W * r.H;
            for (var c = 0; c < cells; c++)
            {
                var l = r.Levels[c];
                if (l == null || l.Count == 0)
                {
                    bw.Write(0);
                    continue;
                }

                bw.Write(l.Count);
                foreach (var v in l)
                {
                    bw.Write(v);
                }
            }

            // mesh-sourced levels, pool-mesh datum (the blit shifts them onto the door level)
            for (var c = 0; c < cells; c++)
            {
                var l = r.MeshLevels[c];
                if (l == null || l.Count == 0)
                {
                    bw.Write(0);
                    continue;
                }

                bw.Write(l.Count);
                foreach (var v in l)
                {
                    bw.Write(v);
                }
            }

            // blocked as a bitmap
            bw.Write((cells + 7) / 8);
            for (var c = 0; c < cells; c += 8)
            {
                byte bits = 0;
                for (var bit = 0; bit < 8 && c + bit < cells; bit++)
                {
                    if (r.Blocked[c + bit])
                    {
                        bits |= (byte)(1 << bit);
                    }
                }

                bw.Write(bits);
            }

            bw.Write(r.Portals.Count);
            foreach (var p in r.Portals)
            {
                bw.Write(p.X);
                bw.Write(p.Z);
                bw.Write(p.Nx);
                bw.Write(p.Nz);
                bw.Write(p.MeshLevel);
                bw.Write(p.Cells.Count);
                foreach (var (cx, cy, level) in p.Cells)
                {
                    bw.Write(cx);
                    bw.Write(cy);
                    bw.Write(level);
                }
            }
        }
    }
}

/// <summary>One pool room's lattice, north-aligned pool frame. Cell (i, j) spans
/// [Ox + i*Cell, Ox + (i+1)*Cell) x [Oz + j*Cell, Oz + (j+1)*Cell); Blocked cells carry no
/// passage even where a level exists (wall band, no headroom, or erosion).</summary>
public sealed class NorthboundRoom
{
    public int RoomIndex;
    public float Ox, Oz;
    public int W, H;

    /// <summary>Tile-sourced levels, ABSOLUTE (the heightfield carries the placed world's own
    /// heights). These never shift.</summary>
    public List<float>[] Levels = new List<float>[0];

    /// <summary>Mesh-sourced levels, in the pool mesh's OWN datum - which sits metres off the
    /// tile datum (grey_mh4: floor 13.1 over a 5.0 base). The blit shifts them by
    /// placedDoorY − portal.MeshLevel before merging; a mesh level without that shift is a
    /// different floor than it claims to be.</summary>
    public List<float>[] MeshLevels = new List<float>[0];
    public bool[] Blocked = new bool[0];
    public List<NorthboundPortal> Portals = new();

    public int CellIndex(float x, float z) =>
        (int)Math.Floor((z - Oz) / 0.2f) * W + (int)Math.Floor((x - Ox) / 0.2f);
}

public sealed class NorthboundPortal
{
    public double X, Z, Nx, Nz; // the doorway centre and outward normal, pool frame
    public List<(int cx, int cy, float level)> Cells = new(); // the portal's cells and their level
    public float MeshLevel = float.NaN; // the portal's floor in the MESH datum - the anchor for the mesh shift
}

/// <summary>
///     Rasterizes one pool room's collision/walls chunks into its northbound lattice. Floors:
///     collision.bin triangles flatter than ~41 degrees (WalkNy), sampled at every 20 cm cell
///     centre their projection covers (barycentric), plane-interpolated - a ramp becomes a
///     slope. Blockers: every steeper triangle (walls.bin plus collision's 41-60 degree band)
///     whose body at the cell centre crosses the standing band [level+BodyLow, level+BodyHigh]
///     of one of the cell's levels - a lintel over the passage stays, a pillar or table blocks.
///     Headroom: two levels closer than BodyHigh block the lower (the table-top rule). The
///     door portals are marked last and cleared - doors are walkable by default.
/// </summary>
public static class NorthboundBuilder
{
    private const float WalkNy = 0.5f;   // the extractor.s own walkable cutoff: flatter than 60 deg
                                         // is a surface - cave shell floors are 41-60 degree slopes
    private const float BodyLow = 0.3f, BodyHigh = 1.9f;
    private const float MergeLevels = 0.25f;
    private const int MaxLevels = 8;
    private const float DoorAcross = 1.25f, DoorAlong = 3.5f; // as FloorGrid.StampDoorways

    public static NorthboundRoom Build(int roomIndex, NavDungeon.Room pr, List<float[]> collision, List<float[]> walls, List<AOBuddyNav.Doorway> doorways, NavDungeon pool)
    {
        // bounds over everything the room carries
        float minx = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxz = float.MinValue;
        void Take(float[] v)
        {
            for (int i = 0; i + 2 < v.Length; i += 3)
            {
                minx = Math.Min(minx, v[i]);
                maxx = Math.Max(maxx, v[i]);
                minz = Math.Min(minz, v[i + 2]);
                maxz = Math.Max(maxz, v[i + 2]);
            }
        }

        foreach (var v in collision)
        {
            Take(v);
        }

        foreach (var v in walls)
        {
            Take(v);
        }

        if (minx > maxx)
        {
            return null;
        }

        const float cell = 0.2f;
        var r = new NorthboundRoom
        {
            RoomIndex = roomIndex,
            Ox = (float)(Math.Floor(minx / cell) - 2) * cell,
            Oz = (float)(Math.Floor(minz / cell) - 2) * cell,
        };
        r.W = (int)Math.Ceiling((maxx - r.Ox) / cell) + 2;
        r.H = (int)Math.Ceiling((maxz - r.Oz) / cell) + 2;
        var levels = new List<float>[r.W * r.H];        // tile-sourced, absolute
        var mesh = new List<float>[r.W * r.H];          // mesh-sourced, pool-mesh datum
        var blocked = new bool[r.W * r.H];

        void Blocker(float[] v, int o)
        {
            var (yMin, yMax) = MinMax(v, o + 1);
            int i0 = XCell(v[o]), i1 = Math.Max(XCell(v[o + 3]), XCell(v[o + 6]));
            int j0 = ZCell(v[o + 2]), j1 = Math.Max(ZCell(v[o + 5]), ZCell(v[o + 8]));
            for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
            {
                for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                {
                    double cx = r.Ox + (i + 0.5f) * cell, cz = r.Oz + (j + 0.5f) * cell;
                    if (!InXZ(v, o, cx, cz))
                    {
                        continue;
                    }

                    var l = levels[j * r.W + i];
                    var m = mesh[j * r.W + i];
                    if (l == null && m == null)
                    {
                        continue;
                    }

                    foreach (var lv in (l ?? Enumerable.Empty<float>()).Concat(m ?? Enumerable.Empty<float>()))
                    {
                        if (yMax >= lv + BodyLow && yMin <= lv + BodyHigh)
                        {
                            blocked[j * r.W + i] = true;
                            break;
                        }
                    }
                }
            }
        }

        void Surface(float[] v, int o)
        {
            int i0 = XCell(v[o]), i1 = Math.Max(XCell(v[o + 3]), XCell(v[o + 6]));
            int j0 = ZCell(v[o + 2]), j1 = Math.Max(ZCell(v[o + 5]), ZCell(v[o + 8]));
            for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
            {
                for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                {
                    double bx = r.Ox + i * cell, bz = r.Oz + j * cell;
                    // conservative coverage: centre + 4 inner corners - a cell centre alone falls
                    // in the seams between coplanar triangles and the floor map comes out
                    // Swiss-cheese (the static grid hit exactly that, Neutral Supermarket)
                    var hit = false;
                    var y = 0.0;
                    foreach (var (fx, fz) in new[]
                             {
                                 (0.5, 0.5), (0.15, 0.15), (0.85, 0.15), (0.15, 0.85), (0.85, 0.85)
                             })
                    {
                        double px = bx + fx * cell, pz = bz + fz * cell;
                        if (InXZ(v, o, px, pz))
                        {
                            hit = true;
                            y = PlaneY(v, o, px, pz);
                            break;
                        }
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    var idx = j * r.W + i;
                    var l = mesh[idx] ??= new List<float>();
                    if (l.Count > 0 && y - l[^1] <= MergeLevels && y >= l[^1])
                    {
                        continue; // ascending insert; the near neighbour above is the last
                    }

                    var pos = l.FindLastIndex(x => x < y) + 1;
                    if (pos > 0 && y - l[pos - 1] <= MergeLevels)
                    {
                        continue;
                    }

                    l.Insert(pos, (float)y);
                    if (l.Count > MaxLevels)
                    {
                        l.RemoveAt(0);
                    }
                }
            }
        }

        // per triangle: its own normal decides surface vs blocker (the bins arrive pre-split,
        // but a misfiled triangle lands where its normal says)
        void Rasterize(float[] v, bool forceBlocker)
        {
            for (int o = 0; o + 8 < v.Length; o += 9)
            {
                float ux = v[o + 3] - v[o], uy = v[o + 4] - v[o + 1], uz = v[o + 5] - v[o + 2];
                float wx = v[o + 6] - v[o], wy = v[o + 7] - v[o + 1], wz = v[o + 8] - v[o + 2];
                var ny = uz * wx - ux * wz;
                var len = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz) * (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                if (len < 1e-9f)
                {
                    continue;
                }

                if (!forceBlocker && ny / len >= WalkNy)
                {
                    Surface(v, o);
                }
                else
                {
                    Blocker(v, o);
                }
            }
        }

        foreach (var v in collision)
        {
            Rasterize(v, forceBlocker: false);
        }

        foreach (var v in walls)
        {
            Rasterize(v, forceBlocker: true);
        }

        // THE TILE FLOOR (StampRoomFloors, ported to the lattice): in these pools the walkable
        // room floor exists ONLY as the heightfield - collision.bin carries just the structures
        // above it (Startroom's centre: not one mesh triangle, tiles say 5.8). The tile heights
        // are absolute, so they need no door anchoring; the mesh's own datum rides alongside as
        // upper levels. Where a steep mesh band crosses a tile level's body band AND the mesh
        // offers its own level within a step, the tile yields (the stairway-tread rule); a tile
        // with no mesh alternative stands even under a lintel or an upper deck.
        if (pr.Tile != null && pr.Height != null && pr.Rect != null)
        {
            int x1 = pr.Rect[0], z1 = pr.Rect[1];
            double mx = (pr.Rect[0] + pr.Rect[2] + 1) / 2.0, mz = (pr.Rect[1] + pr.Rect[3] + 1) / 2.0;
            for (int tr = 0; tr < pr.Tile.Length; tr++)
            {
                for (int tc = 0; tc < pr.Tile[tr].Length; tc++)
                {
                    if (pr.Tile[tr][tc] == 0 || pr.Height[tr] == null || tc >= pr.Height[tr].Length)
                    {
                        continue;
                    }

                    double wxp = pr.Pos[0] + (x1 + tc - mx) * pool.Cell;
                    double wzp = pr.Pos[2] + (z1 + tr - mz) * pool.Cell;
                    var th = (float)(pr.Pos[1] + (pr.Height[tr][tc] - pr.HeightBase) * pool.HeightScale);
                    int i0 = XCell((float)(wxp - pool.Cell / 2)), i1 = XCell((float)(wxp + pool.Cell / 2));
                    int j0 = ZCell((float)(wzp - pool.Cell / 2)), j1 = ZCell((float)(wzp + pool.Cell / 2));
                    for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                    {
                        for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                        {
                            var idx = j * r.W + i;
                            var l = levels[idx] ??= new List<float>();
                            // MERGE: neighbouring heightfield cells differ by ~0.2 (5.0 vs 5.2) -
                            // that is one floor, and an unmerged pair would let the headroom rule
                            // block the lower half of the room's own floor
                            if (l.Count > 0 && Math.Abs(l[^1] - th) <= 0.3f)
                            {
                                continue;
                            }

                            var pos = l.FindLastIndex(x => x < th) + 1;
                            if (pos > 0 && th - l[pos - 1] <= 0.3f)
                            {
                                continue;
                            }

                            l.Insert(pos, th);
                            if (l.Count > MaxLevels)
                            {
                                l.RemoveAt(0);
                            }
                        }
                    }
                }
            }
        }

        // portals last: the door's keep-open box, cleared and levelled (doors are walkable by
        // default - a locked door is runtime state, not nav data)
        if (doorways != null)
        {
            foreach (var dw in doorways)
            {
                var portal = new NorthboundPortal { X = dw.X, Z = dw.Z, Nx = dw.Nx, Nz = dw.Nz };
                double ax = Math.Abs(dw.Nx) > 0.5 ? DoorAlong : DoorAcross;
                double az = Math.Abs(dw.Nz) > 0.5 ? DoorAlong : DoorAcross;
                int i0 = XCell((float)(dw.X - ax)), i1 = XCell((float)(dw.X + ax));
                int j0 = ZCell((float)(dw.Z - az)), j1 = ZCell((float)(dw.Z + az));
                for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                {
                    for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                    {
                        blocked[j * r.W + i] = false;
                        var l = levels[j * r.W + i];
                        var m = mesh[j * r.W + i];
                        var level = l is { Count: > 0 } ? l[0]
                            : m is { Count: > 0 } ? m[0]
                            : float.NaN;
                        portal.Cells.Add((i, j, level));
                        if (m is { Count: > 0 } && float.IsNaN(portal.MeshLevel))
                        {
                            portal.MeshLevel = m[0];
                        }
                    }
                }

                r.Portals.Add(portal);
            }
        }

        r.Levels = levels;
        r.MeshLevels = mesh;
        r.Blocked = blocked;
        return r;

        int XCell(float x) => (int)Math.Floor((x - r.Ox) / cell);
        int ZCell(float z) => (int)Math.Floor((z - r.Oz) / cell);
    }

    private static (float min, float max) MinMax(float[] v, int o)
    {
        var min = Math.Min(v[o], Math.Min(v[o + 3], v[o + 6]));
        var max = Math.Max(v[o], Math.Max(v[o + 3], v[o + 6]));
        return (min, max);
    }

    private static bool InXZ(float[] v, int o, double px, double pz)
    {
        double ax = v[o], az = v[o + 2], bx = v[o + 3], bz = v[o + 5], cx = v[o + 6], cz = v[o + 8];
        double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        var neg = d1 < 0 || d2 < 0 || d3 < 0;
        var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static double PlaneY(float[] v, int o, double px, double pz)
    {
        double ax = v[o], ay = v[o + 1], az = v[o + 2];
        double ux = v[o + 3] - ax, uy = v[o + 4] - ay, uz = v[o + 5] - az;
        double wx = v[o + 6] - ax, wy = v[o + 7] - ay, wz = v[o + 8] - az;
        double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
        if (Math.Abs(ny) < 1e-9)
        {
            return ay;
        }

        return ay - (nx * (px - ax) + nz * (pz - az)) / ny;
    }
}
