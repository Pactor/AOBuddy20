// ---------------------------------------------------------------------------------------
// Solution: AOBuddy20
// Project: AOBuddy20
// Filename: WantData.cs
//
// Last modified: 2026-10-08
// Created:       2026-10-08 (ported from AOBuddy10 WantData.cs)
//
// Long live OmniCell and AOBuddy
// ---------------------------------------------------------------------------------------

#nullable disable

using System.Text;

namespace AOBuddy20.Utils;

/// <summary>
///     The item data the want list matches rewards against (GameData/ItemWantData.bin, "AOWD" v1,
///     made by tools/eng-gear-extractor --wantdata from items.ocp): which nano program each nano
///     crystal uploads (its use event's Upload function 53019 - 'Nano Crystal (Shatter Bone)'
///     82011 -> 82007), and each template's ItemClass (stat 0x4C: 1 weapon, 2 armor incl. rings
///     and wearables, 3 implant, 4 NPC-only equipment, 5 spirit).
/// </summary>
public static class WantData
{
    public const int Weapon = 1, Armor = 2, Implant = 3, NpcEquip = 4, Spirit = 5;

    private static Dictionary<int, int> _crystalNano = new();
    private static Dictionary<int, byte> _class = new();

    public static int CrystalCount => _crystalNano.Count;

    /// <summary>The nano program a crystal uploads, or 0 when the template is no nano crystal.</summary>
    public static int NanoOf(int crystal)
    {
        return _crystalNano.TryGetValue(crystal, out var n) ? n : 0;
    }

    public static bool IsCrystal(int template)
    {
        return _crystalNano.ContainsKey(template);
    }

    /// <summary>ItemClass (see the constants), 0 when unknown.</summary>
    public static int ClassOf(int template)
    {
        return _class.TryGetValue(template, out var c) ? c : 0;
    }

    public static IEnumerable<KeyValuePair<int, int>> Crystals => _crystalNano;

    public static void Load(string baseDir, Action<string> log)
    {
        var file = Path.Combine(baseDir, "GameData", "ItemWantData.bin");
        try
        {
            using var r = new BinaryReader(File.OpenRead(file));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "AOWD" || r.ReadInt32() != 1)
            {
                log?.Invoke($"WANTDATA: {file} is not a version 1 table.");
                return;
            }

            var n = r.ReadInt32();
            var cn = new Dictionary<int, int>(n);
            for (var i = 0; i < n; i++)
            {
                var c = r.ReadInt32();
                cn[c] = r.ReadInt32();
            }

            var m = r.ReadInt32();
            var cl = new Dictionary<int, byte>(m);
            for (var i = 0; i < m; i++)
            {
                var id = r.ReadInt32();
                cl[id] = r.ReadByte();
            }

            _crystalNano = cn;
            _class = cl;
            log?.Invoke($"WANTDATA: {cn.Count} nano crystals, {cl.Count} item classes loaded.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"WANTDATA: couldn't load {file}: {ex.Message}");
        }
    }
}