using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SELL_ITEM (CMD 24): NPC shop sales with the sell-price table (07 §5 group 10).
/// </summary>
public sealed class SellItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<SellItemHandler> logger) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.SellItemCommand;

    public string Name => "SellItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ShopParsers.TryReadSellItem(
                request.Body,
                out var listType,
                out var slot,
                out var requestedCount)
            || listType != 0
            || !session.Inventory.Main.TryGetValue(slot, out var soldItem)
            || !itemCatalog.TryCalculateSellPrice(
                soldItem.ItemId,
                soldItem.Durability,
                out var unitSellPrice))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 0x11);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var soldCount = (uint)Math.Clamp(
            requestedCount,
            1,
            (int)Math.Min(soldItem.CountOrValue, int.MaxValue));
        var remaining = soldItem.CountOrValue - soldCount;
        if (remaining == 0)
        {
            session.Inventory.Main.Remove(slot);
        }
        else
        {
            session.Inventory.Main[slot] = soldItem with { CountOrValue = remaining };
        }

        session.Currency.Gold = checked(session.Currency.Gold
            + unitSellPrice * (int)soldCount);
        session.Character.Active = await store.SaveCharacterInventoryAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            cancellationToken) ?? session.Character.Active;
        var sellReply = ShopPacketBuilders.CreateSellItemReply(
            updatedGold: checked((uint)session.Currency.Gold),
            itemSpace: listType,
            slot,
            soldCount: checked((ushort)soldCount));
        await context.Outbound.SendAsync(sellReply, Name, cancellationToken);
        logger.LogInformation(
            "Sold item {ItemId} x{Count} from slot {Slot}; unitPrice={UnitPrice}, durability={Durability}, gold={Gold}",
            soldItem.ItemId,
            soldCount,
            slot,
            unitSellPrice,
            soldItem.Durability,
            session.Currency.Gold);
    }
}
