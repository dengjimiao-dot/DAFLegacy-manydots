using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// MAILBOX_EXTRACT (CMD 98): claims the mail, places gold and the attachment into the matching inventory space and refreshes the affected lists (07 §5 group 6).
/// </summary>
public sealed class MailboxExtractHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.MailboxExtractItemCommand;

    public string Name => "MAILBOXEXTRACT";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !MailboxParsers.TryReadMailboxExtract(request.Body, out var mailId))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var mail = (await store.GetCharacterMailsAsync(
                session.Character.Active.Id,
                cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == mailId);
        if (mail is null
            || mail.Gold == 0 && mail.Attachment is null
            || mail.Gold > int.MaxValue - session.Currency.Gold
            || !CharacterPacketBuilders.TryPlanMailAttachment(
                session.Inventory.Main,
                session.Inventory.Avatar,
                session.Inventory.Creature,
                mail.Attachment,
                itemCatalog,
                senders.GetInventoryWeightLimit(context, session.Character.Active),
                out var plannedInventory,
                out var plannedAvatarInventory,
                out var plannedCreatureInventory,
                out var mailDestinationListType,
                out var destinationSlot))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: mail is null ? (byte)10 : (byte)4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedGold = checked(session.Currency.Gold + mail.Gold);
        var mailUpgradeTarget = session.Inventory.WarehouseCapacity;
        var isWarehouseUpgradeAttachment = mail.Attachment is { } attachment
            && CharacterWarehouseProgression.TryGetItemTargetCapacity(
                attachment.ItemId,
                out mailUpgradeTarget);
        var plannedWarehouseCapacity = isWarehouseUpgradeAttachment
            ? Math.Max(session.Inventory.WarehouseCapacity, mailUpgradeTarget)
            : session.Inventory.WarehouseCapacity;
        var claim = await store.ClaimCharacterMailAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            mail.Id,
            plannedGold,
            plannedInventory.Values,
            session.Inventory.Equipped.Values,
            cancellationToken,
            plannedAvatarInventory.Values,
            plannedCreatureInventory.Values,
            warehouseCapacity: plannedWarehouseCapacity,
            expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
        if (claim is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var warehouseCapacityChanged =
            senders.AdoptSavedWarehouseUpgradeState(context, claim.Character);
        session.Currency.Gold = plannedGold;
        session.Inventory.Avatar = (claim.Character.AvatarInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = (claim.Character.CreatureInventory ?? [])
            .ToDictionary(item => item.Slot);
        var extractReply = MailboxPacketBuilders.CreateMailboxExtractReply(mail.Id);
        await context.Outbound.SendAsync(extractReply, Name, cancellationToken);

        if (mail.Gold != 0 || mailDestinationListType == 0)
        {
            var inventoryUpdate = builders.CreateMainInventoryPacket(
                session.Currency.Gold,
                session.Currency.VictoryPoints,
                session.Inventory.Main.Values,
                destinationSlot);
            await context.Outbound.SendAsync(inventoryUpdate, Name, cancellationToken);
        }

        if (mailDestinationListType == 1)
        {
            var avatarUpdate = builders.CreateAvatarInventoryPacket(
                session.Inventory.Avatar.Values);
            await context.Outbound.SendAsync(avatarUpdate, Name, cancellationToken);
        }

        if (mailDestinationListType == 7)
        {
            foreach (var creatureUpdate in
                     builders.CreateCreatureInventoryPackets(
                         session.Inventory.Creature.Values))
            {
                await context.Outbound.SendAsync(creatureUpdate, Name, cancellationToken);
            }
        }

        if (warehouseCapacityChanged
            || isWarehouseUpgradeAttachment)
        {
            await senders.SendWarehouseStateAsync(context, cancellationToken);
        }

    }
}
