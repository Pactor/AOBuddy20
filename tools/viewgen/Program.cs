using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOSharp.Common.GameData;
using AOBuddy20.Nav;

// THE MISSION FLOOR VIEWER GENERATOR: the stitched mission (placed rooms) as layers:
//   mesh        - the pool rooms' collision+walls triangles, transformed by the blit's placement
//   tiles       - the GNDA tile floors, same transform
//   floors v13  - the Recast lattice levels as height-coloured cell quads (holes = no standable level)
//   floors v12  - the interval lattice levels, same view, for diffing the two producers
//   seam pairs  - per server connection: the yellow row marker, the two rooms' own placed
//                 sockets colour-matched (cyan = the row's room, magenta = the neighbour)
// Output: Build/mission-floor-viewer.html (self-contained, Three.js from CDN)
var pluginDir = "F:/testcellao/AOBuddy20/AOBuddy20/Build";
var navDir = "F:/testcellao/AOBuddy20/AOBuddy20/Build/GameData/Nav";
// args: [missionPf] [layoutFile] [unreachableCsv] - defaults: 341 / 14678642 / 5
int Pf = args.Length > 0 ? int.Parse(args[0]) : 341;
var layoutFile = args.Length > 1 ? args[1] : pluginDir + "/mission-layout-14678642.txt";
var unreachableArg = args.Length > 2 ? args[2] : "5";
double D(string s) => double.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);

var nav = AOBuddyNav.Load(pluginDir, Pf);
var pool = nav.Dungeon;
List<List<float[]>> ReadBin(string file)
{
    var byRoom = new List<List<float[]>>();
    foreach (var c in NavCollision.Read(Path.Combine(navDir, Pf.ToString(), file)).Chunks)
    {
        var idx = c.Instance & 0xFFFF;
        while (byRoom.Count <= idx) byRoom.Add(null);
        (byRoom[idx] ??= new List<float[]>()).Add(c.Verts);
    }
    return byRoom;
}
var colByRoom = ReadBin("collision.bin");
var wallByRoom = ReadBin("walls.bin");

var placed = new List<(NavDungeon.Room pool, double cx, double cz, int rot)>();
var missionIndexByName = new Dictionary<string, int>();
var dumpY = new List<double>();
foreach (var line in File.ReadAllLines(layoutFile))
{
    var mt = Regex.Match(line, @"^\s*room (\d+) (\S+) f-?\d+ centre \(([\d,]+)\) y ([\d,]+) rot (\d)");
    if (!mt.Success) continue;
    var parts = mt.Groups[3].Value.Split(',');
    double cx = parts.Length >= 4 ? D(parts[0] + "." + parts[1]) : D(parts[0]);
    double cz = parts.Length >= 4 ? D(parts[^2] + "." + parts[^1]) : D(parts[^1]);
    var pr = pool.Rooms.FirstOrDefault(r => r.Name == mt.Groups[2].Value) ?? throw new Exception("pool has no room " + mt.Groups[2].Value);
    placed.Add((pr, cx, cz, int.Parse(mt.Groups[5].Value)));
    missionIndexByName[mt.Groups[2].Value] = placed.Count - 1;
    dumpY.Add(D(mt.Groups[4].Value));
}
Console.WriteLine($"placed rooms: {placed.Count}");

// THE SERVER-DOOR CHAIN (the same correction the bot runs): rooms snap onto the first-placed
// room's socket per connection, so the viewer draws the stitched placement, not the raw slots
var missionRooms = new List<NavDungeon.Room>();
for (var i = 0; i < placed.Count; i++)
{
    var (pr, cx, cz, rot) = placed[i];
    missionRooms.Add(new NavDungeon.Room
    {
        Index = i,
        Name = pr.Name,
        PoolIndex = pr.Index,
        PoolName = pr.Name,
        Flags = pr.Flags,
        Rot = rot,
        Rect = pr.Rect,
        Pos = new[] { (float)cx, (float)dumpY[i], (float)cz },
        GeomPos = new[] { (float)cx, (float)dumpY[i], (float)cz }, // the chain shifts it with Pos
        Floor = 0,
        HeightBase = pr.HeightBase,
        Tile = pr.Tile,
        Height = pr.Height,
        Height2 = pr.Height2,
        Flags3 = pr.Flags3,
    });
}

var missionNav = AOBuddyNav.ForDungeon(Pf, pool.Name,
    new NavDungeon { Cell = pool.Cell, HeightScale = pool.HeightScale, Rooms = missionRooms });
missionNav.Layout = new AOBuddyNav.MissionLayout { TemplatePlayfield = Pf };
var sdoors = new List<(short room, short adjoining, Vector3 pos)>();
foreach (var line in File.ReadAllLines(layoutFile))
{
    foreach (Match mt in Regex.Matches(line, @"Room=(-?\d+) Adj=(-?\d+) \(([\d,]+)\)"))
    {
        var pp = mt.Groups[3].Value.Split(',');
        double x = pp.Length >= 6 ? D(pp[0] + "." + pp[1]) : D(pp[0]);
        double y = pp.Length >= 6 ? D(pp[2] + "." + pp[3]) : float.NaN;
        double z = pp.Length >= 6 ? D(pp[4] + "." + pp[5]) : D(pp[^1]);
        sdoors.Add(((short)int.Parse(mt.Groups[1].Value), (short)int.Parse(mt.Groups[2].Value),
            new Vector3((float)x, (float)y, (float)z)));
    }
}

if (sdoors.Count > 0)
{
    foreach (var l in AOBuddyNav.CorrectWithServerDoors(pluginDir, missionNav, sdoors, out _, apply: true))
    {
        Console.WriteLine("  " + l);
    }

    // rebuild the placed tuples from the CORRECTED positions
    for (var i = 0; i < placed.Count; i++)
    {
        var p = placed[i];
        placed[i] = (p.pool, missionRooms[i].Pos[0], missionRooms[i].Pos[2], p.rot);
    }
}

// PER-ROOM sinks: every layer is emitted per room, so the viewer can toggle rooms
var meshBy = new Dictionary<int, List<float>>();
var tilesBy = new Dictionary<int, List<float>>();
var recastBy = new Dictionary<int, List<float>>();
var legacyBy = new Dictionary<int, List<float>>();
var meshOnlyBy = new Dictionary<int, List<float>>();
var roomSockets = new List<(int roomIdx, double x, double y, double z)>();
float mnx = 1e9f, mxx = -1e9f, mnz = 1e9f, mxz = -1e9f;
void Take(double x, double z)
{
    mnx = Math.Min(mnx, (float)x);
    mxx = Math.Max(mxx, (float)x);
    mnz = Math.Min(mnz, (float)z);
    mxz = Math.Max(mxz, (float)z);
}

void EmitTri(List<float> sink, (double x, double y, double z)[] pts)
{
    foreach (var p in pts)
    {
        sink.Add((float)p.x);
        sink.Add((float)p.y);
        sink.Add((float)p.z);
    }
}

foreach (var (pr, pcx, pcz, prot) in placed)
{
    var ri = placed.IndexOf((pr, pcx, pcz, prot));
    var mesh = meshBy[ri] = new List<float>();
    var tiles = tilesBy[ri] = new List<float>();
    var floorsRecast = recastBy[ri] = new List<float>();
    var floorsLegacy = legacyBy[ri] = new List<float>();
    var floorsMeshOnly = meshOnlyBy[ri] = new List<float>();
    var turns = ((-prot) % 4 + 4) % 4;
    (double x, double z) Place(double x, double z)
    {
        var dx = x - pr.Pos[0];
        var dz = z - pr.Pos[2];
        Turn:
        for (var t = 0; t < turns; t++)
        {
            (dx, dz) = (-dz, dx);
        }

        return (pcx + dx, pcz + dz);
    }

    foreach (var dw in AOBuddyNav.DoorwaysFromField(pr))
    {
        var (x, z) = Place(dw.X, dw.Z);
        var dx = x - pcx;
        var dz = z - pcz;
        double nx = dw.Nx, nz = dw.Nz;
        for (var t = 0; t < turns; t++)
        {
            (nx, nz) = (-nz, nx);
        }

        roomSockets.Add((ri, x, dw.Y, z));
    }

    foreach (var src in new[] { colByRoom, wallByRoom })
    {
        if (pr.Index >= src.Count || src[pr.Index] == null) continue;
        foreach (var arr in src[pr.Index])
        for (int o = 0; o + 8 < arr.Length; o += 9)
        {
            var pts = new (double, double, double)[3];
            var spill = false;
            for (var k = 0; k < 3; k++)
            {
                var (x, z) = Place(arr[o + k * 3], arr[o + k * 3 + 2]);
                if (Math.Abs(x - pcx) > 40 || Math.Abs(z - pcz) > 40) { spill = true; break; }
                pts[k] = (x, arr[o + k * 3 + 1], z);
            }
            if (spill) continue;
            foreach (var p in pts) Take(p.Item1, p.Item3);
            EmitTri(mesh, pts);
        }
    }

    if (pr.Tile != null && pr.Height != null && pr.Rect != null && pr.Pos != null)
    {
        int x1 = pr.Rect[0], z1 = pr.Rect[1];
        int trows = pr.Tile.Length, tcols = pr.Tile.Max(t => t?.Length ?? 0);
        double mx = (pr.Rect[0] + pr.Rect[2]) / 2.0, mz = (pr.Rect[1] + pr.Rect[3]) / 2.0; // EXCLUSIVE convention // the tile grid's centre
        // CORNER-INTERPOLATED TILE QUADS (the same vertex grid the builder feeds Recast): a
        // (w+1)x(h+1) vertex lattice, vertex = the mean of the adjacent tiles' heights - flat
        // plates showed cliffs where the real floor is a slope (owner, 2026-10-11, Medium16)
        var thG = new float[trows + 2, tcols + 2];
        var okG = new bool[trows + 2, tcols + 2];
        for (int tr = 0; tr < trows; tr++)
        {
            if (pr.Tile[tr] == null) continue;
            for (int tc = 0; tc < pr.Tile[tr].Length; tc++)
            {
                if (pr.Tile[tr][tc] == 0 || pr.Height[tr] == null || tc >= pr.Height[tr].Length) continue;
                var th = pr.Pos[1] + (pr.Height[tr][tc] - pr.HeightBase) * pool.HeightScale;
                if (Math.Abs(th - pr.Pos[1]) > 30f) continue;
                thG[tr + 1, tc + 1] = (float)th;
                okG[tr + 1, tc + 1] = true;
            }
        }

        var cG = new float[trows + 1, tcols + 1];
        for (var jj = 0; jj <= trows; jj++)
        for (var ii = 0; ii <= tcols; ii++)
        {
            float sum = 0;
            var n = 0;
            for (var dj = 0; dj <= 1; dj++)
            for (var di = 0; di <= 1; di++)
            {
                if (okG[jj + dj, ii + di]) { sum += thG[jj + dj, ii + di]; n++; }
            }

            cG[jj, ii] = n > 0 ? sum / n : float.NaN;
        }

        for (int tr = 0; tr < trows; tr++)
        {
            if (pr.Tile[tr] == null) continue;
            for (int tc = 0; tc < pr.Tile[tr].Length; tc++)
            {
                if (pr.Tile[tr][tc] == 0 || !okG[tr + 1, tc + 1]) continue;
                double wxp = pr.Pos[0] + (x1 + tc - mx) * pool.Cell;
                double wzp = pr.Pos[2] + (z1 + tr - mz) * pool.Cell;
                var h2 = pool.Cell / 2.0;
                var corners = new (double x, double z, double y)[]
                {
                    (wxp - h2, wzp - h2, cG[tr, tc]),
                    (wxp - h2, wzp + h2, cG[tr + 1, tc]),
                    (wxp + h2, wzp + h2, cG[tr + 1, tc + 1]),
                    (wxp + h2, wzp - h2, cG[tr, tc + 1]),
                };
                var pc = corners.Select(c => Place(c.x, c.z)).ToList();
                foreach (var c in pc) Take((float)c.x, (float)c.z);
                EmitTri(tiles, new[] { (pc[0].x, (double)corners[0].y, pc[0].z), (pc[1].x, (double)corners[1].y, pc[1].z), (pc[2].x, (double)corners[2].y, pc[2].z) });
                EmitTri(tiles, new[] { (pc[0].x, (double)corners[0].y, pc[0].z), (pc[2].x, (double)corners[2].y, pc[2].z), (pc[3].x, (double)corners[3].y, pc[3].z) });
            }
        }
    }

    var cols = pr.Index < colByRoom.Count ? colByRoom[pr.Index] ?? new List<float[]>() : new List<float[]>();
    var wls = pr.Index < wallByRoom.Count ? wallByRoom[pr.Index] ?? new List<float[]>() : new List<float[]>();
    var dws = AOBuddyNav.DoorwaysFromField(pr);
    var legacy = NorthboundBuilder.Build(pr.Index, pr, cols, wls, dws, pool, recast: false);
    var recast = NorthboundBuilder.Build(pr.Index, pr, cols, wls, dws, pool, recast: true);
    var meshOnly = NorthboundBuilder.Build(pr.Index, pr, cols, wls, dws, pool, recast: true, tilesInRecast: false);
    foreach (var (lat, sink) in new[] { (recast, floorsRecast), (legacy, floorsLegacy), (meshOnly, floorsMeshOnly) })
    {
        if (lat == null) continue;
        for (var j = 0; j < lat.H; j++)
        for (var i = 0; i < lat.W; i++)
        {
            var lv = lat.Levels[j * lat.W + i];
            if (lv == null || lv.Count == 0) continue;
            var (cx, cz) = Place(lat.Ox + (i + 0.5f) * 0.2f, lat.Oz + (j + 0.5f) * 0.2f);
            Take(cx, cz);
            var low = lv[0];
            const float q = 0.09f;
            var quad = new (double x, double z)[] { (cx - q, cz - q), (cx + q, cz - q), (cx + q, cz + q), (cx - q, cz + q) };
            foreach (var k in new[] { 0, 1, 2, 0, 2, 3 })
            {
                sink.Add((float)quad[k].x); sink.Add((float)low); sink.Add((float)quad[k].z);
            }
        }
    }
}

// THE UNREACHABLE ROOMS (from the leafs sweep): red overlay quads + red labels
var unreachable = new HashSet<int>(unreachableArg.Split(',').Where(s => s.Length > 0).Select(int.Parse));
var unreachQuads = new List<float>();
foreach (var (pr, pcx, pcz, prot) in placed)
{
    var ri = placed.IndexOf((pr, pcx, pcz, prot));
    if (!unreachable.Contains(ri) || pr.Rect == null || pr.Pos == null) continue;
    int turns = ((-prot) % 4 + 4) % 4;
    double mx2 = (pr.Rect[0] + pr.Rect[2]) / 2.0, mz2 = (pr.Rect[1] + pr.Rect[3]) / 2.0;
    var cs2 = new List<(double x, double z)>();
    foreach (var (rx, rz) in new[] { (pr.Rect[0], pr.Rect[1]), (pr.Rect[2] + 1, pr.Rect[1]), (pr.Rect[2] + 1, pr.Rect[3] + 1), (pr.Rect[0], pr.Rect[3] + 1) })
    {
        double dx = (rx - mx2) * pool.Cell, dz = (rz - mz2) * pool.Cell;
        for (var t = 0; t < turns; t++) (dx, dz) = (-dz, dx);
        cs2.Add((pcx + dx, pcz + dz));
    }

    var yq = dumpY[ri] + 0.4;
    for (var k = 0; k < 3; k++)
    {
        unreachQuads.Add((float)cs2[k].x); unreachQuads.Add((float)yq); unreachQuads.Add((float)cs2[k].z);
    }
    for (var k = 0; k < 3; k++)
    {
        var j2 = new[] { 0, 3, 2 }[k];
        unreachQuads.Add((float)cs2[j2].x); unreachQuads.Add((float)yq); unreachQuads.Add((float)cs2[j2].z);
    }
}

// the server rows + the paired sockets (A = the row's room, B = the neighbour)
var rows = new List<(int room, int adj, double x, double y, double z)>();
foreach (var line in File.ReadAllLines(layoutFile))
{
    foreach (Match mt in Regex.Matches(line, @"Room=(-?\d+) Adj=(\d+) \(([\d,]+)\)"))
    {
        var p = mt.Groups[3].Value.Split(',');
        double x = p.Length >= 6 ? D(p[0] + "." + p[1]) : D(p[0]);
        double y = p.Length >= 6 ? D(p[2] + "." + p[3]) : 5.0;
        double z = p.Length >= 6 ? D(p[4] + "." + p[5]) : D(p[^1]);
        rows.Add((int.Parse(mt.Groups[1].Value), int.Parse(mt.Groups[2].Value), x, y, z));
    }
}

var pairRows = new List<double[]>();
var pairA = new List<double[]>();
var pairB = new List<double[]>();
var labels = new List<object>();
foreach (var row in rows)
{
    if (row.room < 0 || row.room >= placed.Count) continue;
    var candA = roomSockets.Where(s => s.roomIdx == row.room).OrderBy(s => (s.x - row.x) * (s.x - row.x) + (s.z - row.z) * (s.z - row.z)).FirstOrDefault();
    var candB = roomSockets.Where(s => s.roomIdx == row.adj).OrderBy(s => (s.x - row.x) * (s.x - row.x) + (s.z - row.z) * (s.z - row.z)).FirstOrDefault();
    if (candA.roomIdx == row.room)
    {
        Take(candA.x, candA.z);
        pairA.Add(new[] { candA.x, candA.y, candA.z });
        labels.Add(new { x = candA.x, y = candA.y, z = candA.z, text = $"A r{row.room} ({candA.x:0.00},{candA.z:0.00})" });
    }

    if (candB.roomIdx == row.adj)
    {
        Take(candB.x, candB.z);
        pairB.Add(new[] { candB.x, candB.y, candB.z });
        labels.Add(new { x = candB.x, y = candB.y, z = candB.z, text = $"B r{row.adj} ({candB.x:0.00},{candB.z:0.00})" });
    }


}

var cx0 = (mnx + mxx) / 2f;
var cz0 = (mnz + mxz) / 2f;
void Rec(List<float> l)
{
    for (var k = 0; k < l.Count; k += 3)
    {
        l[k] -= (float)cx0;
        l[k + 2] -= (float)cz0;
    }
}
Rec(unreachQuads);
foreach (var l in meshBy.Values) Rec(l);
foreach (var l in tilesBy.Values) Rec(l);
foreach (var l in recastBy.Values) Rec(l);
foreach (var l in legacyBy.Values) Rec(l);
foreach (var l in meshOnlyBy.Values) Rec(l);
var labelsShifted = labels.Select(o =>
{
    var t = (dynamic)o;
    return new { x = t.x - cx0, y = t.y, z = t.z - cz0, t.text };
}).ToList();

var json = JsonSerializer.Serialize(new
{
    centre = new[] { cx0, cz0 },
    meshBy = meshBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
    tilesBy = tilesBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
    recastBy = recastBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
    legacyBy = legacyBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
    meshOnlyBy = meshOnlyBy.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
    unreach = unreachQuads.ToArray(),
    unreachable = unreachable.ToArray(),
    pairA = pairA.Select(a => new[] { a[0] - cx0, a[1], a[2] - cz0 }).ToArray(),
    pairB = pairB.Select(a => new[] { a[0] - cx0, a[1], a[2] - cz0 }).ToArray(),
    labels = labelsShifted,
    rooms = placed.Select(t => new { Index = placed.IndexOf(t), PoolIndex = t.pool.Index, PoolName = t.pool.Name, x = t.cx - cx0, z = t.cz - cz0 }),
});

var html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>__TITLE__ - stitched mission floor</title>
<style>
  body { margin:0; overflow:hidden; background:#101018; color:#dde; font:13px system-ui; }
  #panel { position:fixed; top:10px; left:10px; background:#0009; padding:10px 14px; border-radius:8px; user-select:none; max-height:95vh; overflow:auto; }
  #panel h1 { font-size:14px; margin:0 0 6px; }
  #panel label { display:block; cursor:pointer; margin:2px 0; }
  #info { position:fixed; bottom:10px; left:10px; background:#0009; padding:8px 12px; border-radius:8px; white-space:pre; }
  input[type=checkbox] { vertical-align:-2px; }
</style>
<script type="importmap">
{ "imports": {
    "three": "https://cdn.jsdelivr.net/npm/three@0.161.0/build/three.module.js",
    "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.161.0/examples/jsm/"
} }
</script>
</head>
<body>
<div id="panel">
  <h1>__TITLE__</h1>
  <label><input type="checkbox" id="gMesh" checked> room mesh <span style="color:#9aa7bd">&#9632;</span></label>
  <label><input type="checkbox" id="gTiles" checked> GNDA tile floors <span style="color:#33cc66">&#9632;</span></label>
  <label><input type="checkbox" id="gRecast" checked> floors - RECAST v13 <span style="color:#66aaff">&#9632;</span></label>
  <label><input type="checkbox" id="gLegacy"> floors - interval v12 <span style="color:#ffcc44">&#9632;</span></label>
  <label><input type="checkbox" id="gMeshOnly"> floors - mesh only (no GNDA) <span style="color:#ff8844">&#9632;</span></label>
  <label><input type="checkbox" id="gUnreach" checked> UNREACHABLE rooms <span style="color:#ff2222">&#9632;</span></label>
  <label><input type="checkbox" id="gPairs" checked> SEAM PAIRS <span style="color:#00ffff">&#9679;</span>A <span style="color:#ff44ff">&#9679;</span>B</label>
  <label><input type="checkbox" id="gLabels" checked> coordinate labels</label>
  <label><input type="checkbox" id="gRooms" checked> room labels</label>
  <div style="margin-top:6px; border-top:1px solid #334; padding-top:4px;">
    ROOMS <button id="allRooms" style="font:11px system-ui">all</button> <button id="noRooms" style="font:11px system-ui">none</button>
    <div id="rooms" style="max-height:45vh; overflow:auto;"></div>
  </div>
</div>
<div id="info">__INFO__</div>
<script type="module">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const DATA = __DATA__;

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x101018);
const camera = new THREE.PerspectiveCamera(55, innerWidth / innerHeight, 0.05, 2000);
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
document.body.appendChild(renderer.domElement);
const controls = new OrbitControls(camera, renderer.domElement);

function triMesh(arr, color, opacity) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
  g.computeVertexNormals();
  const m = new THREE.Mesh(g, new THREE.MeshStandardMaterial({
    color, transparent: opacity < 1, opacity, side: THREE.DoubleSide, flatShading: true }));
  return m;
}
function floorMesh(arr) {
  let lo = Infinity, hi = -Infinity;
  for (let i = 1; i < arr.length; i += 3) { lo = Math.min(lo, arr[i]); hi = Math.max(hi, arr[i]); }
  const span = Math.max(1e-6, hi - lo);
  const g = new THREE.BufferGeometry();
  const pos = new Float32Array(arr);
  const col = new Float32Array(arr.length);
  for (let i = 0; i < arr.length; i += 3) {
    const t = (arr[i + 1] - lo) / span;
    const c = new THREE.Color().setHSL(0.38 - 0.5 * t, 0.85, 0.5);
    col[i] = c.r; col[i + 1] = c.g; col[i + 2] = c.b;
  }
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setAttribute('color', new THREE.BufferAttribute(col, 3));
  g.computeVertexNormals();
  return new THREE.Mesh(g, new THREE.MeshStandardMaterial({ vertexColors: true, side: THREE.DoubleSide }));
}

// THE LAYERS and THE ROOMS: every layer exists PER ROOM; visibility = layerOn[id] && roomOn[ri]
const layerDefs = [
  { id: 'gMesh', key: 'meshBy', kind: 'tri', color: 0x9aa7bd, opacity: 0.85 },
  { id: 'gTiles', key: 'tilesBy', kind: 'tri', color: 0x33cc66, opacity: 0.35 },
  { id: 'gRecast', key: 'recastBy', kind: 'floor' },
  { id: 'gLegacy', key: 'legacyBy', kind: 'floor' },
  { id: 'gMeshOnly', key: 'meshOnlyBy', kind: 'floor' },
];
const layerOn = { gMesh: true, gTiles: true, gRecast: true, gLegacy: false, gMeshOnly: false };
const layerMeshes = {};
const roomOn = {};
const roomGroups = {};
const roomIdxs = [...new Set([
  ...Object.keys(DATA.meshBy), ...Object.keys(DATA.tilesBy), ...Object.keys(DATA.recastBy),
].map(Number))].sort((a, b) => a - b);
for (const ri of roomIdxs) {
  const g = new THREE.Group();
  for (const ld of layerDefs) {
    const arr = DATA[ld.key][ri];
    if (!arr || arr.length === 0) continue;
    const m = ld.kind === 'floor' ? floorMesh(arr) : triMesh(arr, ld.color, ld.opacity);
    m.visible = layerOn[ld.id];
    g.add(m);
    (layerMeshes[ld.id] ??= []).push(m);
  }
  scene.add(g);
  roomGroups[ri] = g;
  roomOn[ri] = true;
}
function updateVis() {
  for (const ri of roomIdxs) roomGroups[ri].visible = roomOn[ri];
  for (const ld of layerDefs) for (const m of layerMeshes[ld.id] ?? []) m.visible = layerOn[ld.id];
}

// the unreachable overlay + the seam A/B pairs: global
const extraGroups = {};
{
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(DATA.unreach, 3));
  const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: 0xff2222, transparent: true, opacity: 0.45, side: THREE.DoubleSide, depthWrite: false }));
  scene.add(m); extraGroups.gUnreach = m;
}
{
  const pg = new THREE.Group();
  for (const p of DATA.pairA) {
    const m = new THREE.Mesh(new THREE.SphereGeometry(0.22, 12, 10), new THREE.MeshBasicMaterial({ color: 0x00ffff }));
    m.position.set(p[0], p[1] + 0.25, p[2]); pg.add(m);
  }
  for (const p of DATA.pairB) {
    const m = new THREE.Mesh(new THREE.SphereGeometry(0.22, 12, 10), new THREE.MeshBasicMaterial({ color: 0xff44ff }));
    m.position.set(p[0], p[1] + 0.25, p[2]); pg.add(m);
  }
  scene.add(pg); extraGroups.gPairs = pg;
}
for (const id of ['gUnreach', 'gPairs'])
  document.getElementById(id).addEventListener('change', e => { extraGroups[id].visible = e.target.checked; });

// the room panel: a checkbox per room + click-the-name to solo
const roomPanel = document.getElementById('rooms');
function updateRoomBoxes() {
  for (const ri of roomIdxs) document.getElementById('room' + ri).checked = roomOn[ri];
}
for (const ri of roomIdxs) {
  const r = DATA.rooms.find(x => x.Index === ri);
  const name = r ? r.Index + ' ' + r.PoolName : 'room ' + ri;
  const row = document.createElement('label');
  row.innerHTML = '<input type="checkbox" id="room' + ri + '" checked> <span class="rname" style="cursor:pointer">' + name + '</span>';
  roomPanel.appendChild(row);
  row.querySelector('input').addEventListener('change', e => { roomOn[ri] = e.target.checked; updateVis(); });
  row.querySelector('.rname').addEventListener('click', () => {
    const solo = !Object.keys(roomOn).every(k => (k == ri) ? roomOn[k] : !roomOn[k]);
    for (const k of Object.keys(roomOn)) roomOn[k] = (k == ri) ? solo : !solo;
    updateRoomBoxes(); updateVis();
  });
}
document.getElementById('allRooms').addEventListener('click', () => {
  for (const k of Object.keys(roomOn)) roomOn[k] = true;
  updateRoomBoxes(); updateVis();
});
document.getElementById('noRooms').addEventListener('click', () => {
  for (const k of Object.keys(roomOn)) roomOn[k] = false;
  updateRoomBoxes(); updateVis();
});

// the layer checkboxes
for (const ld of layerDefs)
  document.getElementById(ld.id).addEventListener('change', e => { layerOn[ld.id] = e.target.checked; updateVis(); });
for (const id of ['gUnreach', 'gPairs'])
  document.getElementById(id).addEventListener('change', e => { extraGroups[id].visible = e.target.checked; });

scene.add(new THREE.AxesHelper(5));
const grid = new THREE.GridHelper(400, 200, 0x334455, 0x223344);
scene.add(grid);
scene.add(new THREE.HemisphereLight(0xbfd4ff, 0x30281e, 1.4));
const dl = new THREE.DirectionalLight(0xffffff, 1.2); dl.position.set(60, 120, 40); scene.add(dl);

const box = new THREE.Box3();
for (const key of ['meshBy', 'recastBy']) for (const ri of Object.keys(DATA[key]))
  for (let i = 0; i < DATA[key][ri].length; i += 3) box.expandByPoint(new THREE.Vector3(DATA[key][ri][i], DATA[key][ri][i + 1], DATA[key][ri][i + 2]));
const c = box.getCenter(new THREE.Vector3()), s = box.getSize(new THREE.Vector3()).length();
camera.position.set(c.x + s * 0.4, c.y + s * 0.5, c.z + s * 0.4);
controls.target.copy(c);
controls.update();

// the text labels: doors (A/B) and rooms
const textTags = [];
function addTag(text, x, y, z, cls, group) {
  const tag = document.createElement('div');
  tag.textContent = text;
  tag.style.cssText = 'position:fixed; font:11px monospace; text-shadow:0 0 3px #000; pointer-events:none; ' + (cls || 'color:#8ff;');
  document.body.appendChild(tag);
  textTags.push({ tag, x, y, z, group });
}
for (const l of DATA.labels) addTag(l.text, l.x, l.y + 0.6, l.z, 'color:#ffe27a;', 'labels');
for (const r of DATA.rooms) {
  const dead = (DATA.unreachable || []).includes(r.Index);
  addTag((dead ? 'UNREACHABLE ' : '') + r.Index + ' ' + r.PoolName, r.x, 12, r.z,
    dead ? 'color:#ff4444; font-weight:bold;' : 'color:#8ff;', 'rooms');
}
const labelGroups = { labels: true, rooms: true };
for (const id of ['gLabels', 'gRooms'])
  document.getElementById(id).addEventListener('change', e => { labelGroups[id === 'gLabels' ? 'labels' : 'rooms'] = e.target.checked; });

const v = new THREE.Vector3();
(function tick() {
  requestAnimationFrame(tick);
  controls.update();
  renderer.render(scene, camera);
  for (const t of textTags) {
    if (!labelGroups[t.group]) { t.tag.style.display = 'none'; continue; }
    v.set(t.x, t.y, t.z).project(camera);
    t.tag.style.left = ((v.x * 0.5 + 0.5) * innerWidth) + 'px';
    t.tag.style.top = ((-v.y * 0.5 + 0.5) * innerHeight) + 'px';
    t.tag.style.display = '';
  }
})();
addEventListener('resize', () => { camera.aspect = innerWidth / innerHeight; camera.updateProjectionMatrix(); renderer.setSize(innerWidth, innerHeight); });
</script></script>
</body>
</html>
""";

html = html.Replace("__TITLE__", $"mission pf {Pf} ({placed.Count} rooms)")
    .Replace("__INFO__", $"rooms {placed.Count}\ncentred on ({cx0:0.0},{cz0:0.0})")
    .Replace("__DATA__", json);
var outPath = $"F:/testcellao/AOBuddy20/AOBuddy20/Build/mission-floor-viewer-{Pf}.html";
File.WriteAllText(outPath, html);
Console.WriteLine("wrote " + outPath + " (" + (new FileInfo(outPath).Length / 1048576.0).ToString("0.0") + " MB)");
