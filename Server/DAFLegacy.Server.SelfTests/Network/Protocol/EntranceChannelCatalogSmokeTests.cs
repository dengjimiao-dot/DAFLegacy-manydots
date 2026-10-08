using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;
using DAFLegacy.Server;

internal static class EntranceChannelCatalogSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        const string etcText = """
            // 频道目录（两种行形态并存：A21 样本反引号名 + 现网裸名）
            [dungeon]
            `[elven_guard]`
            1
            2
            [/dungeon]

            [server]
            1 // 组一
            // 频道编号、频道名称、频道分类、分类名称、额外经验比例、1~10
                11 `<4::chn_channel_info_004>` 1 `[sky_catle]` 5 0 0 0 0 0 0 0 0 0 0 0 ``
                200 <4::channel_info_cname_1> 2 `[elven_guard]` 10 0 0 0 0 0 0 0 0 0 0 0
            [/server]

            [server]
            2 // 组二
                11 `<4::chn_channel_info_004>` 1 `[sky_catle]` 5 0
            [/server]
            """;

        var displayNames = ChannelCatalog.TryParseChannelStringTable(
            "channel_info_dname_0>艾尔文防线\n"
            + "<chn_channel_info_004>格兰之森 A\n"
            + "channel_info_cname_1>新手 - 洛兰\n"
            + "a>测试频道\n"
            + "num>洛兰 2\n");
        check(displayNames is { Count: 5 }
              && displayNames!["channel_info_dname_0"] == "艾尔文防线"
              && displayNames["chn_channel_info_004"] == "格兰之森 A",
            "string table parses key>value lines with and without angle brackets");

        var parsed = ChannelCatalog.TryParseChannelInfoEtc(etcText, displayNames);
        check(parsed is { Count: 2 }, "etc parser keeps group 1 rows only");
        check(parsed![0] is { GroupIndex: 0, ChannelNumber: 11, Type: 1, Tag: "[sky_catle]" }
              && parsed[0].DisplayName == "格兰之森 A",
            "etc parser resolves backticked-name rows via the string table");
        check(parsed[1] is { ChannelNumber: 200, Type: 2, Tag: "[elven_guard]" }
              && parsed[1].DisplayName == "新手 - 洛兰",
            "etc parser resolves bare-name rows (current-network layout)");

        var tabbed = ChannelCatalog.TryParseChannelInfoEtc(
            "[server]\n1\n11    `<4::chn_a>`    1    `[sky_catle]`    5\n",
            new Dictionary<string, string> { ["chn_a"] = "斯顿雪域 B" });
        check(tabbed is { Count: 1 } && tabbed[0].ChannelNumber == 11 && tabbed[0].Type == 1,
            "etc parser splits tab-separated rows");

        var lenient = ChannelCatalog.TryParseChannelInfoEtc("""
            [server]
            1
            11 `<4::a>` 1 `[t]` 5
            300 `<4::b>` 2 `[t]` 5
            12 `<4::c>`
            0 `<4::d>` 2 `[t]` 5
            13 `<4::missing>` 2 `[t]` 5
            14 <4::num> 2 `[t]` 5
            [/server]
            """, displayNames);
        check(lenient is { Count: 1 } && lenient[0].ChannelNumber == 11,
            "etc parser drops out-of-range, typeless, unresolved and digit-bearing name rows");
        // ServerGroup 配置：默认收组 1；指定 2 时改收组 2 且 GroupIndex 相应 +1。
        var twoGroups = """
[server]
1
11 `<4::a>` 1 `[t]` 5
2
21 `<4::a>` 1 `[t]` 7
""";
        var groupOne = ChannelCatalog.TryParseChannelInfoEtc(twoGroups, displayNames);
        check(groupOne is not null
            && groupOne.Count == 1
            && groupOne[0].ChannelNumber == 11
            && groupOne[0].GroupIndex == 0,
            "default server group 1 collects the first group with group index 0");
        var groupTwo = ChannelCatalog.TryParseChannelInfoEtc(twoGroups, displayNames, serverGroup: 2);
        check(groupTwo is not null
            && groupTwo.Count == 1
            && groupTwo[0].ChannelNumber == 21
            && groupTwo[0].GroupIndex == 1,
            "server group 2 collects the second group with group index 1");

        // 真实 PVF 样本形态：组号行带行内注释（1 // 凯恩）、整行注释、
        // 15 列频道行（编号/名称/分类/分类名/额外经验/十个等级区间）。
        var realStyleSourceText = """
            [server]                
            1 // 凯恩                
            // 脚本用数据
            // 频道编号、频道名称、频道分类、分类名称、额外经验比例、1~10…
                1    <4::channel_info_cname_1>    2    `[elven_guard]`    10    0    0    0    0    0    0    0    0    0    0
                6    <4::a>    3    `[none]`    0    0    0    0    0    0    0    0    0    0    0
                10    <4::channel_info_dname_0>    0    `[granfloris]`    5    0    0    0    0    0    0    0    0    0    0
            [/server]                

            [server]                
            2 // 狄瑞吉                
                1    <4::channel_info_cname_1>    2    `[elven_guard]`    10    0    0    0    0    0    0    0    0    0    0
            [/server]
            """;
        var realStyle = ChannelCatalog.TryParseChannelInfoEtc(realStyleSourceText, displayNames);
        check(realStyle is { Count: 3 }
            && realStyle[0] is { GroupIndex: 0, ChannelNumber: 1, Type: 2 }
            && realStyle[0].DisplayName == "新手 - 洛兰"
            && realStyle[1] is { ChannelNumber: 6, Type: 3 }
            && realStyle[2] is { ChannelNumber: 10, Type: 0, GroupIndex: 0 },
            "real-style group line (inline comment) and 15-column rows parse with correct types");
        var realStyleTwo = ChannelCatalog.TryParseChannelInfoEtc(realStyleSourceText, displayNames, serverGroup: 2);
        check(realStyleTwo is { Count: 1 }
            && realStyleTwo[0].ChannelNumber == 1
            && realStyleTwo[0].GroupIndex == 1,
            "server group 2 selects the second block by its group number");

        check(ChannelCatalog.TryParseChannelInfoEtc(null, displayNames) is null,
            "missing etc text parses to no catalog");
        check(ChannelCatalog.TryParseChannelInfoEtc("[server]\n1\n11 `<4::a>` 1 `[t]` 5\n", null) is null,
            "no display-name table resolves no catalog");

        var defaults = new EntranceChannel("127.0.0.1", 10001, 10, 3);
        var composed = EntranceMetadata.BuildChannels(defaults, parsed, channelPortBase: 20000);
        check(composed.Length == 700, "parsed catalog feeds the builder (2 etc channels + 8 default records)");
        var clientText = PvfEncodings.Cp936Lossy();
        check(clientText.GetString(composed, 28, 20).TrimEnd('\0') == "格兰之森 A 11",
            "record names compose the display name with the channel number (client atol semantics)");
        check(BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(48, 4)) == 10
              && BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(52, 4)) == 3,
            "records carry the configured capacity and the live session count");
        check(Encoding.ASCII.GetString(composed, 124, 7) == "diregie",
            "groups without catalog entries keep the single default record");
        check(Encoding.ASCII.GetString(composed, 148, 15) == "Local Channel 1",
            "the default record keeps the verified fallback display name");
        check(BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(72, 4)) == 20011
              && BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(120, 4)) == 20200,
            "catalog records advertise their per-channel listener ports (PortBase + number)");
        check(Encoding.ASCII.GetString(composed, 56, 9) == "127.0.0.1"
              && BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(192, 4)) == 10001,
            "hosts stay shared and fallback-group records keep the fallback endpoint");

        var metadata = EntranceMetadata.BuildChannels(
            defaults,
            [
                new ChannelCatalogEntry(0, 1, 0, null, "Local Channel"),
                new ChannelCatalogEntry(0, 6, 0, null, "Local Channel"),
                new ChannelCatalogEntry(0, 10, 0, null, "Local Channel")
            ],
            channelPortBase: 20000);
        check(metadata.Length == 748, "nine-group catalog metadata length (3 + 8 default records)");
        check(Encoding.ASCII.GetString(metadata, 4, 4) == "cain", "catalog channels land in group 0 (cain)");
        check(Encoding.ASCII.GetString(metadata, 28, 15) == "Local Channel 1", "first catalog record display name");
        check(Encoding.ASCII.GetString(metadata, 76, 15) == "Local Channel 6", "catalog records sort by channel number");
        check(BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(72, 4)) == 20001
              && BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(120, 4)) == 20006
              && BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(168, 4)) == 20010,
            "each catalog record carries PortBase + its channel number");
        check(Encoding.ASCII.GetString(metadata, 676, 5) == "anton", "ninth group preserved");

        var populated = EntranceMetadata.BuildChannels(
            defaults,
            [
                new ChannelCatalogEntry(0, 1, 0, null, "Local Channel"),
                new ChannelCatalogEntry(0, 6, 0, null, "Local Channel")
            ],
            channelPortBase: 20000,
            currentUserByChannel: number => number == 6 ? 4 : 0);
        check(BinaryPrimitives.ReadInt32LittleEndian(populated.AsSpan(52, 4)) == 0
              && BinaryPrimitives.ReadInt32LittleEndian(populated.AsSpan(100, 4)) == 4,
            "catalog records carry their per-channel live session count");
        check(BinaryPrimitives.ReadInt32LittleEndian(populated.AsSpan(172, 4)) == 3,
            "fallback-group records keep the caller-provided session count");

        var single = EntranceMetadata.BuildSingleLocalChannel(defaults);
        var fallback = EntranceMetadata.BuildChannels(defaults, [], channelPortBase: 20000);
        check(single.AsSpan().SequenceEqual(fallback),
            "empty catalog is byte-identical to the single-channel builder");

        check(Throws<ArgumentException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 1, 0, null, "A"), new ChannelCatalogEntry(0, 1, 0, null, "B")], 20000)),
            "duplicate (group, number) entries are rejected");
        check(Throws<ArgumentOutOfRangeException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 0, 0, null, "A")], 20000)),
            "channel number 0 is rejected");
        check(Throws<ArgumentOutOfRangeException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(9, 1, 0, null, "A")], 20000)),
            "group index beyond the nine groups is rejected");
        check(Throws<ArgumentException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 1, 0, null, "")], 20000)),
            "entries without a resolved display name are rejected");
        check(Throws<ArgumentException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 1, 0, null, "洛兰 2")], 20000)),
            "display names containing digits are rejected (client atol semantics)");
        check(Throws<ArgumentException>(() => EntranceMetadata.BuildSingleLocalChannel(
                new EntranceChannel("1234567890123456", 10001, 10, 1))),
            "oversized host fields are rejected");
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
