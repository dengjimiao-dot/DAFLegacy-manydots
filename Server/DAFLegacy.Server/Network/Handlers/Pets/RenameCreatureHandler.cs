using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// RENAME_CREATURE (CMD 103): applies the rename card to the equipped Creature and refreshes the creature list (07 §5 group 12).
/// </summary>
public sealed class RenameCreatureHandler(IGameStore store,
    ItemCatalog itemCatalog,
    CharacterPacketBuilders builders) : IGameCommandHandler
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.RENAME_CREATURE;

    public string Name => "RenameCreature";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var parsedRename = PetsParsers.TryReadRenameCreature(
            request.Body,
            out var renameCardSlot,
            out var renameItemSpace,
            out var renameNameBytes);
        var requestedCreatureName = parsedRename
            ? ClientEncoding.GetString(renameNameBytes)
            : string.Empty;
        var renameFailure = string.Empty;
        CharacterCreatureRenamePlan renamePlan = null!;
        var validRename = session.Character.Active is not null
            && parsedRename
            && renameItemSpace == 7
            && CharacterCreatureInventoryPlanner.TryRename(
                session.Inventory.Creature,
                renameCardSlot,
                requestedCreatureName,
                itemCatalog,
                out renamePlan,
                out renameFailure);
        if (!validRename)
        {
            var renameError =
                PetsPacketBuilders.CreateRenameCreatureReply(
                    success: false,
                    errorCode: 4);
            await context.Queue.SendAsync(renameError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected Creature rename itemSpace={ItemSpace}, cardSlot={CardSlot}, parsed={Parsed}: {Failure}.",
                renameItemSpace,
                renameCardSlot,
                parsedRename,
                session.Character.Active is null
                    ? "no active character"
                    : renameFailure);
            return;
        }

        var renamingCharacter = session.Character.Active!;
        var savedRename = await store.SaveCharacterItemSpacesAsync(
            session.Login.AccountName,
            renamingCharacter.Id,
            session.Currency.Gold,
            session.Inventory.Main.Values,
            session.Inventory.Equipped.Values,
            session.Inventory.Warehouse.Values,
            cancellationToken,
            avatarInventory: session.Inventory.Avatar.Values,
            creatureInventory: renamePlan.Inventory.Values);
        if (savedRename is null)
        {
            var renameError =
                PetsPacketBuilders.CreateRenameCreatureReply(
                    success: false,
                    errorCode: 2);
            await context.Queue.SendAsync(renameError, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Could not persist Creature rename using card slot {CardSlot} for character {CharacterId}.",
                renameCardSlot,
                renamingCharacter.Id);
            return;
        }

        session.Character.Active = savedRename;
        session.Inventory.Creature = renamePlan.Inventory;
        var renameNotification =
            PetsPacketBuilders.CreateCreatureRename(
                session.Character.LocalUserId,
                builders.EncodeCreatureName(
                    renamePlan.CreatureName,
                    session.Inventory.Creature[
                        CharacterCreatureInventoryLayout
                            .EquippedCreatureSlot].ItemId));
        await context.Queue.SendAsync(renameNotification, Name, cancellationToken);

        var renameReply =
            PetsPacketBuilders.CreateRenameCreatureReply(
                success: true,
                renameCardSlot,
                renameItemSpace);
        await context.Queue.SendAsync(renameReply, Name, cancellationToken);
        foreach (var creaturePacket in builders.CreateCreatureInventoryPackets(
                     session.Inventory.Creature.Values))
        {
            await context.Queue.SendAsync(creaturePacket, Name, cancellationToken);
        }

        RuntimeLogger.Info(
            "Renamed equipped Creature uid={CreatureUid} to {CreatureName} using card slot {CardSlot} for character {CharacterId}.",
            renamePlan.CreatureUid,
            renamePlan.CreatureName,
            renameCardSlot,
            session.Character.Active.Id);
    }
}
