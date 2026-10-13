// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: NavController.cs
//
// Last modified: 2026-10-01
// Created:       2026-10-01 (ported from AOBuddy10 NavController.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     NAV — persistent, per-playfield walkable MEMORY (ported from AOBuddy10). Records the OWNER's clean
///     footsteps into nav/&lt;playfieldId&gt;.json so ground we've already walked is never guessed again, and
///     catalogues the playfield's interactable objects and mob spawns. WALL-SAFETY BY CONSTRUCTION: the graph
///     holds ONLY edges the owner actually walked - no synthetic shortcuts, no straightening across
///     un-walked space; a recorded run cannot pass through a wall because the owner didn't.
///     This controller NEVER moves the body. It only supplies routes (lists of already-walked points).
///     THREADING (owner, 2026-10-01): all state sits under one lock, so RecordOwner/SetPlayfield/Tick may
///     run on the update thread while RouteToward/NearestTransition/EntryPoint are read from any other.
///     CROSSINGS (owner, 2026-10-13): TeleportWatcher handles the server's N3Teleport, so a warp nano's
///     pf change records a Kind="teleport" transition; a pf change with neither a warp nor a crossing
///     position stays unrecorded (the desync path in SetPlayfield).
/// </summary>
public class NavController
{
    // Knobs, AOBuddy10 Config.cs:337-345 defaults.
    public bool NavRecord = true; // master record switch
    public float NavPointSpacing = 1.5f; // clean-line thinning, metres
    public float NavSnapMeters = 15f; // bot AND owner must be within this of the same recorded run to reuse it
    public float NavSegmentBreakMeters = 12f; // a gap bigger than this in the owner track splits the run
    public float NavWeldMeters = 3f; // join points from different runs within this distance (same walked junction)
    public float NavAutosaveSec = 20f; // flush the file this often while dirty

    private readonly Action<string> _log;
    private readonly string _navDir;
    private readonly object _sync = new();

    private readonly HashSet<int> _seenObj = new(); // object instances already catalogued this pf
    private readonly List<float[]> _seg = new(); // the run being recorded right now
    private Vector3? _lastPt; // last recorded owner point (segment tail)
    private bool _dirty;
    private double _saveAccum;
    private double _scanAccum; // throttle for the interactable-object scan
    private NavZone _zone; // the current playfield's memory (null until first SetPlayfield)

    public NavController(string dataDir, Action<string> log = null)
    {
        _log = log;
        _navDir = Path.Combine(dataDir, "nav");
        try
        {
            Directory.CreateDirectory(_navDir);
        }
        catch
        {
        }
    }

    public int PlayfieldId
    {
        get
        {
            lock (_sync)
            {
                return _zone?.Playfield ?? 0;
            }
        }
    }

    // ---- Playfield switching -------------------------------------------------

    // Called every frame with the live playfield id. No-ops unless it changed. On a change: finalise the
    // current run, record a transition from the old pf's exit spot to the new pf (real crossings only -
    // see the class note), save the old file, then load the new pf's memory. kind names HOW we crossed:
    // "zone" (a walked crossing) or "teleport" (a warp nano carried us - spot NOT walk-to-useful).
    public void SetPlayfield(int pf, string name, Vector3? crossPos, bool realCrossing, string kind = "zone")
    {
        if (pf <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_zone != null && _zone.Playfield == pf)
            {
                return;
            }

            var oldPf = _zone != null ? _zone.Playfield : 0;
            EndSegmentLocked("zone");

            if (_zone != null)
            {
                if (realCrossing && oldPf > 0 && crossPos.HasValue)
                {
                    _zone.Transitions.Add(new NavTransition { X = crossPos.Value.X, Y = crossPos.Value.Y, Z = crossPos.Value.Z, ToPf = pf, Kind = kind, Name = "" });
                    _dirty = true;
                }
                else if (oldPf > 0)
                {
                    _log?.Invoke($"NAV: pf {oldPf} -> {pf} with NO teleport - not recording a zone transition (desync/partial zone).");
                }

                SaveLocked();
            }

            LoadLocked(pf, name);
            _log?.Invoke($"NAV: now in pf {pf} ({name}) - {_zone.Segments.Count} segments, {TotalPointsLocked()} points, {_zone.Transitions.Count} transitions on file.");
        }
    }

    private void LoadLocked(int pf, string name)
    {
        _seg.Clear();
        _lastPt = null;
        string file = FileFor(pf);
        try
        {
            if (File.Exists(file))
            {
                _zone = JsonStore.Load<NavZone>(file, _log) ?? NewZone(pf, name);
                _zone.Segments ??= new List<List<float[]>>();
                _zone.Transitions ??= new List<NavTransition>();
                _zone.Playfield = pf;
                if (string.IsNullOrEmpty(_zone.Name))
                {
                    _zone.Name = name;
                }
            }
            else
            {
                _zone = NewZone(pf, name);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NAV: load {file} failed ({ex.Message}) - starting fresh.");
            _zone = NewZone(pf, name);
        }

        _seenObj.Clear();
        if (_zone.Objects != null)
        {
            foreach (var o in _zone.Objects)
            {
                _seenObj.Add(o.Instance);
            }
        }

        _scanAccum = 0;
        _dirty = false;
    }

    private static NavZone NewZone(int pf, string name) =>
        new() { Playfield = pf, Name = name, Segments = new List<List<float[]>>(), Transitions = new List<NavTransition>() };

    // ---- Recording -----------------------------------------------------------

    // Feed the owner's position each frame. clean = his walking is trustworthy this frame (in AOBuddy10:
    // following properly; here: simply visible - the gap splitter below guards teleports). When not clean,
    // the current run is closed so a break episode never becomes part of a segment.
    public void RecordOwner(Vector3 p, bool clean)
    {
        lock (_sync)
        {
            if (!NavRecord || _zone == null)
            {
                return;
            }

            if (!clean)
            {
                EndSegmentLocked("not-clean");
                return;
            }

            if (_lastPt.HasValue)
            {
                float d = Vector3.Distance(p, _lastPt.Value);
                if (d < NavPointSpacing)
                {
                    return; // thin to a clean line
                }

                if (d > NavSegmentBreakMeters)
                {
                    EndSegmentLocked("gap"); // owner blinked/teleported - split
                }
            }

            _seg.Add(new[] { p.X, p.Y, p.Z });
            _lastPt = p;
            _dirty = true;
        }
    }

    // Finalise the run in progress. Runs of a single point are dropped (no edge).
    private void EndSegmentLocked(string why)
    {
        if (_seg.Count >= 2 && _zone != null)
        {
            _zone.Segments.Add(new List<float[]>(_seg));
            _dirty = true;
            _log?.Invoke($"NAV: segment closed ({why}) - {_seg.Count} pts; pf {_zone.Playfield} now {_zone.Segments.Count} segments.");
        }

        _seg.Clear();
        _lastPt = null;
    }

    public void Tick(double dt)
    {
        lock (_sync)
        {
            if (_zone != null && (_scanAccum += dt) >= 2.0)
            {
                _scanAccum = 0;
                ScanObjectsLocked();
                ScanMobsLocked();
            }

            if (!_dirty)
            {
                return;
            }

            _saveAccum += dt;
            if (_saveAccum >= NavAutosaveSec)
            {
                _saveAccum = 0;
                SaveLocked();
            }
        }
    }

    // Catalog every interactable world object the bot can currently see (terminals, doors, containers,
    // ...). Skips characters (players/NPCs/self) and transient drops (corpses/loot). Deduped by instance.
    // AOBuddy10 also logged each mob's 3D model (MobModels); that reader is not ported yet.
    private void ScanObjectsLocked()
    {
        if (_zone == null)
        {
            return;
        }

        _zone.Objects ??= new List<NavObject>();
        foreach (var dyn in DynelManager.AllDynels)
        {
            if (dyn == null || dyn is SimpleChar)
            {
                continue; // not players/NPCs/self
            }

            int inst = dyn.Identity.Instance;
            if (!_seenObj.Add(inst))
            {
                continue; // already have it
            }

            string type = dyn.Identity.Type.ToString();
            if (type.IndexOf("Corpse", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue; // transient
            }

            Vector3 p = dyn.Transform.Position;
            _zone.Objects.Add(new NavObject { X = p.X, Y = p.Y, Z = p.Z, Type = (int)dyn.Identity.Type, Instance = inst, Name = dyn.Name ?? "" });
            _dirty = true;
            _log?.Invoke($"NAV: object '{dyn.Name}' [{type}] at ({p.X:0},{p.Y:0},{p.Z:0}).");
        }
    }

    // Catalog mob SPAWNS (mob spawns are consistent per zone): deduped by name within a radius, so one
    // entry per named spawn point. Pc/PetType from what the dynel itself says (an owned NPC is a pet).
    private void ScanMobsLocked()
    {
        if (_zone == null)
        {
            return;
        }

        _zone.Mobs ??= new List<NavMob>();
        foreach (var npc in DynelManager.Npcs)
        {
            if (npc == null || string.IsNullOrEmpty(npc.Name))
            {
                continue;
            }

            Vector3 p = npc.Transform.Position;
            var known = false;
            foreach (var m in _zone.Mobs)
            {
                if (string.Equals(m.Name, npc.Name, StringComparison.OrdinalIgnoreCase)
                    && Vector3.Distance(new Vector3(m.X, m.Y, m.Z), p) < 20f)
                {
                    known = true;
                    break;
                }
            }

            if (known)
            {
                continue;
            }

            int lvl = 0;
            npc.TryGetStat(Stat.Level, out lvl);
            _zone.Mobs.Add(new NavMob { X = p.X, Y = p.Y, Z = p.Z, Name = npc.Name, Level = lvl, PetType = npc.Owner.HasValue ? 1 : 0 });
            _dirty = true;
            _log?.Invoke($"NAV: mob spawn '{npc.Name}' (lvl {lvl}) at ({p.X:0},{p.Y:0},{p.Z:0}).");
        }
    }

    // Flush the current run into the file too, so a save never loses the last (still-open) run.
    public void Save()
    {
        lock (_sync)
        {
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        if (_zone == null)
        {
            return;
        }

        try
        {
            // Persist the in-progress run without ending it (copy, don't clear).
            var snapshot = JsonConvert.DeserializeObject<NavZone>(JsonConvert.SerializeObject(_zone));
            if (_seg.Count >= 2)
            {
                snapshot.Segments.Add(new List<float[]>(_seg));
            }

            snapshot.Updated = DateTime.Now.ToString("s");
            if (JsonStore.Save(FileFor(_zone.Playfield), JsonConvert.SerializeObject(snapshot, Formatting.Indented), _log))
            {
                _dirty = false;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NAV: save failed ({ex.Message}).");
        }
    }

    // ---- Use: a known route toward a spot -------------------------------------

    // Wall-safe first cut: only replay WITHIN one recorded run (guaranteed walked), welded between runs
    // where the owner stood at both. Dijkstra over the welded walked graph; never invents an edge.
    public List<Vector3> RouteToward(Vector3 from, Vector3 toward)
    {
        lock (_sync)
        {
            if (_zone == null || _zone.Segments == null || _zone.Segments.Count == 0)
            {
                return null;
            }

            // Flatten every recorded point into one node set (recordings fragment into many short runs).
            var nodes = new List<Vector3>();
            var seg = new List<int>();
            var idx = new List<int>();
            for (var s = 0; s < _zone.Segments.Count; s++)
            {
                var pts = _zone.Segments[s];
                for (var i = 0; i < pts.Count; i++)
                {
                    nodes.Add(At(pts, i));
                    seg.Add(s);
                    idx.Add(i);
                }
            }

            var n = nodes.Count;
            if (n < 2)
            {
                return null;
            }

            int src = Nearest(nodes, from, out float dFrom);
            int dst = Nearest(nodes, toward, out float dTo);
            if (src < 0 || dst < 0 || src == dst)
            {
                return null;
            }

            if (dFrom > NavSnapMeters || dTo > NavSnapMeters)
            {
                return null; // both must be ON a recorded run
            }

            // Edges: consecutive points WITHIN a run (real walked steps), PLUS "welds" between points from
            // any runs that lie within NavWeldMeters - the owner stood at both, so that short hop is the
            // same walkable junction. Both kinds are ground the owner actually walked.
            var adj = new List<KeyValuePair<int, float>>[n];
            for (var k = 0; k < n; k++)
            {
                adj[k] = new List<KeyValuePair<int, float>>();
            }

            for (var a = 0; a < n; a++)
            {
                for (var b = a + 1; b < n; b++)
                {
                    var sameRun = seg[a] == seg[b] && Math.Abs(idx[a] - idx[b]) == 1;
                    float d = Vector3.Distance(nodes[a], nodes[b]);
                    if (sameRun || d <= NavWeldMeters)
                    {
                        adj[a].Add(new KeyValuePair<int, float>(b, d));
                        adj[b].Add(new KeyValuePair<int, float>(a, d));
                    }
                }
            }

            var dist = new float[n];
            var prev = new int[n];
            var done = new bool[n];
            for (var k = 0; k < n; k++)
            {
                dist[k] = float.MaxValue;
                prev[k] = -1;
            }

            dist[src] = 0;
            for (var it = 0; it < n; it++)
            {
                int u = -1;
                float best = float.MaxValue;
                for (var k = 0; k < n; k++)
                {
                    if (!done[k] && dist[k] < best)
                    {
                        best = dist[k];
                        u = k;
                    }
                }

                if (u < 0 || u == dst)
                {
                    break;
                }

                done[u] = true;
                foreach (var e in adj[u])
                {
                    float nd = dist[u] + e.Value;
                    if (nd < dist[e.Key])
                    {
                        dist[e.Key] = nd;
                        prev[e.Key] = u;
                    }
                }
            }

            if (prev[dst] == -1)
            {
                return null; // the walked graph doesn't connect the two
            }

            var route = new List<Vector3>();
            for (int k = dst; k != -1; k = prev[k])
            {
                route.Add(nodes[k]);
            }

            route.Reverse();
            return route.Count >= 2 ? route : null;
        }
    }

    // The recorded spots where the owner crossed OUT of THIS playfield.
    public Vector3? NearestTransition(Vector3 from)
    {
        lock (_sync)
        {
            if (_zone == null || _zone.Transitions == null || _zone.Transitions.Count == 0)
            {
                return null;
            }

            Vector3 best = default;
            float bd = float.MaxValue;
            var found = false;
            foreach (var t in _zone.Transitions)
            {
                var p = new Vector3(t.X, t.Y, t.Z);
                float d = Vector3.Distance(p, from);
                if (d < bd)
                {
                    bd = d;
                    best = p;
                    found = true;
                }
            }

            return found ? best : null;
        }
    }

    // The spot we first stood on after zoning IN (the earliest recorded point): the zone out is just
    // the entry, backwards.
    public Vector3? EntryPoint()
    {
        lock (_sync)
        {
            if (_zone == null || _zone.Segments == null)
            {
                return null;
            }

            foreach (var s in _zone.Segments)
            {
                if (s != null && s.Count > 0)
                {
                    return At(s, 0);
                }
            }

            return null;
        }
    }

    public string Status()
    {
        lock (_sync)
        {
            if (_zone == null)
            {
                return "Nav: no playfield loaded yet.";
            }

            int warps = _zone.Warps?.Count ?? 0;
            int objs = _zone.Objects?.Count ?? 0;
            int mobs = _zone.Mobs?.Count ?? 0;
            return $"Nav pf={_zone.Playfield} ({_zone.Name}): {_zone.Segments.Count} segments, {TotalPointsLocked()} points, " +
                   $"{_zone.Transitions.Count} transitions, {warps} warps, {objs} objects, {mobs} mobs. " +
                   $"recording={NavRecord} curRun={_seg.Count}pts dirty={_dirty}.";
        }
    }

    private int TotalPointsLocked() => _zone?.Segments?.Sum(s => s.Count) ?? 0;

    private string FileFor(int pf) => Path.Combine(_navDir, pf + ".json");

    private static Vector3 At(List<float[]> seg, int i) => new(seg[i][0], seg[i][1], seg[i][2]);

    private static int Nearest(List<Vector3> nodes, Vector3 p, out float d)
    {
        var best = -1;
        d = float.MaxValue;
        for (var i = 0; i < nodes.Count; i++)
        {
            float dd = Vector3.Distance(p, nodes[i]);
            if (dd < d)
            {
                d = dd;
                best = i;
            }
        }

        return best;
    }
}

// ---- On-disk model (one file per playfield) ----------------------------------

public class NavZone
{
    public int Playfield;
    public string Name;
    public string Updated;
    public List<List<float[]>> Segments = new(); // each = one walked run [ [x,y,z], ... ]
    public List<NavTransition> Transitions = new();
    public List<NavWarp> Warps = new(); // floor buttons / lifts (used-object teleports)
    public List<NavObject> Objects = new(); // interactables: terminals (shop/bank/insurance/mission), doors, containers...
    public List<NavMob> Mobs = new(); // mob spawn points (name + where it spawns), for "go kill X"
}

// A logged mob spawn: where a named mob is found (spawns are consistent per zone).
public class NavMob
{
    public float X, Y, Z;
    public string Name;
    public int Level;
    public int? MonsterData, Mesh, MonsterTexture; // AOBuddy10's MobModels filled these; not ported yet

    public bool? Pc; // who it is (a person is not a spawn) - filled when we can tell
    public int? PetType; // an owned NPC is a pet
}

// A logged interactable world object: its position, its identity type, and its NAME (which is what tells
// a Shop from a Bank from an Insurance/Mission terminal - they're all "Terminal" type).
public class NavObject
{
    public float X, Y, Z;
    public int Type, Instance;
    public string Name;
}

// A learned "warp": at (From) in this playfield, Using object (ObjType:ObjInstance) teleports you to
// (To) (in ToPf). Captures same-playfield floor buttons that a pf-change transition can't see.
public class NavWarp
{
    public float FromX, FromY, FromZ;
    public float ToX, ToY, ToZ;
    public int ToPf;
    public int ObjType, ObjInstance;
}

public class NavTransition
{
    public float X, Y, Z;
    public int ToPf; // playfield the owner ended up in
    public string Kind; // "zone" (generic crossing)
    public string Name; // optional label
}