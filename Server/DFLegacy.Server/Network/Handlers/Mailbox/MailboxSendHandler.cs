using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// MAILBOX_SEND (CMD 97): validates gold/attachment, persists the mail and refreshes the sender's inventory delta (07 §5 group 6).
/// </summary>
public sealed class MailboxSendHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    CharacterSessionRegistry characterSessions,
    CharacterPacketBuilders builders) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.MailboxSendCommand;

    public string Name => "MAILBOXSEND";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || !MailboxParsers.TryReadMailboxSend(request.Body, out var sendRequest))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (sendRequest.Gold > session.Currency.Gold)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 17);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedInventory = session.Inventory.Main.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        CharacterMailAttachmentRecord? attachment = null;
        ushort? sourceSlot = null;
        if (sendRequest.ItemId != 0)
        {
            if (sendRequest.ListType != 0
                || sendRequest.CountOrValue == 0
                || !plannedInventory.TryGetValue(
                    sendRequest.Slot,
                    out var sourceItem)
                || sourceItem.ItemId != sendRequest.ItemId)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 7);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                return;
            }

            if (!itemCatalog.TryGetDefinition(
                    sourceItem.ItemId,
                    out var sourceDefinition)
                || !CharacterItemSealing.CanTrade(
                    sourceItem,
                    sourceDefinition))
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 7);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                return;
            }

            var isEquipment = sourceDefinition.IsEquipment;
            if (!isEquipment
                && sendRequest.CountOrValue > sourceItem.CountOrValue)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 7);
                await context.Outbound.SendAsync(error, Name, cancellationToken);
                return;
            }

            var attachmentValue = isEquipment
                ? sourceItem.CountOrValue
                : sendRequest.CountOrValue;
            attachment = new CharacterMailAttachmentRecord(
                sourceItem.ItemId,
                attachmentValue,
                sourceItem.State,
                sourceItem.Durability,
                sourceItem.SealState,
                EquipmentQualitySeed:
                    sourceItem.EquipmentQualitySeed,
                InstanceId: sourceItem.InstanceId);
            sourceSlot = sourceItem.Slot;
            var remaining = isEquipment
                ? 0
                : sourceItem.CountOrValue - attachmentValue;
            if (remaining == 0)
            {
                plannedInventory.Remove(sourceItem.Slot);
            }
            else
            {
                plannedInventory[sourceItem.Slot] = sourceItem with
                {
                    CountOrValue = remaining
                };
            }
        }
        else if (sendRequest.ListType != 0
            || sendRequest.Slot != 0
            || sendRequest.CountOrValue != 0)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 7);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var plannedGold = session.Currency.Gold - sendRequest.Gold;
        var sendResult = await store.SendCharacterMailFromCharacterAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            sendRequest.Recipient,
            new CreateCharacterMailRequest(
                session.Character.Active.Name,
                sendRequest.Text,
                sendRequest.Gold,
                attachment),
            plannedGold,
            plannedInventory.Values,
            session.Inventory.Equipped.Values,
            cancellationToken);
        if (!sendResult.Sent || sendResult.Sender is null)
        {
            var errorCode = sendResult.Failure switch
            {
                CharacterMailSendFailure.RecipientMailboxFull => (byte)90,
                CharacterMailSendFailure.SenderStateChanged => (byte)17,
                CharacterMailSendFailure.RecipientNotFound => (byte)10,
                _ => (byte)7
            };
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = sendResult.Sender;
        session.Currency.Gold = plannedGold;
        session.Inventory.Main = plannedInventory;
        var sendReply = ConnectionPacketBuilders.CreateCommandReply(
            request,
            success: true);
        await context.Outbound.SendAsync(sendReply, Name, cancellationToken);

        var senderUpdates = new List<GameInventoryEntry>();
        if (sendRequest.Gold != 0)
        {
            senderUpdates.Add(new GameInventoryEntry(
                Slot: 0,
                ItemId: 0,
                CountOrValue: checked((uint)session.Currency.Gold)));
        }

        if (sourceSlot.HasValue)
        {
            senderUpdates.Add(session.Inventory.Main.TryGetValue(
                    sourceSlot.Value,
                    out var remainingItem)
                ? builders.ToGameInventoryEntry(remainingItem)
                : new GameInventoryEntry(
                    sourceSlot.Value,
                    ushort.MaxValue,
                    0));
        }

        if (senderUpdates.Count > 0)
        {
            var inventoryUpdate = InventoryPacketBuilders.CreateUpdateItemList(
                0,
                senderUpdates);
            await context.Outbound.SendAsync(inventoryUpdate, Name, cancellationToken);
        }

        // The registry callback enqueues the mail alarm event for the target
        // session; a self-addressed mail therefore alarms through the owning
        // connection's event drain exactly like any other recipient (R6).
        characterSessions.NotifyMailReceived(
            sendResult.RecipientCharacterId);
    }
}
