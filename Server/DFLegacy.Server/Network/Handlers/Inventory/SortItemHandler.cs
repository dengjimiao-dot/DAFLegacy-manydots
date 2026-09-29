using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SORT_ITEM (CMD 20): sorts main, warehouse or creature space and pushes the rebuilt list (07 §5 group 9).
/// </summary>
public sealed class SortItemHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    ILogger<SortItemHandler> logger,
    CharacterPacketBuilders builders) : IGameCommandHandler
{

    public ushort ProtocolId => GameProtocolEngine.SortItemCommand;

    public string Name => "SortItem";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var parsedSort = InventoryParsers.TryReadSortItem(
            request.Body,
            out var itemSpace);
        if (session.Character.Active is null
            || !parsedSort
            || itemSpace is not (0 or 2 or 7))
        {
            var errorReply = GameProtocolEngine.CreateSortItemError(
                itemSpace);
            await context.Outbound.SendAsync(errorReply, Name, cancellationToken);
            logger.LogWarning(
                "Rejected item sort for item space {ItemSpace}; active character: {HasCharacter}, parsed: {Parsed}.",
                itemSpace,
                session.Character.Active is not null,
                parsedSort);
            return;
        }

        var arrangedMain = itemSpace == 0
            ? CharacterInventorySorter.SortMain(session.Inventory.Main, itemCatalog)
            : session.Inventory.Main;
        var arrangedWarehouse = itemSpace == 2
            ? CharacterInventorySorter.SortWarehouse(
                session.Inventory.Warehouse,
                itemCatalog,
                session.Inventory.WarehouseCapacity)
            : session.Inventory.Warehouse;
        var arrangedCreature = itemSpace == 7
            ? CharacterCreatureInventorySorter.Sort(session.Inventory.Creature)
            : session.Inventory.Creature;
        var savedSort = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Currency.Gold,
            arrangedMain.Values,
            session.Inventory.Equipped.Values,
            arrangedWarehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: arrangedCreature.Values);
        if (savedSort is null)
        {
            var errorReply = GameProtocolEngine.CreateSortItemError(
                itemSpace);
            await context.Outbound.SendAsync(errorReply, Name, cancellationToken);
            logger.LogWarning(
                "Could not persist item sort for character {CharacterId} in item space {ItemSpace}.",
                session.Character.Active.Id,
                itemSpace);
            return;
        }

        session.Character.Active = savedSort;
        session.Inventory.Main = arrangedMain;
        session.Inventory.Warehouse = arrangedWarehouse;
        session.Inventory.Creature = arrangedCreature;

        var sortReply = GameProtocolEngine.CreateSortItemReply(itemSpace);
        await context.Outbound.SendAsync(sortReply, Name, cancellationToken);

        // The 60CN client's incremental item path (listType
        // != 3) ignores 0xFFFF tombstones and announces adds,
        // so a relocation can only be conveyed by the full
        // list rebuild (NOTI 13), which is silent.
        var sortedPackets = itemSpace switch
        {
            0 => [builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values)],
            2 => [builders.CreateWarehousePacket(
                session.Inventory.Warehouse.Values,
                session.Inventory.WarehouseCapacity)],
            7 => builders.CreateCreatureInventoryPackets(
                    session.Inventory.Creature.Values)
                .ToArray(),
            _ => Array.Empty<GameServerPacket>()
        };
        foreach (var sortedItems in sortedPackets)
        {
            await context.Outbound.SendAsync(sortedItems, Name, cancellationToken);
        }

        logger.LogInformation(
            "Sorted item space {ItemSpace} for character {CharacterId}.",
            itemSpace,
            session.Character.Active.Id);
    }
}
