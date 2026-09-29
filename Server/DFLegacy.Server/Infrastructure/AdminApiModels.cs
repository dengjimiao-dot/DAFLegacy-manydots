using DFLegacy.Protocol;

namespace DFLegacy.Server;

public enum AdminMailAttachmentKind : byte
{
    None = 0,
    Item = 1,
    Avatar = 2,
    CreatureEgg = 3
}

public sealed record AdminSendMailRequest(
    string Sender,
    string Text,
    int Gold = 0,
    AdminMailAttachmentKind AttachmentKind = AdminMailAttachmentKind.None,
    ushort? ItemId = null,
    uint Quantity = 1,
    ushort AvatarAbilityIndex = 0);

public sealed record AdminBroadcastMessageRequest(
    string Message,
    GameMessageType MessageType = GameMessageType.Type16Brown,
    ushort TargetAreaUserId = 0);

public sealed record AdminBroadcastPopupRequest(string Message);

public sealed record AdminSetDungeonDifficultyRequest(byte MaximumDifficulty);

public static class AdminMailComposer
{
    public static bool TryCompose(
        AdminSendMailRequest request,
        ItemCatalog catalog,
        out CreateCharacterMailRequest mail,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);

        mail = null!;
        error = ValidateEnvelope(request);
        if (error is not null)
        {
            return false;
        }

        if (request.AttachmentKind == AdminMailAttachmentKind.None)
        {
            mail = new CreateCharacterMailRequest(
                request.Sender,
                request.Text,
                request.Gold);
            return true;
        }

        if (request.ItemId is not ushort itemId || itemId == 0)
        {
            error = "An attachment item id is required.";
            return false;
        }

        if (!catalog.TryGetDefinition(itemId, out var definition))
        {
            error = $"Item {itemId} is not present in the loaded PVF.";
            return false;
        }

        var attachmentKind = GetAttachmentKind(definition);
        var isAvatar = attachmentKind == AdminMailAttachmentKind.Avatar;
        var isCreatureEgg = attachmentKind == AdminMailAttachmentKind.CreatureEgg;
        if (CharacterCreatureInventoryLayout.IsCreature(definition)
            && !isCreatureEgg)
        {
            error = $"Item {itemId} is a hatched Creature; only Creature eggs can be mailed.";
            return false;
        }
        if (request.AttachmentKind == AdminMailAttachmentKind.Avatar && !isAvatar)
        {
            error = $"Item {itemId} is not an Avatar equipment item.";
            return false;
        }

        if (request.AttachmentKind == AdminMailAttachmentKind.CreatureEgg
            && !isCreatureEgg)
        {
            error = $"Item {itemId} is not a Creature egg.";
            return false;
        }

        if (request.AttachmentKind == AdminMailAttachmentKind.Item
            && (isAvatar || isCreatureEgg))
        {
            error = isAvatar
                ? $"Item {itemId} must be sent as Avatar equipment."
                : $"Item {itemId} must be sent as a Creature egg.";
            return false;
        }

        if (request.Quantity == 0)
        {
            error = "Attachment quantity must be greater than zero.";
            return false;
        }

        if (!definition.IsStackable && request.Quantity != 1)
        {
            error = "Non-stackable equipment can only be sent one item per mail.";
            return false;
        }

        var attachment = CharacterItemIdentity.Ensure(
            new CharacterMailAttachmentRecord(
                itemId,
                request.Quantity,
                Durability: definition.InventoryCategory == ItemInventoryCategory.Equipment
                    ? catalog.GetInitialDurability(itemId)
                    : (ushort)0,
                SealState: definition.AttachType == ItemAttachType.Sealing
                    ? (byte)1
                    : (byte)0,
                // DF2008 reads this as a remaining duration. Zero means permanent.
                AvatarRemainingSeconds: isAvatar ? 0u : null,
                AvatarAbilityIndex: isAvatar ? request.AvatarAbilityIndex : null),
            definition);
        mail = new CreateCharacterMailRequest(
            request.Sender,
            request.Text,
            request.Gold,
            attachment);
        return true;
    }

    public static AdminMailAttachmentKind GetAttachmentKind(ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            return AdminMailAttachmentKind.Avatar;
        }

        return CharacterCreatureInventoryLayout.IsCreature(definition)
            && definition.CreatureSubType == 1
                ? AdminMailAttachmentKind.CreatureEgg
                : AdminMailAttachmentKind.Item;
    }

    public static bool IsSupportedAttachment(ItemDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return !CharacterCreatureInventoryLayout.IsCreature(definition)
            || definition.CreatureSubType == 1;
    }

    private static string? ValidateEnvelope(AdminSendMailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sender) || request.Sender.Length > 20)
        {
            return "Mail sender length must be 1-20 characters.";
        }

        if (request.Text is null)
        {
            return "Mail text must not be null.";
        }

        if (request.Text.Length > 255)
        {
            return "Mail text must not exceed 255 characters.";
        }

        if (request.Gold < 0)
        {
            return "Mail gold must not be negative.";
        }

        return Enum.IsDefined(request.AttachmentKind)
            ? null
            : "AttachmentKind is not supported.";
    }
}
