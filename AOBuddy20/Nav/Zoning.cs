// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: Zoning.cs
//
// Last modified: 2026-10-01
// Created:       2026-10-01 (ported from AOBuddy10 Zoning.cs: data layer + route planner)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Enums;
using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using AOSharp.Common.GameData;
using Newtonsoft.Json;

namespace AOBuddy20.Nav;

/// <summary>
///     One way out of a playfield. Zone lines are crossed by walking over A-B; the rest are objects at A
///     (== B) - whompas and teleporters are USED (GenericCmd Use), booths, grid exits and lift beams
///     (kind Line) are walked ONTO; Scotty warps are a tell to scottyboi and work from anywhere (FromPf 0).
/// </summary>
public sealed class ZoneExit
{
    public ExitKind Kind;
    public int FromPf, ToPf;
    public Vector3 A, B;
    public Vector3? Arrival; // where you come out; null = not known
    public Vector3? ArrivalA, ArrivalB; // zone lines: the far side's arrival line, runs opposite to A-B
    public bool ArrivalGuessed; // Arrival is estimated from the exits leading back
    public int Idx, Flags;
    public int[][] Reqs; // [stat, operator, value], postfix; null = none
    public string ReqText;
    public int ObjType, ObjInstance, Template;
    public string Tell, Label; // Scotty only

    // A proxy playfield's exit door (tools/rdb-zoning emits them, to 0 in the data): the client
    // data holds the door but not its destination - the server wires it per instance. ToPf is
    // resolved at route time to the playfield we entered this instance from (ProxyOrigin).
    public bool Back;

    public override string ToString()
    {
        return Kind switch
        {
            ExitKind.ZoneLine when Back => "exit door back out",
            ExitKind.ZoneLine => $"zone line to {Zoning.Name(ToPf)}",
            ExitKind.Line => $"pad to {Zoning.Name(ToPf)}",
            ExitKind.Scotty => $"/tell scty {Tell} ({Label}, {Zoning.Name(ToPf)})",
            _ => $"{Kind.ToString().ToLower()} {ObjType}:{ObjInstance} to {Zoning.Name(ToPf)}"
        };
    }
}

/// <summary>One crossing: walk to WalkTo in FromPf (then on to CrossTo for zone lines), use Exit, come out at ArriveAt.</summary>
public sealed class ZoneHop
{
    public int FromPf;
    public ZoneExit Exit;
    public Vector3? WalkTo; // null for Scotty
    public Vector3? CrossTo; // zone lines: a few metres past the line
    public Vector3? ArriveAt; // estimate; null = not known
    public double Cost; // cumulative, up to and including this hop

    public override string ToString()
    {
        var at = WalkTo.HasValue ? $" at ({WalkTo.Value.X:0},{WalkTo.Value.Z:0})" : "";
        var arrive = ArriveAt.HasValue
            ? $" -> ({ArriveAt.Value.X:0},{ArriveAt.Value.Z:0}){(Exit.ArrivalGuessed ? "~" : "")}"
            : " -> (?)";
        return $"{Zoning.Name(FromPf)}: {Exit}{at}{arrive}";
    }
}

/// <summary>A planned way across playfields, ending with the final walk to the goal.</summary>
public sealed class ZoneRoute
{
    public List<ZoneHop> Hops = new();
    public double Cost; // includes the final walk to the goal

    public string Describe()
    {
        if (Hops.Count == 0)
        {
            return $"already in the playfield, cost {Cost:0}";
        }

        return $"{Hops.Count} crossing(s), cost {Cost:0}: " + string.Join("; ", Hops.Select((h, i) => $"{i + 1}. {h}"));
    }
}

/// <summary>
///     What a route ask allows and what it thinks a crossing costs (AOBuddy10's proven numbers, all in
///     metres of walking). Stat is the requirement checker's eye: a stat it cannot read counts as
///     unknown, and unknown passes (UnknownPasses).
/// </summary>
public sealed class ZoneRouteOptions
{
    public double ZoneLineCost = 20;

    // A teleport hop is a ZONE: reconnect, FullCharacter, grid-cache reload - 10 s and more each, and a
    // failed one strands the trip. At 30 it was cheaper than the metres it can save, so the planner
    // BOUGHT extra hops to shave a walk (owner, 2026-10-08: the sell/resupply trip to Fair Trade (1187)
    // walked 19 m to a Newland City whompa, hopped to Newland Desert and 19 m more - because the city's
    // OWN 1187 entrance 96 m away priced dearer; every city has one). 150 m of walk-equivalent per hop:
    // an entrance within ~150 m now always beats an extra zone.
    public double TeleportCost = 150;
    public double ScottyCost = 400;
    public double UnknownWalk = 250; // walking from a point we don't know
    public bool UseScotty = false;
    // Leave out walking straight from the start to the goal (same zone, and the walk grid found no way
    // on foot): the route must go through at least one exit and land more than 30 m from the start.
    public bool NoDirectWalk;

    // The Grid (152) looks cheap to the planner - its inside is counted as flat straight-line walking -
    // but the real trip crosses decks and rides lift beams, so it is far slower than it plans (owner,
    // 2026-09-25: a 3-wompah-hop lost to a grid route). Every hop touching the Grid pays this on top.
    public double GridCost = 250;

    // Playfields stacked in levels where walking never changes level (the Grid: decks joined only by lift
    // beams, which are exits). There a walk between points more than LevelGap apart in height is impossible.
    public Func<int, bool> SameLevelOnly = pf => pf == 152;
    public double LevelGap = 3;

    // An exit is usable when Filter (if set) allows it and, with Stat set (stat id -> value, null when
    // unreadable), its Reqs pass; terms the checker can't read count as UnknownPasses.
    public Func<ZoneExit, bool> Filter;
    public Func<int, int?> Stat;
    public bool UnknownPasses = true;
}

/// <summary>
///     The playfield graph's data and its route planner, from GameData/Zoning.json (tools/rdb-zoning)
///     - every playfield's name and its ways out: zone lines as walked A-B segments; doors, whompas,
///     teleporters and proxies as objects at a spot.
///     ROUTING (AOBuddy10, wire-proven): Dijkstra over the exits - walking inside a playfield prices as
///     flat straight-line metres, each crossing adds its kind's cost, requirements are checked against
///     the live stats tri-state, and an exit the bot cannot name (no object identity) or that failed on
///     this trip is filtered out. Whompas and the grid are what make city-to-city trips sane: 567 ->
///     800 walks to Newland City (566) and takes the whompa, instead of refusing for lack of zone lines.
///     Arrival points: a zone line's far-side entry is that side's own line back, wound the other way
///     (crossing at fraction t comes out at 1-t); wall polygons wind so that (-dz, dx) of a line points
///     INTO its playfield, so (dz, -dx) points outward. Proxies carry no destination: guessed from the
///     destination's exits back, or left unknown - the travel legs re-plan from wherever we actually are
///     after every zone, so a wrong guess costs cost accuracy, never the trip.
///     BACK EXITS (proxy playfields): a playfield entered through a proxy door (shop, house) carries
///     its exit door in the data but NOT its destination - the server wires it per instance
///     (tools/rdb-zoning emits those doors as zoneLines with to 0). The destination comes from the
///     ZONE-IN MESSAGE (PlayfieldAnarchyFMessage.ProxyReturn, 0xC0090000 | pf) - live server data,
///     never a remembered file: SetProxyOrigin keeps it for the playfield just entered, FindRoute
///     resolves the back exits of that one playfield against it and leaves them unusable while it
///     is unknown.
///     THREADING: Load once at startup, before the session starts and before any walk - the loaded maps
///     are immutable afterwards, so ExitsFrom/FindRoute/CrossLine are pure reads from any thread (the
///     _wet table is the one mutable side table, guarded by its lock; SetWet registers the playfield the
///     walk currently has nav data for).
///     Scotty warps (GameData/ScottyWarps.json) are in the plan from any playfield (a tell to scottyboi
///     works everywhere but RubiKa2019), at their ScottyCost of walking metres; the leg itself is no
///     walk at all - MovementController sends the tell from wherever we stand and waits the cast out.
/// </summary>
public static class Zoning
{
    private static Dictionary<int, string> _names = new();
    private static Dictionary<int, List<ZoneExit>> _exits = new();
    private static List<ZoneExit> _scotty = new();
    private static List<ZoneExit> _all = new();

    // The proxy playfield the bot is in (or last entered through a proxy), and the playfield it
    // came from - the only truth a back exit's destination has, since the client data names none.
    // LIVE SERVER DATA ONLY (owner, 2026-10-02): the zone-in message itself names the return
    // playfield (PlayfieldAnarchyFMessage.ProxyReturn), so nothing is remembered across sessions -
    // a bot moved to another instance by the live client, or one that relogs inside a shop, reads
    // the way out from the packet it is actually in. Swapped as a whole, read without locks.
    private sealed class ProxyOrigin
    {
        public int Pf, FromPf;
        public Vector3? FromPos; // where we came out in FromPf (the door/terminal we used), if known
    }

    private static volatile ProxyOrigin _proxyOrigin;

    /// <summary>The bot zoned (or logged in) inside `pf`, having come from `fromPf` (-1 = unknown),
    /// coming out by the object at `fromPos` when known. Safe from any thread.</summary>
    public static void SetProxyOrigin(int pf, int fromPf, Vector3? fromPos)
    {
        _proxyOrigin = new ProxyOrigin { Pf = pf, FromPf = fromPf, FromPos = fromPos };
    }

    public static bool Loaded => _names.Count > 0;

    public static string Name(int pf) => _names.TryGetValue(pf, out var n) ? $"{n} ({pf})" : pf.ToString();

    /// <summary>The options every route ask starts from: requirements read live off the character (TryGetStat
    /// is the cross-thread-safe read - the ConcurrentDictionary stats). Each asker then adds its own Filter.</summary>
    public static ZoneRouteOptions RouteOptions(LocalPlayer me) => new ZoneRouteOptions
    {
        Stat = id => me.TryGetStat((Stat)id, out var v) ? v : (int?)null,
        // RubiKa2019 has no Scotty (AOBuddy10, owner 2026-09-26: "no scotywarp on 2019"). Everywhere
        // else the warps are in the plan, at their ScottyCost of metres of walking.
        UseScotty = AOSharp.Clientless.Client.Dimension != AOSharp.Clientless.Common.Dimension.RubiKa2019,
    };

    /// <summary>The Scotty warps (a tell to scottyboi works from any playfield - FromPf 0), for callers
    /// that need the landings (RouteCache's fixed ends).</summary>
    public static IReadOnlyList<ZoneExit> ScottyWarps => _scotty;

    public static IReadOnlyList<ZoneExit> ExitsFrom(int pf) =>
        _exits.TryGetValue(pf, out var l) ? l : Array.Empty<ZoneExit>();

    private static readonly Dictionary<int, Func<float, float, bool>> _wet = new();

    /// <summary>Where this playfield's ground is water (the walk registers its nav data's verdict here so a
    /// zone line is crossed at a dry point - Stret East Bank's line to Andromeda runs the whole south
    /// border, and its nearest point was open water that never took the bot, owner 2026-09-27). null unregisters.</summary>
    public static void SetWet(int pf, Func<float, float, bool> wet)
    {
        lock (_wet)
        {
            if (wet == null)
            {
                _wet.Remove(pf);
            }
            else
            {
                _wet[pf] = wet;
            }
        }
    }

    public static void Load(string dataDir, Action<string> log)
    {
        var names = new Dictionary<int, string>();
        var exits = new Dictionary<int, List<ZoneExit>>();
        var scotty = new List<ZoneExit>();
        var zf = Path.Combine(dataDir, "GameData", "Zoning.json");
        var sf = Path.Combine(dataDir, "GameData", "ScottyWarps.json");
        try
        {
            var z = JsonConvert.DeserializeObject<ZoningFile>(File.ReadAllText(zf));
            foreach (var kv in z.playfields)
            {
                names[int.Parse(kv.Key)] = kv.Value.name;
            }

            foreach (var kv in z.playfields)
            {
                int pf = int.Parse(kv.Key);
                var list = new List<ZoneExit>();
                foreach (var l in kv.Value.zoneLines ?? new List<ZlDto>())
                {
                    if (l.back)
                    {
                        // A proxy playfield's exit door (to 0): usable only once the runtime knows
                        // where we entered the instance from; FindRoute resolves and filters it.
                        list.Add(new ZoneExit
                        {
                            Kind = ExitKind.ZoneLine, FromPf = pf, ToPf = 0, Back = true,
                            A = V(l.a), B = V(l.b), ObjType = l.id?[0] ?? 0, ObjInstance = l.id?[1] ?? 0,
                            Template = l.template,
                        });
                        continue;
                    }

                    if (!z.playfields.ContainsKey(l.to.ToString()))
                    {
                        continue;
                    }

                    var e = new ZoneExit
                    { Kind = ExitKind.ZoneLine, FromPf = pf, ToPf = l.to, A = V(l.a), B = V(l.b), Idx = l.idx, Flags = l.flags };
                    if (z.playfields[l.to.ToString()].arrivals.TryGetValue(l.idx.ToString(), out var ar))
                    {
                        e.ArrivalA = V(ar[0]);
                        e.ArrivalB = V(ar[1]);
                        e.Arrival = Mid(e.ArrivalA.Value, e.ArrivalB.Value);
                    }

                    list.Add(e);
                }

                foreach (var t in kv.Value.teleports ?? new List<TpDto>())
                {
                    int to = t.kind == "teleport" && t.to == 0 ? pf : t.to; // teleport to 0: same playfield (lifts)
                    if (!z.playfields.ContainsKey(to.ToString()))
                    {
                        continue; // proxies to instances / garbage args
                    }

                    var e = new ZoneExit
                    {
                        FromPf = pf, ToPf = to, A = V(t.pos), B = V(t.pos), Idx = t.idx, Reqs = t.reqs, ReqText = t.reqText,
                        ObjType = t.id?[0] ?? 0, ObjInstance = t.id?[1] ?? 0, Template = t.template,
                    };
                    switch (t.kind)
                    {
                        case "teleport":
                            e.Kind = ExitKind.Teleport;
                            e.Arrival = V(t.dest);
                            break;
                        case "line":
                            e.Kind = ExitKind.Line;
                            if (z.playfields[to.ToString()].arrivals.TryGetValue(t.idx.ToString(), out var ar))
                            {
                                e.Arrival = Mid(V(ar[0]), V(ar[1]));
                            }

                            break;
                        default:
                            e.Kind = ExitKind.Proxy;
                            break;
                    }

                    list.Add(e);
                }

                exits[pf] = list;
            }

            // A proxy names no destination point: estimate it from the destination's exits leading back
            // (they usually sit beside the door we come out of).
            foreach (var e in exits.Values.SelectMany(l => l))
            {
                if (e.Arrival == null)
                {
                    e.Arrival = GuessArrival(exits, e);
                    e.ArrivalGuessed = e.Arrival != null;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"ZONING: couldn't load {zf}: {ex.Message}");
            return;
        }

        try
        {
            if (File.Exists(sf))
            {
                foreach (var w in JsonConvert.DeserializeObject<ScottyFile>(File.ReadAllText(sf)).warps)
                {
                    if (w.playfieldId == null)
                    {
                        continue; // playfield not identified (BS Room)
                    }

                    Vector3? p = w.pos == null ? null : new Vector3(w.pos.x, 0f, w.pos.z);
                    scotty.Add(new ZoneExit { Kind = ExitKind.Scotty, ToPf = w.playfieldId.Value, Arrival = p, Tell = w.tell, Label = w.label });
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"ZONING: couldn't load {sf}, planning without Scotty: {ex.Message}");
            scotty.Clear();
        }

        _names = names;
        _exits = exits;
        _scotty = scotty;
        _all = exits.Values.SelectMany(l => l).Concat(scotty).ToList();
        var count = _all.Count - scotty.Count;
        log?.Invoke($"ZONING: {names.Count} playfields, {count} exits, {scotty.Count} Scotty warps loaded.");
    }

    /// <summary>Playfield by id, exact name, then name prefix / substring (case-insensitive). 0 when none.</summary>
    public static int FindPlayfield(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return 0;
        }

        s = s.Trim();
        if (int.TryParse(s, out int id))
        {
            return _names.ContainsKey(id) ? id : 0;
        }

        foreach (var m in new Func<string, bool>[]
                 {
                     n => n.Equals(s, StringComparison.OrdinalIgnoreCase),
                     n => n.StartsWith(s, StringComparison.OrdinalIgnoreCase),
                     n => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                 })
        {
            var hit = _names.Where(kv => m(kv.Value)).OrderBy(kv => kv.Key).ToList();
            if (hit.Count > 0)
            {
                return hit[0].Key;
            }
        }

        return 0;
    }

    /// <summary>The cheapest way from (fromPf, from) to (toPf, goal). goal null = anywhere in toPf. Null when there is none.</summary>
    public static ZoneRoute FindRoute(int fromPf, Vector3 from, int toPf, Vector3? goal, ZoneRouteOptions opt = null)
    {
        opt = opt ?? new ZoneRouteOptions();
        int n = _all.Count, start = n, end = n + 1;
        var dist = new double[n + 2];
        var prev = new int[n + 2];
        var pos = new Vector3?[n + 2];
        var walkTo = new Vector3?[n + 2];
        var crossTo = new Vector3?[n + 2];
        var done = new bool[n + 2];
        var usable = new bool?[n];
        var index = new Dictionary<ZoneExit, int>(n);
        for (int i = 0; i < n; i++)
        {
            index[_all[i]] = i;
        }

        for (int i = 0; i < n + 2; i++)
        {
            dist[i] = double.PositiveInfinity;
            prev[i] = -1;
        }

        dist[start] = 0;
        pos[start] = from;

        // A back exit's destination is the playfield we entered this proxy instance from - 0 (=
        // unknown) while the runtime has no origin for this playfield, which makes it unusable.
        int ToOf(ZoneExit e) => !e.Back ? e.ToPf
            : _proxyOrigin != null && _proxyOrigin.Pf == e.FromPf ? _proxyOrigin.FromPf : 0;
        int PfOf(int node) => node == start ? fromPf : ToOf(_all[node]);
        bool Usable(int i) => usable[i] ?? (usable[i] = ToOf(_all[i]) > 0 && CanUse(_all[i], opt)).Value;
        double Walk(Vector3? a, Vector3 b) => a.HasValue ? Flat(a.Value, b) : opt.UnknownWalk; // Scotty points and goals have no height
        bool OtherLevel(int pf, Vector3? a, Vector3 b) =>
            a.HasValue && opt.SameLevelOnly != null && opt.SameLevelOnly(pf) && Math.Abs(a.Value.Y - b.Y) > opt.LevelGap;

        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0);

        void Relax(int u, int v, double c, Vector3? p, Vector3? walk, Vector3? cross)
        {
            if (done[v] || c >= dist[v])
            {
                return;
            }

            dist[v] = c;
            prev[v] = u;
            pos[v] = p;
            walkTo[v] = walk;
            crossTo[v] = cross;
            queue.Enqueue(v, c);
        }

        while (queue.TryDequeue(out int u, out double d))
        {
            if (done[u] || d > dist[u])
            {
                continue;
            }

            done[u] = true;
            if (u == end)
            {
                break;
            }

            int pf = PfOf(u);
            Vector3? p = pos[u];
            // NoDirectWalk: nor from a landing back on the same spot (a teleport there and straight back).
            bool sameSpot = opt.NoDirectWalk && (u == start || (p.HasValue && Flat(p.Value, from) < 30));
            if (pf == toPf && !sameSpot)
            {
                Relax(u, end, d + (goal.HasValue ? Walk(p, goal.Value) : 0), goal, goal, null);
            }

            if (_exits.TryGetValue(pf, out var list))
            {
                foreach (var e in list)
                {
                    int v = index[e];
                    if (done[v] || !Usable(v))
                    {
                        continue;
                    }

                    if (e.Kind == ExitKind.ZoneLine)
                    {
                        var (at, beyond, arrive) = CrossLine(e, p);
                        if (OtherLevel(pf, p, at))
                        {
                            continue;
                        }

                        Relax(u, v, d + Walk(p, at) + opt.ZoneLineCost, arrive, at, beyond);
                    }
                    else
                    {
                        if (OtherLevel(pf, p, e.A))
                        {
                            continue;
                        }

                        double hop = opt.TeleportCost + (e.FromPf == 152 || e.ToPf == 152 ? opt.GridCost : 0);
                        Relax(u, v, d + Walk(p, e.A) + hop, e.Arrival, e.A, null);
                    }
                }
            }

            if (opt.UseScotty)
            {
                foreach (var e in _scotty)
                {
                    int v = index[e];
                    if (!done[v] && Usable(v))
                    {
                        Relax(start, v, opt.ScottyCost + (goal.HasValue ? Walk(e.Arrival, goal.Value) : 250), e.Arrival, null, null);
                    }
                }
            }
        }

        if (double.IsPositiveInfinity(dist[end]))
        {
            return null;
        }

        var route = new ZoneRoute { Cost = dist[end] };
        for (int v = prev[end]; v != start; v = prev[v])
        {
            route.Hops.Add(new ZoneHop
            { FromPf = PfOf(prev[v]), Exit = _all[v], WalkTo = walkTo[v], CrossTo = crossTo[v], ArriveAt = pos[v], Cost = dist[v] });
        }

        route.Hops.Reverse();
        return route;
    }

    /// <summary>
    ///     Where to cross a zone line coming from p: the nearest point on it, kept 2 m off the ends, a
    ///     point 3 m past it (outward = the line's wound direction), and where that comes out on the far
    ///     side. Never swim to a zone line: where the zone's ground is registered (SetWet), a wet crossing
    ///     point moves along the line to the nearest dry one.
    /// </summary>
    public static (Vector3 at, Vector3 beyond, Vector3? arrive) CrossLine(ZoneExit e, Vector3? p)
    {
        float dx = e.B.X - e.A.X, dz = e.B.Z - e.A.Z;
        float len = (float)Math.Sqrt(dx * dx + dz * dz);
        float t = 0.5f;
        if (p.HasValue && len > 0.01f)
        {
            t = ((p.Value.X - e.A.X) * dx + (p.Value.Z - e.A.Z) * dz) / (len * len);
            float margin = Math.Min(0.5f, 2f / len);
            t = Math.Max(margin, Math.Min(1 - margin, t));
        }

        Func<float, float, bool> wet = null;
        lock (_wet)
        {
            _wet.TryGetValue(e.FromPf, out wet);
        }

        if (p.HasValue && len > 0.01f && wet != null)
        {
            float margin = Math.Min(0.5f, 2f / len);
            Vector3 c = Lerp(e.A, e.B, t);
            if (wet(c.X, c.Z))
            {
                float step = 2f / len;
                for (float k = step; k <= 1f; k += step)
                {
                    float lo = t - k, hi = t + k;
                    Vector3 q;
                    if (hi <= 1 - margin && !wet((q = Lerp(e.A, e.B, hi)).X, q.Z))
                    {
                        t = hi;
                        break;
                    }

                    if (lo >= margin && !wet((q = Lerp(e.A, e.B, lo)).X, q.Z))
                    {
                        t = lo;
                        break;
                    }

                    if (hi > 1 - margin && lo < margin)
                    {
                        break;
                    }
                }
            }
        }

        Vector3 at = Lerp(e.A, e.B, t);
        Vector3 beyond = len > 0.01f ? new Vector3(at.X + dz / len * 3f, at.Y, at.Z - dx / len * 3f) : at; // outward = -(-dz, dx)
        // A back exit comes out by the door or terminal we once entered through - the proxy
        // origin's remembered position, when it has one.
        Vector3? arrive = e.ArrivalA.HasValue ? Lerp(e.ArrivalA.Value, e.ArrivalB.Value, 1 - t)
            : e.Back ? _proxyOrigin?.FromPos
            : e.Arrival;
        return (at, beyond, arrive);
    }

    public static bool CanUse(ZoneExit e, ZoneRouteOptions opt)
    {
        if (opt.Filter != null && !opt.Filter(e))
        {
            return false;
        }

        if (opt.Stat == null || e.Reqs == null || e.Reqs.Length == 0)
        {
            return true;
        }

        return MeetsRequirements(e.Reqs, opt.Stat) ?? opt.UnknownPasses;
    }

    /// <summary>
    ///     Evaluates postfix requirements ([stat, op, value]; op 3/4/42 = Or/And/Not joining the terms before it).
    ///     Null when the answer hangs on a term that can't be read (an operator other than a plain stat compare,
    ///     or a stat the getter returns null for). Terms left over at the end are And-ed.
    /// </summary>
    public static bool? MeetsRequirements(int[][] reqs, Func<int, int?> stat)
    {
        var stack = new Stack<bool?>();
        foreach (var r in reqs)
        {
            int s = r[0], op = r[1], v = r[2];
            switch ((UseCriteriaOperator)op)
            {
                case UseCriteriaOperator.And:
                case UseCriteriaOperator.Or:
                    if (stack.Count < 2)
                    {
                        return null;
                    }

                    bool? y = stack.Pop(), x = stack.Pop();
                    stack.Push(op == (int)UseCriteriaOperator.And ? And(x, y) : Or(x, y));
                    break;
                case UseCriteriaOperator.Not:
                    if (stack.Count < 1)
                    {
                        return null;
                    }

                    bool? a = stack.Pop();
                    stack.Push(a.HasValue ? !a.Value : (bool?)null);
                    break;
                default:
                    stack.Push(Compare(stat(s), (UseCriteriaOperator)op, v));
                    break;
            }
        }

        bool? all = true;
        foreach (var b in stack)
        {
            all = And(all, b);
        }

        return all;
    }

    private static bool? Compare(int? have, UseCriteriaOperator op, int v)
    {
        if (!have.HasValue)
        {
            return null;
        }

        int h = have.Value;
        return op switch
        {
            UseCriteriaOperator.EqualTo => h == v,
            UseCriteriaOperator.LessThan => h < v,
            UseCriteriaOperator.GreaterThan => h > v,
            UseCriteriaOperator.Unequal => h != v,
            _ => null
        };
    }

    private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a == true && b == true ? true : (bool?)null;
    private static bool? Or(bool? a, bool? b) => a == true || b == true ? true : a == false && b == false ? false : (bool?)null;

    // A proxy names no destination point. The exits in the destination that lead back to where we came
    // from are usually beside the door we come out of: use their centre when they sit within 30 m of it.
    // A proxy playfield's own back exit door is exactly that spot once we have been there (the origin
    // context names this very playfield as entered-from here); before that, nothing to guess from.
    private static Vector3? GuessArrival(Dictionary<int, List<ZoneExit>> exits, ZoneExit e)
    {
        if (!exits.TryGetValue(e.ToPf, out var there))
        {
            return null;
        }

        var origin = _proxyOrigin;
        var back = there
            .Where(b => b.ToPf == e.FromPf && b.ToPf != b.FromPf
                        || b.Back && origin != null && origin.Pf == e.ToPf && origin.FromPf == e.FromPf)
            .Select(b => Mid(b.A, b.B)).ToList();
        if (back.Count == 0)
        {
            return null;
        }

        var c = new Vector3(back.Average(b => b.X), back.Average(b => b.Y), back.Average(b => b.Z));
        return back.All(b => Vector3.Distance(b, c) <= 30f) ? c : null;
    }

    private static double Flat(Vector3 a, Vector3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    private static Vector3 Mid(Vector3 a, Vector3 b) => Lerp(a, b, 0.5f);

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    // ---- file shapes (filled by Json.NET) ----
#pragma warning disable CS0649
    private sealed class ZoningFile
    {
        public int version;
        public Dictionary<string, PfDto> playfields;
    }

    private sealed class PfDto
    {
        public string name;
        public List<ZlDto> zoneLines;
        public List<TpDto> teleports;
        public Dictionary<string, float[][]> arrivals;
    }

    private sealed class ZlDto
    {
        public int to, idx, flags;
        public float[] a, b;
        public bool back; // proxy playfield's exit door: to 0, destination known only at runtime
        public int[] id;
        public int template;
    }

    private sealed class TpDto
    {
        public string kind;
        public int to, idx, template;
        public float[] pos, dest;
        public int[] id;
        public int[][] reqs;
        public string reqText;
    }

    private sealed class ScottyFile
    {
        public List<ScottyDto> warps;
    }

    private sealed class ScottyDto
    {
        public int? playfieldId;
        public ScottyPos pos;
        public string tell, label;
    }

    private sealed class ScottyPos
    {
        public float x, z;
    }
#pragma warning restore CS0649
}