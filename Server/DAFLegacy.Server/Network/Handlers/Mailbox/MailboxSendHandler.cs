using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// MAILBOX_SEND (CMD 97): validates gold/attachment, persists the mail and refreshes the sender's inventory delta (07 §5 group 6).
/// </summary>
public sealed class MailboxSendHandler(IGameStore store,
    ItemCatalog itemCatalog,
    CharacterSessionRegistry characterSessions,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.MAILBOX_SEND;

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
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (!CharacterMailPostage.TryDebit(session.Currency.Gold, sendRequest.Gold,
                sendRequest.ItemId != 0, out var plannedGold))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 10);
            await context.Queue.SendAsync(error, Name, cancellationToken);
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
                await context.Queue.SendAsync(error, Name, cancellationToken);
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
                await context.Queue.SendAsync(error, Name, cancellationToken);
                return;
            }

            var isEquipment = sourceDefinition.IsEquipment;
            if (!isEquipment
                && sendRequest.CountOrValue > sourceItem.CountOrValue)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 7);
                await context.Queue.SendAsync(error, Name, cancellationToken);
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
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

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
                CharacterMailSendFailure.InsufficientGold => (byte)10,
                CharacterMailSendFailure.SenderStateChanged => (byte)17,
                CharacterMailSendFailure.RecipientNotFound => (byte)10,
                _ => (byte)7
            };
            var error = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Character.Active = sendResult.Sender;
        session.Currency.Gold = plannedGold;
        session.Inventory.Main = plannedInventory;
        var sendReply = ConnectionPacketBuilders.CreateCommandReply(
            request,
            success: true);
        await context.Queue.SendAsync(sendReply, Name, cancellationToken);

        // CMD97 success applies the client's own gold + postage debit.
        // Sync the same absolute balance afterward, including fee-only
        // letters, so this update cannot refund the postage as a gain.
        var senderUpdates = new List<GameInventoryEntry>
        {
            new(
                Slot: 0,
                ItemId: 0,
                CountOrValue: checked((uint)session.Currency.Gold))
        };

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
            await context.Queue.SendAsync(inventoryUpdate, Name, cancellationToken);
        }

        // Mailing attachments away can unsatisfy seeking quests; keep the
        // stored trigger in step with the inventory delta above.
        await senders.SendSeekingQuestTriggerUpdatesAsync(
            context,
            Name,
            cancellationToken);

        // The registry callback enqueues the mail alarm event for the target
        // session; a self-addressed mail therefore alarms through the owning
        // connection's event drain exactly like any other recipient (R6).
        characterSessions.NotifyMailReceived(
            sendResult.RecipientCharacterId);
    }
}
