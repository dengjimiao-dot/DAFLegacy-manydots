using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// USE_LOTTERY_ITEM (CMD 29): lottery box draws with the reward tables (07 §5 group 11).
/// </summary>
public sealed class UseLotteryItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<UseLotteryItemHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => GameProtocolEngine.UseLotteryItemCommand;

    public string Name => "UseLotteryItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var sourceSlot = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, sizeof(ushort)));
        var roll = GameRandomSource.Shared.Next(
            LotteryItemDefinition.RollUpperBound);
        if (!LotteryItemPlanner.TryCreate(
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Inventory.Creature,
                sourceSlot,
                session.Character.Active.Level,
                session.Currency.Gold,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                roll,
                itemCatalog,
                out var lotteryPlan,
                out var lotteryFailure))
        {
            var errorCode = lotteryFailure switch
            {
                LotteryUseFailure.LevelTooLow => (byte)14,
                LotteryUseFailure.InsufficientGold => (byte)19,
                LotteryUseFailure.InventoryFull => (byte)4,
                LotteryUseFailure.GoldLimit => (byte)19,
                _ => (byte)17
            };
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected lottery item use at slot {SourceSlot}: {Failure}.",
                sourceSlot,
                lotteryFailure);
            return;
        }

        var lotteryWarehouseSettlement =
            senders.PlanWarehouseUpgradeSettlement(context, 
                lotteryPlan.MainInventory.Values,
                session.Inventory.Warehouse.Values,
                lotteryPlan.Reward.ItemId == 0
                    ? []
                    : [lotteryPlan.Reward.ItemId]);
        var isLotteryWarehouseUpgrade =
            CharacterWarehouseProgression.TryGetItemTargetCapacity(
                lotteryPlan.Reward.ItemId,
                out _);
        var savedLottery = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            lotteryPlan.Gold,
            lotteryWarehouseSettlement.Inventory,
            session.Inventory.Equipped.Values,
            lotteryWarehouseSettlement.Warehouse,
            cancellationToken,
            avatarInventory: lotteryPlan.AvatarInventory.Values,
            creatureInventory: lotteryPlan.CreatureInventory.Values,
            warehouseCapacity:
                lotteryWarehouseSettlement.Capacity,
            expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
        if (savedLottery is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, savedLottery);
        session.Currency.Gold = lotteryPlan.Gold;
        session.Inventory.Avatar = (savedLottery.AvatarInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = (savedLottery.CreatureInventory ?? [])
            .ToDictionary(item => item.Slot);
        var lotteryReply = CraftPacketBuilders.CreateUseLotteryItemReply(
            lotteryPlan.SourceSlot,
            lotteryPlan.DestinationSlot,
            lotteryPlan.Reward.ItemId,
            lotteryPlan.ResponseCountOrValue,
            lotteryPlan.InstanceValue,
            lotteryPlan.ItemAttribute);
        await context.Outbound.SendAsync(lotteryReply, Name, cancellationToken);

        if (warehouseCapacityChanged || isLotteryWarehouseUpgrade)
        {
            var inventoryRefresh = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values);
            await context.Outbound.SendAsync(inventoryRefresh, Name, cancellationToken);
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

        if (lotteryPlan.Reward.ItemId != 0
            && itemCatalog.TryGetDefinition(
                lotteryPlan.Reward.ItemId,
                out var lotteryRewardDefinition)
            && lotteryRewardDefinition.InventoryCategory
                == ItemInventoryCategory.Creature)
        {
            foreach (var creaturePacket in builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creaturePacket, Name, cancellationToken);
            }
        }

        logger.LogInformation(
            "Used lottery item at slot {SourceSlot}; roll={Roll}, reward={RewardItemId} x{RewardCount}, destination={DestinationSlot}, gold={Gold}.",
            sourceSlot,
            roll,
            lotteryPlan.Reward.ItemId,
            lotteryPlan.Reward.CountOrValue,
            lotteryPlan.DestinationSlot,
            session.Currency.Gold);
    }
}
