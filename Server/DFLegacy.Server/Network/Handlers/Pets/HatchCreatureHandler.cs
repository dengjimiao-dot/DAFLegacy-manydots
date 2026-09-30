using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// HATCH_CREATURE (CMD 105): hatches the creature egg at the requested slot and refreshes the creature list (07 §5 group 12).
/// </summary>
public sealed class HatchCreatureHandler(IGameStore store,
    ItemCatalog itemCatalog,
    CharacterPacketBuilders builders) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.HATCH_CREATURE;

    public string Name => "HatchCreature";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var parsedHatch = PetsParsers.TryReadHatchCreature(
            request.Body,
            out var hatchItemSpace,
            out var hatchSlot);
        var hatchFailure = string.Empty;
        CharacterCreatureHatchPlan hatchPlan = null!;
        var validHatch = session.Character.Active is not null
            && parsedHatch
            && hatchItemSpace == 7
            && CharacterCreatureInventoryPlanner.TryHatch(
                session.Inventory.Creature,
                hatchSlot,
                itemCatalog,
                out hatchPlan,
                out hatchFailure);
        if (!validHatch)
        {
            var hatchError = PetsPacketBuilders.CreateHatchCreatureReply(
                success: false,
                errorCode: 4);
            await context.Queue.SendAsync(hatchError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected Creature hatch itemSpace={ItemSpace}, slot={Slot}, parsed={Parsed}: {Failure}.",
                hatchItemSpace,
                hatchSlot,
                parsedHatch,
                session.Character.Active is null
                    ? "no active character"
                    : hatchFailure);
            return;
        }

        var hatchingCharacter = session.Character.Active!;
        var savedHatch = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            hatchingCharacter.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: hatchPlan.Inventory.Values);
        if (savedHatch is null)
        {
            var hatchError = PetsPacketBuilders.CreateHatchCreatureReply(
                success: false,
                errorCode: 2);
            await context.Queue.SendAsync(hatchError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Could not persist Creature hatch at slot {Slot} for character {CharacterId}.",
                hatchSlot,
                hatchingCharacter.Id);
            return;
        }

        session.Character.Active = savedHatch;
        session.Inventory.Creature = hatchPlan.Inventory;
        var hatchReply = PetsPacketBuilders.CreateHatchCreatureReply(
            success: true);
        await context.Queue.SendAsync(hatchReply, Name, cancellationToken);
        foreach (var creaturePacket in builders.CreateCreatureInventoryPackets(
                     session.Inventory.Creature.Values))
        {
            await context.Queue.SendAsync(creaturePacket, Name, cancellationToken);
        }

        RuntimeLogger.Info(
            "Hatched Creature egg {EggItemId} into {CreatureItemId} at slot {Slot} for character {CharacterId}.",
            hatchPlan.EggItemId,
            hatchPlan.CreatureItemId,
            hatchPlan.Slot,
            session.Character.Active.Id);
    }
}
