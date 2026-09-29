using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// DROP_ITEM (CMD 50): world-drop placement for the current map (07 §5 group 11).
/// </summary>
public sealed class DropItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<DropItemHandler> logger) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.DROP_ITEM;

    public string Name => "DropItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var replyItemSpace = request.Body.Length > 6
            ? request.Body[6]
            : (byte)0;
        if (!CraftPacketBuilders.TryParseDropItemCommand(
                request.Body,
                out var dropCommand))
        {
            var error =
                CraftPacketBuilders.CreateDropItemErrorReply(
                    17,
                    replyItemSpace);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        replyItemSpace = dropCommand.ItemSpace;
        if (session.Character.Active is null || session.Dungeon.CurrentDungeonId == 0)
        {
            var error =
                CraftPacketBuilders.CreateDropItemErrorReply(
                    19,
                    replyItemSpace);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        IReadOnlyDictionary<ushort, CharacterItemRecord>
            sourceInventory = dropCommand.ItemSpace switch
            {
                0 => session.Inventory.Main,
                7 => session.Inventory.Creature,
                // The planner rejects unsupported spaces before
                // consulting this fallback dictionary.
                _ => session.Inventory.Main
            };
        if (!DungeonItemDropPlanner.TryPlan(
                sourceInventory,
                dropCommand.ItemSpace,
                dropCommand.Slot,
                dropCommand.Count,
                itemCatalog,
                out var dropPlan,
                out var dropFailure))
        {
            var errorCode = dropFailure is
                DungeonItemDropFailure.NotTradeable
                    or DungeonItemDropFailure.InvalidItem
                    or DungeonItemDropFailure.UnsupportedItemSpace
                ? (byte)23
                : (byte)17;
            var error =
                CraftPacketBuilders.CreateDropItemErrorReply(
                    errorCode,
                    replyItemSpace);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogInformation(
                "Rejected dungeon item drop from {ItemSpace}:{Slot} x{Count}: {Failure}.",
                dropCommand.ItemSpace,
                dropCommand.Slot,
                dropCommand.Count,
                dropFailure);
            return;
        }

        DungeonGroundItem groundItem;
        try
        {
            groundItem = session.Dungeon.GroundItems.AddPlayerDropped(
                dropPlan.DroppedItem,
                session.Character.LocalUserId,
                session.Dungeon.RoomX,
                session.Dungeon.RoomY,
                dropCommand.X,
                dropCommand.Y);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Could not allocate a ground id for item {ItemId}.",
                dropPlan.DroppedItem.ItemId);
            var error =
                CraftPacketBuilders.CreateDropItemErrorReply(
                    19,
                    replyItemSpace);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedMainInventory = dropPlan.ItemSpace == 0
            ? dropPlan.Inventory
            : session.Inventory.Main;
        var plannedCreatureInventory = dropPlan.ItemSpace == 7
            ? dropPlan.Inventory
            : session.Inventory.Creature;
        var savedDrop = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            plannedMainInventory.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: plannedCreatureInventory.Values);
        if (savedDrop is null)
        {
            session.Dungeon.GroundItems.Remove(groundItem.GroundId);
            var error =
                CraftPacketBuilders.CreateDropItemErrorReply(
                    19,
                    replyItemSpace);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = savedDrop;
        session.Inventory.Main = plannedMainInventory;
        session.Inventory.Creature = plannedCreatureInventory;

        var dropNotification = CraftPacketBuilders.CreateDropItem(
            session.Character.LocalUserId,
            dropCommand.X,
            dropCommand.Y,
            new GameDungeonDrop(
                groundItem.GroundId,
                groundItem.ItemId,
                groundItem.GetClientAddInfo(itemCatalog),
                groundItem.GetClientDurability(itemCatalog),
                groundItem.OwnerUserId,
                groundItem.GetClientItemAttr(itemCatalog)));
        await context.Outbound.SendAsync(dropNotification, Name, cancellationToken);

        var dropReply = CraftPacketBuilders.CreateDropItemReply(
            dropCommand.ItemSpace,
            dropCommand.Slot,
            dropCommand.Count);
        await context.Outbound.SendAsync(dropReply, Name, cancellationToken);
        logger.LogInformation(
            "Dropped item {ItemId} x{Count} from {ItemSpace}:{Slot} as ground item {GroundId} in dungeon {DungeonId} room ({RoomX},{RoomY}).",
            groundItem.ItemId,
            groundItem.CountOrValue,
            dropCommand.ItemSpace,
            dropCommand.Slot,
            groundItem.GroundId,
            session.Dungeon.CurrentDungeonId,
            session.Dungeon.RoomX,
            session.Dungeon.RoomY);
    }
}
