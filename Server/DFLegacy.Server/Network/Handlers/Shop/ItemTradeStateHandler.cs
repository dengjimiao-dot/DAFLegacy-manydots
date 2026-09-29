using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// ITEMTRADE_STATE (CMD 26): the NPC item-trade lock state used by shops (07 §5 group 10).
/// </summary>
public sealed class ItemTradeStateHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<ItemTradeStateHandler> logger) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.SetItemTradeStateCommand;

    public string Name => "ItemTradeState";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !ShopParsers.TryReadSetItemTradeState(
                request.Body,
                out var listType,
                out var slot,
                out var state)
            || !ChannelSessionSenders.TryGetInventorySpace(
                listType,
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Inventory.Equipped,
                session.Inventory.Warehouse,
                out var itemSpace)
            || !itemSpace.TryGetValue(slot, out var item)
            || !itemCatalog.TryCalculateSellPrice(
                item.ItemId,
                item.Durability,
                out var unitSellPrice))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 0x11);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var soldCount = Math.Min(
            item.CountOrValue,
            state == 0 ? 1u : state);
        var remaining = item.CountOrValue - soldCount;
        if (remaining == 0)
        {
            itemSpace.Remove(slot);
        }
        else
        {
            itemSpace[slot] = item with
            {
                CountOrValue = remaining
            };
        }

        session.Currency.Gold = checked(
            session.Currency.Gold
            + unitSellPrice * (int)soldCount);
        session.Character.Active = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values) ?? session.Character.Active;

        var stateReply =
            ShopPacketBuilders.CreateSetItemTradeStateReply(
                item.ItemId,
                listType);
        await context.Outbound.SendAsync(stateReply, Name, cancellationToken);

        var sellReply = ShopPacketBuilders.CreateSellItemReply(
            updatedGold: checked((uint)session.Currency.Gold),
            itemSpace: listType,
            slot,
            soldCount: checked((ushort)soldCount));
        await context.Outbound.SendAsync(sellReply, Name, cancellationToken);

        logger.LogInformation(
            "Sold item {ItemId} x{SoldCount} through item-trade state at {ListType}:{Slot}; unitPrice={UnitPrice}, durability={Durability}, gold={Gold}",
            item.ItemId,
            soldCount,
            listType,
            slot,
            unitSellPrice,
            item.Durability,
            session.Currency.Gold);
    }
}
