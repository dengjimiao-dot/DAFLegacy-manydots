namespace DFLegacy.Server;

internal sealed record WeightedItemCandidate(
    ushort ItemId,
    byte Grade,
    byte Rarity,
    int CreationRate);

internal sealed record WeightedItemLevelRange(
    byte Level,
    byte MinusLevel,
    byte PlusLevel);

internal sealed record WeightedItemEntry(
    ushort ItemId,
    int CumulativeWeight);

internal sealed record WeightedItemTable(
    int TotalWeight,
    WeightedItemEntry[] Entries);

internal static class WeightedItemTableBuilder
{
    public static IReadOnlyDictionary<
        (byte Level, byte Rarity),
        WeightedItemTable> Build(
            IEnumerable<WeightedItemLevelRange> levelRanges,
            IReadOnlyList<WeightedItemCandidate> candidates,
            byte maximumRarity)
    {
        var result = new Dictionary<
            (byte Level, byte Rarity),
            WeightedItemTable>();
        foreach (var range in levelRanges)
        {
            var minimumGrade = Math.Max(1, range.Level - range.MinusLevel);
            var maximumGradeExclusive = Math.Min(256, range.Level + range.PlusLevel);
            for (byte rarity = 0; rarity <= maximumRarity; rarity++)
            {
                var cumulative = 0L;
                var entries = new List<WeightedItemEntry>();
                foreach (var candidate in candidates)
                {
                    if (candidate.Rarity != rarity
                        || candidate.Grade < minimumGrade
                        || candidate.Grade >= maximumGradeExclusive)
                    {
                        continue;
                    }

                    cumulative += candidate.CreationRate;
                    if (cumulative > int.MaxValue)
                    {
                        entries.Clear();
                        break;
                    }

                    entries.Add(new WeightedItemEntry(
                        candidate.ItemId,
                        checked((int)cumulative)));
                }

                if (entries.Count > 0)
                {
                    result[(range.Level, rarity)] = new WeightedItemTable(
                        checked((int)cumulative),
                        entries.ToArray());
                }
            }
        }

        return result;
    }

    public static bool TryChoose(
        WeightedItemTable table,
        IDropRandomSource random,
        out ushort itemId)
    {
        var selection = random.Next(table.TotalWeight);
        foreach (var entry in table.Entries)
        {
            if (selection < entry.CumulativeWeight)
            {
                itemId = entry.ItemId;
                return true;
            }
        }

        itemId = 0;
        return false;
    }
}
