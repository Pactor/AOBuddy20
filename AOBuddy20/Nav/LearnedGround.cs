// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: LearnedGround.cs
//
// Last modified: 2026-10-03
// Created:       2026-10-03 (ported from AOBuddy10 LearnedGround.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     WHAT WALKING TAUGHT US, for the outdoor planner (AOBuddy10 LearnedGround, owner: "the road is
///     55% longer, unless he gets snapped back 100 times"). The shortest line kept taking him over hills
///     the server doesn't let him cross (Borealis -> Holes in the Wall: his 434 m over the hill, the
///     owner's road 673 m, pulled back 10 m twice at the start), so the grid's cost learns from three
///     things the map cannot show, all persisted across restarts:
///       * ROADS: the owner's clean recorded footsteps. AOBuddy10 recorded them through
///         'record'+'savepath' into paths/*.json; AOBuddy20's NavController already keeps them per
///         playfield in nav/&lt;pf&gt;.json - those segments are the roads, one zone known per file, so
///         no height-band guessing between zones is needed.
///       * WALKS: clean stretches the BOT itself walked with no server correction between the steps,
///         saved per zone (walked.json) - road for the planner too, but never trusted for a drop.
///       * SNAP-BACKS: every spot the server pulled the walking body back, kept per zone across
///         restarts (snapbacks.json); the cells round it cost more, more for each time it happened,
///         and a spot not hit again in 14 days is forgotten.
///     The grid rebuilds its learned cost layers whenever Version changes (OverlandGrid.EnsureLearned).
///     THREADING: all state sits under one lock, so any thread may read; the saves are atomic.
/// </summary>
public static class LearnedGround
{
    public sealed class Snap
    {
        public float X, Z;
        public int N;
        public DateTime Last;
    }

    /// <summary>One clean stretch the bot itself walked, in its playfield.</summary>
    public sealed class Walk
    {
        public int Pf;
        public List<float[]> P;
    }

    // The one field of nav/<pf>.json the roads need (the file carries more: transitions, warps,
    // objects, mobs - Newtonsoft maps just this).
    private sealed class NavRoads
    {
        public List<List<float[]>> Segments;
    }

    private static readonly object Gate = new();
    private static string _dir;
    private static Action<string> _log = _ => { };
    private static Dictionary<int, List<Snap>> _snaps = new();
    private static List<Walk> _walked = new();
    private static readonly Dictionary<int, List<List<Vector3>>> _roads = new(); // per pf: nav/<pf>.json segments
    private static DateTime _roadsAt = DateTime.MinValue;

    /// <summary>Changes whenever the learned picture would come out different (a snap, a walk, new roads).</summary>
    public static int Version { get; private set; }

    private const float Merge = 4f; // snap-backs this close are one spot
    private const int Days = 14; // a spot not hit again in this long is forgotten
    private const int MaxWalksPerZone = 300;
    private const float MinWalkMetres = 20f; // a clean stretch shorter than this is not road

    public static void Init(string dataDir, Action<string> log)
    {
        lock (Gate)
        {
            if (log != null)
            {
                _log = log;
            }

            if (_dir != null)
            {
                return;
            }

            _dir = dataDir;
            try
            {
                var p = Path.Combine(_dir, "snapbacks.json");
                if (File.Exists(p))
                {
                    _snaps = JsonConvert.DeserializeObject<Dictionary<int, List<Snap>>>(File.ReadAllText(p))
                             ?? new Dictionary<int, List<Snap>>();
                    foreach (var k in _snaps.Keys.ToList())
                    {
                        _snaps[k] = _snaps[k].Where(s => (DateTime.UtcNow - s.Last).TotalDays < Days).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _log("LEARNED: snapbacks.json unreadable: " + ex.Message);
                _snaps = new Dictionary<int, List<Snap>>();
            }

            try
            {
                var w = Path.Combine(_dir, "walked.json");
                if (File.Exists(w))
                {
                    _walked = JsonConvert.DeserializeObject<List<Walk>>(File.ReadAllText(w)) ?? new List<Walk>();
                }
            }
            catch (Exception ex)
            {
                _log("LEARNED: walked.json unreadable: " + ex.Message);
                _walked = new List<Walk>();
            }

            LoadRoads();
            Version++;
            var snaps = _snaps.Sum(kv => kv.Value.Count);
            _log($"LEARNED: {snaps} remembered pull-back spot(s) in {_snaps.Count} zone(s), {_walked.Count} walked stretch(es), roads for {_roads.Count} zone(s) from nav/.");
        }
    }

    /// <summary>Re-read nav/*.json when one was saved since the last read (cheap: a directory listing; the
    /// NavController autosaves every 20 s while the owner walks).</summary>
    public static void RefreshRoads()
    {
        lock (Gate)
        {
            if (_dir == null)
            {
                return;
            }

            var d = Path.Combine(_dir, "nav");
            if (!Directory.Exists(d))
            {
                return;
            }

            var newest = Directory.GetFiles(d, "*.json").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (newest <= _roadsAt)
            {
                return;
            }

            LoadRoads();
            Version++;
        }
    }

    private static void LoadRoads()
    {
        _roads.Clear();
        _roadsAt = DateTime.MinValue;
        var d = Path.Combine(_dir ?? "", "nav");
        if (_dir == null || !Directory.Exists(d))
        {
            return;
        }

        foreach (var f in Directory.GetFiles(d, "*.json"))
        {
            try
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out var pf) || pf <= 0)
                {
                    continue; // nav/&lt;pf&gt;.json only
                }

                var zone = JsonConvert.DeserializeObject<NavRoads>(File.ReadAllText(f));
                if (zone?.Segments == null || zone.Segments.Count == 0)
                {
                    continue;
                }

                _roads[pf] = zone.Segments
                    .Where(seg => seg != null && seg.Count >= 2)
                    .Select(seg => seg.Where(a => a != null && a.Length >= 3).Select(a => new Vector3(a[0], a[1], a[2])).ToList())
                    .Where(seg => seg.Count >= 2)
                    .ToList();
                _roadsAt = new[] { _roadsAt, File.GetLastWriteTimeUtc(f) }.Max();
            }
            catch
            {
                // a half-written or foreign json in nav/ costs nothing: the next autosave refresh re-reads
            }
        }
    }

    /// <summary>A server pull-back outdoors at (x, z) in zone pf: remembered across restarts, priced by the grid.</summary>
    public static void NoteSnap(int pf, float x, float z)
    {
        lock (Gate)
        {
            if (_dir == null)
            {
                return;
            }

            if (!_snaps.TryGetValue(pf, out var list))
            {
                _snaps[pf] = list = new List<Snap>();
            }

            var s = list.FirstOrDefault(q => Math.Abs(q.X - x) < Merge && Math.Abs(q.Z - z) < Merge);
            if (s == null)
            {
                list.Add(s = new Snap { X = x, Z = z });
            }

            s.N++;
            s.Last = DateTime.UtcNow;
            Version++;
            RouteCache.DropNear(pf, x, z); // a saved route through the spot is now stale
            try
            {
                File.WriteAllText(Path.Combine(_dir, "snapbacks.json"), JsonConvert.SerializeObject(_snaps, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _log("LEARNED: snapbacks.json save failed: " + ex.Message);
            }
        }
    }

    /// <summary>A clean stretch walked in zone pf (at least 20 m); becomes road for the planner.</summary>
    public static void NoteWalk(int pf, List<Vector3> pts)
    {
        if (pts == null || pts.Count < 2)
        {
            return;
        }

        float len = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            len += Vector3.Distance(pts[i - 1], pts[i]);
        }

        if (len < MinWalkMetres)
        {
            return;
        }

        lock (Gate)
        {
            if (_dir == null)
            {
                return;
            }

            _walked.Add(new Walk
            {
                Pf = pf,
                P = pts.Select(v => new[] { (float)Math.Round(v.X, 1), (float)Math.Round(v.Y, 1), (float)Math.Round(v.Z, 1) }).ToList(),
            });
            var mine = _walked.Where(w => w.Pf == pf).ToList();
            if (mine.Count > MaxWalksPerZone)
            {
                _walked.Remove(mine[0]);
            }

            Version++;
            try
            {
                File.WriteAllText(Path.Combine(_dir, "walked.json"), JsonConvert.SerializeObject(_walked));
            }
            catch (Exception ex)
            {
                _log("LEARNED: walked.json save failed: " + ex.Message);
            }
        }
    }

    /// <summary>The clean stretches the bot itself walked in one zone.</summary>
    public static List<List<Vector3>> WalksIn(int pf)
    {
        lock (Gate)
        {
            return _walked.Where(w => w.Pf == pf)
                .Select(w => w.P.Where(a => a != null && a.Length >= 3).Select(a => new Vector3(a[0], a[1], a[2])).ToList())
                .Where(w => w.Count >= 2)
                .ToList();
        }
    }

    /// <summary>The owner's recorded roads of one zone (nav/&lt;pf&gt;.json segments).</summary>
    public static List<List<Vector3>> RoadsIn(int pf)
    {
        lock (Gate)
        {
            return _roads.TryGetValue(pf, out var l) ? l.Select(r => r.ToList()).ToList() : new List<List<Vector3>>();
        }
    }

    public static List<Snap> SnapsIn(int pf)
    {
        lock (Gate)
        {
            return _snaps.TryGetValue(pf, out var l) ? l.ToList() : new List<Snap>();
        }
    }

    /// <summary>
    ///     How many times the server pulled him back within 'radius' metres of this path (the hits of every
    ///     remembered snap-back spot of zone pf the path passes). A grid route out of the Longest Road town
    ///     climbed the ridge where snapbacks.json held 20+ pull-backs: the grid calls it walkable, the
    ///     server does not - this is how a route over remembered refusals is told from a clean one.
    /// </summary>
    public static int SnapHitsAlong(int pf, List<Vector3> path, float radius = 6f)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }

        lock (Gate)
        {
            if (!_snaps.TryGetValue(pf, out var snaps))
            {
                return 0;
            }

            var hits = 0;
            foreach (var s in snaps)
            {
                var best = float.MaxValue;
                for (var i = 0; i < path.Count; i++)
                {
                    var a = path[i];
                    var b = i + 1 < path.Count ? path[i + 1] : path[i];
                    float dx = b.X - a.X, dz = b.Z - a.Z, l2 = dx * dx + dz * dz;
                    var t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((s.X - a.X) * dx + (s.Z - a.Z) * dz) / l2));
                    float ex = a.X + t * dx - s.X, ez = a.Z + t * dz - s.Z;
                    best = Math.Min(best, (float)Math.Sqrt(ex * ex + ez * ez));
                }

                if (best < radius)
                {
                    hits += s.N;
                }
            }

            return hits;
        }
    }

    /// <summary>
    ///     THE WAY OUT OF A WALLED PLACE (AOBuddy10, owner: "walk out the entrance"): a recorded road of
    ///     zone pf that starts within 'near' metres (and 5 m of height) of pos, oriented start -&gt; far end,
    ///     whose far end lies at least 20 m further from pos than its start does (it leads away, not back).
    ///     The nearest start wins. 'onZone' (optional) must accept the far end - the caller checks it sits
    ///     on this zone's ground. Null when no road starts here.
    /// </summary>
    public static List<Vector3> RoadOut(int pf, Vector3 pos, float near = 30f, Func<Vector3, bool> onZone = null)
    {
        lock (Gate)
        {
            if (!_roads.TryGetValue(pf, out var roads))
            {
                return null;
            }

            List<Vector3> best = null;
            var bestStart = near;
            foreach (var r0 in roads)
            {
                if (r0 == null || r0.Count < 2)
                {
                    continue;
                }

                foreach (var r in new[] { r0, Enumerable.Reverse(r0).ToList() })
                {
                    float dx = r[0].X - pos.X, dz = r[0].Z - pos.Z;
                    var d = (float)Math.Sqrt(dx * dx + dz * dz);
                    var end = r[r.Count - 1];
                    float ex = end.X - pos.X, ez = end.Z - pos.Z;
                    if (d < bestStart && Math.Abs(r[0].Y - pos.Y) < 5f && (float)Math.Sqrt(ex * ex + ez * ez) > d + 20f
                        && (onZone == null || onZone(end)))
                    {
                        bestStart = d;
                        best = r.ToList();
                    }
                }
            }

            return best;
        }
    }
}