using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;
using DFLegacy.Server;

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
            + "a>测试频道\n");
        check(displayNames is { Count: 4 }
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
            "[server]\n1\n11\t`<4::chn_a>`\t1\t`[sky_catle]`\t5\n",
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
            [/server]
            """, displayNames);
        check(lenient is { Count: 1 } && lenient[0].ChannelNumber == 11,
            "etc parser drops out-of-range, typeless and unresolved-name rows");
        check(ChannelCatalog.TryParseChannelInfoEtc(null, displayNames) is null,
            "missing etc text parses to no catalog");
        check(ChannelCatalog.TryParseChannelInfoEtc("[server]\n1\n11 `<4::a>` 1 `[t]` 5\n", null) is null,
            "no display-name table resolves no catalog");

        var defaults = new EntranceChannel("127.0.0.1", 7001, 10, 3);
        var composed = EntranceMetadata.BuildChannels(defaults, parsed);
        check(composed.Length == 700, "parsed catalog feeds the builder (2 etc channels + 8 default records)");
        var clientText = PvfEncodings.Cp936Lossy();
        check(clientText.GetString(composed, 28, 20).TrimEnd('\0') == "格兰之森 A",
            "etc channels carry the etc.chn.str display name in client text");
        check(BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(48, 4)) == 10
              && BinaryPrimitives.ReadInt32LittleEndian(composed.AsSpan(52, 4)) == 3,
            "records carry the configured capacity and the live session count");
        check(Encoding.ASCII.GetString(composed, 124, 7) == "diregie",
            "groups without catalog entries keep the single default record");
        check(Encoding.ASCII.GetString(composed, 148, 15) == "Local Channel 1",
            "the default record keeps the verified fallback display name");

        var metadata = EntranceMetadata.BuildChannels(
            defaults,
            [
                new ChannelCatalogEntry(0, 1, 0, null, "Local Channel 1"),
                new ChannelCatalogEntry(0, 6, 0, null, "Local Channel 6"),
                new ChannelCatalogEntry(0, 10, 0, null, "Local Channel 10")
            ]);
        check(metadata.Length == 748, "nine-group catalog metadata length (3 + 8 default records)");
        check(Encoding.ASCII.GetString(metadata, 4, 4) == "cain", "catalog channels land in group 0 (cain)");
        check(Encoding.ASCII.GetString(metadata, 28, 15) == "Local Channel 1", "first catalog record display name");
        check(Encoding.ASCII.GetString(metadata, 76, 15) == "Local Channel 6", "catalog records sort by channel number");
        check(BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(72, 4)) == 7001,
            "catalog records share the configured game endpoint");
        check(Encoding.ASCII.GetString(metadata, 676, 5) == "anton", "ninth group preserved");

        var single = EntranceMetadata.BuildSingleLocalChannel(defaults);
        var fallback = EntranceMetadata.BuildChannels(defaults, []);
        check(single.AsSpan().SequenceEqual(fallback),
            "empty catalog is byte-identical to the single-channel builder");

        check(Throws<ArgumentException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 1, 0, null, "A"), new ChannelCatalogEntry(0, 1, 0, null, "B")])),
            "duplicate (group, number) entries are rejected");
        check(Throws<ArgumentOutOfRangeException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 0, 0, null, "A")])),
            "channel number 0 is rejected");
        check(Throws<ArgumentOutOfRangeException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(9, 1, 0, null, "A")])),
            "group index beyond the nine groups is rejected");
        check(Throws<ArgumentException>(() => EntranceMetadata.BuildChannels(defaults,
                [new ChannelCatalogEntry(0, 1, 0, null, "")])),
            "entries without a resolved display name are rejected");
        check(Throws<ArgumentException>(() => EntranceMetadata.BuildSingleLocalChannel(
                new EntranceChannel("1234567890123456", 7001, 10, 1))),
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
