// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: PathCache.cs
//
// Last modified: 2026-10-13
// Created:       2026-10-13
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Components;
using AOBuddy20.Utils;
using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     THE PATH CACHE (owner, 2026-10-13: "Add a path cache. There aren't that many mission
///     entrances or path destinations, so after calculating a path, store it in a cache dictionary.
///     Key: start and destination coordinates, value: the calculated path" - then: "stored in
///     GameData\Nav\&lt;pf number&gt;\paths.json", "only load the paths on zonechange"). Nothing more than
///     skipping the lengthy search on the big playfields: the full A* over a zone's lattice is the
///     most expensive thing the walk thread does, and the same plan is asked for again - the travel
///     tick re-setting the identical goal, a re-plan after a yank, the next trip to the same mission
///     entrance, the legs a multi-zone journey walks twice. One playfield's paths in memory at a
///     time, keyed on the coordinates alone - pf and bucket split and reach and goal height make up
///     the string key, the file is per pf (GameData/Nav/&lt;pf&gt;/paths.json, beside the lattice and the
///     walls) - and a zone change is the only load: the pf in memory is flushed to its file and the
///     arriving one's file takes its place. A hit is served only when the ends still match within
///     EndTol and the path crosses none of the plan's hard blocks - without that the memo would
///     answer something today's search would not. Pull-backs drop the paths through them (NoteSnap,
///     same as the saved routes); after MaxUses serves or an hour the entry retires, so one fresh
///     search re-memoizes it against the drifted inputs (learned roads, snap marks). Where RouteCache
///     keeps routes between FIXED objects, this is the plain memo of the coordinates themselves.
/// </summary>
public static class PathCache
{
    public sealed class Entry
    {
        public float[] A, B;
        public List<float[]> P;
        public DateTime Made;
        public int Uses;
    }

    private static readonly object Gate = new();
    private static Action<string> _log = _ => { };
    private static string _dir;
    private static int _pf = -1; // the playfield whose paths are in memory; a call for another swaps the file in
    private static Dictionary<string, Entry> _paths = new();

    private const float KeyStep = 8f;   // metres; a start or goal within one bucket of the plan counts as it
    private const float EndTol = 10f;   // metres; the stored ends must still be this close to the ask (a bucket's diagonal)
    private const int MaxUses = 10;     // the learned layers drift under the key; retire and re-memoize
    private const double MaxMinutes = 60;
    private const int Cap = 96;         // entries per playfield; the oldest goes when a new one needs the room
    private const float MinLength = 30f; // metres; shorter plans are cheap to make (RouteCache's rule)
    private const float SnapDrop = 6f;  // a pull-back this close to a cached path drops it (RouteCache's rule)

    public static void Init(string dataDir, Action<string> log)
    {
        lock (Gate)
        {
            if (log != null)
            {
                _log = log;
            }

            _dir = dataDir;
        }
    }

    private static int Bucket(float v) => (int)MathF.Round(v / KeyStep);

    private static string Key(Vector3 a, Vector3 b, float reach, float goalY)
        => $"{Bucket(a.X)},{Bucket(a.Z)}>{Bucket(b.X)},{Bucket(b.Z)}" +
           $":r{(int)MathF.Round(reach * 2)}:y{(float.IsNaN(goalY) ? "-" : (int)MathF.Round(goalY))}";

    private static string File(int pf) => Path.Combine(AOBuddyNav.FolderFor(_dir ?? "", pf), "paths.json");

    /// <summary>The zone change: flush the playfield in memory to its file, read the arriving one's. The only load there is.</summary>
    private static void SwapPf(int pf)
    {
        if (pf == _pf)
        {
            return;
        }

        if (_paths.Count > 0)
        {
            Save(_pf);
        }

        _pf = pf;
        _paths = JsonStore.Load<Dictionary<string, Entry>>(_dir == null ? null : File(pf), _log) ?? new Dictionary<string, Entry>();
        _log($"PATHCACHE: {pf} in memory - {_paths.Count} cached path(s) from paths.json.");
    }

    /// <summary>The cached path for a to b, or null (plan it). extra is this walk's hard blocks: a path across one is not this walk's path.</summary>
    public static List<Vector3> Get(IWalkGrid grid, Vector3 a, Vector3 b, float reach, float goalY, HashSet<int> extra)
    {
        lock (Gate)
        {
            SwapPf(grid.Pf);
            var key = Key(a, b, reach, goalY);
            if (!_paths.TryGetValue(key, out var e))
            {
                return null;
            }

            if (e.Uses >= MaxUses || (DateTime.UtcNow - e.Made).TotalMinutes > MaxMinutes ||
                Movement.Flat(V(e.A), a) > EndTol || Movement.Flat(V(e.B), b) > EndTol)
            {
                _paths.Remove(key);
                Save(_pf);
                return null;
            }

            var cells = new HashSet<int>();
            for (var i = 1; i < e.P.Count; i++)
            {
                grid.CellsAlong(V(e.P[i - 1]), V(e.P[i]), 0f, cells);
            }

            if (extra != null && extra.Count > 0 && cells.Overlaps(extra))
            {
                _paths.Remove(key);
                Save(_pf);
                _log("PATHCACHE: the cached path crosses a blocked cell this time; planning afresh.");
                return null;
            }

            e.Uses++;
            Save(_pf);
            _log($"PATHCACHE: using the cached path ({e.P.Count} points, use {e.Uses}/{MaxUses}).");
            return e.P.Select(V).ToList(); // a copy: the caller keeps the list as its route and clears it in place
        }
    }

    public static void Put(int pf, Vector3 a, Vector3 b, float reach, float goalY, List<Vector3> route)
    {
        if (route == null || route.Count < 2)
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

        lock (Gate)
        {
            SwapPf(pf);
            if (_paths.Count >= Cap && !_paths.ContainsKey(Key(a, b, reach, goalY)))
            {
                string oldest = null;
                var om = DateTime.MaxValue;
                foreach (var kv in _paths)
                {
                    if (kv.Value.Made < om)
                    {
                        om = kv.Value.Made;
                        oldest = kv.Key;
                    }
                }

                _paths.Remove(oldest);
            }

            _paths[Key(a, b, reach, goalY)] = new Entry
            {
                A = F(a),
                B = F(b),
                P = route.Select(F).ToList(),
                Made = DateTime.UtcNow,
            };
            Save(_pf);
        }
    }

    /// <summary>A server pull-back at (x, z): cached paths passing within 6 m are dropped (LearnedGround.NoteSnap calls this, like RouteCache.DropNear). The pull-back is where the body is - its playfield is the one in memory.</summary>
    public static void DropNear(int pf, float x, float z)
    {
        lock (Gate)
        {
            if (pf != _pf)
            {
                return;
            }

            var hit = _paths.Where(kv => Near(kv.Value.P, x, z)).Select(kv => kv.Key).ToList();
            if (hit.Count == 0)
            {
                return;
            }

            foreach (var k in hit)
            {
                _paths.Remove(k);
            }

            Save(_pf);
            _log($"PATHCACHE: pulled back at ({x:0},{z:0}); dropped {hit.Count} cached path(s) through there.");
        }
    }

    private static void Save(int pf)
    {
        if (_dir == null || pf < 0)
        {
            return; // unconfigured, or nothing swapped in yet: the memo stays memory-only
        }

        try
        {
            Directory.CreateDirectory(AOBuddyNav.FolderFor(_dir, pf));
        }
        catch
        {
            // the cache is an optimization: a failed mkdir costs the next save, nothing else
        }

        JsonStore.Save(File(pf), JsonConvert.SerializeObject(_paths, Formatting.Indented), _log);
    }

    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);

    private static float[] F(Vector3 v) => new[] { (float)Math.Round(v.X, 2), (float)Math.Round(v.Y, 2), (float)Math.Round(v.Z, 2) };

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
}
