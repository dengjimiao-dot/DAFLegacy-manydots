namespace DFLegacy.Server;

public static class ClearRewardCardPlanner
{
    public static byte? FindLowestAvailableFreeCardIndex(
        int cardColumnCount,
        IEnumerable<byte> occupiedCardIndices)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cardColumnCount);
        if (cardColumnCount > byte.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cardColumnCount));
        }

        var occupied = new HashSet<byte>(occupiedCardIndices);
        for (var cardIndex = 0; cardIndex < cardColumnCount; cardIndex++)
        {
            var candidate = checked((byte)cardIndex);
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
