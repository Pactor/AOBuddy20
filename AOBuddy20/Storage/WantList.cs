// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: WantList.cs
//
// Last modified: 2026-10-08
// Created:       2026-10-08 (ported from AOBuddy10 WantList.cs, owner 2026-09-25)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using AOBuddy20.Utils;
using AOSharp.Clientless;
using AOSharp.Common.GameData;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace AOBuddy20.Storage;

/// <summary>
///     THE WANT LIST (owner, 2026-09-25, ported from AOBuddy10): what the mission run rolls for.
///     One list per character, wants-&lt;character&gt;.json beside the executable, changeable while
///     it runs (commands, or the file edited by hand - re-read when it changes). Entries are an
///     exact item name, or a query: kind (nano, implant, weapon, armor, gear = weapon or armor,
///     spirit, ncu, any), optionally a profession (nanos: the nano program's profession
///     requirement) and a QL band.
///     Mode 'always' = a standing filter; 'list' = collect everything on it, then say done and
///     stop. Learned nanos do NOT count as had (the owner rolls for other professions); held items
///     (inventory, bags, the bank as last seen) and the got record do.
///     Matching is by template: a nano crystal's nano program comes from WantData (its Upload
///     function), its profession from that program's requirements, gear and implants from their
///     ItemClass.
/// </summary>
public sealed class WantList
{
    public sealed class Entry
    {
        public string Name; // exact item name, or null for a query
        public string Kind = "any"; // nano | implant | weapon | armor | gear | spirit | ncu | any
        public int Prof; // Stat.Profession value, 0 = any
        public int QlMin, QlMax = 1000;
        public string Line; // nano line (NanoLine name, part of it, spaces/underscores ignored), or null

        public override string ToString()
        {
            if (Name != null)
            {
                return "'" + Name + "'";
            }

            var p = Prof > 0 ? " " + ProfName(Prof) : "";
            var q = QlMin <= 0 && QlMax >= 1000 ? "" : QlMax >= 1000 ? $" QL {QlMin}+" : $" QL {QlMin}-{QlMax}";
            return Kind + p + (Line != null ? " line " + Line : "") + q;
        }
    }

    public string Mode = "list";
    public readonly List<Entry> Entries = new();
    public readonly HashSet<int> Got = new(); // templates (low id) collected by want runs

    private readonly string _path;
    private readonly Action<string> _log;
    private DateTime _stamp;

    public WantList(string character, Action<string> log)
    {
        _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"wants-{character}.json");
        _log = log;
        Reload(true);
    }

    // ---- file ---------------------------------------------------------------------------

    /// <summary>Re-read the wants file when it changed on disk (or when forced). True when it was (re)loaded.</summary>
    public bool Reload(bool force = false)
    {
        try
        {
            if (!File.Exists(_path))
            {
                if (force)
                {
                    Entries.Clear();
                    Got.Clear();
                }

                return false;
            }

            var t = File.GetLastWriteTimeUtc(_path);
            if (!force && t == _stamp)
            {
                return false;
            }

            _stamp = t;
            var o = JObject.Parse(File.ReadAllText(_path));
            Mode = (string)o["mode"] ?? "list";
            Entries.Clear();
            foreach (var e in (JArray)o["entries"] ?? new JArray())
            {
                Entries.Add(new Entry
                {
                    Name = (string)e["name"],
                    Kind = (string)e["kind"] ?? "any",
                    Prof = (int?)e["prof"] ?? 0,
                    QlMin = (int?)e["qlMin"] ?? 0,
                    QlMax = (int?)e["qlMax"] ?? 1000,
                    Line = (string)e["line"],
                });
            }

            Got.Clear();
            foreach (var g in (JArray)o["got"] ?? new JArray())
            {
                Got.Add((int)g);
            }

            _log?.Invoke($"WANTS: loaded {Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")} ({Mode}).");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"WANTS: couldn't read {_path}: {ex.Message}");
            return false;
        }
    }

    public void Save()
    {
        var o = new JObject
        {
            ["mode"] = Mode,
            ["entries"] = new JArray(Entries.Select(e => e.Name != null
                ? new JObject { ["name"] = e.Name }
                : new JObject { ["kind"] = e.Kind, ["prof"] = e.Prof, ["qlMin"] = e.QlMin, ["qlMax"] = e.QlMax, ["line"] = e.Line })),
            ["got"] = new JArray(Got.OrderBy(x => x)),
        };
        if (JsonStore.Save(_path, o.ToString(), _log))
        {
            try
            {
                _stamp = File.GetLastWriteTimeUtc(_path);
            }
            catch
            {
            }
        }
    }

    // ---- parsing ('mission want add ...') -------------------------------------------------

    private static readonly Dictionary<string, int> Profs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "soldier", 1 }, { "sol", 1 }, { "martialartist", 2 }, { "ma", 2 }, { "engineer", 3 }, { "engi", 3 }, { "fixer", 4 },
        { "agent", 5 }, { "adventurer", 6 }, { "adv", 6 }, { "trader", 7 }, { "bureaucrat", 8 }, { "crat", 8 },
        { "enforcer", 9 }, { "enf", 9 }, { "doctor", 10 }, { "doc", 10 }, { "nanotechnician", 11 }, { "nt", 11 },
        { "metaphysicist", 12 }, { "mp", 12 }, { "keeper", 14 }, { "shade", 15 },
    };

    public static string ProfName(int p)
    {
        return Profs.Where(kv => kv.Value == p).OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Key).FirstOrDefault() ?? p.ToString();
    }

    private static readonly Dictionary<string, string> KindWords = new(StringComparer.OrdinalIgnoreCase)
    {
        { "nano", "nano" }, { "nanos", "nano" }, { "implant", "implant" }, { "implants", "implant" }, { "weapon", "weapon" },
        { "weapons", "weapon" }, { "armor", "armor" }, { "armour", "armor" }, { "gear", "gear" }, { "spirit", "spirit" },
        { "spirits", "spirit" }, { "ncu", "ncu" }, { "ncus", "ncu" }, { "any", "any" },
    };

    /// <summary>'nano engineer ql 20-30', 'implant ql 200+', 'gear', 'ncu ql 30-45', or an exact item name.</summary>
    public static Entry Parse(string text)
    {
        text = (text ?? "").Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        if (!KindWords.TryGetValue(words[0], out var kind))
        {
            return new Entry { Name = text };
        }

        var e = new Entry { Kind = kind };
        var m = Regex.Match(text, @"ql\s*(\d+)\s*(?:-\s*(\d+)|(\+))?", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            e.QlMin = int.Parse(m.Groups[1].Value);
            e.QlMax = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : m.Groups[3].Success ? 1000 : e.QlMin;
        }

        var ln = Regex.Match(text, @"\bline\s+(.+?)(?=\s+ql\b|$)", RegexOptions.IgnoreCase);
        if (ln.Success)
        {
            e.Line = ln.Groups[1].Value.Trim();
        }

        var beforeLine = ln.Success ? text.Substring(0, ln.Index) : text;
        foreach (var w in beforeLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (Profs.TryGetValue(w, out var p))
            {
                e.Prof = p;
            }
        }

        return e;
    }

    // ---- matching ---------------------------------------------------------------------------

    /// <summary>The nano program's professions (its Profession / VisualProfession requirements); empty = any.</summary>
    public static HashSet<int> NanoProfs(int nanoId)
    {
        var set = new HashSet<int>();
        if (!ItemData.Find(nanoId, out NanoItem ni) || ni?.Criteria == null)
        {
            return set;
        }

        foreach (var list in ni.Criteria.Values)
        {
            foreach (var c in list)
            {
                if ((c.Param1 == (int)Stat.Profession || c.Param1 == (int)Stat.VisualProfession) && c.Param2 > 0 && c.Param2 < 20)
                {
                    set.Add(c.Param2);
                }
            }
        }

        return set;
    }

    private static string Squash(string s)
    {
        return new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    /// <summary>The nano program's line fits the query's line words (AttackPets fits "attack pets", "pets", ...).</summary>
    public static bool LineFits(int nanoId, string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return true;
        }

        if (!ItemData.Find(nanoId, out NanoItem ni) || ni == null)
        {
            return false;
        }

        return Squash(ni.NanoLine.ToString()).Contains(Squash(line));
    }

    /// <summary>Every nano line name, for 'want lines'.</summary>
    public static IEnumerable<string> LineNames(string part)
    {
        return Enum.GetNames(typeof(NanoLine)).Where(n => Squash(n).Contains(Squash(part ?? "")));
    }

    // Resolved through ItemBase, not DummyItem: ItemData caches by id and refuses type mismatches,
    // and other code (e.g. EngineerPetBrain) resolves crystals as the common ItemBase - a Find
    // through the narrower type would miss everything already cached (AOBuddy20's ItemData cache).
    public static string NameOf(int template)
    {
        return ItemData.Find(template, out ItemBase d) && d?.Name != null ? d.Name : null;
    }

    private static bool NameHas(string name, string part)
    {
        return name != null && part != null && name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Does this reward (template, QL) fit the entry?</summary>
    public static bool Fits(Entry e, int low, int ql)
    {
        // A name matches as a part of the item name too: 'Obtru Steel-Ribbed' takes every piece of
        // that set, Worn and High-Quality (owner, 2026-09-25). A full exact name still matches only
        // itself (and longer names).
        if (e.Name != null)
        {
            return NameHas(NameOf(low), e.Name);
        }

        if (ql < e.QlMin || ql > e.QlMax)
        {
            return false;
        }

        var cls = WantData.ClassOf(low);
        switch (e.Kind)
        {
            case "nano":
                var nano = WantData.NanoOf(low);
                if (nano == 0)
                {
                    return false;
                }

                return (e.Prof == 0 || NanoProfs(nano).Contains(e.Prof)) && LineFits(nano, e.Line);
            case "implant":
                return cls == WantData.Implant;
            case "weapon":
                return cls == WantData.Weapon;
            case "armor":
                return cls == WantData.Armor;
            case "gear":
                return cls == WantData.Weapon || cls == WantData.Armor;
            case "spirit":
                return cls == WantData.Spirit;
            // NCU memory rewards ('4 - 7 NCU Memory', 'NCU Coolant Sink', 2026-09-25 rolls): the name
            // carries the tier, so match the word NCU on anything that is not a nano crystal
            // ('NanoCrystal (NCU Compressor)').
            case "ncu":
                return WantData.NanoOf(low) == 0 && Regex.IsMatch(NameOf(low) ?? "", @"\bNCU\b", RegexOptions.IgnoreCase);
            default:
                return cls != WantData.NpcEquip;
        }
    }

    /// <summary>
    ///     Is this reward wanted now: fits an entry and is not had yet. In 'list' mode a nano query
    ///     wants each of its nanos once, a named item once; a gear/implant/spirit query stays open
    ///     (it has no fixed set).
    /// </summary>
    public Entry Wanted(int low, int ql, ISet<int> held)
    {
        foreach (var e in Entries)
        {
            if (!Fits(e, low, ql))
            {
                continue;
            }

            if (Mode == "list" && e.Name != null && (Got.Contains(low) || held.Contains(low)))
            {
                continue;
            }

            // A nano is had whichever crystal of it he holds: Keeper 'Sidestep' is both 'Nano Crystal
            // (Sidestep)' and 'Cracked and Miskept Shadow Crystal (Sidestep)' (item data, 2026-09-25).
            if (Mode == "list" && e.Kind == "nano" && e.Name == null && HaveNano(WantData.NanoOf(low), held))
            {
                continue;
            }

            return e;
        }

        return null;
    }

    /// <summary>The crystals a nano query stands for: every crystal whose nano fits it.</summary>
    // Expanding a query walks all ~6000 crystals through the item data (seconds): once per query,
    // cached (want status is answered after every roll paid for it, 2026-09-25).
    private static readonly Dictionary<string, List<int>> _expand = new();

    public static List<int> CrystalsFor(Entry e)
    {
        if (e.Name != null || e.Kind != "nano")
        {
            return new List<int>();
        }

        var key = $"{e.Prof}:{e.QlMin}:{e.QlMax}:{e.Line}";
        lock (_expand)
        {
            if (_expand.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var list = new List<int>();
        foreach (var kv in WantData.Crystals)
        {
            // The QL is the crystal's (Shatter Bone: crystal QL 37, what the terminal offers); the
            // nano program itself reads QL 1 in the item data. Found through ItemBase - see NameOf.
            if (!ItemData.Find(kv.Key, out ItemBase cr) || cr == null)
            {
                continue;
            }

            if (cr.Ql < e.QlMin || cr.Ql > e.QlMax)
            {
                continue;
            }

            if (e.Prof != 0 && !NanoProfs(kv.Value).Contains(e.Prof))
            {
                continue;
            }

            if (!LineFits(kv.Value, e.Line))
            {
                continue;
            }

            list.Add(kv.Key);
        }

        lock (_expand)
        {
            _expand[key] = list;
        }

        return list;
    }

    public bool HaveNano(int nano, ISet<int> held)
    {
        return nano != 0 && (Got.Any(g => WantData.NanoOf(g) == nano) || held.Any(h => WantData.NanoOf(h) == nano));
    }

    /// <summary>The nanos a nano query stands for, one crystal each (the lowest QL one) - several
    /// crystals can carry the same nano.</summary>
    private static readonly Dictionary<string, List<int>> _nanos = new();

    public static List<int> NanosFor(Entry e)
    {
        var key = $"{e.Kind}:{e.Name}:{e.Prof}:{e.QlMin}:{e.QlMax}:{e.Line}";
        lock (_nanos)
        {
            if (_nanos.TryGetValue(key, out var hit))
            {
                return hit;
            }
        }

        var list = CrystalsFor(e).GroupBy(WantData.NanoOf)
            .Select(g => g.OrderBy(c => ItemData.Find(c, out ItemBase d) && d != null ? d.Ql : 0).First()).ToList();
        lock (_nanos)
        {
            _nanos[key] = list;
        }

        return list;
    }

    /// <summary>Still to collect, per entry: templates left (nano query: one crystal per missing nano;
    /// named item: -1 while not had); open queries: null.</summary>
    public List<(Entry e, List<int> left)> Remaining(ISet<int> held)
    {
        var r = new List<(Entry, List<int>)>();
        foreach (var e in Entries)
        {
            if (e.Kind == "nano" && e.Name == null)
            {
                r.Add((e, NanosFor(e).Where(c => !HaveNano(WantData.NanoOf(c), held)).ToList()));
            }
            else if (e.Name != null)
            {
                var have = Got.Any(g => NameHas(NameOf(g), e.Name)) || held.Any(h => NameHas(NameOf(h), e.Name));
                r.Add((e, have ? new List<int>() : new List<int> { -1 }));
            }
            else
            {
                r.Add((e, null));
            }
        }

        return r;
    }
}