using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DFLegacy.Server;

public sealed record WorldMapItemCondition(ushort ItemId, uint Count);

public sealed record WorldMapDefinition(
    int Id,
    bool HasHellDungeon,
    ushort[] DungeonIds,
    ushort[] HellQuestIds,
    WorldMapItemCondition[] HellItems)
{
    public bool MeetsHellQuests(IReadOnlySet<ushort> completedQuestIds) =>
        HasHellDungeon && HellQuestIds.All(completedQuestIds.Contains);

    public bool HasHellItems(IEnumerable<CharacterItemRecord> inventory)
    {
        var counts = inventory.GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.CountOrValue));
        return HellItems.All(item => counts.GetValueOrDefault(item.ItemId) >= item.Count);
    }

    public bool TryConsumeHellItems(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        out Dictionary<ushort, CharacterItemRecord> remaining)
    {
        remaining = inventory.ToDictionary();
        if (!HasHellItems(inventory.Values))
        {
            return false;
        }

        foreach (var condition in HellItems)
        {
            var needed = condition.Count;
            foreach (var item in inventory.Values.Where(item => item.ItemId == condition.ItemId).OrderBy(item => item.Slot))
            {
                var count = Math.Min(needed, item.CountOrValue);
                needed -= count;
                if (count == item.CountOrValue)
                {
                    remaining.Remove(item.Slot);
                }
                else
                {
                    remaining[item.Slot] = item with { CountOrValue = item.CountOrValue - count };
                }

                if (needed == 0)
                {
                    break;
                }
            }
        }

        return true;
    }
}

public sealed class WorldMapCatalog(ScriptFileSystem scripts)
{
    private readonly Lazy<CatalogState> _state = new(() => Load(scripts));

    public bool TryGetForGate(byte townId, byte areaId, out WorldMapDefinition definition)
    {
        definition = null!;
        return _state.Value.Gates.TryGetValue((townId, areaId), out var worldMapId)
            && _state.Value.Maps.TryGetValue(worldMapId, out definition!);
    }

    private static CatalogState Load(ScriptFileSystem scripts)
    {
        var encoding = PvfEncodings.Cp949();
        var maps = new Dictionary<int, WorldMapDefinition>();
        var gates = new Dictionary<(byte Town, byte Area), int>();
        foreach (var (id, path) in ReadList("worldmap/worldmap.lst", "worldmap/"))
        {
            var text = Read(path);
            var dungeonIds = Rows(Block(text, "dungeon"))
                .Where(row => row.Length >= 2 && row[0] is > 0 and <= ushort.MaxValue)
                .Select(row => (ushort)row[0]).Distinct().ToArray();
            var quests = Rows(Block(text, "hell quest")).SelectMany(row => row)
                .Where(value => value is > 0 and <= ushort.MaxValue)
                .Select(value => (ushort)value).Distinct().ToArray();
            var items = Rows(Block(text, "item condition"))
                .Where(row => row.Length >= 2 && row[0] is > 0 and <= ushort.MaxValue && row[1] > 0)
                .GroupBy(row => (ushort)row[0])
                .Select(group => new WorldMapItemCondition(group.Key, checked((uint)group.Sum(row => (long)row[1]))))
                .ToArray();
            var hell = Regex.Match(text, @"\[hell dungeon\]\s*(-?\d+)");
            maps[id] = new WorldMapDefinition(id,
                hell.Success && int.Parse(hell.Groups[1].Value, CultureInfo.InvariantCulture) != 0,
                dungeonIds, quests, items);
        }

        foreach (var (id, path) in ReadList("town/town.lst", "town/"))
        {
            if (id is < 0 or > byte.MaxValue)
            {
                continue;
            }

            foreach (Match area in Regex.Matches(Read(path), @"\[area\](.*?)\[/area\]", RegexOptions.Singleline))
            {
                var areaId = Regex.Match(area.Groups[1].Value, @"^\s*(\d+)");
                var gate = Regex.Match(area.Groups[1].Value, @"`\[dungeon gate\]`\s*(\d+)");
                if (areaId.Success && gate.Success
                    && byte.TryParse(areaId.Groups[1].Value, out var parsedArea))
                {
                    gates[((byte)id, parsedArea)] = int.Parse(gate.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return new CatalogState(maps, gates);

        string Read(string path) => scripts.FileExists(path)
            ? Regex.Replace(scripts.ReadAllText(path, encoding), @"//[^\r\n]*", "")
            : string.Empty;

        IEnumerable<(int Id, string Path)> ReadList(string list, string prefix) =>
            Regex.Matches(Read(list), @"(?m)^\s*(\d+)\s+`([^`]+)`")
                .Select(match => (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    prefix + match.Groups[2].Value.Replace('\\', '/')));
    }

    private static string Block(string text, string tag) => Regex.Match(text,
        $@"(?ims)^\s*\[{Regex.Escape(tag)}\]\s*(.*?)(?=^\s*\[|\z)").Groups[1].Value;

    private static IEnumerable<int[]> Rows(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => Regex.Matches(line, @"-?\d+")
            .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray());

    private sealed record CatalogState(
        IReadOnlyDictionary<int, WorldMapDefinition> Maps,
        IReadOnlyDictionary<(byte Town, byte Area), int> Gates);
}
