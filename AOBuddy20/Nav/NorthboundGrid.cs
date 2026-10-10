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
    public const string Magic = "NBGR"; // file signature
    public const int Version = 4; // bump when the binary layout OR the decode changes (v4: portal mesh-level candidate lists; v3: OmniCell socket decode; Load refuses other versions)

    public float Cell = 0.2f; // lattice resolution: the 20 cm the whole design hangs on
    public int PoolPf; // the pool playfield the lattices were rasterized from
    public string SourceHash = ""; // cheap provenance marker (chunk counts) for the cache file
    public Dictionary<int, NorthboundRoom> Rooms = new(); // pool room index -> its north-aligned lattice

    /// <summary>
    ///     The pool grid for a playfield: load the cached file, or build it on first encounter
    ///     (a second or two, once per pool) and save it next to the bins. Null on any failure -
    ///     the caller falls back to the per-mission triangle path.
    /// </summary>
    public static NorthboundPool? For(string pluginDir, int poolPf, Action<string> log)
    {
        var path = Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "poolgrid.northbound");
        if (File.Exists(path))
        {
            try
            {
                return Load(path); // the cache hit: the whole point of the offline precalculation
            }
            catch (Exception ex)
            {
                log?.Invoke($"NORTHBOUND: {path} unreadable ({ex.Message}) - rebuilding.");
            }
        }

        try
        {
            var pool = NavDungeon.Read(Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), "rooms.json")); // the pool's room list + tile data
            List<List<float[]>?> ReadBin(string file) // instances are sparse: a room index may have no chunk
            {
                var byRoom = new List<List<float[]>?>();
                var bp = Path.Combine(AOBuddyNav.FolderFor(pluginDir, poolPf), file);
                if (!File.Exists(bp))
                {
                    return byRoom;
                }

                // regroup the bin's chunks by their low 16 instance bits: one entry per pool room
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
            var wallsBin = ReadBin("walls.bin");
            var built = new NorthboundPool { PoolPf = poolPf, Cell = 0.2f, SourceHash = $"{collision.Count}x{wallsBin.Count}" };
            var count = 0;
            foreach (var poolRoom in pool.Rooms)
            {
                var collisions = poolRoom.Index < collision.Count ? collision[poolRoom.Index] : null;
                var walls = poolRoom.Index < wallsBin.Count ? wallsBin[poolRoom.Index] : null;
                if (collisions == null && walls == null)
                {
                    continue; // no geometry for this room type at all
                }

                // rasterize this pool room's triangles into its 20 cm lattice
                var room = NorthboundBuilder.Build(poolRoom.Index, poolRoom, collisions ?? new List<float[]>(), walls ?? new List<float[]>(),
                    AOBuddyNav.DoorwaysFromField(poolRoom), pool);
                if (room == null)
                {
                    continue;
                }

                built.Rooms[poolRoom.Index] = room;
                count++;
            }

            if (count == 0)
            {
                return null; // nothing built: the caller falls back to the triangle path
            }

            built.Save(path); // cache for every future mission of this pool
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
        using var fileStream = File.OpenRead(path);
        using var zStream = new ZlibStream(fileStream, CompressionMode.Decompress); // the file is zlib-compressed
        using var reader = new BinaryReader(zStream);
        if (new string(reader.ReadChars(4)) != Magic)
        {
            throw new InvalidDataException($"{path}: not a northbound pool grid");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"{path}: not a northbound v{Version} pool grid");
        }

        var pool = new NorthboundPool
        {
            PoolPf = reader.ReadInt32(),
            Cell = reader.ReadSingle(),
            SourceHash = reader.ReadString(),
        };
        var n = reader.ReadInt32();
        for (var ri = 0; ri < n; ri++)
        {
            // one room lattice: its frame first, then the four per-cell layers, then the portals
            var r = new NorthboundRoom
            {
                RoomIndex = reader.ReadInt32(),
                Ox = reader.ReadSingle(),
                Oz = reader.ReadSingle(),
                W = reader.ReadInt32(),
                H = reader.ReadInt32(),
            };
            var cells = r.W * r.H;
            r.Levels = new List<float>[cells];
            for (var cellIndex = 0; cellIndex < cells; cellIndex++)
            {
                var count = reader.ReadInt32();
                if (count > 0)
                {
                    var l = new List<float>(count);
                    for (var k = 0; k < count; k++)
                    {
                        l.Add(reader.ReadSingle());
                    }

                    r.Levels[cellIndex] = l; // 0 = void: the entry stays null
                }
            }

            r.MeshLevels = new List<float>[cells]; // same layout, mesh-datum heights
            for (var c = 0; c < cells; c++)
            {
                var count = reader.ReadInt32();
                if (count > 0)
                {
                    var l = new List<float>(count);
                    for (var k = 0; k < count; k++)
                    {
                        l.Add(reader.ReadSingle());
                    }

                    r.MeshLevels[c] = l;
                }
            }

            var blockedBytes = reader.ReadInt32();
            r.Blocked = new bool[cells];
            for (var b = 0; b < blockedBytes; b++)
            {
                var bits = reader.ReadByte(); // blocked as a bitmap, LSB-first
                for (var bit = 0; bit < 8 && b * 8 + bit < cells; bit++)
                {
                    if ((bits & (1 << bit)) != 0)
                    {
                        r.Blocked[b * 8 + bit] = true;
                    }
                }
            }

            r.Hug = reader.ReadBytes(cells);
            if (r.Hug.Length < cells)
            {
                r.Hug = new byte[cells]; // truncated file: no hug info, cost stays flat
            }

            var portals = reader.ReadInt32();
            for (var p = 0; p < portals; p++)
            {
                var portal = new NorthboundPortal
                {
                    X = reader.ReadDouble(),
                    Z = reader.ReadDouble(),
                    Nx = reader.ReadDouble(),
                    Nz = reader.ReadDouble(),
                    MeshLevel = reader.ReadSingle(),
                };
                var pc = reader.ReadInt32();
                for (var q = 0; q < pc; q++)
                {
                    portal.Cells.Add((reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle())); // (lattice x, lattice y, level at that cell)
                }

                if (version >= 4)
                {
                    var mlc = reader.ReadInt32();
                    for (var q = 0; q < mlc; q++)
                    {
                        portal.MeshLevels.Add(reader.ReadSingle());
                    }
                }

                r.Portals.Add(portal);
            }

            pool.Rooms[r.RoomIndex] = r;
        }

        return pool;
    }

    public void Save(string path)
    {
        // exact mirror of Load, field for field (zlib BestSpeed: load speed beats ratio here)
        using var fileStream = File.Create(path);
        using var zStream = new ZlibStream(fileStream, CompressionMode.Compress, CompressionLevel.BestSpeed);
        using var writer = new BinaryWriter(zStream);
        writer.Write(Magic.ToCharArray());
        writer.Write(Version);
        writer.Write(PoolPf);
        writer.Write(Cell);
        writer.Write(SourceHash ?? "");
        writer.Write(Rooms.Count);
        foreach (var r in Rooms.Values)
        {
            writer.Write(r.RoomIndex);
            writer.Write(r.Ox);
            writer.Write(r.Oz);
            writer.Write(r.W);
            writer.Write(r.H);
            var cells = r.W * r.H;
            // per-cell tile-sourced levels (absolute heights); a 0 count marks a void cell
            for (var cellIndex = 0; cellIndex < cells; cellIndex++)
            {
                var l = r.Levels[cellIndex];
                if (l == null || l.Count == 0)
                {
                    writer.Write(0);
                    continue;
                }

                writer.Write(l.Count);
                foreach (var v in l)
                {
                    writer.Write(v);
                }
            }

            // mesh-sourced levels, pool-mesh datum (the blit shifts them onto the door level)
            for (var cellIndex = 0; cellIndex < cells; cellIndex++)
            {
                var l = r.MeshLevels[cellIndex];
                if (l == null || l.Count == 0)
                {
                    writer.Write(0);
                    continue;
                }

                writer.Write(l.Count);
                foreach (var v in l)
                {
                    writer.Write(v);
                }
            }

            // blocked as a bitmap, LSB-first, (cells + 7) / 8 bytes
            writer.Write((cells + 7) / 8);
            for (var cellIndex = 0; cellIndex < cells; cellIndex += 8)
            {
                byte bits = 0;
                for (var bit = 0; bit < 8 && cellIndex + bit < cells; bit++)
                {
                    if (r.Blocked[cellIndex + bit])
                    {
                        bits |= (byte)(1 << bit);
                    }
                }

                writer.Write(bits);
            }

            // the hug cost byte per cell
            for (var cellIndex = 0; cellIndex < cells; cellIndex++)
            {
                writer.Write(cellIndex < r.Hug.Length ? r.Hug[cellIndex] : (byte)0);
            }

            // the door portals: centre, normal, mesh anchor level, their cells
            writer.Write(r.Portals.Count);
            foreach (var portal in r.Portals)
            {
                writer.Write(portal.X);
                writer.Write(portal.Z);
                writer.Write(portal.Nx);
                writer.Write(portal.Nz);
                writer.Write(portal.MeshLevel);
                writer.Write(portal.MeshLevels.Count);
                foreach (var ml in portal.MeshLevels)
                {
                    writer.Write(ml);
                }

                writer.Write(portal.Cells.Count);
                foreach (var (cx, cy, level) in portal.Cells)
                {
                    writer.Write(cx);
                    writer.Write(cy);
                    writer.Write(level);
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
    public int RoomIndex; // the pool room index this lattice belongs to
    public float Ox, Oz; // world position of lattice cell (0,0) - north-aligned, pool frame
    public int W, H; // lattice size in 20 cm cells

    /// <summary>Tile-sourced levels, ABSOLUTE (the heightfield carries the placed world's own
    /// heights). These never shift.</summary>
    public List<float>[] Levels = new List<float>[0];

    /// <summary>Mesh-sourced levels, in the pool mesh's OWN datum - which sits metres off the
    /// tile datum (grey_mh4: floor 13.1 over a 5.0 base). The blit shifts them by
    /// placedDoorY − portal.MeshLevel before merging; a mesh level without that shift is a
    /// different floor than it claims to be.</summary>
    public List<float>[] MeshLevels = new List<float>[0];
    public bool[] Blocked = new bool[0]; // per cell: no passage even where a level exists (wall band, no headroom, erosion)

    /// <summary>Per-cell hug cost, 0-255: how close the cell sits to a wall/column/void
    /// (255 = on it). The blit turns it into the A*'s per-cell penalty, so routes naturally
    /// run the centres of rooms and hallways. Precalculated - zero runtime cost.</summary>
    public byte[] Hug = new byte[0];
    public List<NorthboundPortal> Portals = new(); // the room's doorways, in pool frame

    // world position -> packed lattice cell index (the 0.2f is this class's own Cell, hardcoded)
    public int CellIndex(float x, float z) =>
        (int)Math.Floor((z - Oz) / 0.2f) * W + (int)Math.Floor((x - Ox) / 0.2f);
}

// One doorway of a pool room, recorded in the lattice's own frame.
public sealed class NorthboundPortal
{
    public double X, Z, Nx, Nz; // the doorway centre and outward normal, pool frame
    public List<(int cx, int cy, float level)> Cells = new(); // the portal's cells and their level
    public float MeshLevel = float.NaN; // the portal's floor in the MESH datum - legacy single anchor (nearest-first)

    /// <summary>Every distinct mesh level of the portal's cells, nearest-first. A cave socket
    /// carries the sill AND ledges above it; the blit picks the candidate nearest the placed
    /// door's level - the server's own truth names the sill (see FloorGrid.UseNorthbound).</summary>
    public List<float> MeshLevels = new();
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
    private const float WalkNy = 0.5f;   // the extractor's own walkable cutoff: flatter than 60 deg
                                         // is a surface - cave shell floors are 41-60 degree slopes
    private const float BodyLow = 0.3f, BodyHigh = 1.9f;
    private const float MergeLevels = 0.25f;
    private const int MaxLevels = 8;
    private const float DoorAcross = 1.25f, DoorAlong = 3.5f; // as FloorGrid.StampDoorways

    public static NorthboundRoom? Build(int roomIndex, NavDungeon.Room pr, List<float[]> collision, List<float[]> walls, List<AOBuddyNav.Doorway> doorways, NavDungeon pool)
    {
        // bounds over everything the room carries
        float minx = float.MaxValue, minz = float.MaxValue, maxx = float.MinValue, maxz = float.MinValue;
        void Take(float[] v)
        {
            // widen the XZ bounding box by every vertex of this triangle soup
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

        // THE TILE FOOTPRINT bounds into the lattice (owner, 2026-10-09): a room's tiles can
        // reach past its mesh chunks (the start room: 12 m of tiles over ~11 m of chunks) - a
        // lattice clipped at the chunk edge cut the room's own floor AND its wall ring with it,
        // which read as "two walls without hug gradient".
        if (pr.Pos != null)
        {
            // the two far corners of the tile rect, in pool world: two 1-vertex "triangles" for Take
            double mx = (pr.Rect[0] + pr.Rect[2] + 1) / 2.0, mz = (pr.Rect[1] + pr.Rect[3] + 1) / 2.0;
            Take(new[] { (float)(pr.Pos[0] + (pr.Rect[0] - mx) * pool.Cell), 0f,
                          (float)(pr.Pos[2] + (pr.Rect[1] - mz) * pool.Cell) });
            Take(new[] { (float)(pr.Pos[0] + (pr.Rect[2] + 1 - mx) * pool.Cell), 0f,
                          (float)(pr.Pos[2] + (pr.Rect[3] + 1 - mz) * pool.Cell) });
        }

        if (minx > maxx)
        {
            return null; // nothing bounded the room: no lattice
        }

        const float cell = 0.2f;
        // the lattice frame: origin one row/column of padding beyond the bounding box, 2 cells of margin
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
        var tiled = new bool[r.W * r.H];                // the room's own tile footprint (the wall ring's inner edge)
        var portalCells = new HashSet<int>();           // doorway cells: exempt from the wall ring

        // A steep triangle (Rasterize sends everything flatter-than-WalkNy fails, plus all of
        // walls.bin here) marks the cells it would crash a standing body into.
        //
        // THE TEST, per cell: a walker standing on floor level `lv` occupies the body band
        // [lv + BodyLow, lv + BodyHigh] = [lv + 0.3, lv + 1.9] metres - knees to head; below
        // 0.3 is foot/ground clearance, above 1.9 is over the head. This triangle occupies the
        // vertical span [yMin, yMax] somewhere over the cell. The two intervals OVERLAP when
        //
        //     yMax >= lv + BodyLow   (the wall reaches up past the body's knees)   AND
        //     yMin <= lv + BodyHigh  (the wall reaches down below the body's head),
        //
        // and then the body cannot cross this cell at that level: blocked. The everyday cases
        // fall out correctly: a lintel whose whole span sits above lv + 1.9 fails the second
        // test (walked under); a sill below lv + 0.3 fails the first (stepped over); a pillar
        // or wall face spanning both crosses the band (blocked).
        //
        // DELIBERATE COARSENESS - this is the offline lattice, it can afford to over-block:
        //  * The Y span is the triangle's WHOLE [yMin, yMax], not the plane height at the cell
        //    centre: a tall slanted wall whose top leans out over neighbouring cells blocks
        //    those too. For walls, blocking too much just routes the A* around; blocking too
        //    little walks the bot into geometry.
        //  * The projection test is the cell CENTRE alone (InXZ at the centre). A steep
        //    triangle clipping only a cell corner is not seen. Surfaces use five probes (see
        //    Surface) because a missed floor punches a hole in the map; a missed wall sliver
        //    merely leaves a cell open, and real walls are wide against a 20 cm cell.
        //
        // GRANULARITY: blocked is ONE bool per cell (unlike FloorGrid's per-(cell,floor)
        // verdicts). The first level whose band the triangle crosses seals the cell at ALL its
        // levels; the blit (FloorGrid.UseNorthbound) then blocks every level the merged world
        // cell ends up with. Doorway cells are un-blocked again afterwards by the portal pass
        // below - the door frame's own triangles must never seal the threshold they belong to.
        //
        // ORDERING: Blocker only ever runs inside the two Rasterize passes, BEFORE the tile
        // stamp fills `levels` - so in practice `l` is null at every call and the band test
        // judges the triangle-sampled (mesh) levels; the read of `levels` keeps the helper
        // general. Tile floors join later and stand or fall by the wall-ring seeds, the
        // portals and the blit-time headroom rule instead.
        void Blocker(float[] v, int o)
        {
            // the triangle's vertical span: the Y coordinate of each vertex (offset +1 in every
            // vertex triplet within the flat array)
            var (yMin, yMax) = MinMax(v, o + 1);
            // the cell bounding box to visit. The LOW bound rides on vertex A alone, the HIGH
            // bound on max(B, C): valid for the bins' vertex order, where A is the min corner.
            // (The visited range must cover the triangle - a too-narrow range would silently
            // skip cells, since InXZ never runs outside it; too wide only wastes iterations.)
            int i0 = XCell(v[o]), i1 = Math.Max(XCell(v[o + 3]), XCell(v[o + 6]));
            int j0 = ZCell(v[o + 2]), j1 = Math.Max(ZCell(v[o + 5]), ZCell(v[o + 8]));
            for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++) // clamped to the lattice
            {
                for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                {
                    double cx = r.Ox + (i + 0.5f) * cell, cz = r.Oz + (j + 0.5f) * cell; // the cell's centre, pool world
                    if (!InXZ(v, o, cx, cz))
                    {
                        continue; // the projection misses this cell's centre: the wall is not over this cell
                    }

                    var l = levels[j * r.W + i]; // tile levels (still null during the raster passes - see ORDERING)
                    var m = mesh[j * r.W + i];   // triangle-sampled levels, filling as the collision pass runs
                    if (l == null && m == null)
                    {
                        continue; // nothing to stand on here: nothing to block
                    }

                    // test every level the cell carries; one crossing is enough
                    foreach (var lv in (l ?? Enumerable.Empty<float>()).Concat(m ?? Enumerable.Empty<float>()))
                    {
                        if (yMax >= lv + BodyLow && yMin <= lv + BodyHigh) // interval overlap: wall span vs body band
                        {
                            blocked[j * r.W + i] = true; // the wall's body crosses the standing band: cell sealed
                            break;                        // per-cell verdict - no other level can change it
                        }
                    }
                }
            }
        }

        // A walkable triangle (Rasterize sends everything with normal Y >= WalkNy here): lay a
        // floor level into every cell the triangle passes over, at the plane's own height
        // there. This is what turns a ramp into a slope - one level per 20 cm of run - instead
        // of a single flat 2 m tile step.
        //
        // WHICH LIST, and why: everything triangle-sampled lands in the MESH list,
        // collision.bin surfaces included - "mesh" here means "heights in the pool mesh's own
        // datum", which sits metres off the tile datum (grey_mh4: floor 13.1 over a 5.0 base).
        // The blit shifts the whole list onto the placed door level before merging
        // (FloorGrid.UseNorthbound). The tile stamp further down fills the separate,
        // never-shifted `levels` list; the two are only reconciled at blit time.
        //
        // NO HEADROOM RULE HERE: two levels closer than BodyHigh are not detected in the
        // builder - the table-top rule runs on the merged world stacks at blit time
        // (FloorGrid.StampHeadroom), once both sources have contributed.
        void Surface(float[] v, int o)
        {
            // the cell bounding box to visit - same vertex-order-dependent box as Blocker
            // (A is the min corner in the bins' vertex order; the visited range must cover the
            // triangle or cells are silently skipped)
            int i0 = XCell(v[o]), i1 = Math.Max(XCell(v[o + 3]), XCell(v[o + 6]));
            int j0 = ZCell(v[o + 2]), j1 = Math.Max(ZCell(v[o + 5]), ZCell(v[o + 8]));
            for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++) // clamped to the lattice
            {
                for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                {
                    double bx = r.Ox + i * cell, bz = r.Oz + j * cell; // the cell's MIN CORNER, pool world
                    // conservative coverage: centre + 4 inner corners - a cell centre alone falls
                    // in the seams between coplanar triangles and the floor map comes out
                    // Swiss-cheese (the static grid hit exactly that, Neutral Supermarket).
                    // (0.5, 0.5) is the centre; the 0.15/0.85 quartet probes well inside the
                    // cell, so a neighbour triangle's edge sliver cannot claim the cell while
                    // the triangle that truly covers it misses all five probes.
                    var hit = false;
                    var y = 0.0;
                    foreach (var (fx, fz) in new[]
                             {
                                 (0.5, 0.5), (0.15, 0.15), (0.85, 0.15), (0.15, 0.85), (0.85, 0.85)
                             })
                    {
                        double px = bx + fx * cell, pz = bz + fz * cell; // the probe point, pool world
                        if (InXZ(v, o, px, pz))
                        {
                            hit = true;
                            y = PlaneY(v, o, px, pz); // the plane's height AT that probe point; first hit wins (no averaging) -
                            break;                    // for a slope this is the height at the spot, which is the point
                        }
                    }

                    if (!hit)
                    {
                        continue; // none of the five probes sits over this triangle: it does not cover the cell
                    }

                    // insert the level into the cell's mesh list, ascending and deduped
                    var idx = j * r.W + i;
                    var l = mesh[idx] ??= new List<float>(); // first surface in a void cell: create the list
                    if (l.Count > 0 && y - l[^1] <= MergeLevels && y >= l[^1])
                    {
                        continue; // ascending insert; the near neighbour above is the last
                    }

                    var pos = l.FindLastIndex(x => x < y) + 1; // the insert position that keeps the list ascending
                    if (pos > 0 && y - l[pos - 1] <= MergeLevels)
                    {
                        continue; // a near-duplicate of the level just below: the same floor, sampled twice
                    }

                    l.Insert(pos, (float)y);
                    if (l.Count > MaxLevels)
                    {
                        l.RemoveAt(0); // cap at MaxLevels (8): drop the LOWEST - decks above matter more than the deep stack below
                    }
                }
            }
        }

        // per triangle: its own normal decides surface vs blocker (the bins arrive pre-split,
        // but a misfiled triangle lands where its normal says). The classifier both raster
        // passes below share: collision.bin goes through it FREE (every triangle judged by its
        // own normal - the 41-60 degree cave-slope band and the walls hiding in collision.bin
        // come out here), walls.bin FORCED (every triangle a blocker however it is wound -
        // walls.bin carries no walkable truth by convention).
        void Rasterize(float[] v, bool forceBlocker)
        {
            for (int o = 0; o + 8 < v.Length; o += 9) // flat triangle triplets: 9 floats = A(x,y,z) B(x,y,z) C(x,y,z)
            {
                // the two edge vectors u = B - A and w = C - A; their cross product is the face
                // normal, but only its Y component matters here ("how flat is this triangle?"),
                // so the X/Z components are never computed:
                //     (u x w).y = uz*wx - ux*wz
                // (the sign follows the winding: up-facing triangles yield positive ny)
                float ux = v[o + 3] - v[o], uy = v[o + 4] - v[o + 1], uz = v[o + 5] - v[o + 2];
                float wx = v[o + 6] - v[o], wy = v[o + 7] - v[o + 1], wz = v[o + 8] - v[o + 2];
                var ny = uz * wx - ux * wz; // the normal's Y: 0 for a vertical face, maximal for a flat floor
                var len = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz) * (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                // NOTE the denominator: |u|*|w|, NOT |u x w| (which is |u||w| * sin(apex angle)).
                // This is exactly the extractor's own classification formula, so a triangle
                // re-judged here at load lands in the same bucket the producer's pre-split put
                // it in. For skinny triangles (small apex angle) it reads STRICTER than a true
                // slope cosine - a near-vertical bias, the safe side for navigation.
                if (len < 1e-9f)
                {
                    continue; // degenerate triangle (a zero-length edge): nothing to raster
                }

                if (!forceBlocker && ny / len >= WalkNy)
                {
                    Surface(v, o); // flat enough (>= 0.5, i.e. flatter than ~60 degrees): walkable ground
                }
                else
                {
                   Blocker(v, o); // too steep (or walls.bin, forced): a wall
                }
            }
        }

        foreach (var v in collision)
        {
            Rasterize(v, forceBlocker: false); // collision classifies itself
        }

        foreach (var v in walls)
        {
            Rasterize(v, forceBlocker: true); // walls.bin is a wall by definition
        }

        // THE TILE FLOOR (StampRoomFloors, ported to the lattice): in these pools the walkable
        // room floor exists ONLY as the heightfield - collision.bin carries just the structures
        // above it (Startroom's centre: not one mesh triangle, tiles say 5.8). The tile heights
        // are absolute, so they need no door anchoring; the mesh's own datum rides alongside as
        // upper levels. Where a steep mesh band crosses a tile level's body band AND the mesh
        // offers its own level within a step, the tile yields (the stairway-tread rule); a tile
        // with no mesh alternative stands even under a lintel or an upper deck.
        if (pr.Tile != null && pr.Height != null && pr.Rect != null && pr.Pos != null)
        {
            int x1 = pr.Rect[0], z1 = pr.Rect[1];
            double mx = (pr.Rect[0] + pr.Rect[2] + 1) / 2.0, mz = (pr.Rect[1] + pr.Rect[3] + 1) / 2.0; // the tile grid's centre
            for (int tr = 0; tr < pr.Tile.Length; tr++)
            {
                var trow = pr.Tile[tr];
                if (trow == null)
                {
                    continue;
                }

                for (int tc = 0; tc < trow.Length; tc++)
                {
                    var hrow = pr.Height[tr];
                    if (trow[tc] == 0 || hrow == null || tc >= hrow.Length)
                    {
                        continue; // no tile, or no height for it
                    }

                    // the tile centre in pool world, and its absolute floor height
                    double wxp = pr.Pos[0] + (x1 + tc - mx) * pool.Cell;
                    double wzp = pr.Pos[2] + (z1 + tr - mz) * pool.Cell;
                    var th = (float)(pr.Pos[1] + (hrow[tc] - pr.HeightBase) * pool.HeightScale);
                    // the tile (pool.Cell metres square) covers these lattice sub-cells
                    int i0 = XCell((float)(wxp - pool.Cell / 2)), i1 = XCell((float)(wxp + pool.Cell / 2));
                    int j0 = ZCell((float)(wzp - pool.Cell / 2)), j1 = ZCell((float)(wzp + pool.Cell / 2));
                    for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                    {
                        for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                        {
                            var idx = j * r.W + i;
                            tiled[idx] = true; // the room's own tile: the wall ring's inner edge
                            var l = levels[idx] ??= new List<float>();
                            // MERGE: neighbouring heightfield cells differ by ~0.2 (5.0 vs 5.2) -
                            // that is one floor, and an unmerged pair would let the headroom rule
                            // block the lower half of the room's own floor
                            if (l.Count > 0 && Math.Abs(l[^1] - th) <= 0.3f)
                            {
                                continue;
                            }

                            var pos = l.FindLastIndex(x => x < th) + 1; // keep the list ascending
                            if (pos > 0 && th - l[pos - 1] <= 0.3f)
                            {
                                continue; // near-duplicate of the level just below
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
                // the keep-open box: ACROSS the normal is the doorway's width, ALONG it the
                // threshold strip (which axis is which follows from where the normal points)
                double ax = Math.Abs(dw.Nx) > 0.5 ? DoorAlong : DoorAcross;
                double az = Math.Abs(dw.Nz) > 0.5 ? DoorAlong : DoorAcross;
                int i0 = XCell((float)(dw.X - ax)), i1 = XCell((float)(dw.X + ax));
                int j0 = ZCell((float)(dw.Z - az)), j1 = ZCell((float)(dw.Z + az));
                for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                {
                    for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                    {
                        blocked[j * r.W + i] = false; // the door frame must never block its own threshold
                        portalCells.Add(j * r.W + i); // exempt from the hug wall ring below
                        var l = levels[j * r.W + i];
                        var m = mesh[j * r.W + i];
                        var level = l is { Count: > 0 } ? l[0] // the lowest tile level, else the lowest mesh level, else unknown
                            : m is { Count: > 0 } ? m[0]
                            : float.NaN;
                        portal.Cells.Add((i, j, level));
                        if (m is { Count: > 0 })
                        {
                            // every distinct mesh level of the portal's cells: a cave socket
                            // carries the sill AND ledges above it, and nearest-first ordering
                            // made some portals anchor on a ledge metres above the sill
                            foreach (var v in m)
                            {
                                if (!portal.MeshLevels.Any(x => Math.Abs(x - v) < 0.005f))
                                {
                                    portal.MeshLevels.Add(v);
                                }
                            }

                            if (float.IsNaN(portal.MeshLevel))
                            {
                                portal.MeshLevel = m[0]; // legacy single anchor: nearest-first
                            }
                        }
                    }
                }

                r.Portals.Add(portal);
            }
        }

        // THE HUG COST: multi-source brushfire from every wall cell (blocked, or void - no level
        // at all, or the WALL RING: a cell inside the lattice that the room's own tiles don't
        // cover is the wall assembly / the atlas neighbour, and its hug must grade into the
        // room the same way a wall triangle's does - owner, 2026-10-09: two of a square room's
        // four walls carried no gradient because the atlas neighbour's floor continued behind
        // them, leaving those sides seedless). Doorway cells are exempt - the doors are
        // walkable and their centres stay cheap. Distance in metres with diagonal steps; a
        // cell's hug rises linearly from 0 at HugRadius to full at the wall.
        var hug = new byte[r.W * r.H];
        {
            const float radius = 1.2f;                       // metres of "near the wall"
            var dist = new float[r.W * r.H];                 // per cell: metres to the nearest wall seed
            Array.Fill(dist, float.MaxValue);
            var queue = new Queue<int>();
            for (var c = 0; c < r.W * r.H; c++)
            {
                // a wall seed: blocked, void, or outside the room's own tile footprint (the wall
                // assembly / the atlas neighbour's floor continuing behind the wall)
                var wall = blocked[c] || levels[c] == null || levels[c].Count == 0 ||
                           (!tiled[c] && !portalCells.Contains(c));
                if (wall)
                {
                    dist[c] = 0;
                    queue.Enqueue(c);
                }
            }

            // Dijkstra brushfire over the 8 neighbours, diagonals cost sqrt(2) of the cell
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                var ci = c % r.W;
                var cj = c / r.W;
                for (var dj = -1; dj <= 1; dj++)
                {
                    for (var di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0)
                        {
                            continue;
                        }

                        var ni = ci + di;
                        var nj = cj + dj;
                        if (ni < 0 || nj < 0 || ni >= r.W || nj >= r.H)
                        {
                            continue;
                        }

                        var n = nj * r.W + ni;
                        var step = di != 0 && dj != 0 ? cell * 1.4142f : cell;
                        if (dist[c] + step < dist[n] - 1e-3f)
                        {
                            dist[n] = dist[c] + step;
                            queue.Enqueue(n); // re-relax: plain queue, not a priority queue
                        }
                    }
                }
            }

            // distance -> hug byte: 255 at the wall, linearly to 0 at radius
            for (var c = 0; c < hug.Length; c++)
            {
                if (dist[c] >= radius)
                {
                    continue;
                }

                hug[c] = (byte)Math.Round(255f * (radius - dist[c]) / radius);
            }
        }

        r.Levels = levels;
        r.MeshLevels = mesh;
        r.Blocked = blocked;
        r.Hug = hug;
        return r;

        // world coordinate -> lattice cell index (local helpers over the frame set up above)
        int XCell(float x) => (int)Math.Floor((x - r.Ox) / cell);
        int ZCell(float z) => (int)Math.Floor((z - r.Oz) / cell);
    }

    // min/max of one coordinate over a triangle's three vertices (o strides in floats)
    private static (float min, float max) MinMax(float[] v, int o)
    {
        var min = Math.Min(v[o], Math.Min(v[o + 3], v[o + 6]));
        var max = Math.Max(v[o], Math.Max(v[o + 3], v[o + 6]));
        return (min, max);
    }

    // Is (px, pz) inside the triangle's XZ projection? Three signed edge cross products: inside
    // when they do not disagree (sign-wise). Degenerate/edge-on points count as inside.
    private static bool InXZ(float[] v, int o, double px, double pz)
    {
        double ax = v[o], az = v[o + 2], bx = v[o + 3], bz = v[o + 5], cx = v[o + 6], cz = v[o + 8];
        double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz); // cross of edge AB with P-B
        double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz); // edge BC with P-C
        double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az); // edge CA with P-A
        var neg = d1 < 0 || d2 < 0 || d3 < 0;
        var pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos); // mixed signs = outside
    }

    // The triangle plane's height at (px, pz), from the plane equation through vertex a with the
    // cross-product normal. Flat-normal degeneracy returns a's height.
    private static double PlaneY(float[] v, int o, double px, double pz)
    {
        double ax = v[o], ay = v[o + 1], az = v[o + 2];
        double ux = v[o + 3] - ax, uy = v[o + 4] - ay, uz = v[o + 5] - az;
        double wx = v[o + 6] - ax, wy = v[o + 7] - ay, wz = v[o + 8] - az;
        double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
        if (Math.Abs(ny) < 1e-9)
        {
            return ay; // a vertical plane: no height to interpolate
        }

        return ay - (nx * (px - ax) + nz * (pz - az)) / ny; // solve n . (p - a) = 0 for y
    }
}
