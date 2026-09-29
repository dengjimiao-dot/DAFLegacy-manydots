using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Creature-space senders and the Creature-move planner cluster
/// (07 §4.7): equipping flows, move deltas and the move-plan search.
/// </summary>
public sealed partial class ChannelSessionSenders
{
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

    var died = GameProtocolEngine.CreateDiedCreature(context.State.Character.LocalUserId);
    await context.Outbound.SendAsync(died, "CREATURE_DIED", cancellationToken);
    context.State.Weakness.CreatureDeathNotificationsSent.Add(creatureUid);
    logger.LogInformation(
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

    var creatureState = GameProtocolEngine.CreateCreatureState(
        equippedCreature.CountOrValue,
        (uint)Math.Clamp(
            (int)(equippedCreature.CreatureStomach ?? 100),
            0,
            CreatureHungerPlanner.MaximumStomach));
    await context.Outbound.SendAsync(creatureState, "CREATURE_STATE", cancellationToken);
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
        GameProtocolEngine.CreateCreatureResponse(context.State.Character.LocalUserId);
    await context.Outbound.SendAsync(creatureResponse, "CREATURE_RESPONSE", cancellationToken);
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
    var noOpReply = GameProtocolEngine.CreateMoveItemSpaceReply(
        sourceListType,
        sourceSlot,
        movedCount: 0,
        destinationListType,
        destinationSlot);
    await context.Outbound.SendAsync(noOpReply, "INVENTORY_DELTA", cancellationToken);

    if (sourceListType == 0 || destinationListType == 0)
    {
        var mainRefresh = builders.CreateMainInventoryPacket(
            context.State.Currency.Gold,
            context.State.Currency.VictoryPoints,
            context.State.Inventory.Main.Values);
        await context.Outbound.SendAsync(mainRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 2 || destinationListType == 2)
    {
        var warehouseRefresh = builders.CreateWarehousePacket(
            context.State.Inventory.Warehouse.Values,
            context.State.Inventory.WarehouseCapacity);
        await context.Outbound.SendAsync(warehouseRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 1 || destinationListType == 1)
    {
        var avatarRefresh = builders.CreateAvatarInventoryPacket(
            context.State.Inventory.Avatar.Values);
        await context.Outbound.SendAsync(avatarRefresh, "INVENTORY_DELTA", cancellationToken);
    }

    if (sourceListType == 7 || destinationListType == 7)
    {
        foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                     context.State.Inventory.Creature.Values))
        {
            await context.Outbound.SendAsync(creatureRefresh, "INVENTORY_DELTA", cancellationToken);
        }
    }

    if ((sourceListType == 3 || destinationListType == 3)
        && sourceListType != 7
        && destinationListType != 7)
    {
        await SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
    }

    logger.LogInformation(
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
        var update = GameProtocolEngine.CreateUpdateItemList(0, [entry]);
        await context.Outbound.SendAsync(update, "INVENTORY_DELTA", cancellationToken);
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
        out string failure)
    {
        plan = null!;
        failure = string.Empty;
        if (sourceListType is not (2 or 3 or 7)
            || destinationListType is not (2 or 3 or 7)
            || sourceListType != 7 && destinationListType != 7)
        {
            failure = "Creature space only supports its native equipment slots and character storage";
            return false;
        }

        if (!IsCreatureMoveSlotValid(sourceListType, sourceSlot, warehouseCapacity)
            || destinationSlot != ushort.MaxValue
                && !IsCreatureMoveSlotValid(
                    destinationListType,
                    destinationSlot,
                    warehouseCapacity))
        {
            failure = "Creature move contains an invalid slot";
            return false;
        }

        var plannedWarehouse = warehouse.ToDictionary(pair => pair.Key, pair => pair.Value);
        var plannedCreature = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var actualSourceList = sourceListType;
        var actualSourceClientSlot = sourceSlot;
        var actualDestinationList = destinationListType;
        var actualDestinationClientSlot = destinationSlot;
        var actualSourceStorageSlot = MapCreatureEndpointSlot(
            actualSourceList,
            actualSourceClientSlot);
        var actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
            ? ushort.MaxValue
            : MapCreatureEndpointSlot(
                actualDestinationList,
                actualDestinationClientSlot);
        var sourceSpace = GetCreatureMoveSpace(
            actualSourceList,
            plannedWarehouse,
            plannedCreature);
        var destinationSpace = GetCreatureMoveSpace(
            actualDestinationList,
            plannedWarehouse,
            plannedCreature);
        if (!sourceSpace.TryGetValue(actualSourceStorageSlot, out var sourceItem))
        {
            if (actualDestinationStorageSlot == ushort.MaxValue
                || !destinationSpace.TryGetValue(
                    actualDestinationStorageSlot,
                    out sourceItem))
            {
                failure = "Creature move source is empty";
                return false;
            }

            (actualSourceList, actualDestinationList) =
                (actualDestinationList, actualSourceList);
            (actualSourceClientSlot, actualDestinationClientSlot) =
                (actualDestinationClientSlot, actualSourceClientSlot);
            (actualSourceStorageSlot, actualDestinationStorageSlot) =
                (actualDestinationStorageSlot, actualSourceStorageSlot);
            sourceSpace = GetCreatureMoveSpace(
                actualSourceList,
                plannedWarehouse,
                plannedCreature);
            destinationSpace = GetCreatureMoveSpace(
                actualDestinationList,
                plannedWarehouse,
                plannedCreature);
        }

        if (!itemCatalog.TryGetDefinition(sourceItem.ItemId, out var sourceDefinition))
        {
            failure = "Creature move item is unknown";
            return false;
        }

        if (actualSourceList == 3 || actualDestinationList == 3)
        {
            sourceItem = CharacterItemSealing.UnsealWhenEquipped(
                sourceItem,
                sourceDefinition);
        }

        if (actualDestinationClientSlot == ushort.MaxValue)
        {
            actualDestinationClientSlot = FindCreatureMoveDestination(
                destinationSpace,
                actualDestinationList,
                sourceItem,
                sourceDefinition,
                actualSourceList == actualDestinationList
                    ? actualSourceClientSlot
                    : null,
                warehouseCapacity);
            actualDestinationStorageSlot = actualDestinationClientSlot == ushort.MaxValue
                ? ushort.MaxValue
                : MapCreatureEndpointSlot(
                    actualDestinationList,
                    actualDestinationClientSlot);
        }

        if (actualDestinationClientSlot == ushort.MaxValue
            || !IsCreatureMoveSlotValid(
                actualDestinationList,
                actualDestinationClientSlot,
                warehouseCapacity)
            || !IsCompatibleCreatureMoveSlot(
                actualDestinationList,
                sourceDefinition,
                actualDestinationClientSlot,
                warehouseCapacity))
        {
            failure = "Creature move destination is full or incompatible";
            return false;
        }

        if (actualSourceList == actualDestinationList
            && actualSourceStorageSlot == actualDestinationStorageSlot)
        {
            plan = new CreatureInventoryMovePlan(
                plannedWarehouse,
                plannedCreature,
                sourceDefinition.IsStackable ? sourceItem.CountOrValue : 1,
                destinationSlot == ushort.MaxValue
                    ? actualDestinationClientSlot
                    : destinationSlot,
                sourceItem.ItemId,
                EquippedSlotsChanged: false,
                EquippedCreatureChanged: false);
            return true;
        }

        destinationSpace.TryGetValue(actualDestinationStorageSlot, out var displacedItem);
        if (displacedItem is not null
            && (!itemCatalog.TryGetDefinition(
                    displacedItem.ItemId,
                    out var displacedDefinition)
                || !IsCompatibleCreatureMoveSlot(
                    actualSourceList,
                    displacedDefinition,
                    actualSourceClientSlot,
                    warehouseCapacity)))
        {
            failure = "Creature move would displace an incompatible item";
            return false;
        }

        if (displacedItem is not null
            && (actualSourceList == 3 || actualDestinationList == 3))
        {
            displacedItem = CharacterItemSealing.UnsealWhenEquipped(
                displacedItem,
                itemCatalog);
        }

        var availableQuantity = sourceDefinition.IsStackable
            ? sourceItem.CountOrValue
            : 1u;
        var requestedQuantity = moveCount <= 0
            ? availableQuantity
            : Math.Min(availableQuantity, checked((uint)moveCount));
        if (requestedQuantity == 0)
        {
            failure = "Creature move count is zero";
            return false;
        }

        var canStack = sourceDefinition.IsStackable
            && displacedItem is not null
            && displacedItem.ItemId == sourceItem.ItemId
            && displacedItem.State == sourceItem.State
            && displacedItem.Durability == sourceItem.Durability
            && displacedItem.SealState == sourceItem.SealState;
        if (canStack)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(sourceDefinition);
            var available = stackLimit > displacedItem!.CountOrValue
                ? stackLimit - displacedItem.CountOrValue
                : 0;
            if (available == 0)
            {
                failure = "Creature destination stack is full";
                return false;
            }

            requestedQuantity = Math.Min(requestedQuantity, available);
            destinationSpace[actualDestinationStorageSlot] = displacedItem with
            {
                CountOrValue = displacedItem.CountOrValue + requestedQuantity
            };
            if (requestedQuantity == sourceItem.CountOrValue)
            {
                sourceSpace.Remove(actualSourceStorageSlot);
            }
            else
            {
                sourceSpace[actualSourceStorageSlot] = sourceItem with
                {
                    CountOrValue = sourceItem.CountOrValue - requestedQuantity
                };
            }
        }
        else if (sourceDefinition.IsStackable
            && displacedItem is null
            && requestedQuantity < sourceItem.CountOrValue)
        {
            sourceSpace[actualSourceStorageSlot] = sourceItem with
            {
                CountOrValue = sourceItem.CountOrValue - requestedQuantity
            };
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot,
                CountOrValue = requestedQuantity
            };
        }
        else
        {
            sourceSpace.Remove(actualSourceStorageSlot);
            destinationSpace[actualDestinationStorageSlot] = sourceItem with
            {
                Slot = actualDestinationStorageSlot
            };
            if (displacedItem is not null)
            {
                sourceSpace[actualSourceStorageSlot] = displacedItem with
                {
                    Slot = actualSourceStorageSlot
                };
            }
        }

        var normalizedCreature = CharacterCreatureInventoryPlanner.Normalize(
            plannedCreature.Values,
            itemCatalog);
        if (normalizedCreature.OverflowItems.Count != 0)
        {
            failure = "Creature move normalization overflowed";
            return false;
        }

        var equippedChanged = Enumerable.Range(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.Capacity
                    - CharacterCreatureInventoryLayout.EquippedCreatureSlot)
            .Select(checkedSlot => checked((ushort)checkedSlot))
            .Any(slot => !Equals(
                creatureInventory.GetValueOrDefault(slot),
                normalizedCreature.Inventory.GetValueOrDefault(slot)));
        var equippedCreatureChanged = !Equals(
            creatureInventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot),
            normalizedCreature.Inventory.GetValueOrDefault(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot));
        plan = new CreatureInventoryMovePlan(
            plannedWarehouse,
            normalizedCreature.Inventory,
            requestedQuantity,
            destinationSlot == ushort.MaxValue
                ? actualDestinationClientSlot
                : destinationSlot,
            sourceItem.ItemId,
            equippedChanged,
            equippedCreatureChanged);
        return true;
    }

    internal ushort FindCreatureMoveDestination(
        IReadOnlyDictionary<ushort, CharacterItemRecord> destination,
        byte destinationListType,
        CharacterItemRecord item,
        ItemDefinition definition,
        ushort? excludedSlot,
        ushort warehouseCapacity)
    {
        if (destinationListType == 3)
        {
            if (!CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                    definition,
                    out var equippedStorageSlot)
                || destination.ContainsKey(equippedStorageSlot))
            {
                return ushort.MaxValue;
            }

            return MapCreatureStorageSlotToClient(equippedStorageSlot);
        }

        if (definition.IsStackable)
        {
            var stackLimit = CharacterInventoryPlanner.GetStackLimit(definition);
            var partial = destination.Values
                .Where(candidate => candidate.Slot != excludedSlot
                    && candidate.ItemId == item.ItemId
                    && candidate.State == item.State
                    && candidate.Durability == item.Durability
                    && candidate.SealState == item.SealState
                    && candidate.CountOrValue < stackLimit
                    && IsCompatibleCreatureMoveSlot(
                        destinationListType,
                        definition,
                        candidate.Slot,
                        warehouseCapacity))
                .OrderBy(candidate => candidate.Slot)
                .Select(candidate => candidate.Slot)
                .DefaultIfEmpty(ushort.MaxValue)
                .First();
            if (partial != ushort.MaxValue)
            {
                return partial;
            }
        }

        if (destinationListType == 2)
        {
            return FindFreeInventorySlot(
                destination,
                0,
                warehouseCapacity);
        }

        if (!CharacterCreatureInventoryLayout.TryGetBagRange(
                definition,
                out var start,
                out var end))
        {
            return ushort.MaxValue;
        }

        return FindFreeInventorySlot(destination, start, end);
    }

    internal bool IsCompatibleCreatureMoveSlot(
        byte listType,
        ItemDefinition definition,
        ushort slot,
        ushort warehouseCapacity) => listType switch
    {
        2 => definition.InventoryCategory != ItemInventoryCategory.Avatar
            && slot < warehouseCapacity,
        3 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.TryGetEquippedSlot(
                definition,
                out var equippedSlot)
            && equippedSlot == MapCreatureEndpointSlot(3, slot),
        7 => definition.InventoryCategory == ItemInventoryCategory.Creature
            && CharacterCreatureInventoryLayout.IsCompatibleSlot(definition, slot),
        _ => false
    };

    internal static bool IsCreatureMoveSlotValid(
        byte listType,
        ushort slot,
        ushort warehouseCapacity) =>
        listType switch
        {
            2 => slot < warehouseCapacity,
            3 => slot is >= CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                and <= CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
            7 => CharacterCreatureInventoryLayout.IsInventorySlot(slot),
            _ => false
        };

    internal static Dictionary<ushort, CharacterItemRecord> GetCreatureMoveSpace(
        byte listType,
        Dictionary<ushort, CharacterItemRecord> warehouse,
        Dictionary<ushort, CharacterItemRecord> creatureInventory) =>
        listType is 3 or 7 ? creatureInventory : warehouse;

    internal static ushort MapCreatureEndpointSlot(byte listType, ushort slot) =>
        listType == 3
            ? slot switch
            {
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot =>
                    CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot =>
                    CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                _ => ushort.MaxValue
            }
            : slot;

    internal static ushort MapCreatureStorageSlotToClient(ushort slot) => slot switch
    {
        CharacterCreatureInventoryLayout.EquippedCreatureSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactRedSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot,
        CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot =>
            CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot,
        _ => ushort.MaxValue
    };

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
