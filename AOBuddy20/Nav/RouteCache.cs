// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: RouteCache.cs
//
// Last modified: 2026-10-03
// Created:       2026-10-03 (ported from AOBuddy10 RouteCache.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Components;
using AOBuddy20.Enums;
using AOSharp.Common.GameData;
using AOBuddy20.Utils;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     SAVED ROUTES (AOBuddy10 RouteCache, Algorithman: "if both zone enter and exit are wompahs or
///     wompah and grid terminal or non-mission doors (fair trades etc) makes no sense to recalculate the
///     routes over and over again"). Those objects never move, so a walk from one to another is planned
///     once and kept (routes.json) - same zone, both ends within 6 m of a fixed object: a used exit
///     (whompah, grid, door) or where one sets you down. Zone lines are not fixed ends (you cross them
///     anywhere along the line).
///     A saved route is planned again when:
///       * the server pulled him back within 6 m of it (the spot is now costly; LearnedGround.NoteSnap
///         drops it),
///       * it crosses a cell the run has marked stuck or a zone line this time (skipped, not dropped),
///       * it has been used 10 times or is 3 days old (walked roads keep improving the planner; pick
///         them up).
///     When the start is more than 1 m from the saved start, a short leg is planned to the route's
///     second point.
///     AOBuddy10 also counted Scotty warp landings among the fixed ends; Scotty warps are not loaded in
///     AOBuddy20 yet, so they are simply not cached ends (Zoning.cs).
/// </summary>
public static class RouteCache
{
    public sealed class Entry
    {
        public int Pf;
        public float[] A, B;
        public float Reach, GoalY;
        public List<float[]> P;
        public DateTime Made;
        public int Uses;
    }

    private static readonly object Gate = new();
    private static string _file;
    private static Action<string> _log = _ => { };
    private static Dictionary<string, Entry> _routes = new();

    private const float EndMatch = 6f; // a start or goal this close to a fixed object counts as it
    private const float SnapDrop = 6f; // a pull-back this close to a route drops it
    private const int MaxUses = 10;
    private const float MinLength = 30f; // metres; shorter routes are not kept
    private const double MaxDays = 3;

    public static void Init(string dataDir, Action<string> log)
    {
        lock (Gate)
        {
            if (log != null)
            {
                _log = log;
            }

            if (_file != null)
            {
                return;
            }

            _file = Path.Combine(dataDir, "routes.json");
            try
            {
                if (File.Exists(_file))
                {
                    _routes = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(_file))
                              ?? new Dictionary<string, Entry>();
                }
            }
            catch (Exception ex)
            {
                _log("ROUTECACHE: routes.json unreadable: " + ex.Message);
                _routes = new Dictionary<string, Entry>();
            }
        }
    }

    /// <summary>The fixed objects of zone pf: its used exits, and where exits from other zones set you down in it.</summary>
    private static List<Vector3> Ends(int pf)
    {
        lock (Gate)
        {
            if (_ends.TryGetValue(pf, out var l))
            {
                return l;
            }

            l = new List<Vector3>();
            if (!Zoning.Loaded)
            {
                return l; // not cached: try again once the zone data is in
            }

            foreach (var e in Zoning.ExitsFrom(pf))
            {
                if (e.Kind != ExitKind.ZoneLine)
                {
                    l.Add(e.A);
                }

                foreach (var back in Zoning.ExitsFrom(e.ToPf))
                {
                    if (back.ToPf == pf && back.Kind != ExitKind.ZoneLine && back.Arrival.HasValue)
                    {
                        l.Add(back.Arrival.Value);
                    }
                }
            }

            return _ends[pf] = l;
        }
    }

    private static readonly Dictionary<int, List<Vector3>> _ends = new();

    private static int EndAt(int pf, Vector3 p)
    {
        var l = Ends(pf);
        int best = -1;
        var bd = EndMatch;
        for (var i = 0; i < l.Count; i++)
        {
            var d = Movement.Flat(l[i], p);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }

        return best;
    }

    private static string Key(int pf, int ea, int eb, float reach, float goalY)
    {
        var l = Ends(pf);
        return $"{pf}:{l[ea].X:0},{l[ea].Z:0}>{l[eb].X:0},{l[eb].Z:0}:r{reach:0.0}:y{(float.IsNaN(goalY) ? "-" : goalY.ToString("0"))}";
    }

    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

    private static float[] F(Vector3 v) => new[] { (float)Math.Round(v.X, 2), (float)Math.Round(v.Y, 2), (float)Math.Round(v.Z, 2) };

    /// <summary>A saved route from a to b, or null (plan it). leg plans the short join when the start moved.</summary>
    public static List<Vector3> Get(OverlandGrid grid, Vector3 a, Vector3 b, float reach, float goalY, HashSet<int> extra,
        Func<Vector3, Vector3, List<Vector3>> leg)
    {
        lock (Gate)
        {
            if (_file == null)
            {
                return null;
            }

            var ea = EndAt(grid.Pf, a);
            var eb = EndAt(grid.Pf, b);
            if (ea < 0 || eb < 0 || ea == eb)
            {
                return null;
            }

            var key = Key(grid.Pf, ea, eb, reach, goalY);
            if (!_routes.TryGetValue(key, out var r))
            {
                return null;
            }

            if (r.Uses >= MaxUses || (DateTime.UtcNow - r.Made).TotalDays > MaxDays || Movement.Flat(V(r.B), b) > 1f)
            {
                _routes.Remove(key);
                Save();
                return null;
            }

            var pts = r.P.Select(V).ToList();
            if (extra != null && extra.Count > 0)
            {
                var cells = new HashSet<int>();
                for (var i = 1; i < pts.Count; i++)
                {
                    grid.CellsAlong(pts[i - 1], pts[i], 0f, cells);
                }

                if (cells.Overlaps(extra))
                {
                    _log("ROUTECACHE: the saved route crosses a blocked cell this time; planning afresh.");
                    return null;
                }
            }

            List<Vector3> route;
            if (Movement.Flat(a, pts[0]) <= 1f || pts.Count < 2)
            {
                route = new List<Vector3> { a };
                route.AddRange(pts.Skip(1));
            }
            else
            {
                var join = leg(a, pts[1]);
                if (join == null)
                {
                    return null;
                }

                route = join;
                route.AddRange(pts.Skip(2));
            }

            r.Uses++;
            Save();
            _log($"ROUTECACHE: using the saved route ({r.P.Count} points, use {r.Uses}/{MaxUses}).");
            return route;
        }
    }

    public static void Put(OverlandGrid grid, Vector3 a, Vector3 b, float reach, float goalY, List<Vector3> route)
    {
        lock (Gate)
        {
            if (_file == null || route == null || route.Count < 2)
            {
                return;
            }

            float len = 0;
            for (var i = 1; i < route.Count; i++)
            {
                len += Movement.Flat(route[i - 1], route[i]);
            }

            if (len < MinLength)
            {
                return; // short hops are cheap to plan
            }

            var ea = EndAt(grid.Pf, a);
            var eb = EndAt(grid.Pf, b);
            if (ea < 0 || eb < 0 || ea == eb)
            {
                return;
            }

            _routes[Key(grid.Pf, ea, eb, reach, goalY)] = new Entry
            {
                Pf = grid.Pf,
                A = F(a),
                B = F(b),
                Reach = reach,
                GoalY = goalY,
                P = route.Select(F).ToList(),
                Made = DateTime.UtcNow,
            };
            Save();
            _log($"ROUTECACHE: saved the route ({route.Count} points) between two fixed objects in {Zoning.Name(grid.Pf)}.");
        }
    }

    /// <summary>A server pull-back at (x, z): routes passing within 6 m are dropped (LearnedGround.NoteSnap calls this).</summary>
    public static void DropNear(int pf, float x, float z)
    {
        lock (Gate)
        {
            var hit = _routes.Where(kv => kv.Value.Pf == pf && Near(kv.Value.P, x, z)).Select(kv => kv.Key).ToList();
            if (hit.Count == 0)
            {
                return;
            }

            foreach (var k in hit)
            {
                _routes.Remove(k);
            }

            Save();
            _log($"ROUTECACHE: pulled back at ({x:0},{z:0}); dropped {hit.Count} saved route(s) through there.");
        }
    }

    private static bool Near(List<float[]> p, float x, float z)
    {
        for (var i = 1; i < p.Count; i++)
        {
            float ax = p[i - 1][0], az = p[i - 1][2], bx = p[i][0], bz = p[i][2];
            float dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            var t = l2 < 1e-6f ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0f, 1f);
            float qx = ax + dx * t - x, qz = az + dz * t - z;
            if (qx * qx + qz * qz < SnapDrop * SnapDrop)
            {
                return true;
            }
        }

        return false;
    }

    private static void Save()
    {
        try
        {
            JsonStore.Save(_file, JsonConvert.SerializeObject(_routes, Formatting.Indented), _log);
        }
        catch
        {
            // the cache is an optimization: a failed save costs the next plan, nothing else
        }
    }
}