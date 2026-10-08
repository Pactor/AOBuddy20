// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: NavData.cs
//
// Last modified: 2026-10-03
// Created:       2026-10-01 (ported from AOBuddy10 AOBuddyNav.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using System.IO.Compression;
using System.Linq;
using System.Text;
using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     Reader for the per-playfield navigation data in GameData/Nav/&lt;pf&gt;/ (formats: GameData/Nav/README.md,
///     how they were found: AOBuddy10's NAV-CLIENTDATA.md). Loads a playfield's outdoor heightfield, dungeon
///     room list and near-horizontal collision triangles, and answers "where is the floor under (x, z)?".
///     Nothing here moves the body.
///     THREADING (owner, 2026-10-01): a loaded instance is IMMUTABLE - NavGround builds its fallback water
///     eagerly in Read, so every query (HeightAt, SwimY, FloorNear, HeightsUnder) is a pure read and any
///     thread may call them. Loading happens once per playfield, off-thread, in NavGridCache.
/// </summary>
public sealed class AOBuddyNav
{
    public readonly int Playfield;
    public readonly string Kind; // outdoor / dungeon / none
    public readonly string Name;
    public readonly NavGround Ground; // outdoor only
    public readonly NavDungeon Dungeon; // dungeon only
    public readonly NavCollision Collision; // whenever the client has surfaces for the zone
    public MissionLayout Layout; // a mission instance only: the zone-in placement it was composed from
    public List<Doorway> MissionDoorways = new List<Doorway>(); // a mission instance only: every placed
    // room's doorways to neighbours, world coordinates - the walk grid keeps these cells open (the pool
    // walls.bin stamps the door leaves solid, and nothing else in the data says a doorway is a passage)

    private AOBuddyNav(int pf, string kind, string name, NavGround g, NavDungeon d, NavCollision c)
    {
        Playfield = pf;
        Kind = kind;
        Name = name;
        Ground = g;
        Dungeon = d;
        Collision = c;
    }

    public static string FolderFor(string pluginDir, int pf) => Path.Combine(pluginDir, "GameData", "Nav", pf.ToString());

    /// <summary>One 90° turn of (x, z) about the origin, per turn count: (x, z) -> (-z, x).</summary>
    private static (double x, double z) turned(double x, double z, int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            var t = x;
            x = -z;
            z = t;
        }

        return (x, z);
    }

    /// <summary>
    ///     A STATIC dungeon's room doorways in world coordinates - every doors entry of every room,
    ///     including the inner (shop section) doors that <see cref="DoorwaysFromField" /> skips for
    ///     missions. The geometry floor centre follows the AOBuddy10 mission rule, WITH the room's
    ///     rotation applied to the parity: centre = pos + turned(-1 m on an even-sized axis, 0 on an
    ///     odd one) (a room's floor is (w-1) x (h-1) cells - the rect's last column and row are the
    ///     cell it shares with its neighbour). Verified on 1187 Neutral Supermarket Advanced: with
    ///     the turned parity, both rooms of every door pair decode to the SAME world point (before,
    ///     every rot-3 room's doors sat (−1,+1) off its rot-0 neighbour's - the unturned parity).
    ///     The walk grid uses these as keep-open cells: walls.bin stamps the door leaves and frames
    ///     solid, and nothing else in the data says a doorway is a passage (2026-10-02: the shop's
    ///     section doors walled the bot into the entrance room).
    ///     Door codes: row = code / (4*(W-1)), col = code % (4*(W-1)); row 0 south, row H-2 north
    ///     (col = half-metres across), col 3 west, col 4*(W-1)-3 east (row = 2 m cell row, +1 m for
    ///     the door's centre line).
    /// </summary>
    public static List<Doorway> StaticDoorways(NavDungeon d)
    {
        var doorways = new List<Doorway>();
        if (d?.Rooms == null)
        {
            return doorways;
        }

        foreach (var rm in d.Rooms)
        {
            if (rm?.Rect == null || rm.Pos == null || rm.Doors == null)
            {
                continue;
            }

            int x1 = rm.Rect[0], z1 = rm.Rect[1], x2 = rm.Rect[2], z2 = rm.Rect[3];
            int w = x2 - x1 + 1, h = z2 - z1 + 1, stride = 4 * (w - 1);
            if (stride <= 0)
            {
                continue;
            }

            var turns = ((-rm.Rot) % 4 + 4) % 4;
            // the geometry floor centre: pos turned-parity corrected (see summary)
            double pcx = w % 2 == 0 ? -1 : 0, pcz = h % 2 == 0 ? -1 : 0;
            var (pcxt, pczt) = turned(pcx, pcz, turns);
            double fcx = rm.Pos[0] + pcxt, fcz = rm.Pos[2] + pczt;

            foreach (var door in rm.Doors)
            {
                if (door == null || door.Length < 2)
                {
                    continue;
                }

                int row = door[1] / stride, col = door[1] % stride;
                double lx, lz;
                if (row == 0)
                {
                    lx = col / 2.0;
                    lz = 0;
                }
                else if (row >= h - 2)
                {
                    lx = col / 2.0 + 1;
                    lz = 2.0 * (h - 1);
                }
                else if (col == 3)
                {
                    lx = 0;
                    lz = row * 2 + 1;
                }
                else if (col == stride - 3)
                {
                    lx = 2.0 * (w - 1);
                    lz = row * 2 + 1;
                }
                else
                {
                    continue; // not on an edge: no confident position, leave the cells as they are
                }

                double ex = lx - (w - 1), ez = lz - (h - 1);
                double nx = 0, nz = 0;
                if (row == 0)
                {
                    nz = -1;
                }
                else if (row >= h - 2)
                {
                    nz = 1;
                }
                else if (col == 3)
                {
                    nx = -1;
                }
                else
                {
                    nx = 1;
                }

                for (var i = 0; i < turns; i++)
                {
                    (ex, ez) = (-ez, ex);
                    (nx, nz) = (-nz, nx);
                }

                // both records of a shared doorway decode to the same world point (the turned parity
                // made them exact) - keep one; the opposite normal adds nothing downstream
                if (doorways.Any(o => Math.Abs(o.X - (fcx + ex)) < 0.25 && Math.Abs(o.Z - (fcz + ez)) < 0.25))
                {
                    continue;
                }

                doorways.Add(new Doorway
                {
                    X = fcx + ex,
                    Y = rm.Pos[1],
                    Z = fcz + ez,
                    Nx = nx,
                    Nz = nz,
                    Floor = 0,
                });
            }
        }

        return doorways;
    }

    /// <summary>Load one playfield's folder. Returns null when there is no folder for it.</summary>
    public static AOBuddyNav Load(string pluginDir, int pf)
    {
        var dir = FolderFor(pluginDir, pf);
        var infoPath = Path.Combine(dir, "info.json");
        if (!File.Exists(infoPath))
        {
            return null;
        }

        var info = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(infoPath));
        var kind = info.TryGetValue("kind", out var k) ? k as string : "none";
        var name = info.TryGetValue("name", out var n) ? n as string : "";
        NavGround g = null;
        NavDungeon d = null;
        NavCollision c = null;
        var gp = Path.Combine(dir, "ground.bin");
        var rp = Path.Combine(dir, "rooms.json");
        var cp = Path.Combine(dir, "collision.bin");
        if (File.Exists(gp))
        {
            g = NavGround.Read(gp);
        }

        if (File.Exists(rp))
        {
            d = NavDungeon.Read(rp);
        }

        if (File.Exists(cp))
        {
            c = NavCollision.Read(cp);
        }

        return new AOBuddyNav(pf, kind, name, g, d, c);
    }

    /// <summary>
    ///     The floor height under (x, z) nearest to a known height y (the character's own Y, say):
    ///     tiles/heightfield first, then collision triangles. NaN when nothing is under the point.
    ///     OUTDOOR ANCHOR (owner, 2026-10-03: "y is off, too high" - the walk staircased up the
    ///     trees): with a heightfield, a collision floor more than AnchorMetres above or below it is
    ///     a branch or a canopy, not the ground under a walker, and is not a candidate. The
    ///     heightfield itself always is - it is the top surface, so bridge decks and wall walks sit
    ///     IN it. Dungeons (no heightfield) consider everything, as before.
    /// </summary>
    public const double AnchorMetres = 1.5;

    public double FloorNear(double x, double y, double z, out string source)
    {
        double best = double.NaN;
        var src = "none";
        void Consider(double h, string s)
        {
            if (!double.IsNaN(h) && (double.IsNaN(best) || Math.Abs(h - y) < Math.Abs(best - y)))
            {
                best = h;
                src = s;
            }
        }

        double anchor = Ground?.HeightAt(x, z) ?? double.NaN;
        if (Ground != null)
        {
            Consider(anchor, "ground");
        }

        if (Dungeon != null)
        {
            foreach (var rm in Dungeon.Rooms)
            {
                double h = Dungeon.FloorHeight(rm, x, z);
                if (!double.IsNaN(h) && (double.IsNaN(anchor) || Math.Abs(h - anchor) <= AnchorMetres))
                {
                    Consider(h, "room " + rm.Index + (rm.Name.Length > 0 ? " " + rm.Name : ""));
                }
            }
        }

        if (Collision != null)
        {
            foreach (double h in Collision.HeightsUnder(x, z))
            {
                if (double.IsNaN(anchor) || Math.Abs(h - anchor) <= AnchorMetres)
                {
                    Consider(h, "collision");
                }
            }
        }

        source = src;
        return best;
    }

    /// <summary>Everything the data says about a point, for the 'navdata' command.</summary>
    public string Explain(double x, double y, double z)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("pf {0} {1} ({2}): ", Playfield, Name, Kind);
        if (Ground != null)
        {
            double h = Ground.HeightAt(x, z);
            sb.AppendFormat("ground {0} tile {1} bld {2}; ", double.IsNaN(h) ? "outside" : h.ToString("0.00"), Ground.TileAt(x, z), Ground.BuildingAt(x, z));
            sb.AppendFormat("water {0}; ", Ground.WaterInfo());
        }

        if (Dungeon != null)
        {
            var hits = 0;
            foreach (var rm in Dungeon.Rooms)
            {
                double h = Dungeon.FloorHeight(rm, x, z);
                if (double.IsNaN(h))
                {
                    continue;
                }

                hits++;
                if (hits <= 3)
                {
                    sb.AppendFormat("room {0} '{1}' floor {2:0.00}; ", rm.Index, rm.Name, h);
                }
            }

            if (hits == 0)
            {
                sb.Append("no room tile here; ");
            }
        }

        if (Collision != null)
        {
            var hs = new List<double>(Collision.HeightsUnder(x, z));
            hs.Sort();
            sb.AppendFormat("collision {0} surface(s)", hs.Count);
            for (var i = 0; i < hs.Count && i < 6; i++)
            {
                sb.AppendFormat(" {0:0.00}", hs[i]);
            }

            sb.Append("; ");
        }

        double best = FloorNear(x, y, z, out var src);
        sb.AppendFormat("nearest floor to y={0:0.00}: {1} ({2})", y, double.IsNaN(best) ? "none" : best.ToString("0.00"), src);
        return sb.ToString();
    }

    /// <summary>
    ///     The acceptance test the formats were built with: every point in the bot's own walked record for
    ///     this playfield (nav/&lt;pf&gt;.json) against the data, within tol metres.
    /// </summary>
    public string SelfTest(string navFile, double tol = 1.0)
    {
        if (!File.Exists(navFile))
        {
            return "no walked record " + navFile;
        }

        var zone = JsonConvert.DeserializeObject<WalkedFile>(File.ReadAllText(navFile));
        var total = 0;
        var floor = 0;
        var coll = 0;
        var none = 0;
        var samples = new List<string>();
        if (zone?.Segments != null)
        {
            foreach (var seg in zone.Segments)
            {
                foreach (var p in seg)
                {
                    if (p == null || p.Length < 3)
                    {
                        continue;
                    }

                    total++;
                    double x = p[0], y = p[1], z = p[2];
                    var ok = false;
                    if (Ground != null)
                    {
                        double h = Ground.HeightAt(x, z);
                        ok = !double.IsNaN(h) && Math.Abs(h - y) <= tol;
                    }
                    else if (Dungeon != null)
                    {
                        foreach (var rm in Dungeon.Rooms)
                        {
                            double h = Dungeon.FloorHeight(rm, x, z);
                            if (!double.IsNaN(h) && Math.Abs(h - y) <= tol)
                            {
                                ok = true;
                                break;
                            }
                        }
                    }

                    if (ok)
                    {
                        floor++;
                        continue;
                    }

                    if (Collision != null)
                    {
                        foreach (double h in Collision.HeightsUnder(x, z))
                        {
                            if (Math.Abs(h - y) <= tol)
                            {
                                ok = true;
                                break;
                            }
                        }
                    }

                    if (ok)
                    {
                        coll++;
                        continue;
                    }

                    none++;
                    if (samples.Count < 5)
                    {
                        samples.Add(string.Format("({0:0},{1:0.0},{2:0})", x, y, z));
                    }
                }
            }
        }

        if (total == 0)
        {
            return "walked record has no points";
        }

        return string.Format("{0} walked points: {1} on floor data, {2} on collision, {3} unexplained ({4:0.0}% explained){5}",
            total, floor, coll, none, 100.0 * (total - none) / total, samples.Count > 0 ? " e.g. " + string.Join(" ", samples) : "");
    }

    private sealed class WalkedFile
    {
        public List<List<float[]>> Segments;
    }

    // ---- missions ----------------------------------------------------------------------------------------
    // A mission instance is not in the client data. The zone-in packet (PlayfieldAnarchyF, version 4)
    // carries BuildingGeneratorData: the template playfield (a pool such as 320 Midtech), the building's
    // slot grid, the floor height, and one (room, floor, x, z, rotation) per placed room. Verified on three
    // pools against 425 walked points (NAV-CLIENTDATA.md): a placed room is the pool room with its stored
    // rotation, x origin = X * 10 m, far z edge = (gridHeight - Z) * 10 m, y = pool height + (floor - lowest floor) * worldHeight.

    public sealed class MissionLayout
    {
        public int Instance, TemplatePlayfield, Width, Height, WorldHeight;
        public float LandX, LandY, LandZ; // where the server put us on zone-in (the entrance)
        public List<int[]> Rooms = new List<int[]>(); // room, floor, x, z, rotation
    }

    /// <summary>Decode the building block of a raw zone-in packet; null when the packet has none (outdoor, static dungeon).</summary>
    public static MissionLayout DecodeZoneIn(byte[] b)
    {
        if (b == null || b.Length < 0x60)
        {
            return null;
        }

        int p = 0x10;
        int I32()
        {
            int v = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            p += 4;
            return v;
        }

        short I16()
        {
            short v = (short)((b[p] << 8) | b[p + 1]);
            p += 2;
            return v;
        }

        I32();
        I32();
        I32();
        p++; // message type, identity, unknown
        int version = I32(); // version, then the landing coordinates
        float F32()
        {
            int v = I32();
            return BitConverter.Int32BitsToSingle(v);
        }

        float lx = F32(), ly = F32(), lz = F32();
        if (version <= 3)
        {
            return null;
        }

        p++;
        int modelType = I32(), modelInst = I32();
        I32();
        I32();
        I32();
        I32();
        int genType = I32(), genInst = I32();
        if (genType != 51103)
        {
            return null; // 0xC79F ACGBuildingGeneratorData
        }

        I32(); // revision
        var m = new MissionLayout { Instance = modelInst, TemplatePlayfield = 0, LandX = lx, LandY = ly, LandZ = lz };
        I16();
        m.Width = I16();
        m.Height = I16();
        m.WorldHeight = I16();
        m.TemplatePlayfield = I32();
        p += 3;
        int n = I32();
        if (n < 0 || n > 512 || p + n * 6 > b.Length)
        {
            return null;
        }

        for (var i = 0; i < n; i++)
        {
            short room = I16();
            sbyte floor = (sbyte)b[p++];
            int x = b[p++], z = b[p++], rot = b[p++];
            m.Rooms.Add(new[] { room, floor, x, z, rot });
        }

        return m;
    }

    /// <summary>Compose a mission instance's rooms from its zone-in packet and the template pool's rooms.json.</summary>
    public static AOBuddyNav LoadMission(string pluginDir, byte[] zoneInPacket)
    {
        var m = DecodeZoneIn(zoneInPacket);
        return m == null ? null : ComposeMission(pluginDir, m);
    }

    /// <summary>Compose the placed rooms from a decoded layout and the pool's rooms.json.</summary>
    public static AOBuddyNav ComposeMission(string pluginDir, MissionLayout m)
    {
        var poolPath = Path.Combine(FolderFor(pluginDir, m.TemplatePlayfield), "rooms.json");
        if (!File.Exists(poolPath))
        {
            return null;
        }

        var pool = NavDungeon.Read(poolPath);
        const double slot = 10.0;
        // Floors are numbered as the server sends them, and a building can sit below its entrance:
        // the Grey Caves mission on 2026-09-23 had floors 0, -1, -2 walked at y 133, 69 and 0. The
        // heights count up from the lowest floor, not from floor 0 (all eight saved missions fit).
        var lowestFloor = int.MaxValue;
        foreach (var pr in m.Rooms)
        {
            lowestFloor = Math.Min(lowestFloor, pr[1]);
        }

        if (lowestFloor == int.MaxValue)
        {
            lowestFloor = 0;
        }

        var d = new NavDungeon { Playfield = m.Instance, Name = "mission from " + pool.Name, Tilemap = pool.Tilemap, Cell = pool.Cell, HeightScale = pool.HeightScale, Atlas = pool.Atlas, Rooms = new List<NavDungeon.Room>() };
        // A room's parked Pos[1] is where the pool atlas happens to park it, not its build height:
        // pool 341 parks WildCave_Long8_1 at y 3 while every room it joins sits at y 5, and the
        // server built it at 5 too (its door packet: (270,5,175) for a room composed at 3). Placed
        // at 3 its doorways bridge at 3 - level islands 2 m under the neighbours, sealed off in the
        // grid (Grey Caves 2026-10-08: the bot could not leave or enter that room at all). So each
        // floor takes its parking height from the FIRST room placed on it - floor 0's first room is
        // the entrance, whose level is where the server drops you - and every later room on that
        // floor is rebased onto it. The (floor - lowestFloor) stacking is unchanged: Grey Caves
        // 2026-09-23 walked floors 0/-1/-2 at y 133/69/0 with parkings 5/5/0.
        var floorPark = new Dictionary<int, float>();
        foreach (var pr in m.Rooms)
        {
            int idx = pr[0], floor = pr[1], X = pr[2], Z = pr[3], rot = pr[4];
            if (idx < 0 || idx >= pool.Rooms.Count)
            {
                continue;
            }

            var src = pool.Rooms[idx];
            if (!floorPark.TryGetValue(floor, out var park))
            {
                floorPark[floor] = park = src.Pos[1];
            }
            int w = src.Rect[2] - src.Rect[0] + 1, h = src.Rect[3] - src.Rect[1] + 1;
            int tw = rot % 2 == 0 ? w : h, th = rot % 2 == 0 ? h : w; // footprint after rotation
            double ox = X * slot, oz = (m.Height - Z) * slot - th * pool.Cell;
            // A room's floor is (w-1) x (h-1) cells: the last column and row of its rect are the cell it
            // shares with its neighbour. The room turns about its FLOOR's centre, and in the mission the
            // shared cell stays on the slot's +x / -z side (world z counts down from the grid's far edge).
            // Turning about the rect's centre instead swung the shared cell round with the room and put
            // rooms 1-2 m off by rotation (Ventil mission 2026-09-23, floor -1). With this every doorway
            // on all four floors meets its neighbour's, and of the owner's 226 walked steps there one
            // crosses a wall.
            int turnsT = ((-rot) % 4 + 4) % 4;
            (double, double) Turned(double x, double z)
            {
                for (int i = 0; i < turnsT; i++)
                {
                    (x, z) = (-z, x);
                }

                return (x, z);
            }

            var (gx, gz) = Turned(w % 2 == 0 ? 1 : 0, h % 2 == 0 ? 1 : 0);
            var (tx, tz) = Turned(1, 1);
            double cx = ox + tw * pool.Cell / 2.0 - 1, cz = oz + th * pool.Cell / 2.0 + 1;
            var y = park + (floor - lowestFloor) * m.WorldHeight;
            d.Rooms.Add(new NavDungeon.Room
            {
                Index = d.Rooms.Count, Name = src.Name + " f" + floor, PoolName = src.Name, PoolIndex = idx, Floor = floor, Flags = src.Flags, Rot = rot, Rect = src.Rect,
                Pos = new[] { (float)(cx + tx), y, (float)(cz + tz) },
                GeomPos = new[] { (float)(cx + gx), y, (float)(cz + gz) },
                HeightBase = src.HeightBase, Doors = src.Doors, Polys = src.Polys, Tile = src.Tile, Height = src.Height, Flags3 = src.Flags3,
            });
        }

        var nav = new AOBuddyNav(m.Instance, "mission", d.Name, null, d, null) { Layout = m, Walls = PlaceBin(pluginDir, m.TemplatePlayfield, pool, d, "walls.bin"),
            Surfaces = PlaceBin(pluginDir, m.TemplatePlayfield, pool, d, "collision.bin"),
        };
        var doors = PlaceDoors(pool, d);
        nav.MissionDoorways = doors.SelectMany(list => list).ToList();
        DoorCheck = CheckDoorways(d, doors);
        nav.Exit = FindExit(d, doors);
        return nav;
    }

    /// <summary>
    ///     A mission only: the door out of the building. The zone-in's landing point is the entrance only
    ///     when you come in from outside; a bot (re)started inside a running mission lands wherever it is,
    ///     so the exit comes from the building instead: the entrance room's doorway that opens onto no
    ///     neighbour.
    /// </summary>
    public Doorway Exit;

    public int ExitFloor => Exit == null ? 0 : Exit.Floor;

    private static Doorway FindExit(NavDungeon mission, List<Doorway>[] doors)
    {
        if (mission.Rooms.Count == 0)
        {
            return null;
        }

        Doorway best = null;
        foreach (var da in doors[0])
        {
            var faces = false;
            for (var b = 1; b < doors.Length && !faces; b++)
            {
                if (mission.Rooms[b].Floor != mission.Rooms[0].Floor)
                {
                    continue;
                }

                foreach (var db in doors[b])
                {
                    if (da.Nx * db.Nx + da.Nz * db.Nz < -0.9 && Math.Sqrt((da.X - db.X) * (da.X - db.X) + (da.Z - db.Z) * (da.Z - db.Z)) < 3.5)
                    {
                        faces = true;
                        break;
                    }
                }
            }

            if (!faces)
            {
                best = da;
                break;
            }
        }

        return best;
    }

    /// <summary>A mission only: every placed room's wall triangles (walls.bin of the pool), world coordinates, 9 floats each. Null when the pool has no walls.bin.</summary>
    public float[] Walls;

    /// <summary>A mission only: every placed room's WALKABLE collision triangles (collision.bin of the
    /// pool - floors, stairs, ramps, bridges, mezzanines; walls.bin is steep-only and holds none of
    /// them, owner 2026-10-03), world coordinates, 9 floats each. Null when the pool has no collision.bin.</summary>
    public float[] Surfaces;

    // The pool's walls.bin / collision.bin hold each pool room's triangles where the pool itself
    // places the room (record index = room index). Move them to where the mission put the room: the
    // offset from the pool room's centre, turned from the pool room's rotation to the mission's,
    // then added to the mission room's geometry pivot; heights shift with the floor. Walls and
    // doors turn about GeomPos BY DESIGN (Pos is the tiles' pivot) - the tile-convention round trip
    // was tried and failed its own check: 6/20 doorways met, misses of 1.0-1.4 m at every parity
    // mismatch (owner, 2026-10-03), where this convention met 20/20.
    private static float[] PlaceBin(string pluginDir, int poolPf, NavDungeon pool, NavDungeon mission, string file)
    {
        var wp = Path.Combine(FolderFor(pluginDir, poolPf), file);
        if (!File.Exists(wp))
        {
            return null;
        }

        var byRoom = new Dictionary<int, List<float[]>>();
        foreach (var c in NavCollision.Read(wp).Chunks)
        {
            int idx = c.Instance & 0xFFFF;
            if (!byRoom.TryGetValue(idx, out var l))
            {
                byRoom[idx] = l = new List<float[]>();
            }

            l.Add(c.Verts);
        }

        var n = mission.Rooms.Count;
        var tris = new List<float>();
        for (var ri = 0; ri < n; ri++)
        {
            var mr = mission.Rooms[ri];
            if (mr.PoolIndex < 0)
            {
                continue;
            }

            var pr = pool.Rooms[mr.PoolIndex];
            var g = mr.GeomPos ?? mr.Pos;
            int turns = ((((-pr.Rot) % 4 + 4) % 4) - (((-mr.Rot) % 4 + 4) % 4) + 4) % 4;
            void Turn(ref double x, ref double z)
            {
                for (int t = 0; t < turns; t++)
                {
                    (x, z) = (z, -x);
                }
            }

            if (byRoom.TryGetValue(mr.PoolIndex, out var chunks))
            {
                foreach (var v in chunks)
                {
                    for (int i = 0; i + 2 < v.Length; i += 3)
                    {
                        double dx = v[i] - pr.Pos[0], dz = v[i + 2] - pr.Pos[2];
                        Turn(ref dx, ref dz);
                        tris.Add((float)(g[0] + dx));
                        tris.Add(v[i + 1] - pr.Pos[1] + g[1]);
                        tris.Add((float)(g[2] + dz));
                    }
                }
            }
        }

        return tris.ToArray();
    }

    /// <summary>Every placed room's doorways to neighbours, in world coordinates (rooms.json 'doors', see DoorwaysFromField).</summary>
    private static List<Doorway>[] PlaceDoors(NavDungeon pool, NavDungeon mission)
    {
        var doors = new List<Doorway>[mission.Rooms.Count];
        for (var ri = 0; ri < doors.Length; ri++)
        {
            var mr = mission.Rooms[ri];
            doors[ri] = new List<Doorway>();
            if (mr.PoolIndex < 0)
            {
                continue;
            }

            var pr = pool.Rooms[mr.PoolIndex];
            var g = mr.GeomPos ?? mr.Pos;
            int turns = ((((-pr.Rot) % 4 + 4) % 4) - (((-mr.Rot) % 4 + 4) % 4) + 4) % 4;
            void Turn(ref double x, ref double z)
            {
                for (int t = 0; t < turns; t++)
                {
                    (double s, double t2) = (x, z);
                    x = z;
                    z = -s;
                }
            }

            foreach (var dw in DoorwaysFromField(pr))
            {
                double cx = dw.X - pr.Pos[0], cz = dw.Z - pr.Pos[2], nx = dw.Nx, nz = dw.Nz;
                Turn(ref cx, ref cz);
                Turn(ref nx, ref nz);
                doors[ri].Add(new Doorway { X = g[0] + cx, Y = g[1], Z = g[2] + cz, Nx = nx, Nz = nz, Floor = mr.Floor });
            }
        }

        return doors;
    }

    // The placement is checked by the doorways: every doorway of a room that faces a neighbour should
    // meet that neighbour's doorway. Rooms are not moved; the log says how many meet and names any that
    // miss, which would mean the placement rule is off for that pool.
    /// <summary>The doorway check of the last mission composed, for the log (single-threaded mission composition).</summary>
    public static string DoorCheck = "";

    // The server's door updates name exact positions and the two rooms each connects (Room
    // indexes the packet's room table, 1-based; -1 = the outside). Each room with server doors
    // has its translation re-solved: every decoded local door paired with every server door
    // implies a centre, and the candidate nearest the room's current centre wins. The move is
    // small when the chain was right and large when it was not - either way the room ends up
    // where the server actually built it, and the walls/surfaces/doorways re-place around it.
    public static List<string> CorrectWithServerDoors(string pluginDir, AOBuddyNav nav,
        List<(short room, short adjoining, Vector3 pos)> doors, out bool movedAny, bool apply = false)
    {
        movedAny = false;
        var log = new List<string>();
        if (nav?.Layout == null || nav.Dungeon?.Rooms == null || doors == null || doors.Count == 0)
        {
            return log;
        }

        var poolPath = Path.Combine(FolderFor(pluginDir, nav.Layout.TemplatePlayfield), "rooms.json");
        if (!File.Exists(poolPath))
        {
            return log;
        }

        var pool = NavDungeon.Read(poolPath);
        var d = nav.Dungeon;
        var moved = 0;
        var worst = 0.0;
        // Room is the packet room list's own 0-based index - proven by the doors themselves
        // (owner, 2026-10-03): Room=2 at (15,60) sits exactly on rooms[2]'s slot footprint,
        // Room=14 at (45,110) on rooms[14]'s, Room=16 on rooms[16]'s - and 16 is the last index
        // of a 17-room building. (AOBuddy10's "runs 1 to 22" remark was a different building's
        // 1-based reading; our capture says 0-based.)

        log.Add($"server doors: {doors.Count} (mapped to rooms by their list index).");
        foreach (var sd in doors.Take(8))
        {
            log.Add($"server door Room={sd.room} Adjoining={sd.adjoining} at ({sd.pos.X:0.0},{sd.pos.Z:0.0}).");
        }

        foreach (var mr in d.Rooms)
        {
            var sd = doors.Where(x => x.room == mr.Index).ToList();
            if (sd.Count == 0 || mr.PoolIndex < 0 || mr.Pos == null)
            {
                continue;
            }

            var pr = pool.Rooms[mr.PoolIndex];
            int x1 = pr.Rect[0], z1 = pr.Rect[1], x2 = pr.Rect[2], z2 = pr.Rect[3];
            int w = x2 - x1 + 1, h = z2 - z1 + 1;
            int turnsT = ((-mr.Rot) % 4 + 4) % 4;
            double tx = 1, tz = 1;
            for (int k = 0; k < turnsT; k++)
            {
                (tx, tz) = (-tz, tx);
            }

            var (gx, gz) = TurnBy(turnsT, w % 2 == 0 ? 1 : 0, h % 2 == 0 ? 1 : 0);
            int tp = ((-pr.Rot) % 4 + 4) % 4;
            var lds = new List<(double lx, double lz)>();
            foreach (var dw in DoorwaysFromField(pr))
            {
                double lx = dw.X - pr.Pos[0], lz = dw.Z - pr.Pos[2];
                for (int t = 0; t < tp; t++)
                {
                    (lx, lz) = (lz, -lx);
                }

                lds.Add((lx, lz));
            }

            var curCx = mr.Pos[0] - tx;
            var curCz = mr.Pos[2] - tz;
            var bestDist = double.MaxValue;
            var bestCx = curCx;
            var bestCz = curCz;
            foreach (var s in sd)
            {
                foreach (var ld in lds)
                {
                    var (fx, fz) = FwdRot(mr.Rot, ld.lx, ld.lz);
                    double icx = s.pos.X - fx - tx, icz = s.pos.Z - fz - tz;
                    var dist = (icx - curCx) * (icx - curCx) + (icz - curCz) * (icz - curCz);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCx = icx;
                        bestCz = icz;
                    }
                }
            }

            var move = Math.Sqrt(bestDist);
            if (move > 0.05)
            {
                worst = Math.Max(worst, move);
                if (apply)
                {
                    moved++;
                    movedAny = true;
                    mr.Pos = new[] { (float)(bestCx + tx), mr.Pos[1], (float)(bestCz + tz) };
                    mr.GeomPos = new[] { (float)(bestCx + gx), mr.Pos[1], (float)(bestCz + gz) };
                }

                log.Add($"room {mr.PoolName} f{mr.Floor} would move {move:0.0} m by its server door(s)" +
                        (apply ? "." : " (log-only - the slot placement stands)."));
            }
        }

        if (apply)
        {
            nav.Walls = PlaceBin(pluginDir, nav.Layout.TemplatePlayfield, pool, d, "walls.bin");
            nav.Surfaces = PlaceBin(pluginDir, nav.Layout.TemplatePlayfield, pool, d, "collision.bin");
            var doorsPlaced = PlaceDoors(pool, d);
            nav.MissionDoorways = doorsPlaced.SelectMany(list => list).ToList();
        }

        var doorsNow = PlaceDoors(pool, d);
        DoorCheck = $"{moved} room(s) moved by server doors (worst {worst:0.0} m); " +
                    CheckDoorways(d, doorsNow);
        log.Add(DoorCheck);
        return log;
    }

    private static (double x, double z) TurnBy(int turns, double x, double z)
    {
        for (var t = 0; t < turns; t++)
        {
            (x, z) = (-z, x);
        }

        return (x, z);
    }

    private static (double x, double z) FwdRot(int rot, double x, double z)
    {
        for (var t = ((-rot) % 4 + 4) % 4; t-- > 0;)
        {
            (x, z) = (-z, x);
        }

        return (x, z);
    }

    private static string CheckDoorways(NavDungeon mission, List<Doorway>[] doors)
    {
        var total = 0;
        var met = 0;
        var off = new List<string>();
        for (var a = 0; a < doors.Length; a++)
        {
            foreach (var da in doors[a])
            {
                double best = double.MaxValue;
                for (var b = 0; b < doors.Length; b++)
                {
                    if (b == a || mission.Rooms[b].Floor != mission.Rooms[a].Floor)
                    {
                        continue;
                    }

                    foreach (var db in doors[b])
                    {
                        if (da.Nx * db.Nx + da.Nz * db.Nz < -0.9)
                        {
                            // Y counts: two doorways that meet in X/Z but sit 2 m apart in height are
                            // not a passage (the Grey Caves room 4 of 2026-10-08 met its neighbours
                            // in plan while its doorway bridges lay 2 m under their floors)
                            var dy = da.Y - db.Y;
                            best = Math.Min(best, Math.Sqrt((da.X - db.X) * (da.X - db.X) + (da.Z - db.Z) * (da.Z - db.Z) + dy * dy));
                        }
                    }
                }

                if (best > 3.5)
                {
                    continue; // a doorway onto nothing (the building's edge): nothing to check
                }

                total++;
                if (best < 0.3)
                {
                    met++;
                }
                else
                {
                    off.Add($"{mission.Rooms[a].PoolName} f{mission.Rooms[a].Floor} ({da.X:0.0},{da.Z:0.0}) {best:0.0} m off");
                }
            }
        }

        return $"{met}/{total} doorways meet their neighbour's" + (off.Count > 0 ? "; OFF: " + string.Join(", ", off.Take(6)) : "");
    }

    public sealed class Doorway
    {
        public double X, Y, Z, Nx, Nz;
        public int Floor;
    } // N: out of the room

    /// <summary>
    ///     A pool room's doorways from its room record, in pool coordinates (pool rooms are unrotated).
    ///     Each entry is (link, code): link 65535 is a doorway to a neighbour, the room's own index an inner
    ///     door (skipped). code = row * 4(W-1) + col: row is the door's 2 m cell row in the room's floor, col
    ///     its x in half metres. Row 0 is the south side, row H-2 the north; otherwise col 3 is the west side
    ///     and 4(W-1)-3 the east. Worked out on pool 351 (Subway - Ventil). North doors decode 1 m short in
    ///     x on every size, corrected here.
    /// </summary>
    private static List<Doorway> DoorwaysFromField(NavDungeon.Room pr)
    {
        var outp = new List<Doorway>();
        if (pr.Doors == null)
        {
            return outp;
        }

        int W = pr.Rect[2] - pr.Rect[0] + 1, H = pr.Rect[3] - pr.Rect[1] + 1;
        int stride = 4 * (W - 1);
        if (stride <= 0)
        {
            return outp;
        }

        double ox = pr.Pos[0] - W + (W % 2 == 1 ? 1 : 0), oz = pr.Pos[2] - H + (H % 2 == 1 ? 1 : 0);
        double fw = 2.0 * (W - 1), fh = 2.0 * (H - 1);
        foreach (var d in pr.Doors)
        {
            if (d == null || d.Length < 2 || d[0] != 65535)
            {
                continue;
            }

            int row = d[1] / stride, col = d[1] % stride;
            double lx, lz;
            double nx = 0, nz = 0;
            if (row == 0)
            {
                lx = col / 2.0;
                lz = 0;
                nz = -1;
            }
            else if (row >= H - 2)
            {
                lx = col / 2.0 + 1;
                lz = fh;
                nz = 1;
            }
            else if (col == 3)
            {
                lx = 0;
                lz = row * 2 + 1;
                nx = -1;
            }
            else if (col == stride - 3)
            {
                lx = fw;
                lz = row * 2 + 1;
                nx = 1;
            }
            else
            {
                continue; // a door inside the room, not to a neighbour
            }

            outp.Add(new Doorway { X = ox + lx, Z = oz + lz, Nx = nx, Nz = nz });
        }

        return outp;
    }
}

/// <summary>
///     ground.bin (AONG v2-v4): outdoor heightfield + tile ids + building nibbles; v3 adds the water
///     planes, v4 the client's own liquid polygons (the playfield record's water rings — see SwimY).
/// </summary>
public sealed class NavGround
{
    public int SamplesX, SamplesZ, SourceBits;
    public float Cell, HeightScale;
    public ushort[] Heights; // [z * SamplesX + x], height = value * HeightScale
    public ushort[] Tiles; // [(SamplesZ-1) * (SamplesX-1)]
    public byte[] Building;
    public float[] WaterY = new float[0]; // v3 legacy: the playfield record's plane-table levels. Informational
                                          // only; v4's rings are the truth

    public byte[] TileColors; // tilecolors.bin beside ground.bin when the extractor wrote one: 256 x [r,g,b]

    public static NavGround Read(string path)
    {
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "AONG")
            {
                throw new InvalidDataException(path + ": not AONG");
            }

            int version = r.ReadInt32();
            if (version < 2 || version > 4)
            {
                throw new InvalidDataException(path + ": AONG version " + version);
            }

            var g = new NavGround { SamplesX = r.ReadInt32(), SamplesZ = r.ReadInt32(), Cell = r.ReadSingle(), HeightScale = r.ReadSingle(), SourceBits = r.ReadInt32() };
            if (version >= 3)
            {
                int wc = r.ReadInt32();
                g.WaterY = new float[wc];
                for (var i = 0; i < wc; i++)
                {
                    g.WaterY[i] = r.ReadSingle();
                }
            }

            if (version >= 4)
            {
                // the client's own liquid polygons (see SwimY): per liquid the ring size, the level, (x, z) pairs
                int qc = r.ReadInt32();
                var lakes = new List<Lake>(qc);
                for (var i = 0; i < qc; i++)
                {
                    int pts = r.ReadInt32();
                    var lk = new Lake { Level = r.ReadSingle(), X = new float[pts], Z = new float[pts] };
                    for (var pt = 0; pt < pts; pt++)
                    {
                        lk.X[pt] = r.ReadSingle();
                        lk.Z[pt] = r.ReadSingle();
                    }

                    lk.MinX = lk.X.Min();
                    lk.MaxX = lk.X.Max();
                    lk.MinZ = lk.Z.Min();
                    lk.MaxZ = lk.Z.Max();
                    lakes.Add(lk);
                }

                g._lakes = lakes.ToArray();
            }

            int rawLen = r.ReadInt32(), zLen = r.ReadInt32();
            byte[] raw = Inflate(r.ReadBytes(zLen), rawLen);
            int w = g.SamplesX, h = g.SamplesZ;
            g.Heights = new ushort[w * h];
            Buffer.BlockCopy(raw, 0, g.Heights, 0, w * h * 2);
            g.Tiles = new ushort[(w - 1) * (h - 1)];
            Buffer.BlockCopy(raw, w * h * 2, g.Tiles, 0, g.Tiles.Length * 2);
            g.Building = new byte[(w - 1) * (h - 1)];
            Buffer.BlockCopy(raw, w * h * 2 + g.Tiles.Length * 2, g.Building, 0, g.Building.Length);
            string tcPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "tilecolors.bin");
            if (File.Exists(tcPath))
            {
                using var tr = new BinaryReader(File.OpenRead(tcPath));
                if (Encoding.ASCII.GetString(tr.ReadBytes(4)) == "AOTC" && tr.ReadInt32() == 1 && tr.ReadInt32() == 256)
                {
                    g.TileColors = tr.ReadBytes(256 * 3);
                }
            }

            // Build the v3 fallback water HERE, at load: a loaded instance is immutable afterwards, so
            // SwimY/HeightAt are pure reads any thread may call (owner, 2026-10-01).
            if (g._lakes == null)
            {
                g.BuildWater();
            }

            return g;
        }
    }

    /// <summary>One liquid polygon from the playfield record: a flat ring of points at one level.</summary>
    private struct Lake
    {
        public float Level;
        public float[] X, Z;
        public float MinX, MaxX, MinZ, MaxZ;
    }

    private Lake[] _lakes; // v4 ground.bin: the client's own liquid polygons — SwimY's whole truth.
                           // Null on v3 data, which falls back to the tile-12 band model below.

    /// <summary>
    ///     The water surface to swim on at (x, z): NaN = dry ground. V4 DATA (2026-09-25): the client's
    ///     own water is a list of flat POLYGONS in the playfield record — per liquid one ring of 3-12
    ///     points at one level. SwimY is a point-in-ring test. V3 DATA (superseded, still the fallback
    ///     for un-regenerated files): the region is guessed from the tilemap's tile-12 band, flooded at
    ///     the stored plane, with gates against the runaway floods (Newland Desert 86%, log 2026-09-25).
    /// </summary>
    public double SwimY(double x, double z, double wadeDepth)
    {
        if (_lakes != null)
        {
            double lakeFloor = HeightAt(x, z);
            if (double.IsNaN(lakeFloor))
            {
                return double.NaN;
            }

            foreach (Lake lk in _lakes)
            {
                if (x < lk.MinX || x > lk.MaxX || z < lk.MinZ || z > lk.MaxZ)
                {
                    continue;
                }

                if (!InRing(lk, x, z))
                {
                    continue;
                }

                return lk.Level - lakeFloor > wadeDepth ? lk.Level : double.NaN;
            }

            return double.NaN;
        }

        if (float.IsNaN(_waterLevel))
        {
            return double.NaN;
        }

        int ix = (int)Math.Floor(x / Cell), iz = (int)Math.Floor(z / Cell);
        if (ix < 0 || iz < 0 || ix >= SamplesX - 1 || iz >= SamplesZ - 1)
        {
            return double.NaN;
        }

        if (!_wet[iz * (SamplesX - 1) + ix])
        {
            return double.NaN;
        }

        double floor = HeightAt(x, z);
        if (double.IsNaN(floor))
        {
            return double.NaN;
        }

        return _waterLevel - floor > wadeDepth ? _waterLevel : double.NaN;
    }

    // Even-odd point-in-polygon over the ring. There are a handful of liquids per playfield and
    // this runs once per walk step at most — no index needed.
    private static bool InRing(Lake lk, double x, double z)
    {
        bool inside = false;
        int n = lk.X.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if ((lk.Z[i] > z) != (lk.Z[j] > z) &&
                x < (lk.X[j] - lk.X[i]) * (z - lk.Z[i]) / (lk.Z[j] - lk.Z[i]) + lk.X[i])
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private bool[] _wet; // [(SamplesZ-1)*(SamplesX-1)]: the flooded water body
    private float _waterLevel = float.NaN;

    /// <summary>
    ///     Flood the water body from the tile-12 band at the stored plane that caps it. Two gates keep a
    ///     stored plane off land it was never meant for, both learned in Newland Desert (565) on
    ///     2026-09-25 19:01 (a 47% desert-texture 'band' and a runaway 86% flood). Called once at Read.
    /// </summary>
    private void BuildWater()
    {
        int w = SamplesX - 1, h = SamplesZ - 1;
        _wet = new bool[w * h];
        // the band: tile low byte 12. A handful of strays is not a body.
        int seeds = 0;
        for (var i = 0; i < w * h; i++)
        {
            if ((Tiles[i] & 0xFF) == 12)
            {
                seeds++;
            }
        }

        if (seeds < 8)
        {
            return;
        }

        // A BAND, NOT THE MAP: where the band IS huge (565, Penumbra) the tile has meant a texture
        // every time it could be checked — so: dry, as far as this data knows.
        if (seeds > _wet.Length / 4)
        {
            return;
        }

        // the band's core height (p10 of the cells' lowest corner — the band slopes into the depths)
        var band = new List<float>(seeds);
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                if ((Tiles[z * w + x] & 0xFF) == 12)
                {
                    band.Add(Math.Min(Math.Min(Corner(z, x), Corner(z, x + 1)), Math.Min(Corner(z + 1, x), Corner(z + 1, x + 1))));
                }
            }
        }

        band.Sort();
        float core = band[band.Count / 10];
        float level = float.NaN;
        foreach (float y in WaterY)
        {
            if (y > core && (float.IsNaN(level) || y > level))
            {
                level = y;
            }
        }

        if (float.IsNaN(level))
        {
            return;
        }

        // CONTAINED: flood with no distance limit as the test of the level itself. A real lake sits in
        // a basin that dry land closes above the plane. Abort at half the map; it only ever takes
        // away runaways (565's 90%, Penumbra Forest's 82%).
        for (var i = 0; i < w * h; i++)
        {
            if ((Tiles[i] & 0xFF) == 12)
            {
                _wet[i] = true;
            }
        }

        if (!Flood((bool[])_wet.Clone(), level, int.MaxValue, _wet.Length / 2))
        {
            return;
        }

        // flood from the band through cells whose lowest corner is under the surface — but only so far:
        // an unbounded flood spills through any lowland into basins that have no water at all
        // (Wailing Wastes 2026-09-25: the wompah station 335 m from the nearest band cell read as a
        // 4 m deep pool and the bot waded at the phantom surface until the server dropped it).
        Flood(_wet, level, Math.Max(4, (int)(150 / Cell)), int.MaxValue);
        _waterLevel = level;
    }

    // Flood from the already-marked seed cells through cells whose lowest corner is under `level`, at
    // most `maxShore` cells beyond a seed. Returns false the moment more than `abortAt` cells are wet.
    private bool Flood(bool[] wet, float level, int maxShore, int abortAt)
    {
        int w = SamplesX - 1, h = SamplesZ - 1;
        var queue = new Queue<(int cell, int dist)>();
        for (var i = 0; i < wet.Length; i++)
        {
            if (wet[i])
            {
                queue.Enqueue((i, 0));
            }
        }

        int count = queue.Count;
        while (queue.Count > 0)
        {
            var (c, dist) = queue.Dequeue();
            if (dist >= maxShore)
            {
                continue;
            }

            int cx = c % w, cz = c / w;
            for (var n = 0; n < 4; n++)
            {
                int nx = cx + (n == 0 ? -1 : n == 1 ? 1 : 0), nz = cz + (n == 2 ? -1 : n == 3 ? 1 : 0);
                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                {
                    continue;
                }

                int nc = nz * w + nx;
                if (wet[nc])
                {
                    continue;
                }

                if (Math.Min(Math.Min(Corner(nz, nx), Corner(nz, nx + 1)), Math.Min(Corner(nz + 1, nx), Corner(nz + 1, nx + 1))) < level)
                {
                    wet[nc] = true;
                    if (++count > abortAt)
                    {
                        return false;
                    }

                    queue.Enqueue((nc, dist + 1));
                }
            }
        }

        return true;
    }

    private float Corner(int sz, int sx) => Heights[sz * SamplesX + sx] * HeightScale;

    /// <summary>The water verdict for 'navdata': the liquids the record names, or the v3 model's guess.</summary>
    public string WaterInfo()
    {
        if (_lakes != null)
        {
            return _lakes.Length == 0 ? "dry (no liquids in the record)"
                : $"{_lakes.Length} liquid(s) at " + string.Join(", ", _lakes.Select(l => l.Level.ToString("0.0")).Distinct());
        }

        int wet = 0;
        foreach (bool b in _wet)
        {
            if (b)
            {
                wet++;
            }
        }

        return float.IsNaN(_waterLevel) ? "dry" : $"{_waterLevel:0.0} over {100.0 * wet / _wet.Length:0}% of the map";
    }

    internal static byte[] Inflate(byte[] z, int rawLen)
    {
        using var ms = new MemoryStream(z);
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);
        var raw = new byte[rawLen];
        int got = 0;
        while (got < rawLen)
        {
            int n = zs.Read(raw, got, rawLen - got);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        if (got != rawLen)
        {
            throw new InvalidDataException("zlib block short: " + got + " of " + rawLen);
        }

        return raw;
    }

    /// <summary>Bilinear height at world (x, z); NaN outside the map.</summary>
    public double HeightAt(double x, double z)
    {
        double fx = x / Cell, fz = z / Cell;
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        if (ix < 0 || iz < 0 || ix + 1 >= SamplesX || iz + 1 >= SamplesZ)
        {
            return double.NaN;
        }

        double tx = fx - ix, tz = fz - iz;
        double v = Heights[iz * SamplesX + ix] * (1 - tx) * (1 - tz) + Heights[iz * SamplesX + ix + 1] * tx * (1 - tz)
                   + Heights[(iz + 1) * SamplesX + ix] * (1 - tx) * tz + Heights[(iz + 1) * SamplesX + ix + 1] * tx * tz;
        return v * HeightScale;
    }

    public int TileAt(double x, double z)
    {
        int ix = (int)Math.Floor(x / Cell), iz = (int)Math.Floor(z / Cell);
        return ix < 0 || iz < 0 || ix >= SamplesX - 1 || iz >= SamplesZ - 1 ? -1 : Tiles[iz * (SamplesX - 1) + ix];
    }

    public int BuildingAt(double x, double z)
    {
        int ix = (int)Math.Floor(x / Cell), iz = (int)Math.Floor(z / Cell);
        return ix < 0 || iz < 0 || ix >= SamplesX - 1 || iz >= SamplesZ - 1 ? -1 : Building[iz * (SamplesX - 1) + ix];
    }
}

/// <summary>rooms.json: dungeon rooms placed in the world, each with its template cells.</summary>
public sealed class NavDungeon
{
    public sealed class Poly
    {
        public List<float[]> verts;
        public List<int[]> tris;
    }

    public sealed class Room
    {
        [JsonProperty("index")] public int Index;
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("flags")] public int Flags;
        [JsonProperty("rot")] public int Rot;
        [JsonProperty("rect")] public int[] Rect;
        [JsonProperty("pos")] public float[] Pos;
        [JsonProperty("heightBase")] public int HeightBase;
        [JsonProperty("doors")] public List<int[]> Doors;
        [JsonProperty("polys")] public List<Poly> Polys;
        [JsonProperty("tile")] public int[][] Tile;
        [JsonProperty("height")] public int[][] Height;
        [JsonProperty("flags3")] public int[][] Flags3;
        [JsonIgnore] public int Floor; // mission rooms only: the floor as the server numbered it
        [JsonIgnore] public string PoolName = ""; // mission rooms only: the pool room's own name
        [JsonIgnore] public int PoolIndex = -1; // mission rooms only: the pool room's index (= its collision record)
        [JsonIgnore] public float[] GeomPos; // mission rooms only: the point its walls and doors turn about (Pos: the tiles')
    }

    [JsonProperty("playfield")] public int Playfield;
    [JsonProperty("name")] public string Name;
    [JsonProperty("tilemap")] public int Tilemap;
    [JsonProperty("cell")] public float Cell;
    [JsonProperty("heightScale")] public float HeightScale;
    [JsonProperty("atlas")] public int[] Atlas;
    [JsonProperty("rooms")] public List<Room> Rooms;

    public static NavDungeon Read(string path) => JsonConvert.DeserializeObject<NavDungeon>(File.ReadAllText(path));

    /// <summary>Template cell of a room under world (x, z), or false when outside its rect.</summary>
    public bool CellOf(Room rm, double x, double z, out int col, out int row)
    {
        double dx = x - rm.Pos[0], dz = z - rm.Pos[2];
        for (int i = 0; i < ((-rm.Rot) % 4 + 4) % 4; i++)
        {
            (dx, dz) = (dz, -dx);
        }

        int x1 = rm.Rect[0], z1 = rm.Rect[1], x2 = rm.Rect[2], z2 = rm.Rect[3];
        int a = (int)Math.Floor((x1 + x2 + 1) / 2.0 + dx / Cell), b = (int)Math.Floor((z1 + z2 + 1) / 2.0 + dz / Cell);
        col = a - x1;
        row = b - z1;
        return a >= x1 && a <= x2 && b >= z1 && b <= z2;
    }

    /// <summary>World floor height of the tile under (x, z) in one room; NaN when outside or no tile.</summary>
    public double FloorHeight(Room rm, double x, double z)
    {
        if (!CellOf(rm, x, z, out int c, out int r))
        {
            return double.NaN;
        }

        if (rm.Tile[r][c] == 0)
        {
            return double.NaN;
        }

        return rm.Pos[1] + (rm.Height[r][c] - rm.HeightBase) * HeightScale;
    }

    /// <summary>Rooms whose floor tiles cover (x, z).</summary>
    public IEnumerable<Room> RoomsAt(double x, double z)
    {
        foreach (var rm in Rooms)
        {
            if (!double.IsNaN(FloorHeight(rm, x, z)))
            {
                yield return rm;
            }
        }
    }
}

/// <summary>collision.bin (AOCL v1): near-horizontal triangles, bucketed on an 8 m grid for point-under queries.</summary>
public sealed class NavCollision
{
    public sealed class Chunk
    {
        public int Instance, TeleportDestPf, LocalizerType, LocalizerInstance;
        public float[] Verts;
    } // 9 floats per triangle, world

    public readonly List<Chunk> Chunks = new List<Chunk>();
    // -> (chunk << TriBits | tri). Was chunk << 20: past 2047 chunks that went negative and HeightsUnder
    // threw on every frame in Lush Fields (695), which has more (log 2026-09-24 01:36).
    private const int TriBits = 11;
    private readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();
    public int Triangles;

    private static long Key(int a, int c) => ((long)a << 32) ^ (uint)c;

    /// <summary>
    ///     A collision set straight from world-space triangle data - a mission instance's composed
    ///     walls (nav.Walls, 9 floats per triangle, exactly the walls.bin layout). One chunk, no file.
    /// </summary>
    public static NavCollision FromTriangles(float[] verts)
    {
        var nc = new NavCollision();
        if (verts != null && verts.Length >= 9)
        {
            nc.Chunks.Add(new Chunk { Instance = 0, Verts = verts });
            nc.Triangles = verts.Length / 9;
            nc.Index();
        }

        return nc;
    }

    public static NavCollision Read(string path)
    {
        var nc = new NavCollision();
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "AOCL")
            {
                throw new InvalidDataException(path + ": not AOCL");
            }

            int version = r.ReadInt32();
            if (version != 1)
            {
                throw new InvalidDataException(path + ": AOCL version " + version);
            }

            int chunks = r.ReadInt32(), rawLen = r.ReadInt32(), zLen = r.ReadInt32();
            byte[] raw = NavGround.Inflate(r.ReadBytes(zLen), rawLen);
            int p = 0;
            for (var ci = 0; ci < chunks; ci++)
            {
                var c = new Chunk
                {
                    Instance = BitConverter.ToInt32(raw, p), TeleportDestPf = BitConverter.ToInt32(raw, p + 4),
                    LocalizerType = BitConverter.ToInt32(raw, p + 8), LocalizerInstance = BitConverter.ToInt32(raw, p + 12),
                };
                float ox = BitConverter.ToSingle(raw, p + 16), oy = BitConverter.ToSingle(raw, p + 20), oz = BitConverter.ToSingle(raw, p + 24);
                int n = BitConverter.ToInt32(raw, p + 28);
                p += 32;
                c.Verts = new float[n * 9];
                for (var i = 0; i < n * 9; i += 3)
                {
                    c.Verts[i] = ox + BitConverter.ToInt16(raw, p) / 100f;
                    c.Verts[i + 1] = oy + BitConverter.ToInt16(raw, p + 2) / 100f;
                    c.Verts[i + 2] = oz + BitConverter.ToInt16(raw, p + 4) / 100f;
                    p += 6;
                }

                nc.Chunks.Add(c);
                nc.Triangles += n;
            }
        }

        nc.Index();
        return nc;
    }

    private void Index()
    {
        for (var ci = 0; ci < Chunks.Count; ci++)
        {
            float[] v = Chunks[ci].Verts;
            for (var t = 0; t * 9 < v.Length; t++)
            {
                int o = t * 9;
                int x0 = (int)Math.Floor(Math.Min(v[o], Math.Min(v[o + 3], v[o + 6])) / 8), x1 = (int)Math.Floor(Math.Max(v[o], Math.Max(v[o + 3], v[o + 6])) / 8);
                int z0 = (int)Math.Floor(Math.Min(v[o + 2], Math.Min(v[o + 5], v[o + 8])) / 8), z1 = (int)Math.Floor(Math.Max(v[o + 2], Math.Max(v[o + 5], v[o + 8])) / 8);
                for (var a = x0; a <= x1; a++)
                {
                    for (var c = z0; c <= z1; c++)
                    {
                        if (!_buckets.TryGetValue(Key(a, c), out var l))
                        {
                            _buckets[Key(a, c)] = l = new List<int>();
                        }

                        l.Add((ci << TriBits) | t); // a chunk holds at most 2048 triangles (the extractor splits records there)
                    }
                }
            }
        }
    }

    // ---- exact line clearance ------------------------------------------------------------------------------

    /// <summary>
    ///     True when any kept triangle crosses the walk line (x0,z0)->(x1,z1) inside the body band the
    ///     caller's h(t) describes: h is the FLOOR height along the line at parameter t (0..1), and a
    ///     triangle blocks where its height at the crossing reaches into [h(t)+low, h(t)+high]. The test
    ///     is EXACT - segment-vs-edge crossings, not point samples - so a perfectly vertical wall (a
    ///     zero-width projection no point sample ever lands inside, which TriContains was blind to)
    ///     blocks the line it physically crosses (owner, 2026-10-03: "pathfinds but runs at/through
    ///     walls"). Triangles are found through the 8 m buckets along the line, so the cost is the
    ///     triangles near it, not the zone's.
    /// </summary>
    public bool LineBlocked(double x0, double z0, double x1, double z1, Func<double, double> h, double low, double high)
    {
        if (Chunks.Count == 0)
        {
            return false;
        }

        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
        if (len < 1e-6)
        {
            return false;
        }

        var seen = new HashSet<long>();
        const double bucket = 8.0;
        int steps = Math.Max(1, (int)Math.Ceiling(len / (bucket / 2)));
        for (var s = 0; s <= steps; s++)
        {
            double t = (double)s / steps;
            int bx = (int)Math.Floor((x0 + (x1 - x0) * t) / bucket), bz = (int)Math.Floor((z0 + (z1 - z0) * t) / bucket);
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_buckets.TryGetValue(Key(bx + dx, bz + dz), out var l))
                    {
                        continue;
                    }

                    foreach (int id in l)
                    {
                        if (!seen.Add(id))
                        {
                            continue;
                        }

                        float[] v = Chunks[id >> TriBits].Verts;
                        if (TriBlocksLine(v, (id & ((1 << TriBits) - 1)) * 9, x0, z0, x1, z1, h, low, high))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     The one-triangle core of <see cref="LineBlocked" />: does this triangle's projection cross the
    ///     walk line inside the body band? Each of the three edges is tested as a 2D segment-segment
    ///     crossing; a crossing on a vertical edge (a wall face) blocks over the edge's full height span,
    ///     one on a slanted edge at the edge's interpolated height there. The fallback for a line END
    ///     inside a non-degenerate projection (no crossing detects it) uses the triangle's whole height
    ///     range - conservative, and a line ending inside a wall deserves it.
    /// </summary>
    internal static bool TriBlocksLine(float[] v, int o, double x0, double z0, double x1, double z1,
        Func<double, double> h, double low, double high)
    {
        double rx = x1 - x0, rz = z1 - z0;
        for (var k = 0; k < 3; k++)
        {
            int a = o + k * 3, b = o + (k + 1) % 3 * 3;
            double ax = v[a], ay = v[a + 1], az = v[a + 2], bx = v[b], by = v[b + 1], bz = v[b + 2];
            double sx = bx - ax, sz = bz - az;
            double den = rx * sz - rz * sx;
            if (Math.Abs(den) < 1e-12)
            {
                continue; // parallel: no single crossing (a collinear graze blocks nothing)
            }

            double t = ((ax - x0) * sz - (az - z0) * sx) / den;
            double u = ((ax - x0) * rz - (az - z0) * rx) / den;
            if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9)
            {
                continue;
            }

            double yLo, yHi;
            if (sx * sx + sz * sz < 1e-8)
            {
                yLo = Math.Min(ay, by); // the edge stands up: this (x,z) is the whole face
                yHi = Math.Max(ay, by);
            }
            else
            {
                yLo = yHi = ay + u * (by - ay);
            }

            double bandLo = h(t) + low, bandHi = h(t) + high;
            if (yLo <= bandHi && yHi >= bandLo)
            {
                return true;
            }
        }

        // the line ends inside the projection: no edge leaves to be crossed
        if (Covers(v, o, x1, z1))
        {
            double mn = Math.Min(v[o + 1], Math.Min(v[o + 4], v[o + 7]));
            double mx = Math.Max(v[o + 1], Math.Max(v[o + 4], v[o + 7]));
            double bandLo = h(1) + low, bandHi = h(1) + high;
            if (mn <= bandHi && mx >= bandLo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Is (x, z) inside the triangle's ground projection? False for a degenerate (zero-area) one.</summary>
    private static bool Covers(float[] v, int o, double x, double z)
    {
        double ax = v[o], az = v[o + 2], bx = v[o + 3], bz = v[o + 5], cx = v[o + 6], cz = v[o + 8];
        double d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(d) < 1e-9)
        {
            return false;
        }

        double l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
        double l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
        double l3 = 1 - l1 - l2;
        return l1 >= -0.001 && l2 >= -0.001 && l3 >= -0.001;
    }

    /// <summary>Heights of every kept triangle directly under world (x, z).</summary>
    public IEnumerable<double> HeightsUnder(double x, double z)
    {
        if (!_buckets.TryGetValue(Key((int)Math.Floor(x / 8), (int)Math.Floor(z / 8)), out var l))
        {
            yield break;
        }

        foreach (int id in l)
        {
            float[] v = Chunks[id >> TriBits].Verts;
            int o = (id & ((1 << TriBits) - 1)) * 9;
            double x0 = v[o], y0 = v[o + 1], z0 = v[o + 2], x1 = v[o + 3], y1 = v[o + 4], z1 = v[o + 5], x2 = v[o + 6], y2 = v[o + 7], z2 = v[o + 8];
            double d = (x1 - x0) * (z2 - z0) - (x2 - x0) * (z1 - z0);
            if (Math.Abs(d) < 1e-9)
            {
                continue;
            }

            double u = ((x - x0) * (z2 - z0) - (x2 - x0) * (z - z0)) / d;
            double w = ((x1 - x0) * (z - z0) - (x - x0) * (z1 - z0)) / d;
            if (u >= -1e-6 && w >= -1e-6 && u + w <= 1 + 1e-6)
            {
                yield return y0 + u * (y1 - y0) + w * (y2 - y0);
            }
        }
    }
}