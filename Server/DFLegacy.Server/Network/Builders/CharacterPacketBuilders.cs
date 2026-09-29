using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Character-roster packet builders for the login/character flow (07 §4.7,
/// organized along the S4A21 InitPacketBuilderRegistry idea): the USERINFO
/// roster the select screen consumes, and starter inventories for newly
/// created characters. Pure orchestration over the store and item catalogs;
/// holds no per-connection state.
/// </summary>
public sealed class CharacterPacketBuilders(
    JsonGameStore store,
    ItemCatalog itemCatalog,
    PvpExperienceCatalog pvpExperienceCatalog)
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private static readonly (ushort ItemId, uint Count)[] TestStarterInventoryItems =
    [
        (8, 100), (27093, 1), (14873, 1), (12874, 1),
        (10874, 1), (16873, 1), (18874, 1), (22021, 1),
        (20093, 1), (24032, 1), (27546, 1), (28223, 1),
        (3037, 1000)
    ];
    private static readonly (ushort ItemId, uint Count)[] NormalStarterInventoryItems =
    [
        (26043, 1),
        (8, 100)
    ];

    public async Task<GameServerPacket> CreateUserInfoAsync(uint accountUid, CancellationToken cancellationToken)
    {
        var characters = await store.GetCharactersAsync(accountUid, cancellationToken);
        var summaries = characters
            .Take(MaximumTransferRoster.MaximumVisibleCharacters)
            .Select(character => new GameCharacterSummary(
                character.Slot,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                (character.Equipment ?? [])
                    .Where(item => item.ItemId != 0
                        && (item.Slot == 9
                            || itemCatalog.TryGetDefinition(
                                item.ItemId,
                                out var definition)
                            && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                                definition,
                                item.Slot)))
                    .Select(ToGameInventoryEntry)
                    .ToArray(),
                character.CharacterNo,
                (byte)Math.Clamp(character.AwakeningType, byte.MinValue, byte.MaxValue),
                pvpExperienceCatalog.GetGradeForPoints(character.PvpPoints)))
            .ToArray();
        return GameProtocolEngine.CreateUserInfo(summaries);
    }

    public List<CharacterItemRecord> CreateStarterInventory(uint accountUid) =>
        GetStarterInventoryItems(accountUid)
            .Select((item, index) => CharacterItemIdentity.Ensure(
                new CharacterItemRecord(
                    Slot: checked((ushort)(index + 9)),
                    ItemId: item.ItemId,
                    CountOrValue: item.Count,
                    Durability: itemCatalog.GetInitialDurability(item.ItemId)),
                itemCatalog))
            .ToList();

    internal static IReadOnlyList<(ushort ItemId, uint Count)>
        GetStarterInventoryItems(uint accountUid) =>
        AccountUidRules.IsTest(accountUid)
            ? TestStarterInventoryItems
            : AccountUidRules.IsNormal(accountUid)
                ? NormalStarterInventoryItems
                : [];

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
}
