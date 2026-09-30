using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Creature-space move sub-flow of <see cref="MoveItemSpaceHandler"/>
/// (07 §4.7: split by sub-flow to keep each file small).
/// </summary>
public sealed partial class MoveItemSpaceHandler
{
    private async Task<bool> TryHandleCreatureSpaceMoveAsync(
        ConnectionContext context,
        bool parsedMove,
        byte sourceListType,
        ushort sourceSlot,
        int moveCount,
        byte destinationListType,
        ushort destinationSlot,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !parsedMove
            || (sourceListType != 7 && destinationListType != 7))
        {
            return false;
        }

        if (session.Dungeon.CurrentDungeonId != 0
            && !await senders.SettleDungeonCreatureHungerAsync(context,
                notifyClient: false,
                cancellationToken))
        {
            await senders.SendInventoryMoveNoOpAsync(context,
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                "creature hunger persistence failed");
            return true;
        }

        if (!senders.TryPlanCreatureInventoryMove(
                session.Inventory.Warehouse,
                session.Inventory.WarehouseCapacity,
                session.Inventory.Creature,
                sourceListType,
                sourceSlot,
                moveCount,
                destinationListType,
                destinationSlot,
                out var creatureMove,
                out var creatureMoveFailure))
        {
            await senders.SendInventoryMoveNoOpAsync(context,
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                creatureMoveFailure);
            return true;
        }

        var savedCreatureMove =
            await store.SaveCharacterItemSpacesAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                session.Inventory.Main.Values,
                session.Inventory.Equipped.Values,
                creatureMove.Warehouse.Values,
                cancellationToken,
                avatarInventory: session.Inventory.Avatar.Values,
                creatureInventory: creatureMove.CreatureInventory.Values);
        if (savedCreatureMove is null)
        {
            await senders.SendInventoryMoveNoOpAsync(context,
                sourceListType,
                sourceSlot,
                destinationListType,
                destinationSlot,
                "creature inventory persistence failed");
            return true;
        }

        session.Character.Active = savedCreatureMove;
        session.Inventory.Warehouse = creatureMove.Warehouse;
        session.Inventory.Creature = creatureMove.CreatureInventory;
        var creatureMoveReply =
            InventoryPacketBuilders.CreateMoveItemSpaceReply(
                sourceListType,
                sourceSlot,
                creatureMove.MovedCount,
                destinationListType,
                creatureMove.ReplyDestinationSlot);
        await context.Queue.SendAsync(creatureMoveReply, Name, cancellationToken);

        foreach (var creaturePacket in
                 builders.CreateCreatureInventoryPackets(
                     session.Inventory.Creature.Values))
        {
            await context.Queue.SendAsync(creaturePacket, Name, cancellationToken);
        }

        if (creatureMove.EquippedSlotsChanged)
        {
            if (creatureMove.EquippedCreatureChanged)
            {
                // A newly constructed equipped
                // Creature object needs its death
                // transition again, even when the same
                // persisted uid was equipped earlier.
                session.Weakness.CreatureDeathNotificationsSent.Clear();
            }

            if (creatureMove.EquippedCreatureChanged
                && session.Dungeon.CurrentDungeonId == 0)
            {
                // State must precede the mode-zero actor
                // refresh so a first equipped Creature is
                // constructed instead of merely replacing
                // an already existing map object.
                await senders.SendTownAppearanceAsync(context, cancellationToken: cancellationToken);
            }
            else
            {
                await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
                if (creatureMove.EquippedCreatureChanged)
                {
                    await senders.SendEquippedCreatureHungerAsync(context, cancellationToken: cancellationToken);
                }
            }
        }

        if (sourceListType == 2 || destinationListType == 2)
        {
            var warehousePacket = builders.CreateWarehousePacket(
                session.Inventory.Warehouse.Values,
                session.Inventory.WarehouseCapacity);
            await context.Queue.SendAsync(warehousePacket, Name, cancellationToken);
        }

        RuntimeLogger.Info(
            "Moved Creature-space item {ItemId} from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}; count={MovedCount}.",
            creatureMove.ItemId,
            sourceListType,
            sourceSlot,
            destinationListType,
            destinationSlot,
            creatureMove.MovedCount);
        return true;
    }
}
