using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
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
    public const int Version = 14; // v14: the OWN-COMPONENT mask - flood from the tiles across standable steps; a lattice cell is the room's when connected (the tile rects do not bound the real rooms: WildCave_Lesser8_5's entrance tube runs ~8 m past its rect, and the atlas spill sits behind walls, disconnected - rectangles cannot separate them, connectivity can; owner's room-5 seal) // v13: the levels come from RECAST (DotRecast), spans filtered by slope 60 / clearance 1.9 / climb 0.8; the tile quads UP-WOUND + mesh windings normalized // v12: the column clip; v11: diagonal intersection (reverted); v10: the interval model, tile sheets in the tile frame, the Tile==0 guard; v9: the band test; v6: water stamps; v5: MissionStep cutoff; v4: portal MeshLevels; v3: OmniCell sockets // bump when the binary layout OR the decode changes (Load refuses other versions)

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

            if (version >= 14)
            {
                var ownBytes = reader.ReadInt32();
                r.Own = new bool[cells];
                for (var b = 0; b < ownBytes; b++)
                {
                    var bits = reader.ReadByte(); // the own-component mask, LSB-first
                    for (var bit = 0; bit < 8 && b * 8 + bit < cells; bit++)
                    {
                        if ((bits & (1 << bit)) != 0)
                        {
                            r.Own[b * 8 + bit] = true;
                        }
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
                if (version >= 4)
                {
                    var mlc = reader.ReadInt32();
                    for (var q = 0; q < mlc; q++)
                    {
                        portal.MeshLevels.Add(reader.ReadSingle()); // the sill candidates - Save wrote them right after MeshLevel
                    }
                }

                var pc = reader.ReadInt32();
                for (var q = 0; q < pc; q++)
                {
                    portal.Cells.Add((reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle())); // (lattice x, lattice y, level at that cell)
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

            // the own-component mask as a bitmap (v14): the cells flood-connected to the tiles
            writer.Write((cells + 7) / 8);
            for (var cellIndex = 0; cellIndex < cells; cellIndex += 8)
            {
                byte bits = 0;
                for (var bit = 0; bit < 8 && cellIndex + bit < cells; bit++)
                {
                    if (cellIndex + bit < r.Own.Length && r.Own[cellIndex + bit])
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

    /// <summary>Per cell: the OWN-COMPONENT mask (v14) - the cells flood-connected to the
    /// room's tiles across standable steps. The tile rects do not bound the real rooms (an
    /// entrance tube can run metres past its rect) and the atlas spill sits behind walls,
    /// disconnected: connectivity separates a room's own floor from the spill, rectangles
    /// cannot. The blit admits levels for own cells; spill stays out.</summary>
    public bool[] Own = new bool[0];

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
///     Builds one pool room's northbound lattice from its collision/walls chunks with the
///     INTERVAL MODEL (the owner's raycast proposal, 2026-10-11, discretised): every triangle
///     whose footprint covers a 20 cm cell contributes a solid Y interval - flat faces their
///     plane range over the cell, steep faces their full vertical span - and the merged runs
///     answer walkability by topology alone. A walkable level is the top of a solid run with
///     at least BodyHigh of open space above it; inside a wall there is no such run, a
///     chest-high bar leaves too little headroom, a lintel enough, an arch deck adds an upper
///     level. The tile heightfield joins as thin sheets in the same frame (the mesh-vs-tile
///     offset verified +0.01/spread 0.00 across pools 341 and 351), carrying the floor where
///     the mesh is silent. Door portals are marked last: their keep-open boxes are unblocked
///     and their tile floor restored (the template's door leaf sits IN the passage; the
///     server opens it at runtime).
/// </summary>
public static class NorthboundBuilder
{
    private const float WalkNy = 0.5f;   // the extractor's own walkable cutoff: flatter than 60 deg
                                         // is a surface - cave shell floors are 41-60 degree slopes (the bow
                                         // bridges' decks at ny 0.40-0.45 are crossed ON THE WATER instead -
                                         // see FloorGrid.UseNorthbound's water surface)
    private const float BodyLow = 0.3f, BodyHigh = 1.9f;
    private const float MergeLevels = 0.25f;
    private const int MaxLevels = 8;
    private const float DoorAcross = 1.25f, DoorAlong = 3.5f; // as FloorGrid.StampDoorways

    public static NorthboundRoom? Build(int roomIndex, NavDungeon.Room pr, List<float[]> collision, List<float[]> walls, List<AOBuddyNav.Doorway> doorways, NavDungeon pool, bool recast = true, bool tilesInRecast = true)
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
            double mx = (pr.Rect[0] + pr.Rect[2]) / 2.0, mz = (pr.Rect[1] + pr.Rect[3]) / 2.0; // the EXCLUSIVE tile-grid convention (mesh-vs-tile overhang symmetry, 2026-10-11)
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
        var runs = new List<(float y0, float y1)>[r.W * r.H]; // per cell: the solid Y intervals collected from every covering triangle
        var tileQuads = new List<float[]>();            // the tile floor as flat triangles (the Recast producer's floor source)
        var levels = new List<float>[r.W * r.H];        // the walkable run tops per cell, ABSOLUTE - the mesh chunks sit in the tile frame (verified 2026-10-11: the mesh-vs-tile offset is +0.01 with spread 0.00 at every socket of every room in pools 341 and 351; the old "pool mesh datum" was rim sampling at the portals)
        var blocked = new bool[r.W * r.H];
        var tileH = new float[r.W * r.H];               // the tile height per lattice cell (the keep-open restore needs it)
        var tiled = new bool[r.W * r.H];                // the room's own tile footprint (the wall ring's inner edge)
        var portalCells = new HashSet<int>();           // doorway cells: exempt from the wall ring

        // THE INTERVAL MODEL (the owner's raycast proposal, 2026-10-11: "walk each x/z/y and
        // cast rays up/down and north/south/east/west - if we're inside the geometry, the
        // triangle winding says the point is not walkable"), discretised to interval
        // arithmetic. Every triangle whose footprint covers a 20 cm cell contributes ONE solid
        // Y interval to it:
        //   * a FLAT face (normal Y >= WalkNy, the extractor's own 60-degree cutoff) contributes
        //     the plane's height range over the cell - a floor sheet, a ramp its local slope;
        //   * a STEEP face contributes its full vertical span - a wall, a column, the bow
        //     bridges' 63-66 degree decks (their spans float far over the water crossing and
        //     block nothing there).
        // Merge the intervals and the cell reads as a column of SOLID RUNS with open gaps
        // between them, and every question we ever band-tested falls out of the topology:
        // inside a wall the runs span the full height (no open gap, no level); a chest-high
        // bar leaves less than BodyHigh above the floor (no level at floor height); a lintel
        // leaves BodyHigh underneath (walked under); an arch deck adds an upper run (walkable
        // on it, the floor below still walkable under the gap). The winding/backface idea is
        // honoured implicitly: a cell INSIDE a solid has run tops only at the solid's outer
        // surfaces, never at the floor the walker needs.
        void Contribute(float[] v)
        {
            for (int o = 0; o + 8 < v.Length; o += 9) // flat triangle triplets: 9 floats = A(x,y,z) B(x,y,z) C(x,y,z)
            {
                // the face normal's Y - the same extractor formula the old Rasterize used, but
                // it now decides only the CONTRIBUTION SHAPE (plane range vs full span), never
                // walkability itself
                float ux = v[o + 3] - v[o], uy = v[o + 4] - v[o + 1], uz = v[o + 5] - v[o + 2];
                float wx = v[o + 6] - v[o], wy = v[o + 7] - v[o + 1], wz = v[o + 8] - v[o + 2];
                var len = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz) * (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                if (len < 1e-9f)
                {
                    continue; // degenerate triangle (a zero-length edge)
                }

                // (the normal is no longer used for classification: the column clip below is
                // exact for every orientation - floors, ramps, verticals, ceilings alike)
                // the cell bounding box to visit. The LOW bound rides on vertex A alone, the
                // HIGH bound on max(B, C): valid for the bins' vertex order, where A is the
                // min corner. Too narrow would silently skip cells; too wide only wastes clips.
                int i0 = XCell(v[o]), i1 = Math.Max(XCell(v[o + 3]), XCell(v[o + 6]));
                int j0 = ZCell(v[o + 2]), j1 = Math.Max(ZCell(v[o + 5]), ZCell(v[o + 8]));
                for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++) // clamped to the lattice
                {
                    for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                    {
                        double bx = r.Ox + i * cell, bz = r.Oz + j * cell; // the cell's MIN CORNER, pool world

                        // THE CONTRIBUTION IS THE TRIANGLE CLIPPED TO THE CELL'S VERTICAL
                        // COLUMN (owner, 2026-10-11: "you have to do it at y levels -
                        // flattening would add geometry from the ceiling, which also has
                        // verticals"). The triangle is clipped against the four half-spaces
                        // x>=bx, x<=bx+c, z>=bz, z<=bz+c (Sutherland-Hodgman, Y interpolated
                        // along clipped edges); the survivor's Y range is the interval. This is
                        // exact for every orientation at once:
                        //   * a flat floor yields its true local sheet - nothing extrapolates
                        //     past the footprint, nothing is missed between probe points;
                        //   * a vertical wall (ZERO XZ area - no point test can see it) survives
                        //     clipping where its line crosses the column, carrying its local
                        //     height range - boundary-riding, strictly-interior and
                        //     corner-clipping faces alike (the diagonal hitting-set rule this
                        //     generalises: any chord of the square crosses a diagonal);
                        //   * a tube-ceiling panel passing 8 m overhead clips to ITS height
                        //     there - its span no longer flattens down onto the floor cell
                        //     (the v10 attempt did exactly that and sealed every tube floor).
                        var (yr, hit) = ClipColumn(v, o, bx, bz, cell);
                        if (!hit)
                        {
                            continue; // the triangle misses this cell entirely
                        }

                        (runs[j * r.W + i] ??= new List<(float y0, float y1)>()).Add((yr.y0, yr.y1));
                    }
                }
            }
        }

        foreach (var v in collision)
        {
            Contribute(v); // collision classifies itself by its own normals
        }

        foreach (var v in walls)
        {
            Contribute(v); // walls.bin joins the same column - a wall is just tall solid runs
        }

        // THE TILE FLOOR as corner-interpolated quads in the same solid column: the heightfield
        // is the room's floor where the mesh carries nothing at all (the Startroom's centre:
        // not one mesh triangle, tiles say 5.8), and it merges with the mesh runs freely - ONE
        // frame, verified (the probe found +0.01 with spread 0.00 at every socket of every room
        // in pools 341 and 351; the old separate "pool mesh datum" was the portals sampling rim
        // ledges as the floor, fixed by the MeshLevels candidate lists).
        //
        // CORNER INTERPOLATION (owner, 2026-10-11: "the tiles will be quads"): the rendered
        // floor is a (w+1)x(h+1) VERTEX grid, not flat 2 m plates - each tile is a quad whose
        // four corners take the mean of the adjacent tiles' heights, so a step between
        // neighbouring tiles becomes a slope the width of the shared corner instead of a cliff.
        if (pr.Tile != null && pr.Height != null && pr.Rect != null && pr.Pos != null)
        {
            int x1 = pr.Rect[0], z1 = pr.Rect[1];
            int rows = pr.Tile.Length, cols = pr.Tile.Max(t => t?.Length ?? 0);
            double mx = (pr.Rect[0] + pr.Rect[2]) / 2.0, mz = (pr.Rect[1] + pr.Rect[3]) / 2.0; // the EXCLUSIVE tile-grid convention (mesh-vs-tile overhang symmetry, 2026-10-11) // the tile grid's centre

            // pass 1: the tile heights (with the sentinel guard), NaN where no tile
            var thG = new float[rows + 2, cols + 2];
            var okG = new bool[rows + 2, cols + 2];
            for (int tr = 0; tr < rows; tr++)
            {
                var trow = pr.Tile[tr];
                if (trow == null)
                {
                    continue;
                }

                for (int tc = 0; tc < trow.Length; tc++)
                {
                    var hrow = pr.Height[tr];
                    // Tile == 0 is NO TILE: the void and the wall band inside the rect. The
                    // sheet pass must skip it exactly like the old stamp did - stamping the
                    // zeros laid phantom floor over every void and across every room seam
                    // (owner, 2026-10-11: "everything inside the room rectangle is walkable").
                    if (trow[tc] == 0 || hrow == null || tc >= hrow.Length)
                    {
                        continue; // no tile, or no height for it
                    }

                    var th = (float)(pr.Pos[1] + (hrow[tc] - pr.HeightBase) * pool.HeightScale);
                    // GARBAGE HEIGHT SENTINELS: some heightfield entries are not floors -
                    // h far outside any plausible relief decodes to th tens of kilometres up
                    // (pool 341: th ~104,804 on sentinel tiles), and a phantom sheet up there
                    // turns every wall column into a walkable "sky" level in Recast. The
                    // room's real floors live within ~30 m of Pos[1] (the recorded walks span
                    // floors 0..-2 at most); anything further is data noise, not floor.
                    if (Math.Abs(th - pr.Pos[1]) > 30f)
                    {
                        continue;
                    }

                    thG[tr + 1, tc + 1] = th;
                    okG[tr + 1, tc + 1] = true;
                }
            }

            // pass 2: the corner grid - vertex (i,j) = the mean of the adjacent tiles' heights
            var cG = new float[rows + 1, cols + 1];
            for (var jj = 0; jj <= rows; jj++)
            {
                for (var ii = 0; ii <= cols; ii++)
                {
                    float sum = 0;
                    var n = 0;
                    for (var dj = 0; dj <= 1; dj++)
                    {
                        for (var di = 0; di <= 1; di++)
                        {
                            if (okG[jj + dj, ii + di])
                            {
                                sum += thG[jj + dj, ii + di];
                                n++;
                            }
                        }
                    }

                    cG[jj, ii] = n > 0 ? sum / n : float.NaN;
                }
            }

            // pass 3: per tile, the quad over its four corners (up-wound: Recast's
            // MarkWalkableTriangles uses the SIGNED normal - the (0,0)->(1,0)->(1,1) order is
            // down-facing and its spans came out NULL-area, wiping the tile floor from every
            // Recast lattice - owner, 2026-10-11: "not one cell is on the mission floor level")
            for (int tr = 0; tr < rows; tr++)
            {
                var trow = pr.Tile[tr];
                if (trow == null)
                {
                    continue;
                }

                for (int tc = 0; tc < trow.Length; tc++)
                {
                    if (trow[tc] == 0 || !okG[tr + 1, tc + 1])
                    {
                        continue; // no tile
                    }

                    var th = thG[tr + 1, tc + 1]; // the tile's own flat height: the sill/band reference
                    double wxp = pr.Pos[0] + (x1 + tc - mx) * pool.Cell;
                    double wzp = pr.Pos[2] + (z1 + tr - mz) * pool.Cell;
                    // the tile (pool.Cell metres square) covers these lattice sub-cells
                    int i0 = XCell((float)(wxp - pool.Cell / 2)), i1 = XCell((float)(wxp + pool.Cell / 2));
                    int j0 = ZCell((float)(wzp - pool.Cell / 2)), j1 = ZCell((float)(wzp + pool.Cell / 2));

                    var y00 = cG[tr, tc];
                    var y10 = cG[tr, tc + 1];
                    var y11 = cG[tr + 1, tc + 1];
                    var y01 = cG[tr + 1, tc];
                    var lo = Math.Min(Math.Min(y00, y10), Math.Min(y11, y01));
                    var hi2 = Math.Max(Math.Max(y00, y10), Math.Max(y11, y01));

                    for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                    {
                        for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                        {
                            var idx = j * r.W + i;
                            tiled[idx] = true; // the room's own tile: the wall ring's inner edge
                            tileH[idx] = th; // the keep-open restore reads it back
                            (runs[idx] ??= new List<(float y0, float y1)>()).Add((lo - 0.05f, hi2 + 0.05f)); // the sloped sheet's range
                        }
                    }

                    var hh = (float)(pool.Cell / 2);
                    tileQuads.Add(new[]
                    {
                        (float)(wxp - hh), y00, (float)(wzp - hh),
                        (float)(wxp - hh), y01, (float)(wzp + hh),
                        (float)(wxp + hh), y11, (float)(wzp + hh),
                    });
                    tileQuads.Add(new[]
                    {
                        (float)(wxp - hh), y00, (float)(wzp - hh),
                        (float)(wxp + hh), y11, (float)(wzp + hh),
                        (float)(wxp + hh), y10, (float)(wzp - hh),
                    });
                }
            }
        }

        if (recast)
        {
            // THE RECAST PRODUCER (owner, 2026-10-11: "why don't we use Recast?"): the full
            // triangle set - collision, walls, the tile sheets - voxelizeed at 0.2 m, spans
            // merged by the MissionStep climb, filtered by the 60-degree slope (the extractor's
            // own WalkNy) and the 1.9 m agent clearance. A standable level = a compact span
            // whose area survived the filters; its top is the level. Verticals need no special
            // handling (voxelization marks their columns), ceilings stay ceilings (their spans
            // carry their own heights), and nothing of the geometry is dropped by any bar.
            var (lvR, blR) = RecastLevels(r.W, r.H, r.Ox, r.Oz, collision, walls, tilesInRecast ? tileQuads : new List<float[]>());
            levels = lvR;
            blocked = blR;
        }
        else
        {

        // MERGE the intervals per cell and read the walkable levels off the run stack: a
        // walkable level is the TOP of a solid run that has at least BodyHigh of open space
        // above it (the next run starts more than BodyHigh higher, or nothing is above at
        // all). Runs are merged with a 1 cm touch tolerance - two samplings of the same floor
        // (the tile sheet and the collision sheet) coalesce into one run.
        for (var c = 0; c < r.W * r.H; c++)
        {
            var l = runs[c];
            if (l == null || l.Count == 0)
            {
                continue; // geometry-free cell: void
            }

            l.Sort((a, b) => a.y0.CompareTo(b.y0));
            var m = new List<(float y0, float y1)> { l[0] };
            for (var k = 1; k < l.Count; k++)
            {
                if (l[k].y0 <= m[^1].y1 + 0.01f)
                {
                    m[^1] = (m[^1].y0, Math.Max(m[^1].y1, l[k].y1)); // touching/overlapping: extend the run
                }
                else
                {
                    m.Add(l[k]); // an open gap: the next solid run
                }
            }

            List<float> lv = null;
            for (var k = 0; k < m.Count; k++)
            {
                var top = m[k].y1;
                if (k + 1 < m.Count && m[k + 1].y0 - top <= BodyHigh)
                {
                    continue; // a run too close above: no standing room (the chest-bar rule; also the table-top rule)
                }

                if (lv is { Count: > 0 } && top - lv[^1] <= MergeLevels)
                {
                    continue; // the same floor sampled twice (the tops ascend with the runs)
                }

                lv ??= new List<float>();
                lv.Add(top);
                if (lv.Count > MaxLevels)
                {
                    lv.RemoveAt(0); // cap at MaxLevels (8): drop the LOWEST - decks above matter more than the deep stack below
                }
            }

            levels[c] = lv!; // null stays null: a cell with no standable level
        }

        // BLOCKED is the honest topology: a cell with no standable level at all - fully inside
        // a wall, or filled solid to the ceiling. Cells whose only levels are wall tops keep
        // those levels (the A* can never step up to them) and the hug pass paints the wall
        // band from the untiled seeds, so no extra band test is needed anywhere.
        for (var c = 0; c < r.W * r.H; c++)
        {
            blocked[c] = levels[c] == null;
        }
        }

        // THE OWN COMPONENT (owner, 2026-10-11): the tile rects do not bound the real rooms -
        // WildCave_Lesser8_5's entrance tube runs ~8 m past its rect, and the atlas spill sits
        // behind other rooms' walls. Rectangles cannot separate a room's own floor from the
        // spill; connectivity can: flood from the tiles across standable steps (any level pair
        // within the walk's 0.8 m climb). The component is the room; the blit admits levels for
        // own cells (plus the rect band and the portals) and drops the rest as spill.
        const float stepLimit = 0.8f;
        var own = new bool[r.W * r.H];
        {
            var q = new Queue<int>();
            for (var c = 0; c < r.W * r.H; c++)
            {
                if (tiled[c] && levels[c] is { Count: > 0 })
                {
                    own[c] = true;
                    q.Enqueue(c);
                }
            }

            while (q.Count > 0)
            {
                var c = q.Dequeue();
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
                        if (own[n] || levels[n] == null)
                        {
                            continue;
                        }

                        var steppable = false;
                        foreach (var a in levels[c])
                        {
                            foreach (var b in levels[n])
                            {
                                if (Math.Abs(a - b) <= stepLimit)
                                {
                                    steppable = true;
                                    break;
                                }
                            }

                            if (steppable)
                            {
                                break;
                            }
                        }

                        if (steppable)
                        {
                            own[n] = true;
                            q.Enqueue(n);
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
                // the box's sill: the lowest tile floor among its tiled cells - the level the
                // server's door sits at in this frame. The untiled threshold cells (the seam
                // strip between the two rooms) restore to it as well.
                var sill = float.NaN;
                for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                {
                    for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                    {
                        if (tiled[j * r.W + i] && (float.IsNaN(sill) || tileH[j * r.W + i] < sill))
                        {
                            sill = tileH[j * r.W + i];
                        }
                    }
                }

                for (var j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                {
                    for (var i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                    {
                        blocked[j * r.W + i] = false; // the door frame must never block its own threshold
                        portalCells.Add(j * r.W + i); // exempt from the hug wall ring below
                        var l = levels[j * r.W + i];
                        // THE CLOSED-DOOR LEAF: in the template the door sits IN the passage (the
                        // server opens it at runtime), so the leaf's solid run merges with the
                        // sill sheet and the run top names the leaf top, not the floor. Restore
                        // the crossing level: the tile floor on tiled cells, the box sill on the
                        // untiled threshold strip (whose leaf geometry the column clip now
                        // catches exactly - the 5-point probes used to miss it and let the sill
                        // survive by luck).
                        var th = tiled[j * r.W + i] ? tileH[j * r.W + i] : sill;
                        if (!float.IsNaN(th))
                        {
                            if (l == null)
                            {
                                l = levels[j * r.W + i] = new List<float> { th };
                            }
                            else if (!l.Any(x => Math.Abs(x - th) <= MergeLevels))
                            {
                                var pos = l.FindLastIndex(x => x < th) + 1; // keep the list ascending
                                l.Insert(pos, th);
                                if (l.Count > MaxLevels)
                                {
                                    l.RemoveAt(0);
                                }
                            }
                        }

                        var level = l is { Count: > 0 } ? l[0] : float.NaN;
                        portal.Cells.Add((i, j, level));
                        if (l is { Count: > 0 })
                        {
                            // every distinct level of the portal's cells: a cave socket carries
                            // the sill AND ledges above it, and nearest-first ordering made some
                            // portals anchor on a ledge metres above the sill (grey_mh7: 8.10
                            // over 5.01). The blit picks the candidate nearest the server's own
                            // door level - the sill the server names.
                            foreach (var v in l)
                            {
                                if (!portal.MeshLevels.Any(x => Math.Abs(x - v) < 0.005f))
                                {
                                    portal.MeshLevels.Add(v);
                                }
                            }

                            if (float.IsNaN(portal.MeshLevel))
                            {
                                portal.MeshLevel = l[0]; // legacy single anchor: nearest-first
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
        r.MeshLevels = new List<float>[r.W * r.H]; // single frame since the interval model: every level is absolute in Levels; the array stays for the file format and the blit's vestigial mesh path
        r.Blocked = blocked;
        r.Own = own;
        r.Hug = hug;
        return r;

        // world coordinate -> lattice cell index (local helpers over the frame set up above)
        int XCell(float x) => (int)Math.Floor((x - r.Ox) / cell);
        int ZCell(float z) => (int)Math.Floor((z - r.Oz) / cell);
    }

    // THE RECAST PRODUCER: voxelize the room's full triangle set at the lattice resolution and
    // read the standable levels off the compact heightfield. Parameters are today's rules in
    // Recast's vocabulary: slope 60 degrees (the extractor's WalkNy), agent clearance 1.9 m
    // (BodyHigh), climb 0.8 m (MissionStep), no radius erosion (the 20 cm cell is the margin,
    // the world grid's step rules and hug do the rest). A standable level = a compact span
    // whose area survived the filters; its top is the level. Verticals are free (voxelization
    // marks their columns), ceilings stay at their own heights (no flattening), and nothing is
    // lost to any height bar - the caller may pass the FULL RDB mesh.
    private static (List<float>[] levels, bool[] blocked) RecastLevels(int w, int h, float ox, float oz,
        List<float[]> collision, List<float[]> walls, List<float[]> tileQuads)
    {
        // THE TILE BAND (checked per triangle at collection, so verts/faces stay paired): the
        // room's tile floors are the server's own truth about where the room lives vertically.
        // The raw vertex range is not - the atlas spill carries outliers (a y~104,860 structure
        // in pool 341) that blow the heightfield's Y bounds up, and every vertical wall then
        // rasterizes as a solid column to the "sky" whose top reads as a walkable level (the
        // first Recast sweep: levels 104859 x 14808). Triangles fully outside the band are
        // atlas garbage and never enter the build.
        var lo = float.MinValue;
        var hi = float.MaxValue;
        if (tileQuads.Count > 0)
        {
            var tmin = float.MaxValue;
            var tmax = float.MinValue;
            foreach (var q in tileQuads)
            {
                tmin = Math.Min(tmin, q[1]);
                tmax = Math.Max(tmax, q[1]);
            }

            lo = tmin - 15f; // the dips reach ~10 m down
            hi = tmax + 40f; // the pools' tallest interior structures reach ~27 m up
        }

        var verts = new List<float>();
        var faces = new List<int>();
        void AddTri(float[] v)
        {
            if (lo != float.MinValue)
            {
                var inside = false;
                for (var k = 1; k < 9; k += 3)
                {
                    if (v[k] >= lo && v[k] <= hi)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                {
                    return; // fully outside the tile band: atlas garbage, not this room
                }
            }

            // DOWN-FACING TRIANGLES ARE EXCLUDED: the bins carry render-order triangles in both
            // windings, and a down-wound face is a CEILING/underside - nothing a walker stands
            // on. Feeding them (flipped up, an earlier attempt) made their undersides walkable
            // area: the shell's underside carpeted every column under it, and the bogus levels
            // punched holes into the tile-ramp spans under the shell (owner's room-5 seal).
            // Their headroom role is minor at these ceiling heights; the up-facing geometry
            // carries the room.
            float ux = v[3] - v[0], uy = v[4] - v[1], uz = v[5] - v[2];
            float wx = v[6] - v[0], wy = v[7] - v[1], wz = v[8] - v[2];
            var ny = uz * wx - ux * wz;
            if (ny < 0)
            {
                return; // a ceiling/underside: not standable, not wanted
            }

            var b = verts.Count / 3;
            for (var k = 0; k < 9; k++)
            {
                verts.Add(v[k]);
            }

            faces.Add(b);
            faces.Add(b + 1);
            faces.Add(b + 2);
        }

        foreach (var arr in collision)
        {
            for (int o = 0; o + 8 < arr.Length; o += 9)
            {
                AddTri(arr.AsSpan(o).ToArray());
            }
        }

        foreach (var arr in walls)
        {
            for (int o = 0; o + 8 < arr.Length; o += 9)
            {
                AddTri(arr.AsSpan(o).ToArray());
            }
        }

        foreach (var q in tileQuads)
        {
            AddTri(q);
        }

        float ymin = float.MaxValue, ymax = float.MinValue;
        for (var k = 1; k < verts.Count; k += 3)
        {
            ymin = Math.Min(ymin, verts[k]);
            ymax = Math.Max(ymax, verts[k]);
        }

        // the heightfield's Y bounds come from the TILE BAND, never the raw vertex range: a
        // wall triangle with one vertex at the floor and one at y=104,860 passes the per-
        // triangle filter, and raw bounds would let its column rasterize to the "sky"
        // (rasterization clamps spans into the bounds - a sky wall just ends at the ceiling).
        var hfLo = lo != float.MinValue ? lo - 1f : ymin - 1f;
        var hfHi = hi != float.MaxValue ? hi + 1f : ymax + 2f;

        const float cs = 0.2f, chh = 0.1f;
        var bmin = new RcVec3f(ox, hfLo, oz);
        var bmax = new RcVec3f(ox + w * cs, hfHi, oz + h * cs);
        var cfg = new RcConfig(false, 0, 0, 0, RcPartition.MONOTONE,
            cs, chh,
            agentMaxSlope: 60f, agentHeight: 1.9f, agentRadius: 0f, agentMaxClimb: 0.8f,
            minRegionArea: 1f, mergeRegionArea: 1f,
            edgeMaxLen: 12f, edgeMaxError: 1.3f, vertsPerPoly: 6,
            detailSampleDist: 0f, detailSampleMaxError: 0f,
            filterLowHangingObstacles: true,
            // filterLedgeSpans OFF: a floor passing UNDER a rim/ledge is flanked by columns
            // whose only walkable span is the rim top metres up - the filter reads that as a
            // deadly drop and nulls the corridor floor (the recorded walks crossed under
            // grey_mh4's rim at y 5; the filter erased exactly those spans). The world grid's
            // own step rules and hug judge real drops.
            filterLedgeSpans: false, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA), buildMeshDetail: false);
        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        var result = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, bmin, bmax), true);
        var chf = result.CompactHeightfield ?? throw new InvalidOperationException("recast: no compact heightfield");

        var levels = new List<float>[w * h];
        var blocked = new bool[w * h];
        for (var j = 0; j < Math.Min(h, chf.height); j++)
        {
            for (var i = 0; i < Math.Min(w, chf.width); i++)
            {
                var cell = chf.cells[i + chf.width * j];
                List<float> lv = null;
                for (var s = 0; s < cell.count; s++)
                {
                    var idx = cell.index + s;
                    if (chf.areas[idx] == RcRecast.RC_NULL_AREA)
                    {
                        continue; // slope- or clearance-filtered: not standable
                    }

                    var span = chf.spans[idx];
                    // DOTRECAST SPAN CONVENTION (the source, RcCompacts.BuildCompactHeightfield):
                    // y = bot = the heightfield span's smax - the STANDABLE TOP; h = the open
                    // clearance above, to the next span or RC_SPAN_MAX_HEIGHT when nothing is
                    // above. (Classic Recast reads y as the bottom and h as the height - reading
                    // DotRecast that way turned every open-sky span into a phantom level tens of
                    // kilometres up.)
                    var top = bmin.Y + span.y * chh;
                    if (lv is { Count: > 0 } && top - lv[^1] <= 0.25f)
                    {
                        continue; // the same floor sampled twice
                    }

                    lv ??= new List<float>();
                    lv.Add(top);
                    if (lv.Count > MaxLevels)
                    {
                        lv.RemoveAt(0);
                    }
                }

                levels[j * w + i] = lv;
                blocked[j * w + i] = lv == null;
            }
        }

        return (levels, blocked);
    }

    // THE COLUMN CLIP: intersect the triangle (v, o) with the cell's vertical column
    // [bx,bx+c] x [bz,bz+c] (Sutherland-Hodgman over the four X/Z half-spaces, Y interpolated
    // along clipped edges) and return its Y range there. Exact for every orientation: a flat
    // floor yields its true local sheet; a vertical wall (zero XZ area - nothing point-based
    // can see it) survives wherever its line crosses the column, boundary-riding or not (the
    // hair of epsilon keeps the wall-on-tile-line case); a ceiling panel clips to ITS height
    // over the cell instead of flattening its whole span onto the floor below (owner,
    // 2026-10-11: "you have to do it at y levels"). The column is inflated by a hair so the
    // boundary cases survive; hit=false when nothing survives.
    private static ((float y0, float y1), bool hit) ClipColumn(float[] v, int o, double bx, double bz, double c)
    {
        const double e = 1e-9;
        var cur = new[]
        {
            new[] { (double)v[o], (double)v[o + 1], (double)v[o + 2] },
            new[] { (double)v[o + 3], (double)v[o + 4], (double)v[o + 5] },
            new[] { (double)v[o + 6], (double)v[o + 7], (double)v[o + 8] },
        };
        var tmp = new double[7][];
        var nxt = new double[7][];
        for (var k = 0; k < 7; k++)
        {
            tmp[k] = new double[3];
            nxt[k] = new double[3];
        }

        var n = 3;
        n = ClipHalf(cur, tmp, n, 0, bx - e, bx + c + e);
        n = ClipHalf(tmp, nxt, n, 2, bz - e, bz + c + e);
        if (n == 0)
        {
            return ((0f, 0f), false);
        }

        n = ClipHalf(nxt, tmp, n, 0, bx - e, bx + c + e);
        if (n == 0)
        {
            return ((0f, 0f), false);
        }

        n = ClipHalf(tmp, nxt, n, 2, bz - e, bz + c + e);
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

    // One Sutherland-Hodgman pass: keep/clips the polygon src[0..n) against axis in [lo,hi]
    // into dst; returns the new vertex count (at most n+1).
    private static int ClipHalf(double[][] src, double[][] dst, int n, int axis, double lo, double hi)
    {
        const double e = 1e-9;
        var m = 0;
        for (var i = 0; i < n; i++)
        {
            var p = src[i];
            var q = src[(i + 1) % n];
            var pin = p[axis] >= lo && p[axis] <= hi;
            var qin = q[axis] >= lo && q[axis] <= hi;
            if (pin)
            {
                dst[m++] = p;
                if (qin)
                {
                    continue;
                }
            }
            else if (!qin)
            {
                continue; // both outside this half-strip: the edge contributes nothing
            }

            dst[m++] = LerpToPlane(p, q, axis, lo, hi); // the edge crosses a boundary: add the crossing
        }

        return m;
    }

    // The point on pq where it crosses the boundary plane lo or hi of the axis (whichever the
    // edge actually passes).
    private static double[] LerpToPlane(double[] p, double[] q, int axis, double lo, double hi)
    {
        var plane = p[axis] < lo || q[axis] < lo ? lo : hi;
        var t = (plane - p[axis]) / (q[axis] - p[axis]);
        return new[]
        {
            p[0] + t * (q[0] - p[0]),
            p[1] + t * (q[1] - p[1]),
            p[2] + t * (q[2] - p[2]),
        };
    }
}
