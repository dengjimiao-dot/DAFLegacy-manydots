using DFLegacy.Protocol;
using Microsoft.Data.Sqlite;

namespace DFLegacy.Server.Sqlite;

/// <summary>
/// 全局倍率与掉落开关（<c>server_settings</c> 表，docs/design/08-sqlite-storage.md D7）。
///
/// <para>这是这些参数的**唯一真源**：原 server.json 的 <c>Drop</c> 与 <c>Experience</c>
/// 两段已删除，频道容量上报值也一并迁入。服务端启动时读一次本表，打印全量字段，
/// 之后读路径一律使用本快照（record，不可变）——掉落与经验判定在每只怪、每次通关
/// 都会触发，不能每次查库。</para>
///
/// <para>未在 schema 中落列的基准值（经验 0.928、通关每怪 0.75、世界掉落
/// 10000/十万分制）仍写死在服务端常量里，本表存的是相对它们的**倍数**。</para>
/// </summary>
public sealed record ServerSettings
{
    // 经验与金币
    public double ExperienceMultiplier { get; init; } = 1.0;
    public double ClearMultiplier { get; init; } = 1.0;
    public double GoldMultiplier { get; init; } = 1.0;

    // 掉落倍率
    public double NormalDropMultiplier { get; init; } = 1.0;
    public double HellDropMultiplier { get; init; } = 1.0;
    public double WorldDropRate { get; init; } = 1.0;
    public double ClearExpPerKill { get; init; } = 1.0;
    // 官方行为：每只怪最多 1 件、深渊不掉配方。基线与本记录的默认值都按官方取。
    public int HellDropRollTimes { get; init; } = 1;
    public double HellRecipeRate { get; init; } = 0.0;

    // 掉落开关与全局系数
    public bool DropEnabled { get; init; } = true;
    public double DropRatePercent { get; init; } = 100.0;
    public double DropEconomicRate { get; init; } = 1.0;
    public bool DropForce { get; init; }

    // 通关经验加成
    public double ClearBonusBlackDiamondRate { get; init; } = 0.05;
    public double ClearBonusEventRate { get; init; }
    public DateTimeOffset? ClearBonusEventStartsAt { get; init; }
    public DateTimeOffset? ClearBonusEventEndsAt { get; init; }

    /// <summary>读全量 singleton 行；表无行时返回默认值（基线已播种，正常不会发生）。</summary>
    internal static ServerSettings Read(IGameDatabase database) =>
        database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT experience_multiplier, clear_multiplier, gold_multiplier,
       normal_drop_multiplier, hell_drop_multiplier, world_drop_rate,
       clear_exp_per_kill, hell_drop_roll_times, hell_recipe_rate,
       drop_enabled, drop_rate_percent, drop_economic_rate, drop_force,
       clear_bonus_black_diamond_rate, clear_bonus_event_rate,
       clear_bonus_event_starts_at, clear_bonus_event_ends_at
FROM server_settings
WHERE settings_id = 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new ServerSettings();

            return new ServerSettings
            {
                ExperienceMultiplier = GetDouble(reader, 0),
                ClearMultiplier = GetDouble(reader, 1),
                GoldMultiplier = GetDouble(reader, 2),
                NormalDropMultiplier = GetDouble(reader, 3),
                HellDropMultiplier = GetDouble(reader, 4),
                WorldDropRate = GetDouble(reader, 5),
                ClearExpPerKill = GetDouble(reader, 6),
                HellDropRollTimes = reader.GetInt32(7),
                HellRecipeRate = GetDouble(reader, 8),
                DropEnabled = reader.GetInt64(9) != 0,
                DropRatePercent = GetDouble(reader, 10),
                DropEconomicRate = GetDouble(reader, 11),
                DropForce = reader.GetInt64(12) != 0,
                ClearBonusBlackDiamondRate = GetDouble(reader, 13),
                ClearBonusEventRate = GetDouble(reader, 14),
                ClearBonusEventStartsAt = GetTimestamp(reader, 15),
                ClearBonusEventEndsAt = GetTimestamp(reader, 16)
            };
        });

    /// <summary>
    /// 启动日志：一次打印全部字段。倍率类与开关类分组列出，便于部署后目视核对
    /// 库里的实际生效值（此前这些值只在 server.json 里，改错无从察觉）。
    /// </summary>
    public string Describe() =>
        $"""
        experienceMultiplier={ExperienceMultiplier} clearMultiplier={ClearMultiplier} goldMultiplier={GoldMultiplier} \
        normalDropMultiplier={NormalDropMultiplier} hellDropMultiplier={HellDropMultiplier} worldDropRate={WorldDropRate} \
        clearExpPerKill={ClearExpPerKill} hellDropRollTimes={HellDropRollTimes} hellRecipeRate={HellRecipeRate} \
        dropEnabled={DropEnabled} dropRatePercent={DropRatePercent} dropEconomicRate={DropEconomicRate} dropForce={DropForce} \
        clearBonusBlackDiamondRate={ClearBonusBlackDiamondRate} clearBonusEventRate={ClearBonusEventRate} \
        clearBonusEventStartsAt={FormatTimestamp(ClearBonusEventStartsAt)} clearBonusEventEndsAt={FormatTimestamp(ClearBonusEventEndsAt)}
        """;

    private static double GetDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);

    /// <summary>
    /// 活动窗口存 ISO-8601 文本。审计时间列用 CURRENT_TIMESTAMP 生成的是
    /// <c>YYYY-MM-DD HH:MM:SS</c>（UTC、无 T、无时区标记），解析时两种形态都接受。
    /// </summary>
    private static DateTimeOffset? GetTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var text = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
        ?? "(unset)";
}