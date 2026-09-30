using Microsoft.Data.Sqlite;

namespace DAFLegacy.Server.Sqlite;

/// <summary>
/// 库行与内存 record 的双向映射（docs/design/08-sqlite-storage.md D6/D7）。
/// 内存 record 树是读路径，本类只做两件事：把 record 的每个字段落到列、把列读回
/// record，不含任何状态变更逻辑。
///
/// <para>两个必须保持的语义：</para>
/// <list type="bullet">
/// <item>九个可空标量（<c>avatar_*</c> / <c>creature_*</c> / <c>equipment_quality_seed</c>）
/// 原样存取 null：消费侧一律 <c>?? 回退值</c>，按 0 落库会改变下发字节。</item>
/// <item>可空集合（五个容器、任务、邮件等）读回时归一为非 null 空表。JSON 模型里
/// null 表示「尚未规范化」，内核与登录流程见到 null 会重写一次；SQLite 侧每次落盘
/// 都写入规范化后的集合，故该门控永不触发——这是有意的归一，见
/// RequiresRewriteAfterLoad。</item>
/// </list>
/// </summary>
internal static class SqliteStateMapper
{
    // list_type 容器码：客户端线上的权威码（act1_schema.sql 已逐一对过）。
    internal const int InventoryListType = 0;
    internal const int AvatarListType = 1;
    internal const int WarehouseListType = 2;
    internal const int EquipmentListType = 3;
    internal const int CreatureListType = 7;

    internal static long FatigueBoundaryUnixSeconds(int fatigueDayKey)
    {
        var year = fatigueDayKey / 10_000;
        var month = fatigueDayKey / 100 % 100;
        var day = fatigueDayKey % 100;
        return new DateTimeOffset(year, month, day, 6, 0, 0, TimeSpan.Zero)
            .ToLocalTime()
            .ToUnixTimeSeconds();
    }

    internal static int FatigueDayKeyFromBoundary(long unixSeconds) =>
        GameStoreCore.GetFatigueDayKey(
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime());

    /// <summary>
    /// 角色指纹。Cash 由账号行单源投影（<c>accounts.cash</c>，见 D6），不参与角色
    /// 指纹，否则账号充值会把每个角色都拖成「已变更」。
    /// </summary>
    internal static string Fingerprint(CharacterRecord character) =>
        System.Text.Json.JsonSerializer.Serialize(character with { Cash = 0 });

    /// <summary>
    /// 账号指纹：只覆盖落在 accounts / account_premium 两张表的字段。
    /// 必须用匿名对象而非值元组——System.Text.Json 序列化元组只会得到 "{}"，
    /// 那样所有账号的指纹恒等，账号级变更（点券、改密、契约）将永远检测不到。
    /// </summary>
    internal static string Fingerprint(AccountRecord account) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            account.UserName,
            account.PasswordHash,
            OicqCode = account.OicqCode ?? string.Empty,
            account.PresetCharactersInitialized,
            account.Cash,
            PremiumServices = account.PremiumServices ?? []
        });

    internal static Guid ParseUid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

    internal static string FormatUid(Guid value) =>
        value == Guid.Empty ? string.Empty : value.ToString("D");

    internal static CharacterItemRecord ReadItem(SqliteDataReader reader) =>
        new(
            Slot: checked((ushort)ReadInt32(reader, "slot_index")),
            ItemId: checked((ushort)ReadInt32(reader, "item_id")),
            CountOrValue: checked((uint)ReadInt64(reader, "item_count")),
            State: checked((byte)ReadInt64(reader, "item_state")),
            Durability: checked((ushort)ReadInt64(reader, "durability")),
            SealState: checked((byte)ReadInt64(reader, "seal_state")),
            AvatarRemainingSeconds: ReadNullableUInt32(reader, "avatar_remaining_seconds"),
            AvatarAbilityIndex: ReadNullableUInt16(reader, "avatar_ability_index"),
            CreatureStomach: ReadNullableByte(reader, "creature_stomach"),
            CreatureExperience: ReadNullableUInt32(reader, "creature_experience"),
            CreatureLevel: ReadNullableByte(reader, "creature_level"),
            CreatureName: ReadNullableString(reader, "creature_name"),
            CreatureNoCharge: ReadNullableBool(reader, "creature_no_charge"),
            CreatureStomachRemainderSeconds: ReadNullableByte(
                reader,
                "creature_stomach_remainder_seconds"),
            EquipmentQualitySeed: ReadNullableUInt32(reader, "equipment_quality_seed"),
            InstanceId: ParseUid(ReadString(reader, "instance_id")));

    internal static CharacterMailAttachmentRecord? ReadAttachment(
        SqliteDataReader reader)
    {
        if (ReadInt64(reader, "item_id") == 0)
            return null;

        return new CharacterMailAttachmentRecord(
            ItemId: checked((ushort)ReadInt64(reader, "item_id")),
            CountOrValue: checked((uint)ReadInt64(reader, "item_count")),
            State: checked((byte)ReadInt64(reader, "item_state")),
            Durability: checked((ushort)ReadInt64(reader, "durability")),
            SealState: checked((byte)ReadInt64(reader, "seal_state")),
            AvatarRemainingSeconds: ReadNullableUInt32(reader, "avatar_remaining_seconds"),
            AvatarAbilityIndex: ReadNullableUInt16(reader, "avatar_ability_index"),
            EquipmentQualitySeed: ReadNullableUInt32(reader, "equipment_quality_seed"),
            InstanceId: ParseUid(ReadString(reader, "instance_id")));
    }

    internal static CharacterMailRecord ReadMail(SqliteDataReader reader) =>
        new(
            Id: checked((uint)ReadInt64(reader, "mail_id")),
            Sender: ReadString(reader, "sender"),
            Text: ReadString(reader, "text"),
            Gold: ReadInt32(reader, "gold"),
            Attachment: ReadAttachment(reader),
            SentAt: checked((uint)ReadInt64(reader, "created_at")),
            ExpiresAt: checked((uint)ReadInt64(reader, "expires_at")),
            IsNew: ReadInt64(reader, "is_new") != 0,
            State: checked((ushort)ReadInt64(reader, "state")));

    internal static void BindItem(
        SqliteCommand command,
        uint characterId,
        int listType,
        CharacterItemRecord item)
    {
        command.Parameters.AddWithValue("@character_id", (long)characterId);
        command.Parameters.AddWithValue("@list_type", listType);
        command.Parameters.AddWithValue("@slot_index", (long)item.Slot);
        command.Parameters.AddWithValue("@item_id", (long)item.ItemId);
        command.Parameters.AddWithValue("@item_count", (long)item.CountOrValue);
        command.Parameters.AddWithValue("@item_state", (long)item.State);
        command.Parameters.AddWithValue("@durability", (long)item.Durability);
        command.Parameters.AddWithValue("@seal_state", (long)item.SealState);
        command.Parameters.AddWithValue("@lock_state", 0L);
        command.Parameters.AddWithValue("@avatar_remaining_seconds", ToSql(item.AvatarRemainingSeconds));
        command.Parameters.AddWithValue("@avatar_ability_index", ToSql(item.AvatarAbilityIndex));
        command.Parameters.AddWithValue("@creature_stomach", ToSql(item.CreatureStomach));
        command.Parameters.AddWithValue("@creature_experience", ToSql(item.CreatureExperience));
        command.Parameters.AddWithValue("@creature_level", ToSql(item.CreatureLevel));
        command.Parameters.AddWithValue("@creature_name", ToSql(item.CreatureName));
        command.Parameters.AddWithValue("@creature_no_charge", ToSql(item.CreatureNoCharge));
        command.Parameters.AddWithValue(
            "@creature_stomach_remainder_seconds",
            ToSql(item.CreatureStomachRemainderSeconds));
        command.Parameters.AddWithValue(
            "@equipment_quality_seed",
            ToSql(item.EquipmentQualitySeed));
        command.Parameters.AddWithValue("@instance_id", FormatUid(item.InstanceId));
    }

    private static object ToSql<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object ToSql(string? value) => value ?? (object)DBNull.Value;

    private static object ToSql(bool? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    internal static int ReadInt32(SqliteDataReader reader, string column) =>
        checked((int)ReadInt64(reader, column));

    internal static long ReadInt64(SqliteDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column));

    internal static string ReadString(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal(column));

    private static string? ReadNullableString(SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column))
            ? null
            : reader.GetString(reader.GetOrdinal(column));

    private static uint? ReadNullableUInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));
    }

    private static ushort? ReadNullableUInt16(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : checked((ushort)reader.GetInt64(ordinal));
    }

    private static byte? ReadNullableByte(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : checked((byte)reader.GetInt64(ordinal));
    }

    private static bool? ReadNullableBool(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    }
}