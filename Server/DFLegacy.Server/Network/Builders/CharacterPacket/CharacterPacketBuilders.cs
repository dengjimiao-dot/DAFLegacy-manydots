using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Character-flow packet builders (07 §4.7, organized along the S4A21
/// InitPacketBuilderRegistry idea). Pure orchestration over the store and
/// item catalogs; holds no per-connection state. Split into partial files by
/// functional block: Roster, Skills, Inventory, Initialization, Creature,
/// Town, Mail, Premium; this file keeps the shared encoding and item
/// projection helpers.
/// </summary>
public sealed partial class CharacterPacketBuilders(
    IGameStore store,
    ItemCatalog itemCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CeraShopCatalog ceraShopCatalog)
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort EquippedTitleSlot = 10;

    internal static byte[] EncodeCharacterName(string name)
    {
        var bytes = ClientEncoding.GetBytes(name);
        if (bytes.Length is > 0 and < 31)
        {
            return bytes;
        }

        return Encoding.ASCII.GetBytes(name.Length == 0 ? "DFLegacy" : name[..Math.Min(name.Length, 20)]);
    }

    internal GameInventoryEntry ToGameInventoryEntry(CharacterItemRecord item)
    {
        var wire = CharacterItemWireProjection.Project(item, itemCatalog);
        return new GameInventoryEntry(
            item.Slot,
            item.ItemId,
            wire.AddInfo,
            wire.ItemAttr,
            wire.Durability,
            wire.SealState);
    }

    // ---- Item/roster/mail builder family migrated verbatim from
    // EntranceService (R1): the SELECT initialization burst and the
    // inventory refresh packets share these builders across groups.
}
