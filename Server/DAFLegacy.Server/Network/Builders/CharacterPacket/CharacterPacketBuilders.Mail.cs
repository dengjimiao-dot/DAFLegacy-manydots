using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Mail builders and attachment planning (07 §4.7): overflow-mail
/// requests, the wire mail projection and attachment placement.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal static CreateCharacterMailRequest[] CreateOverflowMailRequests(
        IEnumerable<CharacterMailAttachmentRecord> items,
        SystemItemMailReason reason) =>
        items.Select(item => SystemMailTemplates.CreateItemReturn(item, reason))
            .ToArray();

    internal GameMailEntry ToGameMailEntry(CharacterMailRecord mail)
    {
        GameMailAttachment? attachment = null;
        if (mail.Attachment is not null)
        {
            var wire = CharacterItemWireProjection.Project(
                mail.Attachment,
                itemCatalog);
            attachment = new GameMailAttachment(
                mail.Attachment.ItemId,
                wire.AddInfo,
                wire.ItemAttr,
                wire.Durability,
                wire.SealState);
        }

        return new GameMailEntry(
            mail.Id,
            EncodeMailField(mail.Sender, 29, SystemMailTemplates.SenderName),
            EncodeMailField(mail.Text, 511, string.Empty),
            checked((uint)Math.Max(0, mail.Gold)),
            attachment,
            mail.SentAt,
            mail.ExpiresAt,
            mail.State is 2 or 3 ? mail.State : (ushort)1);
    }

    internal static byte[] EncodeMailField(
        string value,
        int maximumBytes,
        string fallback)
    {
        value ??= fallback;
        var output = new byte[maximumBytes];
        ClientEncoding.GetEncoder().Convert(
            value.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        if (bytesUsed == 0 && fallback.Length != 0)
        {
            return ClientEncoding.GetBytes(fallback);
        }

        return output.AsSpan(0, bytesUsed).ToArray();
    }

    // ---- Town presence builders (migrated verbatim from EntranceService).

    internal static bool TryPlanMailAttachment(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        CharacterMailAttachmentRecord? attachment,
        ItemCatalog catalog,
        uint weightLimit,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedAvatarInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedCreatureInventory,
        out byte? destinationListType,
        out ushort? destinationSlot)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        plannedAvatarInventory = avatarInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedCreatureInventory = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        destinationListType = null;
        destinationSlot = null;
        if (attachment is null)
        {
            return true;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                attachment.ItemId,
                out _))
        {
            destinationListType = 0;
            return true;
        }

        if (!catalog.TryGetDefinition(attachment.ItemId, out var definition))
        {
            return false;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            if (!CharacterAvatarInventoryPlanner.TryPlace(
                    avatarInventory,
                    attachment,
                    catalog,
                    out var avatarPlacement,
                    out _))
            {
                return false;
            }

            plannedAvatarInventory = avatarPlacement.Inventory;
            destinationListType = 1;
            destinationSlot = avatarPlacement.DestinationSlot;
            return true;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Creature)
        {
            var creatureItem = new CharacterItemRecord(
                ushort.MaxValue,
                attachment.ItemId,
                attachment.CountOrValue,
                attachment.State,
                attachment.Durability,
                attachment.SealState,
                InstanceId: attachment.InstanceId);
            if (!CharacterCreatureInventoryPlanner.TryPlace(
                    creatureInventory,
                    creatureItem,
                    catalog,
                    preferredEmptySlot: null,
                    out var creaturePlacement,
                    out _))
            {
                return false;
            }

            plannedCreatureInventory = creaturePlacement.Inventory;
            destinationListType = 7;
            destinationSlot = creaturePlacement.DestinationSlot;
            return true;
        }

        if (!CharacterInventoryPlanner.TryPlace(
                inventory,
                attachment,
                catalog,
                weightLimit,
                out var placement,
                out _))
        {
            return false;
        }

        plannedInventory = placement.Inventory;
        destinationListType = 0;
        destinationSlot = placement.DestinationSlot;
        return true;
    }
}
