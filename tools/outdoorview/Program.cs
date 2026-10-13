// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: Program.cs (outdoorview)
//
// Created:       2026-10-13
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------
// THE OUTDOOR MESHGRID VIEWER (the mission-floor-viewer style, for city/outdoor playfields):
// terrain heightfield + mesh triangles + the per-cell walkable levels (the column-clip runs)
// as a self-contained Three.js HTML. Also draws the bot's own broadcast walk (CharDCMove, C)
// and the server's echo/placements (S) from a diag-packets.log, so the walk-vs-data mismatch
// the SetPos bursts report is visible in place.
//
// usage: outdoorview <navdir> <pf> [diag-packets.log] [out.html]
//   writes <navdir>\<pf>\meshgrid-viewer.html

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.WriteLine("usage: outdoorview <navdir> <pf> [diag-packets.log] [out.html]");
    return 2;
}

var navDir = args[0];
var pf = int.Parse(args[1], CultureInfo.InvariantCulture);
var cx0 = float.Parse(args[2], CultureInfo.InvariantCulture);
var cz0 = float.Parse(args[3], CultureInfo.InvariantCulture);
var half = args.Length > 4 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 120f;
var diag = args.Length > 5 && args[5] != "-" ? args[5] : null;
var outFile = args.Length > 6 ? args[6] : Path.Combine(navDir, pf.ToString(), "meshgrid-viewer.html");

// ── ground.bin: the terrain heightfield (AONG v4; zlib payload at the tail) ───────────────
var ground = File.ReadAllBytes(Path.Combine(navDir, pf.ToString(), "ground.bin"));
if (ground[0] != 'A' || ground[1] != 'O' || ground[2] != 'N' || ground[3] != 'G')
{
    throw new InvalidDataException("ground.bin: no AONG magic");
}

var gSx = BitConverter.ToInt32(ground, 8);
var gSz = BitConverter.ToInt32(ground, 12);
var gCell = BitConverter.ToSingle(ground, 16);
var gHscale = BitConverter.ToSingle(ground, 20);
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

float Height(int ix, int iz) => BitConverter.ToUInt16(gBody, 2 * (iz * gSx + ix)) * gHscale;
var worldW = (gSx - 1) * gCell;
var worldH = (gSz - 1) * gCell;
Console.WriteLine($"ground: {gSx}x{gSz} samples, cell {gCell} m -> world {worldW}x{worldH} m");

// ── meshes.bin: the unfiltered 1000013 triangles (AOCL) ───────────────────────────────────
var meshPath = Path.Combine(navDir, pf.ToString(), "meshes.bin");
var meshTris = new List<float[]>();
if (File.Exists(meshPath))
{
    var mb = File.ReadAllBytes(meshPath);
    if (mb[0] != 'A' || mb[1] != 'O' || mb[2] != 'C' || mb[3] != 'L')
    {
        throw new InvalidDataException("meshes.bin: no AOCL magic");
    }

    var chunks = BitConverter.ToInt32(mb, 8);
    var rawLen = BitConverter.ToInt32(mb, 12);
    var zLen = BitConverter.ToInt32(mb, 16);
    var body = new byte[rawLen];
    using (var msrc = new MemoryStream(mb, 20, zLen)) // the zlib stream, header included
    using (var inflate = new ZLibStream(msrc, CompressionMode.Decompress))
    {
        for (var got = 0; got < rawLen;)
        {
            got += inflate.Read(body, got, rawLen - got);
        }
    }

    var p = 0;
    for (var c = 0; c < chunks && p + 32 <= rawLen; c++)
    {
        p += 16; // instance, teleportDestPf, localizerType, localizerInstance
        var ox = BitConverter.ToSingle(body, p);
        var oy = BitConverter.ToSingle(body, p + 4);
        var oz = BitConverter.ToSingle(body, p + 8);
        var cnt = BitConverter.ToInt32(body, p + 12);
        p += 16;
        if (p + cnt * 18 > body.Length)
        {
            throw new InvalidDataException($"meshes.bin chunk {c} overruns");
        }

        for (var t = 0; t < cnt; t++)
        {
            var v = new float[9];
            for (var k = 0; k < 9; k++)
            {
                v[k] = (k % 3 == 0 ? ox : k % 3 == 1 ? oy : oz) + BitConverter.ToInt16(body, p + t * 18 + k * 2) / 100f;
            }

            meshTris.Add(v);
        }

        p += cnt * 18;
    }

    Console.WriteLine($"meshes: {meshTris.Count:N0} triangles");
}

// ── lattice.bin (AOCELL1 raw / AOCELL2 zlib): the per-cell walkable levels ────────────────
var latPath = Path.Combine(navDir, pf.ToString(), "lattice.bin");
var lCellsX = 0;
var lCellsZ = 0;
byte[] latPayload;
byte[] latAll;
var latMagic = "";
if (File.Exists(latPath))
{
    latAll = File.ReadAllBytes(latPath);
    latMagic = Encoding.ASCII.GetString(latAll, 0, 7);
    lCellsX = BitConverter.ToInt32(latAll, 19);
    lCellsZ = BitConverter.ToInt32(latAll, 23);
    if (latMagic == "AOCELL2")
    {
        var rawLen = BitConverter.ToInt32(latAll, 31);
        var zLen = BitConverter.ToInt32(latAll, 35);
        latPayload = new byte[rawLen];
        using var zsrc = new MemoryStream(latAll, 39, zLen);
        using var inflate = new ZLibStream(zsrc, CompressionMode.Decompress);
        var got = 0;
        while (got < rawLen)
        {
            got += inflate.Read(latPayload, got, rawLen - got);
        }
    }
    else // AOCELL1: the raw records start right after the 27-byte header
    {
        latPayload = latAll[27..];
    }
}
else
{
    throw new FileNotFoundException("lattice.bin not found: " + latPath);
}

var lLevels = new List<float[]>();
var q = 0;
for (var i = 0; i < lCellsX * lCellsZ; i++)
{
    var n = latPayload[q];
    q += 1;
    if (n == 0)
    {
        lLevels.Add(null);
        continue;
    }

    var lv = new float[n];
    for (var k = 0; k < n; k++)
    {
        lv[k] = BitConverter.ToInt16(latPayload, q + k * 2) / 100f;
    }

    q += n * 2;
    lLevels.Add(lv);
}

var walkable = 0;
var multi = 0;
foreach (var lv in lLevels)
{
    if (lv != null)
    {
        walkable++;
        if (lv.Length > 1)
        {
            multi++;
        }
    }
}

Console.WriteLine($"lattice: {lCellsX}x{lCellsZ} cells, {walkable:,} walkable ({multi:,} multi-level)");

var sw = System.Diagnostics.Stopwatch.StartNew();
// ── the diag capture: the bot's broadcast walk + the server's echo/placements ─────────────
const float Cell = 1.0f;
var walkC = new List<float[]>(); // [x, y, z]
var walkS = new List<float[]>();
if (diag != null && File.Exists(diag))
{
    var rx = new Regex(@"^#\d+ \S+ \S+ (S|C) N3/CharDCMove len=\d+ hex=([0-9a-fA-F]+)");
    foreach (var line in File.ReadAllLines(diag))
    {
        var m = rx.Match(line);
        if (!m.Success || m.Groups[2].Value.Length < 116)
        {
            continue;
        }

        var hex = m.Groups[2].Value;
        var raw = new byte[hex.Length / 2];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        if (raw.Length < 58 || BitConverter.ToInt32(raw, 16) != unchecked((int)0x54111123))
        {
            continue;
        }

        var pnt = new float[3];
        for (var k = 0; k < 3; k++)
        {
            pnt[k] = BitConverter.ToSingle(new[] { raw[46 + k * 4 + 3], raw[46 + k * 4 + 2], raw[46 + k * 4 + 1], raw[46 + k * 4] }, 0);
        }

        (m.Groups[1].Value == "S" ? walkS : walkC).Add(pnt);
    }

    Console.WriteLine($"diag: {walkC.Count} client moves, {walkS.Count} server echoes");
}

// ── the HTML emission ─────────────────────────────────────────────────────────────────────
var Inv = CultureInfo.InvariantCulture;
var sb = new StringBuilder(48 << 20);
sb.Append(@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>meshgrid viewer</title>
<style>
  body { margin:0; overflow:hidden; background:#101018; color:#dde; font:13px system-ui; }
  #panel { position:fixed; top:10px; left:10px; background:#0009; padding:10px 14px; border-radius:8px; user-select:none; }
  #panel label { display:block; cursor:pointer; margin:2px 0; }
  #info { position:fixed; bottom:10px; left:10px; background:#0009; padding:8px 12px; border-radius:8px; white-space:pre; }
</style>
<script type=""importmap"">
{ ""imports"": {
    ""three"": ""https://cdn.jsdelivr.net/npm/three@0.161.0/build/three.module.js"",
    ""three/addons/"": ""https://cdn.jsdelivr.net/npm/three@0.161.0/examples/jsm/""
} }
</script>
</head>
<body>
<div id=""panel"">
  <h1>meshgrid viewer pf ");
sb.Append(pf.ToString(CultureInfo.InvariantCulture));
sb.Append(@"</h1>
  <label><input type=""checkbox"" id=""gTerrain"" checked> terrain <span style=""color:#558833"">&#9632;</span></label>
  <label><input type=""checkbox"" id=""gMesh"" checked> mesh triangles <span style=""color:#8899aa"">&#9632;</span></label>
  <label><input type=""checkbox"" id=""gLevels"" checked> walkable levels <span style=""color:#66aaff"">&#9632;</span></label>
  <label><input type=""checkbox"" id=""gWalkC"" checked> bot walk <span style=""color:#ffcc44"">&#9632;</span></label>
  <label><input type=""checkbox"" id=""gWalkS""> server echo <span style=""color:#66ff88"">&#9632;</span></label>
</div>
<div id=""info"">orbit: LMB | zoom: wheel | pan: RMB</div>
<script type=""module"">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const WORLD_W = ");
sb.Append(worldW.ToString(CultureInfo.InvariantCulture));
sb.Append("; const WORLD_H = ");
sb.Append(worldH.ToString(CultureInfo.InvariantCulture));
sb.Append(@";
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x101018);
const camera = new THREE.PerspectiveCamera(55, innerWidth / innerHeight, 0.5, 20000);
camera.position.set(WORLD_W / 2, 400, WORLD_H / 2 + 600);
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
document.body.appendChild(renderer.domElement);
const controls = new OrbitControls(camera, renderer.domElement);
controls.target.set(WORLD_W / 2, 20, WORLD_H / 2);
addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight);
});
const layers = {};
function layer(name, id) {
  const g = new THREE.Group();
  g.visible = document.getElementById(id).checked;
  document.getElementById(id).addEventListener('change', e => g.visible = e.target.checked);
  scene.add(g);
  layers[id] = g;
  return g;
}
function heightColor(h, lo, hi) {
  const t = Math.max(0, Math.min(1, (h - lo) / Math.max(0.01, hi - lo)));
  const c = new THREE.Color().setHSL(0.66 - 0.66 * t, 0.85, 0.45 + 0.15 * t);
  return [c.r, c.g, c.b];
}
");

// the terrain: decimated 8 m quads, vertex colors by height
sb.Append("\nconst TERRAIN = {quads:[");
var tqSep = "";
var hLo = float.MaxValue;
var hHi = float.MinValue;
var step = 2; // heightfield samples per quad edge (4 m samples -> 8 m quads)
for (var iz = 0; iz < gSz - 1; iz += step)
{
    for (var ix = 0; ix < gSx - 1; ix += step)
    {
        var x0 = ix * gCell;
        var z0 = iz * gCell;
        var x1 = Math.Min(gSx - 1, ix + step) * gCell;
        var z1 = Math.Min(gSz - 1, iz + step) * gCell;
        var a = Height(ix, iz);
        var b = Height(Math.Min(gSx - 1, ix + step), iz);
        var c = Height(ix, Math.Min(gSz - 1, iz + step));
        var d = Height(Math.Min(gSx - 1, ix + step), Math.Min(gSz - 1, iz + step));
        hLo = Math.Min(hLo, Math.Min(a, Math.Min(b, Math.Min(c, d))));
        hHi = Math.Max(hHi, Math.Max(a, Math.Max(b, Math.Max(c, d))));
        sb.Append(tqSep).Append('[')
          .Append(a.ToString("F2", Inv)).Append(',')
          .Append(b.ToString("F2", Inv)).Append(',')
          .Append(c.ToString("F2", Inv)).Append(',')
          .Append(d.ToString("F2", Inv)).Append(',')
          .Append(x0.ToString("F1", Inv)).Append(',')
          .Append(z0.ToString("F1", Inv)).Append(',')
          .Append(x1.ToString("F1", Inv)).Append(',')
          .Append(z1.ToString("F1", Inv)).Append(']');
        tqSep = ",";
    }
}

sb.Append("], hlo:").Append(hLo.ToString("F2", Inv))
  .Append(", hhi:").Append(hHi.ToString("F2", Inv)).Append("};\n");

// the mesh triangles
sb.Append("\nconst MESH = [");
var msep = "";
foreach (var v in meshTris)
{
    sb.Append(msep);
    for (var k = 0; k < 9; k++)
    {
        sb.Append(v[k].ToString("F2", Inv));
        sb.Append(k == 8 ? "\n" : ",");
    }

    msep = ",";
}

sb.Append("];\n");

// the lattice levels: points (cell centre x, level y, cell centre z)
sb.Append("\nconst LEVELS = {cs:").Append(lCellsX.ToString(Inv))
  .Append(", cell:").Append(Cell.ToString("F1", Inv))
  .Append(", pts:[");
var lsep = "";
for (var i = 0; i < lLevels.Count; i++)
{
    var lv = lLevels[i];
    if (lv == null)
    {
        continue;
    }

    var gx = i % lCellsX;
    var gz = i / lCellsX;
    var x = (gx + 0.5f) * Cell;
    var z = (gz + 0.5f) * Cell;

    for (var k = 0; k < lv.Length; k++)
    {
        sb.Append(lsep).Append('[')
          .Append(x.ToString("F1", Inv)).Append(',')
          .Append(lv[k].ToString("F2", Inv)).Append(',')
          .Append(z.ToString("F1", Inv)).Append(']');
        lsep = ",";
    }
}

sb.Append("]};\n");

// the walks
sb.Append("\nconst WALK_C = [");
var wsep = "";
foreach (var p in walkC)
{
    sb.Append(wsep).Append('[').Append(p[0].ToString("F1", Inv)).Append(',')
      .Append(p[1].ToString("F2", Inv)).Append(',')
      .Append(p[2].ToString("F1", Inv)).Append(']');
    wsep = ",";
}

sb.Append("];\nconst WALK_S = [");
wsep = "";
foreach (var p in walkS)
{
    sb.Append(wsep).Append('[').Append(p[0].ToString("F1", Inv)).Append(',')
      .Append(p[1].ToString("F2", Inv)).Append(',')
      .Append(p[2].ToString("F1", Inv)).Append(']');
    wsep = ",";
}

sb.Append("];\n");

// the scene assembly
sb.Append(@"
{
  const g = layer('terrain', 'gTerrain');
  const pos = [], col = [];
  for (const [a, b, c, d, x0, z0, x1, z1] of TERRAIN.quads) {
    const ca = heightColor(a, TERRAIN.hlo, TERRAIN.hhi);
    const cb = heightColor(b, TERRAIN.hlo, TERRAIN.hhi);
    const cc = heightColor(c, TERRAIN.hlo, TERRAIN.hhi);
    const cd = heightColor(d, TERRAIN.hlo, TERRAIN.hhi);
    // corners: a=(x0,z0) b=(x1,z0) c=(x0,z1) d=(x1,z1)
    pos.push(x0, a, z0, x1, d, z1, x1, b, z0);
    col.push(...ca, ...cd, ...cb);
    pos.push(x0, a, z0, x0, c, z1, x1, d, z1);
    col.push(...ca, ...cc, ...cd);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  geo.computeVertexNormals();
  g.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide })));
}
{
  const g = layer('mesh', 'gMesh');
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(MESH, 3));
  geo.computeVertexNormals();
  g.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: 0x8899aa, side: THREE.DoubleSide })));
}
{
  const g = layer('levels', 'gLevels');
  const pos = [], col = [];
  for (const [x, y, z] of LEVELS.pts) {
    pos.push(x, y, z);
    const cc = heightColor(y, TERRAIN.hlo, TERRAIN.hhi);
    col.push(...cc);
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.add(new THREE.Points(geo, new THREE.PointsMaterial({ size: 1.5, vertexColors: true })));
}
{
  const mkLine = (pts, color) => {
    if (pts.length < 2) return null;
    const g = new THREE.Group();
    for (var i = 0; i + 1 < pts.length; i++) {
      const geo = new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(pts[i][0], pts[i][1], pts[i][2]),
        new THREE.Vector3(pts[i + 1][0], pts[i + 1][1], pts[i + 1][2])]);
      g.add(new THREE.Line(geo, new THREE.LineBasicMaterial({ color })));
    }
    return g;
  };
  const gc = mkLine(WALK_C, 0xffcc44);
  if (gc) layer('walkC', 'gWalkC').add(gc);
  const gs = mkLine(WALK_S, 0x66ff88);
  if (gs) layer('walkS', 'gWalkS').add(gs);
}
const animate = () => {
  requestAnimationFrame(animate);
  controls.update();
  renderer.render(scene, camera);
};
animate();
</script>
</body>
</html>
");

File.WriteAllText(outFile, sb.ToString());
Console.WriteLine($"wrote {outFile} ({new FileInfo(outFile).Length / 1048576.0:0.0} MB) in {sw.Elapsed.TotalSeconds:0}s");
return 0;
