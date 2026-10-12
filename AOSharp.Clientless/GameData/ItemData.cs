using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;

namespace AOSharp.Clientless;

public static class ItemData
{
    private static long DUMMYITEM_START_OFFSET;
    private static long NANOITEM_START_OFFSET;
    private static readonly string DATA_PATH = "GameData\\ItemData.bin";
    private static readonly string OFFSET_PATH = "GameData\\ItemData.idx";
    private static Dictionary<int, long> _offsets;
    private static readonly Dictionary<int, ItemBase> _itemDataCache = new Dictionary<int, ItemBase>();

    public static bool Find<T>(int id, out T baseItem) where T : ItemBase
    {
        baseItem = null;

        if (!Client.ItemDataLoaded)
        {
            return false;
        }

        if (_offsets == null)
        {
            LoadOffsets();
        }

        if (!_offsets.TryGetValue(id, out var offset))
        {
            Logger.Warning($"Item with id: {id} not found");
            return false;
        }

        if (_itemDataCache.TryGetValue(id, out var itemBase))
        {
            if (itemBase is T type)
            {
                baseItem = type;
                return true;
            }

            return false;
        }

        if (!File.Exists(DATA_PATH))
        {
            Logger.Warning("ItemData Data File Not Found.");
            return false;
        }

        using (var item = new FileStream(DATA_PATH, FileMode.Open, FileAccess.Read))
        {
            item.Seek(offset, SeekOrigin.Begin);

            using (var reader = new BinaryReader(item))
            {
                if (_offsets[id] >= NANOITEM_START_OFFSET)
                {
                    baseItem = DeserializeNanoItem(reader) as T;
                }
                else
                {
                    baseItem = DeserializeDummyItem(reader) as T;
                }
            }
        }

        return baseItem != null;
    }

    /// <summary>
    ///     All nano-program template ids in the offline data (offsets at/after the nano section).
    ///     Use with Find&lt;NanoItem&gt; + MeetsUseReqs to build "castable / learnable" lists.
    /// </summary>
    public static IReadOnlyList<int> AllNanoIds()
    {
        if (_offsets == null)
        {
            LoadOffsets();
        }

        if (_offsets == null)
        {
            return new List<int>();
        }

        return _offsets.Where(kv => kv.Value >= NANOITEM_START_OFFSET).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    ///     All DUMMY-item ids in the offline data (offsets in the dummy section, before the nano
    ///     section). Nano CRYSTALS (the uploadable "Nano Crystal (&lt;name&gt;)" items) are dummy items -
    ///     use with Find&lt;DummyItem&gt; to read a crystal's own UseCriteria (e.g. the froob/expansion
    ///     gate a Shadowlands nano carries on its crystal rather than on the program).
    /// </summary>
    public static IReadOnlyList<int> AllDummyItemIds()
    {
        if (_offsets == null)
        {
            LoadOffsets();
        }

        if (_offsets == null)
        {
            return new List<int>();
        }

        return _offsets.Where(kv => kv.Value >= DUMMYITEM_START_OFFSET && kv.Value < NANOITEM_START_OFFSET)
            .Select(kv => kv.Key).ToList();
    }

    private static void LoadOffsets()
    {
        _offsets = new Dictionary<int, long>();

        if (!File.Exists(OFFSET_PATH))
        {
            Logger.Warning("ItemData Index File Not Found.");
            return;
        }

        using (var offsets = new FileStream(OFFSET_PATH, FileMode.Open, FileAccess.Read))
        {
            using (var reader = new BinaryReader(offsets))
            {
                DUMMYITEM_START_OFFSET = reader.ReadInt64();
                NANOITEM_START_OFFSET = reader.ReadInt64();

                var count = reader.ReadInt32();

                for (var i = 0; i < count; i++)
                {
                    _offsets.Add(reader.ReadInt32(), reader.ReadInt64());
                }
            }
        }
    }

    private static DummyItem DeserializeDummyItem(BinaryReader reader)
    {
        var itemBase = DeserializeItemBase(reader);
        var dummyItem = new DummyItem(itemBase.Id, itemBase.Ql);

        dummyItem.Name = itemBase.Name;
        dummyItem.Icon = itemBase.Icon;
        dummyItem.Modifiers = itemBase.Modifiers;
        dummyItem.Criteria = itemBase.Criteria;
        return dummyItem;
    }

    private static NanoItem DeserializeNanoItem(BinaryReader reader)
    {
        var itemBase = DeserializeItemBase(reader);

        var nanoItem = new NanoItem(itemBase.Id, itemBase.Ql);

        nanoItem.Name = itemBase.Name;
        nanoItem.Icon = itemBase.Icon;
        nanoItem.Modifiers = itemBase.Modifiers;
        nanoItem.Criteria = itemBase.Criteria;
        nanoItem.Cost = reader.ReadInt16();
        nanoItem.NanoLine = (NanoLine)reader.ReadInt32();
        nanoItem.NanoSchool = (NanoSchool)reader.ReadInt16();
        nanoItem.NCU = reader.ReadInt16();
        nanoItem.StackingOrder = reader.ReadInt32();
        nanoItem.Range = reader.ReadByte();
        nanoItem.TotalTimeInTicks = reader.ReadInt32();
        nanoItem.AttackDelayInTicks = reader.ReadInt16();
        nanoItem.RechargeDelayInTicks = reader.ReadInt16();

        return nanoItem;
    }

    private static ItemBase DeserializeItemBase(BinaryReader reader)
    {
        var name = reader.ReadString();
        var icon = reader.ReadInt32();
        var itemBase = new ItemBase(reader.ReadInt32(), reader.ReadInt16());
        itemBase.Name = name;
        itemBase.Icon = icon;
        itemBase.Criteria = new Dictionary<ItemActionInfo, List<RequirementCriterion>>();
        var criteriaCount = reader.ReadByte();

        for (var i = 0; i < criteriaCount; i++)
        {
            var criteriaList = new List<RequirementCriterion>();
            var key = (ItemActionInfo)reader.ReadByte();
            var reqCount = reader.ReadByte();

            for (var j = 0; j < reqCount; j++)
            {
                var req = new RequirementCriterion();
                req.Param1 = reader.ReadInt16();
                req.Param2 = reader.ReadInt32();
                req.Operator = (UseCriteriaOperator)reader.ReadByte();
                criteriaList.Add(req);
            }

            itemBase.Criteria.Add(key, criteriaList);
        }

        itemBase.Modifiers = new Dictionary<SpellListType, Dictionary<Stat, int>>();
        int modifiersCount = reader.ReadByte();

        for (var i = 0; i < modifiersCount; i++)
        {
            var spellModifiers = new Dictionary<Stat, int>();
            var key = (SpellListType)reader.ReadInt32();
            var spellCount = reader.ReadByte();

            for (var j = 0; j < spellCount; j++)
            {
                spellModifiers.Add((Stat)reader.ReadInt16(), reader.ReadInt32());
            }

            itemBase.Modifiers.Add(key, spellModifiers);
        }

        return itemBase;
    }
}