using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Account roster and starter-inventory builders (07 §4.7): the USERINFO
/// roster the select screen consumes plus the starter items for newly
/// created characters.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    // 新建角色不再发放初始物品（2026-10-10）：取消原先的调试发放——测试账号的整套装备
    // （11 件）+ 一叠消耗品（item 8）+ 一叠无色小晶块（item 3037），以及普通账号的
    // 称号 26043 + 一叠消耗品。账号类型分流的方法形态保留，便于日后需要差异化发放时恢复。
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
        return CharactersPacketBuilders.CreateUserInfo(summaries);
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

    /// <summary>
    /// 新建角色的初始物品清单：当前一律为空（2026-10-10 起不发放任何初始物品）。
    /// 保留账号类型入参，便于日后需要按账号类型差异化发放时恢复。
    /// </summary>
    internal static IReadOnlyList<(ushort ItemId, uint Count)>
        GetStarterInventoryItems(uint accountUid) => [];
}
