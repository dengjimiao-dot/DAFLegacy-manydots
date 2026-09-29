using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace DFLegacy.Protocol;

/// <summary>
/// Field-level plain-text decoding for packet bodies, used by packet tracing.
/// Layouts mirror the GameProtocolEngine serializers (TX) and the confirmed
/// command parsers (RX). Unknown bodies degrade to labeled hex instead of
/// guessing field boundaries.
/// </summary>
public static class PacketPlainText
{
    private const int MaxListEntries = 8;
    private const string MoreEntries = "…";

    private static readonly Lazy<Encoding> Cp936 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    });

    public static string? Decode(byte type, byte protocolId, ReadOnlySpan<byte> data)
    {
        // Client -> server traffic only has a confirmed layout for CMD bodies.
        // Entrance-protocol frames arrive as type=0 (protocols 1/3, 5/6, 9/10,
        // 11/12) and must not be misparsed against NOTI schemas.
        return type == 1 ? DecodeCmd(protocolId, data) : null;
    }

    /// <summary>Decodes a server-sent frame payload; type=1 frames are command ACKs.</summary>
    public static string? DecodeServer(byte type, byte protocolId, ReadOnlySpan<byte> data)
    {
        return type == 1 ? DecodeCmdReply(protocolId, data) : DecodeNoti(protocolId, data);
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        public ReadOnlySpan<byte> Data = data;
        public int Offset;

        public readonly int Remaining => Data.Length - Offset;
        public readonly bool Has(int n) => Remaining >= n;

        public byte U8() { if (Remaining < 1) { Offset = Data.Length; return 0; } var v = Data[Offset]; Offset += 1; return v; }
        public ushort U16() { if (Remaining < 2) { Offset = Data.Length; return 0; } var v = BinaryPrimitives.ReadUInt16LittleEndian(Data.Slice(Offset, 2)); Offset += 2; return v; }
        public uint U32() { if (Remaining < 4) { Offset = Data.Length; return 0; } var v = BinaryPrimitives.ReadUInt32LittleEndian(Data.Slice(Offset, 4)); Offset += 4; return v; }
        public short I16() { if (Remaining < 2) { Offset = Data.Length; return 0; } var v = BinaryPrimitives.ReadInt16LittleEndian(Data.Slice(Offset, 2)); Offset += 2; return v; }

        public string Hex(int n)
        {
            n = Math.Min(n, Remaining);
            var s = Convert.ToHexString(Data.Slice(Offset, n));
            Offset += n;
            return s;
        }

        public string Ip4()
        {
            if (Remaining < 4)
            {
                Offset = Data.Length;
                return "0.0.0.0";
            }

            var v = Data.Slice(Offset, 4);
            Offset += 4;
            return new IPAddress(v).ToString();
        }

        /// <summary>u32 length-prefixed byte string decoded as CP936 (client text encoding).</summary>
        public string Str4()
        {
            if (!Has(4))
            {
                return "<short>";
            }

            var len = BinaryPrimitives.ReadInt32LittleEndian(Data.Slice(Offset, 4));
            Offset += 4;
            if (len < 0 || len > Remaining)
            {
                return "<bad-len>";
            }

            Offset += len;
            return Cp936.Value.GetString(Data.Slice(Offset - len, len));
        }
    }

    private sealed class Out
    {
        private readonly StringBuilder _sb = new();
        private bool _restDone;

        public void Add(string name, object value) =>
            _sb.Append(_sb.Length == 0 ? "" : " ").Append(name).Append('=').Append(value);

        public void Entry(string text) =>
            _sb.Append(_sb.Length == 0 ? "" : " ").Append('{').Append(text).Append('}');

        public void More() =>
            _sb.Append(MoreEntries);

        public void HexRest(Reader r)
        {
            if (_restDone || r.Remaining <= 0)
            {
                return;
            }

            _restDone = true;
            Add("rest", r.Hex(r.Remaining));
        }

        public override string ToString() => _sb.ToString();
    }

    // ---------- NOTI (server -> client, payload) ----------
    private static string? DecodeNoti(byte id, ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        var o = new Out();
        switch (id)
        {
            case 0: // CHECK_CONNECTION: 16B challenge echo
                o.Add("challenge", r.Hex(Math.Min(16, r.Remaining)));
                break;
            case 1: // CHANNELINFO
                o.Add("name", r.Str4());
                o.Add("channelType", r.U32());
                o.Add("result", r.U32());
                o.Add("serverId", r.U8());
                o.Add("channelNumber", r.U8());
                o.Add("seed", r.U32());
                var hostCount = r.U32();
                o.Add("hosts", hostCount);
                for (var i = 0; i < hostCount && i < 4 && r.Has(4); i++)
                {
                    o.Add($"host{i}", r.Str4());
                }

                if (r.Has(8))
                {
                    o.Add("port1", r.U32());
                    o.Add("port2", r.U32());
                }

                o.HexRest(r);
                break;
            case 2: // USERINFO: mode 2 roster / mode 0 town actor / mode 1 details
                o.Add("mode", r.U8());
                o.Add("count", r.U16());
                o.Add("uid", r.U16());
                o.Add("name", r.Str4());
                if (r.Has(6))
                {
                    o.Add("job", r.U8());
                    o.Add("growth", r.U8());
                    o.Add("level", r.U8());
                    o.Add("pvpGrade", r.U8());
                    o.Add("state", r.U8());
                    o.Add("entityState", r.U8());
                }

                o.HexRest(r);
                break;
            case 3: // USER_STATE
                o.Add("uid", r.U16());
                o.Add("state", r.U8());
                break;
            case 4: // STAMINA
                o.Add("stamina", r.U8());
                break;
            case 5: // DUNGEON_PERMISSION
                var permCount = r.U16();
                o.Add("count", permCount);
                for (var i = 0; i < permCount && i < MaxListEntries && r.Has(3); i++)
                {
                    o.Entry($"dungeon={r.U16()} state={r.U8()}");
                }

                if (permCount > MaxListEntries && r.Has(3)) o.More();
                break;
            case 11: // USER_UDP_IP_PORT
                var peerCount = r.U8();
                o.Add("count", peerCount);
                for (var i = 0; i < peerCount && i < MaxListEntries && r.Has(21); i++)
                {
                    o.Entry($"uid={r.U16()} local={r.Ip4()} public={r.Ip4()}:{r.U16()} account={r.U32()} nat={r.U8()} mtu={r.U32()}");
                }

                if (peerCount > MaxListEntries && r.Has(21)) o.More();
                break;
            case 12: // MESSAGE
            case 126: // CREATURE_MESSAGE (also popup)
            case 131: // CREATURE_SCRIPT_MESSAGE
                o.Add("msgType", r.U8());
                o.Add("targetUid", r.U16());
                o.Add("text", r.Str4());
                break;
            case 13: // ITEM_LIST
            case 14: // UPDATE_ITEM_LIST
                var listType = r.U8();
                o.Add("listType", listType);
                if (id == 13 && listType == 2)
                {
                    o.Add("param", r.U16());
                }

                var itemCount = r.U16();
                o.Add("count", itemCount);
                for (var i = 0; i < itemCount && i < MaxListEntries && r.Has(12); i++)
                {
                    o.Entry($"slot={r.U16()} item={r.U16()} n={r.U32()} st={r.U8()} dur={r.U16()} seal={r.U8()}");
                }

                if (itemCount > MaxListEntries && r.Has(12)) o.More();
                break;
            case 19: // SKILLINFO
                o.Add("sp", r.U16());
                var skillCount = r.U8();
                o.Add("count", skillCount);
                for (var i = 0; i < skillCount && i < MaxListEntries && r.Has(3); i++)
                {
                    o.Entry($"slot={r.U8()} skill={r.U8()} lv={r.U8()}");
                }

                if (r.Has(1)) o.Add("tail", r.U8());
                break;
            case 21: // ACCEPTABLE_QUEST_LIST
                var questCount = r.U8();
                o.Add("count", questCount);
                for (var i = 0; i < questCount && i < MaxListEntries && r.Has(2); i++)
                {
                    o.Entry($"quest={r.U16()}");
                }

                if (questCount > MaxListEntries && r.Has(2)) o.More();
                break;
            case 22: // USER_POSITION
                o.Add("uid", r.U16());
                o.Add("x", r.I16());
                o.Add("y", r.I16());
                o.Add("dir", r.U8());
                o.Add("move", r.U16());
                break;
            case 23: // USER_AREA
                o.Add("uid", r.U16());
                o.Add("town", r.U8());
                o.Add("area", r.U8());
                o.Add("x", r.I16());
                o.Add("y", r.I16());
                o.Add("dir", r.U8());
                o.Add("active", r.U8());
                break;
            case 24: // AREA_USERS
                o.Add("town", r.U8());
                o.Add("area", r.U8());
                var areaUserCount = r.U16();
                o.Add("count", areaUserCount);
                for (var i = 0; i < areaUserCount && i < MaxListEntries && r.Has(8); i++)
                {
                    o.Entry($"uid={r.U16()} {r.I16()},{r.I16()} dir={r.U8()} act={r.U8()}");
                }

                if (areaUserCount > MaxListEntries && r.Has(8)) o.More();
                break;
            case 26: // UDP_HOST
                o.Add("partyIndex", r.U8());
                break;
            case 27: // START_GAME / enter select dungeon
                o.Add("hellQuestsOk", r.U8() != 0);
                var missing = r.U8();
                o.Add("missingHellSlots", missing);
                for (var i = 0; i < missing && r.Has(1); i++)
                {
                    o.Add($"slot{i}", r.U8());
                }

                break;
            case 28: // DUNGEON_INFO
                o.Add("dungeon", r.U16());
                o.Add("difficulty", r.U8());
                o.Add("maze", r.U8());
                o.Add("bossX", r.U8());
                o.Add("bossY", r.U8());
                o.Add("flagA", r.U8());
                o.Add("flagB", r.U8());
                break;
            case 29: // START_MAP
                o.Add("roomX", r.U8());
                o.Add("roomY", r.U8());
                o.Add("seed", r.U32());
                var roomState = r.U8();
                o.Add("state", roomState);
                if (roomState != 0)
                {
                    o.Add("map", r.U16());
                    var monsterCount = r.U8();
                    o.Add("monsters", monsterCount);
                    for (var i = 0; i < monsterCount && i < MaxListEntries && r.Has(9); i++)
                    {
                        o.Entry($"#{r.U8()} uid={r.U16()} m={r.U16()} lv={r.U8()} t={r.U8()} box={r.U8()}/{r.U8()}");
                    }

                    if (monsterCount > MaxListEntries && r.Has(9)) o.More();
                    if (r.Has(1))
                    {
                        var passiveCount = r.U8();
                        o.Add("passiveDrops", passiveCount);
                        for (var i = 0; i < passiveCount && i < MaxListEntries && r.Has(13); i++)
                        {
                            o.Entry($"assoc={r.U8()} ground={r.U16()} item={r.U16()} info={r.U32()} owner={r.U32()}");
                        }

                        if (r.Has(1)) o.Add("hellMode", r.U8());
                    }
                }

                break;
            case 30: // FINISH_LOADING
            case 31: // ENABLE_CLEAR_DUNGEON
            case 136: // ENTER_GAMEWORLD_COMPLETE
                break; // empty
            case 32: // DIE_STATE
                o.Add("uid", r.U16());
                o.Add("alive", r.U8() != 0);
                break;
            case 33: // FAIL_CLEAR_DUNGEON
                o.Add("stamina", r.U8());
                break;
            case 34: // PLAY_RESULT
                o.Add("uid", r.U8());
                o.Add("flags", r.Hex(Math.Min(3, r.Remaining)));
                if (r.Has(1)) o.Add("reserved", r.U8());
                break;
            case 35: // CLEAR_DUNGEON_REWARD
                o.Add("uid", r.U8());
                o.Add("base+party", r.U32());
                o.Add("rank", r.U32());
                o.Add("party", r.U32());
                o.Add("avatar", r.U32());
                o.Add("event", r.U32());
                o.Add("blackDiamond", r.U32());
                o.Add("channel", r.U32());
                o.Add("mentor", r.U32());
                o.Add("creature", r.U32());
                if (r.Has(1)) o.Add("attachCount", r.U8());
                o.HexRest(r); // card columns are client-rendered; keep bytes inspectable
                break;
            case 36: // FATIGUE
                o.Add("used", r.U16());
                o.Add("max", r.U16());
                o.Add("premium", r.U16());
                break;
            case 37: // EXP
                o.Add("level", r.U8());
                o.Add("exp", r.U32());
                o.Add("extraA", r.U32());
                o.Add("extraB", r.U32());
                o.Add("sp", r.U16());
                break;
            case 38: // DIE_MONSTER
                o.Add("uid", r.U16());
                var dropCount = r.U8();
                o.Add("drops", dropCount);
                for (var i = 0; i < dropCount && i < MaxListEntries && r.Has(12); i++)
                {
                    o.Entry($"ground={r.U16()} item={r.U16()} info={r.U32()} dur={r.U16()} owner={r.U16()}");
                }

                if (r.Has(3))
                {
                    o.Add("championOrdinal", (sbyte)r.U8());
                    r.U8();
                    r.U8();
                }

                break;
            case 39: // GET_ITEM
                o.Add("ground", r.U16());
                o.Add("uid", r.U16());
                if (r.Remaining >= 20)
                {
                    for (var i = 0; i < 4 && r.Has(5); i++)
                    {
                        var gold = r.U32();
                        r.U8();
                        if (gold != 0)
                        {
                            o.Add($"gold[{i}]", gold);
                        }
                    }
                }
                else if (r.Has(8))
                {
                    o.Add("dice", r.Hex(4));
                    o.Add("uid2", r.U16());
                    o.Add("slot", r.U16());
                }

                break;
            case 40: // DROP_ITEM
                o.Add("uid", r.U16());
                o.Add("x", r.I16());
                o.Add("y", r.I16());
                o.Add("ground", r.U16());
                o.Add("item", r.U16());
                o.Add("attr", r.U8());
                o.Add("info", r.U32());
                o.Add("durability", r.U16());
                break;
            case 48: // PVP_RECORD
                o.Add("win", r.U32());
                o.Add("lose", r.U32());
                o.Add("points", r.U32());
                o.Add("rank", r.U32());
                o.Add("nextRank", r.U32());
                o.Add("grade", r.U8());
                o.Add("gradeExt", r.U8());
                break;
            case 53: // CERA
                o.Add("ok", r.U8() != 0);
                o.Add("cera", r.U32());
                break;
            case 66: // CERA_SPECIALITEM / PremiumService
                o.Add("action", r.U16());
                o.Add("serviceType", r.U8());
                o.Add("remainSeconds", r.U32());
                break;
            case 97: // MAILBOX_MAIL_LIST
                var packageCount = r.U8();
                o.Add("packages", packageCount);
                o.Add("reset", r.U8());
                for (var i = 0; i < packageCount && i < 2 && r.Has(4); i++)
                {
                    var mailId = r.U32();
                    var sender = r.Str4();
                    var gold = r.U32();
                    var item = r.U16();
                    r.Hex(Math.Min(8, r.Remaining)); // seal/value/durability/state
                    r.U32();
                    r.U32();
                    o.Entry($"mail={mailId} from={sender} gold={gold} item={item}");
                }

                if (r.Has(2))
                {
                    o.Add("notLoaded", r.I16());
                    var mailBodyCount = r.U16();
                    o.Add("bodies", mailBodyCount);
                    for (var i = 0; i < mailBodyCount && i < 4 && r.Has(4); i++)
                    {
                        var mailId = r.U32();
                        r.U32();
                        var sender = r.Str4();
                        var text = r.Str4();
                        r.U32();
                        var state = r.Has(2) ? r.U16() : (ushort)0;
                        o.Entry($"mail={mailId} from={sender} \"{text}\" state={state}");
                    }
                }

                break;
            case 98: // MAILBOX_REMOVE_MAIL
                var removeCount = r.U32();
                o.Add("count", removeCount);
                for (var i = 0; i < removeCount && i < MaxListEntries && r.Has(4); i++)
                {
                    o.Entry($"mail={r.U32()}");
                }

                if (removeCount > MaxListEntries && r.Has(4)) o.More();
                break;
            case 99: // MAILBOX_ALARM
                o.Add("newMail", r.U16());
                break;
            case 100: // DIED_CREATURE
            case 107: // REVIVAL_CREATURE
                o.Add("uid", r.U16());
                break;
            case 101: // RENAME_CREATURE
                o.Add("uid", r.U16());
                o.Add("name", r.Str4());
                break;
            case 102: // GAIN_EXP_CREATURE
                o.Add("level", r.U8());
                o.Add("exp", r.U32());
                break;
            case 103: // CREATURE_STATE
                o.Add("uid", r.U32());
                o.Add("state", r.U32());
                break;
            case 104: // RESPONSE_CREATURE
                o.Add("uid", r.U16());
                break;
            case 105: // CREATURE_ITEM_LIST
                var creatureCount = r.U8();
                o.Add("count", creatureCount);
                for (var i = 0; i < creatureCount && i < MaxListEntries && r.Has(4); i++)
                {
                    var uid = r.U32();
                    var stomach = r.U8();
                    var exp = r.U32();
                    var level = r.U8();
                    var name = r.Str4();
                    var noCharge = r.Has(1) ? r.U8() : (byte)0;
                    o.Entry($"uid={uid} \"{name}\" lv={level} exp={exp} stomach={stomach} noCharge={noCharge}");
                }

                break;
            case 106: // EVOLUTE_CREATURE
                o.Add("evoCreature", r.U8());
                o.Add("uid", r.U16());
                break;
            case 120: // EVENT_INFO
                o.Add("featureFlags", r.U32());
                break;
            case 127: // BOSS_DIE_CHECK
                var ok = r.U8();
                o.Add("ok", ok);
                if (ok != 0 && r.Has(3))
                {
                    o.Add("allReported", r.U8() != 0);
                    o.Add("boss", r.U16());
                }
                else if (ok == 0 && r.Has(1))
                {
                    o.Add("error", r.U8());
                }

                break;
            default:
                return null;
        }

        o.HexRest(r);
        return o.ToString();
    }

    // ---------- CMD (client -> server, body = seq + params) ----------
    private static string? DecodeCmd(byte id, ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        var o = new Out();
        if (r.Has(2))
        {
            o.Add("seq", r.U16());
        }

        switch (id)
        {
            case 4: // SELECT_CHARACTER
            case 5: // CREATE_CHARACTER
            case 6: // DELETE_CHARACTER
            case 7: // RETURN_SELECT_CHARACTER
                if (r.Has(1)) o.Add("slot", r.U8());
                o.HexRest(r);
                break;
            case 8: // GET_USERINFO: e.g. FFFF 02
                o.Add("filter", r.Hex(r.Remaining));
                break;
            case 19: // MOVE_ITEMSPACE
                if (r.Has(10))
                {
                    o.Add("srcList", r.U8());
                    o.Add("srcSlot", r.U16());
                    o.Add("count", r.U32());
                    o.Add("dstList", r.U8());
                    o.Add("dstSlot", r.U16());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 20: // SORT_ITEM
                if (r.Has(1)) o.Add("space", r.U8());
                o.HexRest(r);
                break;
            case 27: // COMPOUND_ITEM
                if (r.Has(6))
                {
                    o.Add("source", r.U16());
                    o.Add("isItemId", r.U8() != 0);
                    o.Add("craftCount", r.U8());
                    o.Add("reserved", r.U8());
                    o.Add("fixed", r.U8());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 30: // CHANGE_SKILLSLOT
                if (r.Has(2))
                {
                    o.Add("src", r.U8());
                    o.Add("dst", r.U8());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 33: // ACCEPT_QUEST
            case 34: // GIVEUP_QUEST
            case 36: // FINISH_QUEST
                if (r.Has(2)) o.Add("quest", r.U16());
                o.HexRest(r);
                break;
            case 35: // SET_QUEST_TRIGGER
                if (r.Has(6))
                {
                    o.Add("quest", r.U16());
                    o.Add("trigger", r.U32());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 37: // SET_USER_POSITION
                if (r.Has(7))
                {
                    o.Add("x", r.I16());
                    o.Add("y", r.I16());
                    o.Add("dir", r.U8());
                    o.Add("move", r.U16());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 38: // SET_USER_AREA
                if (r.Has(7))
                {
                    o.Add("town", r.U8());
                    o.Add("area", r.U8());
                    o.Add("x", r.I16());
                    o.Add("y", r.I16());
                    o.Add("dir", r.U8());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 42: // DIE_MONSTER
                if (r.Has(2)) o.Add("uid", r.U16());
                if (r.Remaining >= 21)
                {
                    o.Add("passiveObject", r.Data[r.Offset + 20] == 1);
                }

                o.HexRest(r);
                break;
            case 44: // USE_COIN
                if (r.Has(2)) o.Add("targetUid", r.U16());
                o.HexRest(r);
                break;
            case 50: // DROP_ITEM
                if (r.Has(11))
                {
                    o.Add("x", r.I16());
                    o.Add("y", r.I16());
                    o.Add("space", r.U8());
                    o.Add("slot", r.U16());
                    o.Add("count", r.U32());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 83: // UPGRADE_ITEM
            case 84: // RESET_ITEM_ATTR
                if (r.Has(6))
                {
                    o.Add("targetSlot", r.U16());
                    o.Add("targetItem", r.U16());
                    o.Add("materialSlot", r.U16());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            case 127: // BOSS_DIE_CHECK
                if (r.Has(8))
                {
                    o.Add("participant", r.U16());
                    o.Add("boss", r.U16());
                    o.Add("checksum", r.U32());
                }
                else
                {
                    o.HexRest(r);
                }

                break;
            default:
                o.HexRest(r); // layout not confirmed server-side: keep raw hex after seq
                break;
        }

        return o.ToString();
    }

    // ---------- CMD replies (type=1 TX frames) ----------
    private static string? DecodeCmdReply(byte id, ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        var o = new Out();
        if (!r.Has(1))
        {
            return null;
        }

        var result = r.U8();
        o.Add("result", result);
        switch (id)
        {
            case 6: // DELETE_CHARACTER: result + slot
                if (r.Has(1)) o.Add("slot", r.U8());
                break;
            case 9: // RECOVER_STAMINA
                if (result != 0 && r.Has(4)) o.Add("gold", r.U32());
                else if (result == 0 && r.Has(1)) o.Add("error", r.U8());
                break;
            case 15: // START_GAME failure: error + party index
                if (result == 0 && r.Has(2))
                {
                    o.Add("error", r.U8());
                    o.Add("partyIndex", r.U8());
                }

                break;
            case 20: // SORT_ITEM: result + space / error
                if (r.Has(1)) o.Add(result != 0 ? "space" : "error", r.U8());
                break;
            case 103: // RENAME_CREATURE
                if (result != 0 && r.Has(3))
                {
                    o.Add("cardSlot", r.U16());
                    o.Add("space", r.U8());
                }
                else if (result == 0 && r.Has(1))
                {
                    o.Add("error", r.U8());
                }

                break;
            default:
                o.HexRest(r);
                break;
        }

        return o.ToString();
    }
}
