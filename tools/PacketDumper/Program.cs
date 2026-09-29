// Dumps complete wire bytes for every implemented NOTI (server frame) and
// representative CMD frames (client format) using the real protocol serializers.
using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DFLegacy.Protocol;

var result = new Dictionary<string, object>();

// ---- server -> client frames (GameServerPacket.Encode, payload encrypted) ----
void Noti(int id, GameServerPacket packet) => result[$"noti:{id}"] = Convert.ToHexString(packet.Encode());

Noti(0, GameProtocolEngine.CreateCheckConnection(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }));
Noti(1, GameProtocolEngine.CreateChannelInfo("Channel 1"u8, 1, 1, 1, 1, 0x1234, ["127.0.0.1"u8.ToArray()], 2311, 2311));
Noti(2, GameProtocolEngine.CreateUserInfo([new GameCharacterSummary(0, "DFL"u8.ToArray(), 0, 1, 10, null, 1)]));
Noti(3, GameProtocolEngine.CreateUserState(1, 1));
Noti(4, GameProtocolEngine.CreateStamina(100));
Noti(5, GameProtocolEngine.CreateDungeonPermissions([new GameDungeonPermissionEntry(1001, 3)]));
Noti(11, GameProtocolEngine.CreateUdpPeerInfo([new GamePeerInfo(1, IPAddress.Parse("192.168.1.10"), IPAddress.Parse("1.2.3.4"), 2311, 100, 1, 1400)]));
Noti(12, GameProtocolEngine.CreateMessageNotification(GameMessageType.Type03White, 1, "hello"u8));
Noti(13, GameProtocolEngine.CreateItemList(0, [new GameInventoryEntry(1, 1001, 5, 0, 10, 0)]));
Noti(14, GameProtocolEngine.CreateUpdateItemList(0, [new GameInventoryEntry(1, 1001, 5, 0, 10, 0)]));
Noti(19, GameProtocolEngine.CreateSkillInfo(5, [new GameSkillEntry(1, 1, 0)]));
Noti(21, GameProtocolEngine.CreateAcceptableQuestList([1001, 1002]));
Noti(22, GameProtocolEngine.CreateUserPosition(new GameTownUser(1, 100, 200, 1)));
Noti(23, GameProtocolEngine.CreateUserArea(1, 1, new GameTownUser(1, 100, 200, 1)));
Noti(24, GameProtocolEngine.CreateAreaUsers(1, 1, [new GameTownUser(1, 100, 200, 1)]));
Noti(26, GameProtocolEngine.CreateUdpHost(0));
Noti(27, GameProtocolEngine.CreateEnterSelectDungeon(true, []));
Noti(28, GameProtocolEngine.CreateDungeonInfo(1001, 1, 0, 255, 255));
Noti(29, GameProtocolEngine.CreateStartMap(0, 0, 61000, [new GameDungeonMonster(0, 1, 1, 1)], [], 0xABCD));
Noti(30, GameProtocolEngine.CreateFinishLoading());
Noti(31, GameProtocolEngine.CreateEnableClearDungeon());
Noti(32, GameProtocolEngine.CreateDieState(1, false));
Noti(33, GameProtocolEngine.CreateFailClearDungeon(80));
Noti(34, GameProtocolEngine.CreatePlayResult(1, 0));
Noti(35, GameProtocolEngine.CreateClearDungeonReward(1, 5000, 1000, 2001, true, 2002, false, null, 0,
    new GameDungeonClearExperienceBreakdown { BaseExperience = 1000, RankBonus = 100 }));
Noti(36, GameProtocolEngine.CreateFatigue(10, 156, 0));
Noti(37, GameProtocolEngine.CreateExperienceGained(11, 12345, 0, 0, 2));
Noti(38, GameProtocolEngine.CreateMonsterDie(7, [new GameDungeonDrop(1, 2001, 5)], GameProtocolEngine.NoChampionRewardOrdinal));
Noti(39, GameProtocolEngine.CreateInventoryItemPickup(3, 1, 5));
Noti(40, GameProtocolEngine.CreateDropItem(1, 100, 200, new GameDungeonDrop(3, 2001, 5, 10, 1, 0)));
Noti(48, GameProtocolEngine.CreatePvpRecord(10, 5, 100, 1200, 1500, 3));
Noti(53, GameProtocolEngine.CreateCeraUpdate(true, 5000));
Noti(66, GameProtocolEngine.CreatePremiumServiceNotification(GameProtocolEngine.PremiumServiceUpdateAction, GameProtocolEngine.BlackDiamondServiceType, 86400));
Noti(97, GameProtocolEngine.CreateMailboxList(
    [new GameMailEntry(1, "GM"u8.ToArray(), "Hello"u8.ToArray(), 1000, new GameMailAttachment(2001, 1, 0, 10, 0), 1700000000u, 1800000000u, 1)]));
Noti(98, GameProtocolEngine.CreateMailboxRemoveMail([1]));
Noti(99, GameProtocolEngine.CreateMailboxAlarm(2));
Noti(100, GameProtocolEngine.CreateDiedCreature(1));
Noti(101, GameProtocolEngine.CreateCreatureRename(1, "Pet"u8.ToArray()));
Noti(102, GameProtocolEngine.CreateCreatureGainExperience(5, 3000));
Noti(103, GameProtocolEngine.CreateCreatureState(7, 1));
Noti(104, GameProtocolEngine.CreateCreatureResponse(1));
Noti(105, GameProtocolEngine.CreateCreatureInfo([new GameCreatureInfoEntry(7, 50, 3000, 5, "Pet"u8.ToArray(), false)]));
Noti(106, GameProtocolEngine.CreateCreatureEvolution(2, 1));
Noti(107, GameProtocolEngine.CreateRevivalCreature(1));
Noti(120, GameProtocolEngine.CreateEventInfo(0));
Noti(126, GameProtocolEngine.CreateCreatureMessageNotification(GameMessageType.Type00DualRouteBrown, 1, "woof"u8));
Noti(127, GameProtocolEngine.CreateBossDieCheck(true, true, 9));
Noti(131, GameProtocolEngine.CreateCreatureScriptMessageNotification(GameMessageType.Type03White, 1, "script"u8));
Noti(136, GameProtocolEngine.CreateEnterGameWorldComplete());

// ---- client -> server frames (12B header + seq + LCG-encrypted body) ----
// seed 0x1234 for every example; real sessions advance the seed per packet.
const uint Seed = 0x1234;
byte[] Cmd(int id, byte[] plaintextParams, bool encrypt = true)
{
    const ushort seq = 0x0001;
    var body = new byte[2 + plaintextParams.Length];
    body[0] = (byte)seq;
    body[1] = (byte)(seq >> 8);
    plaintextParams.CopyTo(body, 2);
    var crc = Crc32.Compute(body); // CRC covers sequence + plaintext body (sub_8B24C0 order)
    var frame = new byte[12 + plaintextParams.Length];
    frame[0] = GameProtocolEngine.CommandPacketType;
    frame[1] = (byte)id;
    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), (uint)frame.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), crc);
    frame[10] = (byte)seq;
    frame[11] = (byte)(seq >> 8);
    if (encrypt && plaintextParams.Length > 0)
    {
        var tmp = new byte[plaintextParams.Length];
        plaintextParams.CopyTo(tmp, 0);
        var s = Seed;
        GameClientPayloadCipher.Encrypt(tmp, ref s);
        tmp.CopyTo(frame, 12);
    }
    else
    {
        plaintextParams.CopyTo(frame, 12);
    }
    return frame;
}

void CmdHex(int id, byte[] plaintextParams, bool encrypt = true) =>
    result[$"cmd:{id}"] = Convert.ToHexString(Cmd(id, plaintextParams, encrypt));

// confirmed layouts (server parsers) — real field values
CmdHex(0, []);                                                       // CHECK_CONNECTION: plaintext direct-send
CmdHex(6, [2]);                                                      // DELETE_CHARACTER: slot
CmdHex(27, [0x50, 0x00, 1, 3, 0, 1]);                                // COMPOUND_ITEM: source,isItemId,count,0,1
CmdHex(42, [7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // DIE_MONSTER: 23B body, entityId=7, flag=0
CmdHex(44, [1, 0]);                                                  // USE_COIN: target user id
CmdHex(50, [100, 0, 200, 0, 0, 4, 0, 1, 0, 0, 0]);                   // DROP_ITEM: x,y,space,slot,count
CmdHex(83, [3, 0, 0xE9, 0x07, 9, 0]);                                // UPGRADE_ITEM: targetSlot,targetItemId,materialSlot
CmdHex(84, [3, 0, 0xE9, 0x07, 9, 0]);                                // RESET_ITEM_ATTR: same layout
CmdHex(127, [1, 0, 9, 0, 0x78, 0x56, 0x34, 0x12]);                   // BOSS_DIE_CHECK: participant,boss,checksum
// placeholder bodies (layout not confirmed by a server parser) — sequence-only
foreach (var id in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 18, 19, 20, 21, 24, 25, 26, 28, 29,
    30, 31, 32, 33, 34, 35, 36, 37, 38, 40, 41, 43, 45, 46, 47, 48, 49, 51, 67, 68, 72, 73, 74, 75,
    82, 97, 98, 99, 102, 103, 105, 124, 132, 142, 144, 148, 153 })
{
    CmdHex(id, []);
}

var options = new JsonSerializerOptions { WriteIndented = false };
File.WriteAllText(@"E:\github\DFLegacy\tools\packet_bytes.json", JsonSerializer.Serialize(result, options));
Console.WriteLine($"dumped {result.Count} packet byte samples");

// Round-trip check: decrypt each dumped frame back to plaintext with the
// client-side algorithms, decode it with PacketPlainText, and print the text.
foreach (var kv in result)
{
    var parts = kv.Key.Split(':');
    var id = byte.Parse(parts[1]);
    var bytes = Convert.FromHexString((string)kv.Value);
    byte[] plain;
    if (parts[0] == "noti")
    {
        plain = bytes[6..]; // server frame: 6B header + encrypted payload
        for (var i = 0; i < plain.Length; i++)
        {
            var v = plain[i];
            plain[i] = (byte)(((v << 6) | (v >> 2)) ^ 0xB5); // ROL 6 then XOR key
        }
    }
    else
    {
        plain = bytes[10..]; // client frame: seq(2, plaintext) + encrypted params
        var rv = Advance(Seed);
        var keyByte = (byte)rv;
        var rot = (int)((rv >> 8) & 7);
        for (var i = 2; i < plain.Length; i++)
        {
            plain[i] = (byte)(RotateLeft(plain[i], rot) ^ keyByte);
        }
    }

    var text = parts[0] == "noti"
        ? PacketPlainText.DecodeServer(0, id, plain)
        : PacketPlainText.Decode(1, id, plain);
    Console.WriteLine($"{kv.Key}: {text ?? "<no schema>"}");
}

static uint Advance(uint state)
{
    uint Next(uint v) => unchecked(v * 0x41C64E6D + 0x3039);
    var a = Next(state);
    var b = Next(a);
    var c = Next(b);
    return (uint)(((c >> 16) & 0x3FF) ^ ((((b >> 16) & 0x3FF) ^ (((a >> 16) & 0x7FF) << 10)) << 10));
}

static byte RotateLeft(byte value, int count) => count == 0 ? value : (byte)((value << count) | (value >> (8 - count)));
