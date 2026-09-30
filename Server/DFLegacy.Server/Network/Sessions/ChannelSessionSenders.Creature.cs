using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Creature-space senders and the Creature-move planner cluster
/// (07 §4.7): equipping flows, move deltas and the move-plan search.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    private readonly CreatureInventoryMovePlanner _creatureMovePlanner =
        new(itemCatalog);

    public async Task SendEquippedCreatureDeathAsync(
    ConnectionContext context,
    bool force = false,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null
            || !context.State.Inventory.Creature.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var equippedCreature)
            || equippedCreature.CountOrValue == 0)
        {
            return;
        }

        var creatureUid = equippedCreature.CountOrValue;
        var stomach = Math.Clamp(
            (int)(equippedCreature.CreatureStomach ?? 100),
            0,
            CreatureHungerPlanner.MaximumStomach);
        if (stomach != 0)
        {
            context.State.Weakness.CreatureDeathNotificationsSent.Remove(creatureUid);
            return;
        }

        if (!force
            && context.State.Weakness.CreatureDeathNotificationsSent.Contains(creatureUid))
        {
            return;
        }

        var died = PetsPacketBuilders.CreateDiedCreature(context.State.Character.LocalUserId);
        await context.Queue.SendAsync(died, "CREATURE_DIED", cancellationToken);
        context.State.Weakness.CreatureDeathNotificationsSent.Add(creatureUid);
        RuntimeLogger.Info(
            "Sent DIED_CREATURE for Creature uid {CreatureUid}, area user {UserId}.",
            creatureUid,
            context.State.Character.LocalUserId);
    }

    public async Task SendEquippedCreatureHungerAsync(
    ConnectionContext context,
    bool forceDeathNotification = false,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null
            || !context.State.Inventory.Creature.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var equippedCreature)
            || equippedCreature.CountOrValue == 0)
        {
            return;
        }

        var creatureState = PetsPacketBuilders.CreateCreatureState(
            equippedCreature.CountOrValue,
            (uint)Math.Clamp(
                (int)(equippedCreature.CreatureStomach ?? 100),
                0,
                CreatureHungerPlanner.MaximumStomach));
        await context.Queue.SendAsync(creatureState, "CREATURE_STATE", cancellationToken);
        await SendEquippedCreatureDeathAsync(context, forceDeathNotification, cancellationToken);
    }

    public async Task SendEquippedCreatureResponseAsync(
    ConnectionContext context,
    CancellationToken cancellationToken = default)
    {
        if (context.State.Character.Active is null
            || !context.State.Inventory.Creature.ContainsKey(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot))
        {
            return;
        }

        var creatureResponse =
            PetsPacketBuilders.CreateCreatureResponse(context.State.Character.LocalUserId);
        await context.Queue.SendAsync(creatureResponse, "CREATURE_RESPONSE", cancellationToken);
    }

    public async Task SendInventoryMoveNoOpAsync(
    ConnectionContext context,
    byte sourceListType,
    ushort sourceSlot,
    byte destinationListType,
    ushort destinationSlot,
    string reason,
    CancellationToken cancellationToken = default)
    {
        var noOpReply = InventoryPacketBuilders.CreateMoveItemSpaceReply(
            sourceListType,
            sourceSlot,
            movedCount: 0,
            destinationListType,
            destinationSlot);
        await context.Queue.SendAsync(noOpReply, "INVENTORY_DELTA", cancellationToken);

        if (sourceListType == 0 || destinationListType == 0)
        {
            var mainRefresh = builders.CreateMainInventoryPacket(
                context.State.Currency.Gold,
                context.State.Currency.VictoryPoints,
                context.State.Inventory.Main.Values);
            await context.Queue.SendAsync(mainRefresh, "INVENTORY_DELTA", cancellationToken);
        }

        if (sourceListType == 2 || destinationListType == 2)
        {
            var warehouseRefresh = builders.CreateWarehousePacket(
                context.State.Inventory.Warehouse.Values,
                context.State.Inventory.WarehouseCapacity);
            await context.Queue.SendAsync(warehouseRefresh, "INVENTORY_DELTA", cancellationToken);
        }

        if (sourceListType == 1 || destinationListType == 1)
        {
            var avatarRefresh = builders.CreateAvatarInventoryPacket(
                context.State.Inventory.Avatar.Values);
            await context.Queue.SendAsync(avatarRefresh, "INVENTORY_DELTA", cancellationToken);
        }

        if (sourceListType == 7 || destinationListType == 7)
        {
            foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                         context.State.Inventory.Creature.Values))
            {
                await context.Queue.SendAsync(creatureRefresh, "INVENTORY_DELTA", cancellationToken);
            }
        }

        if ((sourceListType == 3 || destinationListType == 3)
            && sourceListType != 7
            && destinationListType != 7)
        {
            await SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
        }

        RuntimeLogger.Info(
            "Completed rejected inventory move as a no-op from {SourceList}:{SourceSlot} to {DestinationList}:{DestinationSlot}: {Reason}.",
            sourceListType,
            sourceSlot,
            destinationListType,
            destinationSlot,
            reason);
    }

    public async Task SendMainInventoryMoveDeltaAsync(
    ConnectionContext context,
    CancellationToken cancellationToken,
    params ushort[] slots)
    {
        foreach (var slot in slots
                     .Where(CharacterInventoryLayout.IsMainItemSlot)
                     .Distinct())
        {
            var entry = context.State.Inventory.Main.TryGetValue(slot, out var item)
                ? builders.ToGameInventoryEntry(item)
                : new GameInventoryEntry(slot, ushort.MaxValue, 0);
            var update = InventoryPacketBuilders.CreateUpdateItemList(0, [entry]);
            await context.Queue.SendAsync(update, "INVENTORY_DELTA", cancellationToken);
        }
    }

    internal bool TryPlanCreatureInventoryMove(
        IReadOnlyDictionary<ushort, CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        byte sourceListType,
        ushort sourceSlot,
        int moveCount,
        byte destinationListType,
        ushort destinationSlot,
        out CreatureInventoryMovePlan plan,
        out string failure) =>
        _creatureMovePlanner.TryPlanCreatureInventoryMove(warehouse, warehouseCapacity, creatureInventory, sourceListType, sourceSlot, moveCount, destinationListType, destinationSlot, out plan, out failure);

    internal ushort FindCreatureMoveDestination(
        IReadOnlyDictionary<ushort, CharacterItemRecord> destination,
        byte destinationListType,
        CharacterItemRecord item,
        ItemDefinition definition,
        ushort? excludedSlot,
        ushort warehouseCapacity) =>
        _creatureMovePlanner.FindCreatureMoveDestination(destination, destinationListType, item, definition, excludedSlot, warehouseCapacity);

    internal bool IsCompatibleCreatureMoveSlot(
        byte listType,
        ItemDefinition definition,
        ushort slot,
        ushort warehouseCapacity) =>
        _creatureMovePlanner.IsCompatibleCreatureMoveSlot(listType, definition, slot, warehouseCapacity);

    internal static bool IsCreatureMoveSlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        CreatureInventoryMovePlanner.IsCreatureMoveSlotValid(listType, slot, warehouseCapacity);

    internal static Dictionary<ushort, CharacterItemRecord> GetCreatureMoveSpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> warehouse,
        Dictionary<ushort, CharacterItemRecord> creatureInventory) =>
        CreatureInventoryMovePlanner.GetCreatureMoveSpace(listType, warehouse, creatureInventory);

    internal static ushort MapCreatureEndpointSlot(
        byte listType,
        ushort slot) =>
        CreatureInventoryMovePlanner.MapCreatureEndpointSlot(listType, slot);

    internal static ushort MapCreatureStorageSlotToClient(
        ushort slot) =>
        CreatureInventoryMovePlanner.MapCreatureStorageSlotToClient(slot);

    public decimal GetEquippedCreatureExperienceBonusRate(
    ConnectionContext context,
    byte creatureLevel)
    {
        decimal bonusRate = 0;
        foreach (var artifactSlot in new[]
                 {
                 CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                 CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                 CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot
             })
        {
            if (!context.State.Inventory.Creature.TryGetValue(artifactSlot, out var artifact)
                || !itemCatalog.TryGetDefinition(
                    artifact.ItemId,
                    out var definition)
                || !CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                    definition,
                    out var expectedSlot)
                || expectedSlot != artifactSlot
                || definition.CreatureExperienceAmountRate is not decimal rate
                || definition.CreatureMinimumLevel is int minimumLevel
                    && minimumLevel > 0
                    && creatureLevel < minimumLevel)
            {
                continue;
            }

            bonusRate += rate;
        }

        return bonusRate;
    }
}
