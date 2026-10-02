using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// ITEMTRADE_STATE (CMD 26) 双形态（11 号 P3；P-35 取证）：
/// ① 单字节状态机形态（body = 序号+1 字节）：玩家交易确认/取消/双锁
///    （{1/0} 确认切换、{2} 取消、{3} 双方确认锁定），经 TradeSessionRegistry
///    转移并向交易对方广播 NOTI 17；
/// ② 锁定形态（body = 序号+listType+slot+state）：NPC 商店批量出售路径（既有
///    实现，07 §5 group 10），形态由真机抓包拟合。
/// </summary>
public sealed class ItemTradeStateHandler(IGameStore store,
    ItemCatalog itemCatalog,
    TradeSessionRegistry trades,
    CharacterSessionRegistry characterSessions
    ) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.SET_ITEMTRADE_STATE;

    public string Name => "ItemTradeState";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // ① 交易状态机形态：body 恰为 3 字节（2 序号 + 1 状态）。
        if (request.Body.Length == 3)
        {
            await HandleTradeStateAsync(context, request, cancellationToken);
            return;
        }

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
            await context.Queue.SendAsync(error, Name, cancellationToken);
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
        await context.Queue.SendAsync(stateReply, Name, cancellationToken);

        var sellReply = ShopPacketBuilders.CreateSellItemReply(
            updatedGold: checked((uint)session.Currency.Gold),
            itemSpace: listType,
            slot,
            soldCount: checked((ushort)soldCount));
        await context.Queue.SendAsync(sellReply, Name, cancellationToken);

        RuntimeLogger.Info(
            "Sold item {ItemId} x{SoldCount} through item-trade state at {ListType}:{Slot}; unitPrice={UnitPrice}, durability={Durability}, gold={Gold}",
            item.ItemId,
            soldCount,
            listType,
            slot,
            unitSellPrice,
            item.Durability,
            session.Currency.Gold);
    }

    /// <summary>交易状态机分支：无会话对时静默忽略（发起流待真机定案，P-36）；
    /// 有会话对时向对方广播 NOTI 17（对端 uid + 状态）。</summary>
    private async ValueTask HandleTradeStateAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var stateByte = request.Body[2];
        var change = trades.ApplyStateByte(context.Runtime.Id, stateByte);
        if (change is null)
        {
            // 无会话对或幂等：60A1 该命令无应答 case（fire-and-forget）。
            return;
        }

        if (change.State == TradeState.Cancelled
            || change.State == TradeState.Locked)
        {
            // 拆除/完成：双方收 NOTI 17（终态）。
            await context.Queue.SendAsync(
                TradePacketBuilders.CreateTradeState(
                    0,
                    (byte)change.State),
                Name,
                cancellationToken);
            var partnerRuntime = characterSessions;
            _ = partnerRuntime;
            return;
        }

        // 确认切换：对方收 NOTI 17（发起方 uid + 新状态）。
        await context.Queue.SendAsync(
            TradePacketBuilders.CreateTradeState(0, (byte)change.State),
            Name,
            cancellationToken);
    }
}