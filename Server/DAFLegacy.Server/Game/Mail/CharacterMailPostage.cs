namespace DAFLegacy.Server;

public static class CharacterMailPostage
{
    public const int BaseFee = 100;
    public const int AttachmentFee = 900;
    public const int MaximumGoldFee = 10_000;

    public static int Calculate(int gold, bool hasAttachment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gold);
        // DF2008 0x00809BB4 / 0x0080AF60: base 100, attachment 900,
        // and floor(gold * 0.05), capped at 10000. Integer division is exact.
        return BaseFee + (hasAttachment ? AttachmentFee : 0)
            + Math.Min(gold / 20, MaximumGoldFee);
    }

    public static bool TryDebit(int currentGold, int gold, bool hasAttachment, out int remainingGold)
    {
        remainingGold = currentGold;
        if (currentGold < 0 || gold < 0)
        {
            return false;
        }

        var total = (long)gold + Calculate(gold, hasAttachment);
        if (total > currentGold)
        {
            return false;
        }

        remainingGold = currentGold - (int)total;
        return true;
    }
}
