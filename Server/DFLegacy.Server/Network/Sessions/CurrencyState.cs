namespace DFLegacy.Server;

/// <summary>Currency wallets: gold, cash and victory points (07 §4.3).</summary>
public sealed class CurrencyState
{
    public int Gold = 50_000;

    public int Cash;

    public uint VictoryPoints;
}
