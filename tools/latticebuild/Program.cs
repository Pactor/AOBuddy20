// RECAST-NAV-PLAN step 3, interval-model build (the mission grids' mechanism, ported):
//
// ground.bin terrain triangles + meshes.bin (unfiltered 1000013) are CLIPPED against every
// 1 m cell column (Sutherland-Hodgman over x/z, y interpolated - NorthboundGrid.ClipColumn,
// winding-independent, exact for vertical walls and thin slivers a voxel pass would miss).
// Per column the clipped ranges merge into solid runs; a walkable LEVEL is a run top with
// at least BandTop (0.6 m, the calibrated collision band) of open air above it. Vertical
// spans block only their own band: the ICC enclosure's lower wall (with its east/west
// openings) blocks the ground level and stands as the deck rim, while the upper full-circle
// decks are untouched - their ring walls seal only their own decks.
//
// usage: latticebuild <navdir> <pf> [--out <file>]

using System.Globalization;
using System.IO.Compression;

if (args.Length < 2)
{
    Console.WriteLine("usage: latticebuild <navdir> <pf> [--out <file>]");
    return 2;
}

var navDir = args[0];
var pf = int.Parse(args[1], CultureInfo.InvariantCulture);
string outFile = null;
for (var i = 2; i < args.Length; i++)
{
    if (args[i] == "--out")
    {
        outFile = args[++i];
    }
}

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var sw = System.Diagnostics.Stopwatch.StartNew();
const float Cell = 1.0f;
const float BandTop = 0.6f;   // the calibrated collision band's top: standing needs this much air
const float MergeEps = 0.05f; // runs touching within 5 cm are one solid

// â”€â”€ ground.bin: the terrain heightfield (AONG v4; zlib payload at the tail) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
var ground = File.ReadAllBytes(Path.Combine(navDir, pf.ToString(), "ground.bin"));
if (ground[0] != 'A' || ground[1] != 'O' || ground[2] != 'N' || ground[3] != 'G')
{
    throw new InvalidDataException("ground.bin: no AONG magic");
}

var gSx = BitConverter.ToInt32(ground, 8);
var gSz = BitConverter.ToInt32(ground, 12);
var gCell = BitConverter.ToSingle(ground, 16);
var gHscale = BitConverter.ToSingle(ground, 20); // per u16
var gp = 28;
var waterYs = BitConverter.ToInt32(ground, gp);
gp += 4 + waterYs * 4;
var liquids = BitConverter.ToInt32(ground, gp);
gp += 4;
for (var li = 0; li < liquids; li++)
{
    var n = BitConverter.ToInt32(ground, gp);
    gp += 4 + 4 + n * 2 * 4;
}

var gRawLen = BitConverter.ToInt32(ground, gp);
var gzLen = BitConverter.ToInt32(ground, gp + 4);
var gBody = new byte[gRawLen];
using (var src = new MemoryStream(ground, gp + 8 + 2, gzLen - 2))
using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
{
    for (var got = 0; got < gRawLen;)
    {
        got += inflate.Read(gBody, got, gRawLen - got);
    }
}

Console.WriteLine($"ground: {gSx}x{gSz} samples, cell {gCell.ToString(CultureInfo.InvariantCulture)} m");
float Height(int ix, int iz) => BitConverter.ToUInt16(gBody, 2 * (iz * gSx + ix)) * gHscale;

// â”€â”€ meshes.bin: the unfiltered 1000013 triangles (AOCL) - optional â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
var meshTris = new List<float[]>(); // 9 floats per triangle, world coords
var meshPath = Path.Combine(navDir, pf.ToString(), "meshes.bin");
if (File.Exists(meshPath))
{
    var b = File.ReadAllBytes(meshPath);
    if (b[0] != 'A' || b[1] != 'O' || b[2] != 'C' || b[3] != 'L')
    {
        throw new InvalidDataException("meshes.bin: no AOCL magic");
    }

    var chunks = BitConverter.ToInt32(b, 8);
    var rawLen = BitConverter.ToInt32(b, 12);
    var zLen = BitConverter.ToInt32(b, 16);
    var mbody = new byte[rawLen];
    using (var src = new MemoryStream(b, 22, zLen - 2)) // zlib = 2-byte header + raw deflate + adler
    using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
    {
        for (var got = 0; got < rawLen;)
        {
            got += inflate.Read(mbody, got, rawLen - got);
        }
    }

    var p = 0;
    for (var c = 0; c < chunks && p + 32 <= rawLen; c++)
    {
        p += 16; // instance, teleportDestPf, localizerType, localizerInstance - the router's later
        var ox = BitConverter.ToSingle(mbody, p);
        var oy = BitConverter.ToSingle(mbody, p + 4);
        var oz = BitConverter.ToSingle(mbody, p + 8);
        var cnt = BitConverter.ToInt32(mbody, p + 12);
        p += 16;
        if (p + cnt * 18 > mbody.Length)
        {
            throw new InvalidDataException($"meshes.bin chunk {c}: {cnt} tris at {p} overruns {mbody.Length}");
        }

        for (var t = 0; t < cnt; t++)
        {
            var v = new float[9];
            for (var k = 0; k < 9; k++)
            {
                v[k] = (k % 3 == 0 ? ox : k % 3 == 1 ? oy : oz) + BitConverter.ToInt16(mbody, p + t * 18 + k * 2) / 100f;
            }

            meshTris.Add(v);
        }

        p += cnt * 18;
    }

    Console.WriteLine($"meshes: {meshTris.Count:N0} triangles");
}
else
{
    Console.WriteLine("meshes: none (terrain-only playfield)");
}

// â”€â”€ the world grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
var worldW = (gSx - 1) * gCell;
var worldH = (gSz - 1) * gCell;
var cellsX = (int)(worldW / Cell);
var cellsZ = (int)(worldH / Cell);
const float RowMetres = 64f;
var rowsZ = (int)Math.Ceiling(worldH / RowMetres);
Console.WriteLine(
    "world " + worldW.ToString("F0", CultureInfo.InvariantCulture) + "x" + worldH.ToString("F0", CultureInfo.InvariantCulture) +
    " m -> " + cellsX + "x" + cellsZ + " cells, " + rowsZ + " rows of " + RowMetres.ToString("F0", CultureInfo.InvariantCulture) + " m");

outFile ??= Path.Combine(navDir, pf.ToString(), "lattice.bin");

// â”€â”€ the column clip (NorthboundGrid.ClipColumn, ported) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Intersect a triangle with the cell's vertical column [bx,bx+c]x[bz,bz+c]; y is interpolated
// along the clipped edges - a vertical wall survives as its line, exact at any thinness.
static ((float y0, float y1), bool hit) ClipColumn(float[] v, double bx, double bz, double c)
{
    const double e = 1e-9;
    double[][] cur = [[v[0], v[1], v[2]], [v[3], v[4], v[5]], [v[6], v[7], v[8]]];
    double[][] tmp = [new double[3], new double[3], new double[3], new double[3], new double[3], new double[3], new double[3]];
    double[][] nxt = [new double[3], new double[3], new double[3], new double[3], new double[3], new double[3], new double[3]];

    var n = 3;
    // ONE PLANE PER PASS: clipping [lo,hi] in a single pass drops any edge that spans the
    // whole strip (one end below, one above - no endpoint inside, no crossing added), which
    // erased every terrain triangle's middle columns (the 37.6% stripes). Textbook S-H:
    // four half-plane passes, each always adding the edge's crossing.
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

static int ClipPlane(double[][] src, double[][] dst, int n, int axis, bool keepLo, double plane)
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
            src[i].CopyTo(dst[m]);
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

// â”€â”€ row streaming: per 64 m row, clip every triangle into the row's columns â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    var walkTotal = 0L;
    var multi = 0;
    // the per-cell records, zlib-compressed (the AO nav convention: AOCL chunks, ground.bin's
// tail, GridCache - all deflate; the raw level lists repeat neighbor heights millions of times)
var body = new MemoryStream();
using (var bw = new BinaryWriter(body))
{

    // mesh triangles bucketed by z once (a triangle joins every row it vertically spans)
    var rowTris = new List<int>[rowsZ];
    for (var t = 0; t < meshTris.Count; t++)
    {
        var v = meshTris[t];
        var z0 = Math.Max(0, (int)(Math.Min(v[2], Math.Min(v[5], v[8])) / RowMetres));
        var z1 = Math.Min(rowsZ - 1, (int)(Math.Max(v[2], Math.Max(v[5], v[8])) / RowMetres));
        for (var r = z0; r <= z1; r++)
        {
            (rowTris[r] ??= new List<int>()).Add(t);
        }
    }

    for (var rz = 0; rz < rowsZ; rz++)
    {
        var zLo = rz * RowMetres;
        var zHi = zLo + RowMetres;
        var gzLo = rz * (int)RowMetres;
        var gzHi = Math.Min(cellsZ, gzLo + (int)RowMetres);
        // the row's runs, filled by parallel build tiles (each tile owns disjoint columns)
        var runs = new Dictionary<long, List<(float y0, float y1)>>();
        var runLock = new object();

        Parallel.For(0, (int)Math.Ceiling(worldW / 64f), new ParallelOptions { MaxDegreeOfParallelism = 8 }, txx =>
        {
            var bx0 = txx * 64f;
            var local = new Dictionary<long, List<(float y0, float y1)>>();
            void AddRun(int gx, int gz, float y0, float y1)
            {
                var key = gz * (long)cellsX + gx;
                if (!local.TryGetValue(key, out var l))
                {
                    local[key] = [(y0, y1)];
                }
                else
                {
                    l.Add((y0, y1));
                }
            }

            // terrain: the heightfield tiles inside this build tile, both diagonals
            var gx0 = Math.Max(0, (int)(bx0 / gCell));
            var gz0 = Math.Max(0, (int)(zLo / gCell)); // heightfield index: metres / 4 m, NOT the cell row
            var gx1 = Math.Min(gSx - 1, (int)Math.Ceiling((bx0 + 64f) / gCell));
            var gz1 = Math.Min(gSz - 1, (int)Math.Ceiling((zLo + RowMetres) / gCell)); // heightfield rows
            for (var gz = gz0; gz < gz1; gz++)
            {
                for (var gx = gx0; gx < gx1; gx++)
                {
                    var x0 = gx * gCell;
                    var zz0 = gz * gCell;
                    var x1 = x0 + gCell;
                    var zz1 = zz0 + gCell;
                    var h00 = Height(gx, gz);
                    var h10 = Height(gx + 1, gz);
                    var h01 = Height(gx, gz + 1);
                    var h11 = Height(gx + 1, gz + 1);
                    float[][] tris =
                    [
                        [x0, h00, zz0, x1, h11, zz1, x1, h10, zz1],
                        [x0, h00, zz0, x0, h01, zz1, x1, h11, zz1],
                    ];
                    foreach (var v in tris)
                    {
                        var cx0 = Math.Max(0, (int)(Math.Min(v[0], Math.Min(v[3], v[6])) / Cell));
                        var cx1 = Math.Min(cellsX - 1, (int)(Math.Max(v[0], Math.Max(v[3], v[6])) / Cell));
                        var cz0 = Math.Max(gzLo, (int)(Math.Min(v[2], Math.Min(v[5], v[8])) / Cell));
                        var cz1 = Math.Min(gzHi - 1, (int)(Math.Max(v[2], Math.Max(v[5], v[8])) / Cell));
                        for (var cz = cz0; cz <= cz1; cz++)
                        {
                            for (var cx = cx0; cx <= cx1; cx++)
                            {
                                var (r, hit) = ClipColumn(v, cx * (double)Cell, cz * (double)Cell, Cell);
                                if (hit)
                                {
                                    AddRun(cx, cz, r.y0, r.y1);
                                }
                            }
                        }
                    }
                }
            }

            // mesh triangles overlapping this build tile in x and the row in z
            if (rowTris[rz] != null)
            {
                foreach (var t in rowTris[rz])
                {
                    var v = meshTris[t];
                    if (Math.Max(v[0], Math.Max(v[3], v[6])) < bx0 || Math.Min(v[0], Math.Min(v[3], v[6])) >= bx0 + 64f)
                    {
                        continue;
                    }

                    var cx0 = Math.Max(0, (int)(Math.Min(v[0], Math.Min(v[3], v[6])) / Cell));
                    var cx1 = Math.Min(cellsX - 1, (int)(Math.Max(v[0], Math.Max(v[3], v[6])) / Cell));
                    var cz0 = Math.Max(gzLo, (int)(Math.Min(v[2], Math.Min(v[5], v[8])) / Cell));
                    var cz1 = Math.Min(gzHi - 1, (int)(Math.Max(v[2], Math.Max(v[5], v[8])) / Cell));
                    for (var cz = cz0; cz <= cz1; cz++)
                    {
                        for (var cx = cx0; cx <= cx1; cx++)
                        {
                            var (r, hit) = ClipColumn(v, cx * (double)Cell, cz * (double)Cell, Cell);
                            if (hit)
                            {
                                AddRun(cx, cz, r.y0, r.y1);
                            }
                        }
                    }
                }
            }

            lock (runLock)
            {
                var dbgKey = 854L * cellsX + 3192;
                if (rz == 13 && local.ContainsKey(dbgKey))
                {
                    Console.WriteLine("    DBG merge: local has key, runs has = " + runs.ContainsKey(dbgKey));
                }
                foreach (var kv in local)
                {
                    if (runs.TryGetValue(kv.Key, out var l))
                    {
                        l.AddRange(kv.Value);
                    }
                    else
                    {
                        runs[kv.Key] = kv.Value;
                    }
                }
            }

            if (bx0 == 3136f && rz == 13)
            {
                for (var cz = 850; cz <= 858; cz++)
                {
                    var row = "";
                    for (var cx = 3188; cx <= 3200; cx++)
                    {
                        row += (local.TryGetValue(cz * (long)cellsX + cx, out var l) && l.Count > 0) ? "#" : ".";
                    }
                    Console.WriteLine($"    z={cz} {row}");
                }
                Console.WriteLine("    x 3188..3200");
                Console.WriteLine("    after merge, runs at z=854 x 3190..3196:");
                for (var cx = 3190; cx <= 3196; cx++)
                {
                    var has = runs.TryGetValue(854 * (long)cellsX + cx, out var l) && l.Count > 0;
                    var n = has ? l.Count : 0;
                    Console.WriteLine("      x=" + cx + " runs=" + n);
                }
            }
        });

        // per cell: sort the runs, merge overlaps, levels = run tops with BandTop of air above
        for (var gz = gzLo; gz < gzHi; gz++)
        {
            for (var gx = 0; gx < cellsX; gx++)
            {
                var key = gz * (long)cellsX + gx;
                if (!runs.TryGetValue(key, out var list))
                {
                    bw.Write((byte)0);
                    continue;
                }

                list.Sort((a, b) => a.y0.CompareTo(b.y0));
                var merged = new List<(float y0, float y1)>();
                foreach (var (y0, y1) in list)
                {
                    if (merged.Count > 0 && y0 <= merged[^1].y1 + MergeEps)
                    {
                        if (y1 > merged[^1].y1)
                        {
                            merged[^1] = (merged[^1].y0, y1);
                        }
                    }
                    else
                    {
                        merged.Add((y0, y1));
                    }
                }

                // a level is a run top with BandTop of air above it (the next run starts lower
                // than BandTop past the top only when the two runs truly are separate)
                var keep = new List<float>();
                for (var k = 0; k < merged.Count; k++)
                {
                    var top = merged[k].y1;
                    var air = k + 1 < merged.Count ? merged[k + 1].y0 - top : float.PositiveInfinity;
                    if (air >= BandTop)
                    {
                        keep.Add(top);
                    }
                }

                if (keep.Count == 0)
                {
                    bw.Write((byte)0);
                    continue;
                }

                walkTotal += keep.Count;
                if (keep.Count > 1)
                {
                    multi++;
                }

                bw.Write((byte)keep.Count);
                foreach (var cms in keep)
                {
                    bw.Write((short)Math.Clamp(Math.Round(cms * 100f), -32768, 32767));
                }
            }
        }

        Console.WriteLine("  row " + (rz + 1).ToString(CultureInfo.InvariantCulture) + "/" + rowsZ +
                          " (" + sw.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s)");
    }
}

var raw = body.ToArray();
var z = new MemoryStream();
using (var deflate = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
{
    deflate.Write(raw);
}

using (var fs = File.Create(outFile))
using (var bw = new BinaryWriter(fs))
{
    bw.Write("AOCELL2"u8);
    bw.Write(pf);
    bw.Write(Cell);
    bw.Write(0f); // ch: unused - levels are exact column-clip tops, not quantized
    bw.Write(cellsX);
    bw.Write(cellsZ);
    bw.Write(raw.Length);
    bw.Write((int)z.Length);
    z.Position = 0;
    z.CopyTo(fs);
}

Console.WriteLine(
    "wrote " + outFile + " in " + sw.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) +
    "s (" + walkTotal.ToString("N0", CultureInfo.InvariantCulture) + " levels, " +
    multi.ToString("N0", CultureInfo.InvariantCulture) + " multi-level cells)");
return 0;
