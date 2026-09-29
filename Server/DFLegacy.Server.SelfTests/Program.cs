using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

var failures = new List<string>();

static ScriptFileSystem CreateScripts(ServerOptions options) =>
    new(options, NullLogger<ScriptFileSystem>.Instance);

static ItemCatalog CreateItems(ScriptFileSystem scripts) =>
    new(scripts, NullLogger<ItemCatalog>.Instance);

EquipmentReinforcementSmokeTests.Run(Check);
await HiddenDungeonQuestSmokeTests.RunAsync(Check);
QuestMapSmokeTests.Run(Check);
QuestPvpRankSmokeTests.Run(Check);
AvatarCompoundSmokeTests.Run(Check);
WorldMapSmokeTests.Run(Check);
DungeonClearExperienceSmokeTests.Run(Check);
ItemSealingSmokeTests.Run(Check);
AntibotReportSmokeTests.Run(Check);
MegaphoneSmokeTests.Run(Check);
PartySmokeTests.Run(Check);
await CompoundItemSmokeTests.RunAsync(Check);
await EquipmentQualitySmokeTests.RunAsync(Check);

var defaultServerOptions = new ServerOptions();
Check(!defaultServerOptions.EnablePacketTracing, "packet tracing is disabled by default");
Check(defaultServerOptions.GameplayDatagram.MonsterObjectType == 0x0211,
    "DFLegacy IRDMonster network object type is 0x211");
Check(defaultServerOptions.HexDumpLimit <= 64, "default packet dump limit stays lightweight");
Check(!defaultServerOptions.Mcp.Enabled
    && defaultServerOptions.Mcp.Host == "127.0.0.1"
    && defaultServerOptions.Mcp.Port == 12222,
    "DAF-MCP is loopback-bound on port 12222 and disabled by default");

PacketTapFunnelSmokeTests.Run(Check);
McpModuleLoaderSmokeTests.Run(Check);

var sharedRandomValue = GameRandomSource.Shared.Next(10_000);
Check(sharedRandomValue is >= 0 and < 10_000
    && GameRandomSource.Shared.GetStatus() is { Provider: "Random.Shared" },
    "live game random source is the pure managed Random.Shared implementation");

RandomSourceSmokeTests.Run(Check);
PvfScriptSmokeTests.Run(Check);
PathResolverSmokeTests.Run(Check);

var characterSessions = new CharacterSessionRegistry(
    NullLogger<CharacterSessionRegistry>.Instance);
var sharedCharacterId = Guid.NewGuid();
var firstCharacterSessionKicked = false;
var firstCharacterLease = await characterSessions.ClaimAsync(
    sharedCharacterId,
    Guid.NewGuid(),
    () => firstCharacterSessionKicked = true,
    CancellationToken.None);
var secondCharacterLeaseTask = characterSessions.ClaimAsync(
    sharedCharacterId,
    Guid.NewGuid(),
    () => { },
    CancellationToken.None);
await Task.Yield();
Check(firstCharacterSessionKicked && !secondCharacterLeaseTask.IsCompleted,
    "selecting an online character kicks and waits for the older character session");
characterSessions.Release(firstCharacterLease);
var secondCharacterLease = await secondCharacterLeaseTask;
var differentCharacterKicked = false;
var differentCharacterLease = await characterSessions.ClaimAsync(
    Guid.NewGuid(),
    Guid.NewGuid(),
    () => differentCharacterKicked = true,
    CancellationToken.None);
Check(!differentCharacterKicked,
    "different characters can remain online concurrently");
characterSessions.Release(secondCharacterLease);
characterSessions.Release(differentCharacterLease);
var numericMailAlarmRaised = false;
var numericFatigueResetRaised = false;
var numericMessageRaised = false;
var numericPopupRaised = false;
var numericCharacterLease = await characterSessions.ClaimAsync(
    123u,
    Guid.NewGuid(),
    () => { },
    CancellationToken.None,
    mailReceived: () => numericMailAlarmRaised = true,
    fatigueReset: () => numericFatigueResetRaised = true,
    messageNotification: (messageType, targetAreaUserId, messageBytes) =>
        numericMessageRaised = messageType == GameMessageType.Type04Brown
            && targetAreaUserId == 0x1234
            && messageBytes.SequenceEqual(new byte[] { 0x41, 0x42 }),
    popupNotification: messageBytes =>
        numericPopupRaised = messageBytes.SequenceEqual(new byte[] { 0x43, 0x44 }));
Check(numericCharacterLease.CharacterNo == 123
    && characterSessions.NotifyMailReceived(123u)
    && numericMailAlarmRaised
    && characterSessions.NotifyFatigueReset(numericCharacterLease.CharacterId)
    && numericFatigueResetRaised
    && characterSessions.NotifyMessage(
        numericCharacterLease.CharacterId,
        GameMessageType.Type04Brown,
        0x1234,
        new byte[] { 0x41, 0x42 })
    && numericMessageRaised
    && characterSessions.NotifyPopup(
        numericCharacterLease.CharacterId,
        new byte[] { 0x43, 0x44 })
    && numericPopupRaised,
    "online character occupancy and asynchronous notifications can use CharacterNo instead of a roster slot");
characterSessions.Release(numericCharacterLease);

var broadcastMessageCount = 0;
var broadcastPopupCount = 0;
var dungeonPermissionRaised = false;
var broadcastLeaseOne = await characterSessions.ClaimAsync(
    Guid.NewGuid(),
    Guid.NewGuid(),
    () => { },
    CancellationToken.None,
    messageNotification: (_, _, bytes) =>
        broadcastMessageCount += bytes.SequenceEqual(new byte[] { 0x51 }) ? 1 : 0,
    popupNotification: bytes =>
        broadcastPopupCount += bytes.SequenceEqual(new byte[] { 0x52 }) ? 1 : 0,
    dungeonPermission: (dungeonId, maximumDifficulty) =>
        dungeonPermissionRaised = dungeonId == 10_000 && maximumDifficulty == 3);
var broadcastLeaseTwo = await characterSessions.ClaimAsync(
    Guid.NewGuid(),
    Guid.NewGuid(),
    () => { },
    CancellationToken.None,
    messageNotification: (_, _, bytes) =>
        broadcastMessageCount += bytes.SequenceEqual(new byte[] { 0x51 }) ? 1 : 0,
    popupNotification: bytes =>
        broadcastPopupCount += bytes.SequenceEqual(new byte[] { 0x52 }) ? 1 : 0);
Check(characterSessions.NotifyMessageAll(
        GameMessageType.Type16Brown,
        0,
        new byte[] { 0x51 }) == 2
    && characterSessions.NotifyPopupAll(new byte[] { 0x52 }) == 2
    && broadcastMessageCount == 2
    && broadcastPopupCount == 2
    && characterSessions.NotifyDungeonPermission(
        broadcastLeaseOne.CharacterId,
        10_000,
        3)
    && dungeonPermissionRaised
    && characterSessions.IsOnline(broadcastLeaseTwo.CharacterId),
    "admin notifications broadcast once per online session and dungeon permissions target one character");
characterSessions.Release(broadcastLeaseOne);
characterSessions.Release(broadcastLeaseTwo);

PvfEncodings.EnsureRegistered();
var capturedMapChatBody = Convert.FromHexString(
    "0A00030100000000000C000000B0A1B0A1B0A1B0A1B0A1B0A1");
Check(EntranceService.TryReadSendMessage(capturedMapChatBody, out var capturedMapChat)
    && capturedMapChat.MessageType == (byte)GameMessageType.Type03White
    && capturedMapChat.TargetOrItemSpace == 1
    && capturedMapChat.SlotOrReserved == 0
    && Encoding.GetEncoding(936).GetString(capturedMapChat.MessageBytes) == "啊啊啊啊啊啊",
    "captured 2008 CMD 17 map-chat request parses type, target, reserved slot and CP936 text");
Check(!EntranceService.TryReadSendMessage(
        capturedMapChatBody[..^1],
        out _),
    "CMD 17 parser rejects a body shorter than its declared text length");
// Captured on channel:7001 (packet_watch seq 211): a real 1:1 chat send of
// "77777" to partner "Fight" — type 18 with the trailing partner-name dstr.
var capturedOneToOneBody = Convert.FromHexString(
    "210012000000000000050000003737373737050000004669676874");
Check(EntranceService.TryReadSendMessage(capturedOneToOneBody, out var capturedOneToOne)
    && capturedOneToOne.MessageType == (byte)GameMessageType.Type18DirectOutput
    && capturedOneToOne.TargetOrItemSpace == 0
    && capturedOneToOne.SlotOrReserved == 0
    && Encoding.GetEncoding(936).GetString(capturedOneToOne.MessageBytes) == "77777"
    && Encoding.GetEncoding(936).GetString(capturedOneToOne.TargetNameBytes) == "Fight",
    "captured 2008 CMD 17 1:1 chat request parses type 18, text and trailing partner name");
Check(!EntranceService.TryReadSendMessage(
        Convert.FromHexString("21001200000000000005000000373737373704000000466967"),
        out _),
    "CMD 17 type 18 parser rejects a partner name that overruns the body");
Check(!EntranceService.TryReadSendMessage(
        Convert.FromHexString("2100120000000000000500000037373737"),
        out _),
    "CMD 17 type 18 parser rejects a body without the partner-name dstr");
// Captured on channel:7001 (packet_watch seq 148): the chat-window whisper
// "1111" to "Fight" — type 1 carries the partner uid and the same name dstr.
var capturedWhisperBody = Convert.FromHexString(
    "0F00010700000000000400000031313131050000004669676874");
Check(EntranceService.TryReadSendMessage(capturedWhisperBody, out var capturedWhisper)
    && capturedWhisper.MessageType == (byte)GameMessageType.Type01LightGreen
    && capturedWhisper.TargetOrItemSpace == 7
    && Encoding.GetEncoding(936).GetString(capturedWhisper.MessageBytes) == "1111"
    && Encoding.GetEncoding(936).GetString(capturedWhisper.TargetNameBytes) == "Fight",
    "captured 2008 CMD 17 whisper request parses type 1, partner uid, text and name");
var emptyMapChatBody = Convert.FromHexString("0A000301000000000000000000");
Check(!EntranceService.TryReadSendMessage(emptyMapChatBody, out _),
    "CMD 17 parser rejects an empty chat message");
var capturedCreatureAutomaticMessageBody = Convert.FromHexString(
    "3100030000010000000E000000B2E2CAD4B2E2CAD4B2E2CAD4CED2");
Check(EntranceService.TryReadSendMessage(
        capturedCreatureAutomaticMessageBody,
        out var capturedCreatureAutomaticMessage)
    && capturedCreatureAutomaticMessage.MessageType
        == (byte)GameMessageType.Type03White
    && capturedCreatureAutomaticMessage.TargetOrItemSpace == 0
    && capturedCreatureAutomaticMessage.SlotOrReserved == 1
    && Encoding.GetEncoding(936).GetString(
        capturedCreatureAutomaticMessage.MessageBytes) == "测试测试测试我",
    "captured 2008 CMD 124 Bobo automatic message uses the shared chat body layout");
var capturedCreatureScriptBody = Convert.FromHexString(
    "0900030000010000002A000000D6F7C8CBA3ACCED2B6F6B5C3BAC3C4D1CADCB0A1A3ACD1DBC0E1B6BCD2AAC1F7B3F6C0B4C1CBA1ADA1AD");
Check(EntranceService.TryReadSendMessage(
        capturedCreatureScriptBody,
        out var capturedCreatureScript)
    && capturedCreatureScript.MessageType == (byte)GameMessageType.Type03White
    && capturedCreatureScript.TargetOrItemSpace == 0
    && capturedCreatureScript.SlotOrReserved == 1
    && Encoding.GetEncoding(936).GetString(capturedCreatureScript.MessageBytes)
        == "主人，我饿得好难受啊，眼泪都要流出来了……",
    "captured 2008 CMD 132 Creature script request uses the shared chat body layout");
var capturedCreatureScriptType02Body = Convert.FromHexString(
    "2F000200000100000004000000DFF5DFF5");
Check(EntranceService.TryReadSendMessage(
        capturedCreatureScriptType02Body,
        out var capturedCreatureScriptType02)
    && capturedCreatureScriptType02.MessageType
        == (byte)GameMessageType.Type02LightCyan
    && Encoding.GetEncoding(936).GetString(
        capturedCreatureScriptType02.MessageBytes) == "啧啧",
    "captured 2008 CMD 132 also preserves the Creature type-2 script channel");
var notificationText = Encoding.GetEncoding(936).GetBytes("系统通知");
var messageNotification = GameProtocolEngine.CreateMessageNotification(
    messageType: GameMessageType.Type00DualRouteBrown,
    targetAreaUserId: 0x1234,
    notificationText);
var expectedMessagePayload = new byte[7 + notificationText.Length];
expectedMessagePayload[0] = 0;
BinaryPrimitives.WriteUInt16LittleEndian(expectedMessagePayload.AsSpan(1, 2), 0x1234);
BinaryPrimitives.WriteUInt32LittleEndian(
    expectedMessagePayload.AsSpan(3, 4),
    (uint)notificationText.Length);
notificationText.CopyTo(expectedMessagePayload, 7);
Check(messageNotification.Type == GameProtocolEngine.NotificationPacketType
    && messageNotification.ProtocolId == GameProtocolEngine.MessageNotification
    && messageNotification.Payload.SequenceEqual(expectedMessagePayload),
    "2008 message notification uses type, target, u32 length and CP936 bytes without the newer extra flag");
CheckThrows<ArgumentOutOfRangeException>(
    () => GameProtocolEngine.CreateMessageNotification(
        GameMessageType.Type00DualRouteBrown,
        0,
        []),
    "message notification rejects an empty client string");
CheckThrows<ArgumentOutOfRangeException>(
    () => GameProtocolEngine.CreateMessageNotification(
        GameMessageType.Type00DualRouteBrown,
        0,
        new byte[0x100]),
    "message notification rejects strings that do not fit the client buffer");
var creatureScriptNotification =
    GameProtocolEngine.CreateCreatureScriptMessageNotification(
        GameMessageType.Type03White,
        1,
        capturedCreatureScript.MessageBytes);
var expectedCreatureScriptPayload = new byte[7 + capturedCreatureScript.MessageBytes.Length];
expectedCreatureScriptPayload[0] = (byte)GameMessageType.Type03White;
BinaryPrimitives.WriteUInt16LittleEndian(expectedCreatureScriptPayload.AsSpan(1, 2), 1);
BinaryPrimitives.WriteUInt32LittleEndian(
    expectedCreatureScriptPayload.AsSpan(3, 4),
    (uint)capturedCreatureScript.MessageBytes.Length);
capturedCreatureScript.MessageBytes.CopyTo(expectedCreatureScriptPayload, 7);
Check(creatureScriptNotification.Type == GameProtocolEngine.NotificationPacketType
    && creatureScriptNotification.ProtocolId
        == GameProtocolEngine.CreatureScriptMessageNotification
    && creatureScriptNotification.Payload.SequenceEqual(expectedCreatureScriptPayload),
    "2008 NOTI 131 Creature script message uses type, area-user id and CP936 text");
var creatureAutomaticMessageNotification =
    GameProtocolEngine.CreateCreatureMessageNotification(
        GameMessageType.Type03White,
        1,
        capturedCreatureAutomaticMessage.MessageBytes);
var expectedCreatureAutomaticMessagePayload =
    new byte[7 + capturedCreatureAutomaticMessage.MessageBytes.Length];
expectedCreatureAutomaticMessagePayload[0] = (byte)GameMessageType.Type03White;
BinaryPrimitives.WriteUInt16LittleEndian(
    expectedCreatureAutomaticMessagePayload.AsSpan(1, 2),
    1);
BinaryPrimitives.WriteUInt32LittleEndian(
    expectedCreatureAutomaticMessagePayload.AsSpan(3, 4),
    (uint)capturedCreatureAutomaticMessage.MessageBytes.Length);
capturedCreatureAutomaticMessage.MessageBytes.CopyTo(
    expectedCreatureAutomaticMessagePayload,
    7);
Check(creatureAutomaticMessageNotification.Type
        == GameProtocolEngine.NotificationPacketType
    && creatureAutomaticMessageNotification.ProtocolId
        == GameProtocolEngine.CreatureMessageNotification
    && creatureAutomaticMessageNotification.Payload.SequenceEqual(
        expectedCreatureAutomaticMessagePayload),
    "2008 NOTI 126 Bobo automatic message uses type, authoritative area-user id and CP936 text");
var popupNotification = GameProtocolEngine.CreatePopupNotification(
    new byte[] { 0x41, 0x42 });
Check(popupNotification.Type == GameProtocolEngine.NotificationPacketType
    && popupNotification.ProtocolId == GameProtocolEngine.CreatureMessageNotification
    && popupNotification.Payload.SequenceEqual(
        new byte[] { 0, 0, 0, 2, 0, 0, 0, 0x41, 0x42 }),
    "legacy popup notification uses CREATURE_MESSAGE type zero, target zero and a u32 CP936 length");
CheckThrows<ArgumentOutOfRangeException>(
    () => GameProtocolEngine.CreatePopupNotification([]),
    "popup notification rejects an empty client string");
CheckThrows<ArgumentOutOfRangeException>(
    () => GameProtocolEngine.CreatePopupNotification(new byte[0x100]),
    "popup notification rejects strings that do not fit the client buffer");

var unpackedPvpPath = @"D:\DFLegacy\.tmp\pvf2008-work-20260808\working";
if (Directory.Exists(unpackedPvpPath))
{
    var pvpScripts = CreateScripts(new ServerOptions
    {
        ScriptPvfPath = "",
        SkillScriptPath = unpackedPvpPath
    });
    var pvpCatalog = new PvpExperienceCatalog(
        pvpScripts,
        NullLogger<PvpExperienceCatalog>.Instance);
    pvpCatalog.Initialize();
    var pvpLines = pvpScripts.ReadLines("etc/pvp_ref.etc").ToArray();
    Check(pvpScripts.FileExists("etc/pvp_ref.etc"),
        "unpacked PvP reference file is available to the script filesystem");
    Check(pvpLines.Any(line => line.Trim() == "[pvp experience]")
        && pvpLines.Any(line => line.Trim().StartsWith(
            "[pvp experience max grade] 30",
            StringComparison.OrdinalIgnoreCase)),
        "pvp_ref.etc sections and max-grade value are readable");
    Check(pvpCatalog.MaximumGrade == 30,
        "pvp_ref.etc declares maximum grade 30");
    Check(pvpCatalog.ThresholdCount == 30,
        "pvp_ref.etc caches all 30 grade thresholds");
    Check(pvpCatalog.GetGradeForPoints(0) == 0
        && pvpCatalog.GetGradeForPoints(1_999) == 1
        && pvpCatalog.GetGradeForPoints(2_000) == 2
        && pvpCatalog.GetGradeForPoints(2_001) == 2
        && pvpCatalog.GetGradeForPoints(9_999_999) == 30,
        "pvp_ref.etc grade boundaries promote at the exact cumulative threshold");
    Check(pvpCatalog.GetCurrentRankPoint(0) == 0
        && pvpCatalog.GetNextRankPoint(0) == 2_000
        && pvpCatalog.GetCurrentRankPoint(2) == 2_000
        && pvpCatalog.GetNextRankPoint(2) == 8_000
        && pvpCatalog.GetNextRankPoint(30) == 9_999_999,
        "NOTI 48 current and next PvP rank points use adjacent PVF thresholds");
    Check(pvpCatalog.TryGetBookExperience(1121, out var pvpBook1)
        && pvpBook1 == 1_000
        && pvpCatalog.TryGetBookExperience(1122, out var pvpBook2)
        && pvpBook2 == 10_000
        && pvpCatalog.TryGetBookExperience(1123, out var pvpBook3)
        && pvpBook3 == 100_000,
        "PvP books use the fixed 1,000/10,000/100,000 legacy values");
}

var pvpRecord = GameProtocolEngine.CreatePvpRecord(
    wins: 1,
    losses: 2,
    pvpPoints: 3_000,
    currentRankPoint: 2_000,
    nextRankPoint: 8_000,
    pvpGrade: 2,
    pvpGradeExtension: 7);
var expectedPvpRecordPayload = new byte[22];
BinaryPrimitives.WriteInt32LittleEndian(expectedPvpRecordPayload.AsSpan(0, 4), 1);
BinaryPrimitives.WriteInt32LittleEndian(expectedPvpRecordPayload.AsSpan(4, 4), 2);
BinaryPrimitives.WriteInt32LittleEndian(expectedPvpRecordPayload.AsSpan(8, 4), 3_000);
BinaryPrimitives.WriteInt32LittleEndian(expectedPvpRecordPayload.AsSpan(12, 4), 2_000);
BinaryPrimitives.WriteInt32LittleEndian(expectedPvpRecordPayload.AsSpan(16, 4), 8_000);
expectedPvpRecordPayload[20] = 2;
expectedPvpRecordPayload[21] = 7;
Check(pvpRecord.Type == GameProtocolEngine.NotificationPacketType
    && pvpRecord.ProtocolId == GameProtocolEngine.PvpRecordNotification
    && pvpRecord.Payload.SequenceEqual(expectedPvpRecordPayload),
    "NOTI 48 PvP record is five little-endian int32 values followed by two bytes");

var mailAlarmRaised = false;
var mailCharacterId = Guid.NewGuid();
var mailCharacterLease = await characterSessions.ClaimAsync(
    mailCharacterId,
    Guid.NewGuid(),
    () => { },
    CancellationToken.None,
    () => mailAlarmRaised = true);
Check(characterSessions.NotifyMailReceived(mailCharacterId) && mailAlarmRaised,
    "online character sessions receive a pending-mail signal");
characterSessions.Release(mailCharacterLease);

var premiumAlarmRaised = false;
var ceraUpdateRaised = false;
var weaknessRecoveryRaised = false;
var premiumCharacterId = Guid.NewGuid();
var premiumCharacterLease = await characterSessions.ClaimAsync(
    premiumCharacterId,
    Guid.NewGuid(),
    () => { },
    CancellationToken.None,
    premiumInfo: (serviceType, remainSeconds) =>
        premiumAlarmRaised = serviceType == GameProtocolEngine.BlackDiamondServiceType
            && remainSeconds == 123,
    ceraInfo: value => ceraUpdateRaised = value == 456,
    weaknessRecovery: value => weaknessRecoveryRaised = value == 27);
Check(characterSessions.NotifyPremiumInfo(
        premiumCharacterId,
        GameProtocolEngine.BlackDiamondServiceType,
        123)
    && premiumAlarmRaised
    && characterSessions.NotifyCera(premiumCharacterId, 456)
    && ceraUpdateRaised
    && characterSessions.NotifyWeaknessRecovery(premiumCharacterId, 27)
    && weaknessRecoveryRaised,
    "online character sessions receive pending Premium, Cera and weakness-recovery updates");
characterSessions.Release(premiumCharacterLease);

var datagramHeader = LegacyDatagramProtocol.CreateHeader(12, 36);
Check(LegacyDatagramProtocol.TryReadHeader(datagramHeader, out var parsedDatagramHeader), "legacy UDP header parses");
Check(parsedDatagramHeader.ProtocolId == 12, "legacy UDP protocol id round-trip");
Check(parsedDatagramHeader.TotalLength == 47, "legacy UDP total length");
Check(parsedDatagramHeader.PayloadLength == 36, "legacy UDP payload length");
var aesPlaintext = new byte[16];
BinaryPrimitives.WriteInt32LittleEndian(aesPlaintext, 1);
var aesKey = new byte[16];
var aesCiphertext = LegacyDatagramProtocol.EncryptAesEcb(aesPlaintext, aesKey);
Check(LegacyDatagramProtocol.DecryptAesEcb(aesCiphertext, aesKey).SequenceEqual(aesPlaintext), "legacy UDP AES round-trip");
Check(LegacyDatagramProtocol.Compress(aesCiphertext).Length > 0, "legacy UDP zlib payload");

Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926, "CRC32 reference vector");

var original = PacketFrame.Create(11, new byte[32]);
await using (var empty = new MemoryStream())
{
    Check(await PacketFrame.ReadAsync(empty) is null, "clean EOF is not a zero-length frame");
}

await using (var stream = new MemoryStream(original.Encode()))
{
    var decoded = await PacketFrame.ReadAsync(stream);
    Check(decoded is not null, "packet decoded");
    Check(decoded?.ProtocolId == 11, "protocol id round-trip");
    Check(decoded?.Body.SequenceEqual(original.Body) == true, "body round-trip");
}

var session = new EntranceSessionState();
Check(session.SessionToken.Length == 16, "session token has exact client field length");
Check(session.SessionToken[^1] == 0, "session token is null terminated for the legacy client");
Check(Array.IndexOf(session.SessionToken, (byte)0) is > 0 and < 16, "session token terminator is inside the field");
var keyReply = EntranceProtocolEngine.Handle(original, session, new EntranceContent(null, [0]));
Check(keyReply?.ProtocolId == 12, "11 -> 12 key reply");
Check(keyReply?.TotalLength == 47, "key reply exact client length");
Check(keyReply?.RecordCount == 1, "key reply record count");

var cacheReply = EntranceProtocolEngine.Handle(PacketFrame.Create(5, new byte[16]), session, new EntranceContent(null, [0]));
Check(cacheReply?.ProtocolId == 6, "5 -> 6 cache reply");
var cachePlaintext = EntranceCrypto.DecryptBlocks(cacheReply!.Body.AsSpan(1), session.KeyMaterial);
Check(BinaryPrimitives.ReadInt32LittleEndian(cachePlaintext) == 1, "missing script reports cache-hit mode");

var script = Encoding.ASCII.GetBytes("channel-script-test");
var downloadReply = EntranceProtocolEngine.Handle(PacketFrame.Create(9, []), session, new EntranceContent(script, [0]));
var encryptedPadded = EntranceCrypto.Decompress(downloadReply!.Body.AsSpan(1));
var restored = EntranceCrypto.DecryptBlocks(encryptedPadded, session.KeyMaterial);
Check(restored.AsSpan(0, script.Length).SequenceEqual(script)
    && restored.AsSpan(script.Length).IndexOfAnyExcept((byte)0) < 0,
    "compressed encrypted script round-trip");

var metadata = EntranceMetadata.BuildSingleLocalChannel(
    new EntranceChannel("Local Channel", "127.0.0.1", 7001));
Check(metadata.Length == 652, "nine-group channel metadata length");
Check(BinaryPrimitives.ReadInt32LittleEndian(metadata) == 9, "channel metadata group count");
Check(Encoding.ASCII.GetString(metadata, 4, 4) == "cain", "first channel group name");
Check(Encoding.ASCII.GetString(metadata, 580, 5) == "anton", "DFLegacy anton channel group");
Check(BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(24, 4)) == 1, "first group channel count");
Check(Encoding.ASCII.GetString(metadata, 56, 9) == "127.0.0.1", "channel endpoint is loopback");
Check(BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(72, 4)) == 7001, "channel endpoint port");

var exitRequest = new PacketFrame(1, GameProtocolEngine.ExitCommand, 0, [0x34, 0x12]);
var exitReply = GameProtocolEngine.Handle(exitRequest);
Check(exitReply is not null, "channel exit command receives a reply");
Check(exitReply?.Type == 1 && exitReply.ProtocolId == 3, "channel reply preserves command type and id");
Check(exitReply?.TotalLength == 7, "server command reply uses its six-byte header");
Check(exitReply?.Payload.SequenceEqual(new byte[] { 1 }) == true, "channel reply reports success");
var encodedExitReply = exitReply!.Encode();
Check(encodedExitReply.AsSpan(0, 6).SequenceEqual(new byte[] { 1, 3, 7, 0, 0, 0 }), "server command reply header is exact");
var decodedExitPayload = encodedExitReply.AsSpan(6).ToArray();
GamePayloadCipher.DecryptServerPayload(decodedExitPayload);
Check(decodedExitPayload.SequenceEqual(new byte[] { 1 }), "server command reply transform round-trip");
var serverCipherVector = new byte[] { 2, 1, 0 };
GamePayloadCipher.EncryptServerPayload(serverCipherVector);
Check(serverCipherVector.SequenceEqual(new byte[] { 0xDE, 0xD2, 0xD6 }),
    "server payload encryption is the inverse of the DFLegacy native decryptor");
GamePayloadCipher.DecryptServerPayload(serverCipherVector);
Check(serverCipherVector.SequenceEqual(new byte[] { 2, 1, 0 }),
    "DFLegacy native server-payload transform restores the plaintext");

var returnSelectRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.ReturnSelectCharacterCommand,
    0,
    [0x35, 0x12]);
var returnSelectReply = GameProtocolEngine.Handle(returnSelectRequest);
Check(returnSelectReply is not null
    && returnSelectReply.Type == GameProtocolEngine.CommandPacketType
    && returnSelectReply.ProtocolId == GameProtocolEngine.ReturnSelectCharacterCommand
    && returnSelectReply.Payload.SequenceEqual(new byte[] { 1 }),
    "town return-to-character-selection command receives the required success acknowledgement");

var useSkillRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.UseSkillCommand,
    0,
    [0x36, 0x12, 0x2A, 0x01, 0x00]);
var useSkillReply = GameProtocolEngine.Handle(useSkillRequest);
Check(GameProtocolEngine.UseSkillCommand == 41
    && useSkillReply is not null
    && useSkillReply.Type == GameProtocolEngine.CommandPacketType
    && useSkillReply.ProtocolId == 41
    && useSkillReply.Payload.SequenceEqual(new byte[] { 1 }),
    "dungeon USE_SKILL receives the common success acknowledgement");
var privateStorePollRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.PrivateStorePollCommand,
    0,
    [0x37, 0x00]);
var privateStorePollReply = GameProtocolEngine.Handle(privateStorePollRequest);
Check(privateStorePollReply is not null
    && privateStorePollReply.ProtocolId == 82
    && privateStorePollReply.Payload.SequenceEqual(
        new byte[]
        {
            1, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0
        }),
    "DFLegacy private-store polling receives the required empty list layout");
var exchangeCharacterInfoRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.ExchangeServerCharacterInfoCommand,
    0,
    [0x38, 0x00]);
var exchangeCharacterInfoReply = GameProtocolEngine.Handle(exchangeCharacterInfoRequest);
Check(exchangeCharacterInfoReply is not null
    && exchangeCharacterInfoReply.ProtocolId == 130
    && exchangeCharacterInfoReply.Payload.SequenceEqual(new byte[] { 1, 0 }),
    "DFLegacy exchange-server character polling receives an empty success list");
var deleteItemReply = GameProtocolEngine.CreateDeleteItemReply(
    0,
    [new GameItemConsumptionEntry(8, 2)]);
Check(deleteItemReply.Type == GameProtocolEngine.CommandPacketType
    && deleteItemReply.ProtocolId == GameProtocolEngine.DeleteItemCommand
    && deleteItemReply.Payload.SequenceEqual(
        new byte[] { 1, 0, 1, 8, 0, 2, 0, 0, 0 }),
    "material transaction success reports the amount consumed from its slot");
Check(GameProtocolEngine.CreateDeleteItemError(0, 0x11).Payload.SequenceEqual(
        new byte[] { 0, 0x11, 0 }),
    "material transaction failure carries its inventory list type");
var lotteryReply = GameProtocolEngine.CreateUseLotteryItemReply(
    sourceSlot: 43,
    destinationSlot: 9,
    itemId: 14_873,
    countOrValue: 1,
    instanceValue: 1_000);
Check(lotteryReply.Type == GameProtocolEngine.CommandPacketType
    && lotteryReply.ProtocolId == GameProtocolEngine.UseLotteryItemCommand
    && lotteryReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        43, 0,
        9, 0,
        0x19, 0x3A,
        1, 0, 0, 0,
        0xE8, 0x03,
        0
    }),
    "DFLegacy command 29 uses the client-proven lottery result layout");
var increaseStatusReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 45,
    statusType: 0,
    amount: 20);
Check(increaseStatusReply.Type == GameProtocolEngine.CommandPacketType
    && increaseStatusReply.ProtocolId == GameProtocolEngine.IncreaseStatusCommand
    && increaseStatusReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        45, 0,
        0,
        20, 0, 0, 0,
        0, 0,
        0, 0
    }),
    "DFLegacy command 32 returns slot, increase-status type, amount and two auxiliary values");
var attributeStoneReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 52,
    statusType: (byte)IncreaseStatusType.AllElementResistance,
    amount: 10);
Check(attributeStoneReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        52, 0,
        9,
        10, 0, 0, 0,
        0, 0,
        0, 0
    }),
    "DFLegacy command 32 encodes old-client attribute notifications in x10 wire units");
var experienceBookReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 41,
    statusType: (byte)IncreaseStatusType.Experience,
    amount: 100_000,
    firstAuxiliaryValue: 60,
    secondAuxiliaryValue: 0);
Check(experienceBookReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        41, 0,
        1,
        0xA0, 0x86, 0x01, 0x00,
        60, 0,
        0, 0
    }),
    "DFLegacy command 32 experience reply carries granted EXP and level-up SP in the legacy auxiliary field");
var maximumHpStoneReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 53,
    statusType: (byte)IncreaseStatusType.MaximumHp,
    amount: 250);
Check(maximumHpStoneReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        53, 0,
        2,
        250, 0, 0, 0,
        0, 0,
        0, 0
    }),
    "DFLegacy command 32 encodes a persisted 25 HP increase as 250 wire units");
var strengthStoneReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 54,
    statusType: (byte)IncreaseStatusType.Strength,
    amount: 50);
Check(strengthStoneReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        54, 0,
        4,
        50, 0, 0, 0,
        0, 0,
        0, 0
    }),
    "DFLegacy command 32 encodes a persisted 5 STR increase as 50 wire units");
var skillResetReply = GameProtocolEngine.CreateIncreaseStatusReply(
    sourceSlot: 41,
    statusType: (byte)IncreaseStatusType.SkillReset,
    amount: 1);
Check(skillResetReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        41, 0,
        10,
        1, 0, 0, 0,
        0, 0,
        0, 0
    }),
    "DFLegacy command 32 uses client status type ten for a successful skill reset");
var disjointReply = GameProtocolEngine.CreateDisjointItemReply(
    sourceSlot: 9,
    itemSpace: 0,
    [
        new GameDisjointRewardEntry(73, 3_037, 8),
        new GameDisjointRewardEntry(74, 3_035, 1)
    ]);
Check(disjointReply.Type == GameProtocolEngine.CommandPacketType
    && disjointReply.ProtocolId == GameProtocolEngine.DisjointItemCommand
    && disjointReply.Payload.SequenceEqual(
    new byte[]
    {
        1,
        9, 0,
        0,
        2,
        73, 0, 0xDD, 0x0B, 8, 0, 0, 0,
        74, 0, 0xDB, 0x0B, 1, 0, 0, 0
    }),
    "DFLegacy command 28 uses the client-proven u16 disjoint reward layout");

var createCharacterRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.CreateCharacterCommand,
    0,
    [0x02, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, (byte)'1', (byte)'3']);
var createCharacterReply = GameProtocolEngine.Handle(createCharacterRequest);
Check(createCharacterReply is not null, "create-character command receives a reply");
Check(
    createCharacterReply?.Type == GameProtocolEngine.CommandPacketType
    && createCharacterReply.ProtocolId == GameProtocolEngine.CreateCharacterCommand,
    "create-character reply preserves command type and id");
Check(createCharacterReply?.Payload.SequenceEqual(new byte[] { 1 }) == true,
    "create-character reply reports success");
var duplicateCharacterReply = GameProtocolEngine.CreateCommandError(createCharacterRequest, 2);
Check(duplicateCharacterReply.Payload.SequenceEqual(new byte[] { 0, 2 }),
    "create-character failure carries the legacy error code");
var deleteCharacterRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.DeleteCharacterCommand,
    0,
    [0x04, 0x00, 0x03]);
var deleteCharacterReply = GameProtocolEngine.Handle(deleteCharacterRequest);
Check(deleteCharacterReply is not null
    && deleteCharacterReply.Type == GameProtocolEngine.CommandPacketType
    && deleteCharacterReply.ProtocolId == GameProtocolEngine.DeleteCharacterCommand
    && deleteCharacterReply.Payload.SequenceEqual(new byte[] { 1, 3 }),
    "delete-character success echoes the deleted slot required by the client");

var selectCharacterRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.SelectCharacterCommand,
    0,
    [0x03, 0x00, 0x00]);
var selectCharacterReply = GameProtocolEngine.Handle(selectCharacterRequest);
Check(selectCharacterReply is not null, "select-character command receives a reply");
Check(
    selectCharacterReply?.Type == GameProtocolEngine.CommandPacketType
    && selectCharacterReply.ProtocolId == GameProtocolEngine.SelectCharacterCommand,
    "select-character reply preserves command type and id");
Check(selectCharacterReply?.Payload.Length == 42,
    "select-character reply has the exact DFLegacy state-block length");
Check(selectCharacterReply?.Payload[0] == 1,
    "select-character reply reports success");
Check(BinaryPrimitives.ReadUInt16LittleEndian(selectCharacterReply!.Payload.AsSpan(9, 2))
        == GameProtocolEngine.DefaultMaximumFatigue,
    "select-character reply carries the normal DFLegacy fatigue maximum");
Check(BinaryPrimitives.ReadUInt16LittleEndian(selectCharacterReply.Payload.AsSpan(7, 2)) == 0
    && BinaryPrimitives.ReadUInt16LittleEndian(selectCharacterReply.Payload.AsSpan(11, 2)) == 0,
    "select-character reply carries zero used and premium fatigue for an ordinary character");
Check(selectCharacterReply.Payload.AsSpan(18, 18).SequenceEqual(
        new byte[]
        {
            0xFF, 0xFF, 0, 0, 0, 0,
            0xFF, 0xFF, 0, 0, 0, 0,
            0xFF, 0xFF, 0, 0, 0, 0
        }),
    "select-character reply marks all three DFLegacy quest slots with FFFF");
Check(selectCharacterReply.Payload[^6..].SequenceEqual(
        new byte[] { 0, 1, 0xFF, 0xFF, 0xFF, 0xFF }),
    "select-character reply marks all DFLegacy lower tutorial flags complete");
var selectCharacterBeforeTutorial = GameProtocolEngine.CreateSelectCharacterReply(
    selectCharacterRequest,
    completedTutorialFlags: 0);
Check(selectCharacterBeforeTutorial.Payload[^6..].SequenceEqual(
        new byte[] { 0, 1, 0, 0, 0, 0 }),
    "new-character select reply leaves all lower tutorial flags incomplete");
var selectCharacterWithQuests = GameProtocolEngine.CreateSelectCharacterReply(
    selectCharacterRequest,
    activeQuests: [new GameQuestEntry(1016, 3), new GameQuestEntry(22, 7)]);
Check(selectCharacterWithQuests.Payload.Length == 42
    && selectCharacterWithQuests.Payload.AsSpan(18, 18).SequenceEqual(
        new byte[]
        {
            0xF8, 0x03, 3, 0, 0, 0,
            22, 0, 7, 0, 0, 0,
            0xFF, 0xFF, 0, 0, 0, 0
        })
    && selectCharacterWithQuests.Payload[^6..].SequenceEqual(
        new byte[] { 0, 1, 0xFF, 0xFF, 0xFF, 0xFF }),
    "select-character reply restores DFLegacy active quest triggers");
var selectCharacterWithPremium = GameProtocolEngine.CreateSelectCharacterReply(
    selectCharacterRequest,
    maximumFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue,
    premiumFatigue: GameProtocolEngine.BlackDiamondBonusFatigue,
    premiumServices: [new GamePremiumServiceEntry(
        GameProtocolEngine.BlackDiamondServiceType,
        2_592_000)]);
Check(selectCharacterWithPremium.Payload.Length == 47
    && BinaryPrimitives.ReadUInt16LittleEndian(
        selectCharacterWithPremium.Payload.AsSpan(9, 2))
        == GameProtocolEngine.BlackDiamondMaximumFatigue
    && BinaryPrimitives.ReadUInt16LittleEndian(
        selectCharacterWithPremium.Payload.AsSpan(7, 2)) == 0
    && BinaryPrimitives.ReadUInt16LittleEndian(
        selectCharacterWithPremium.Payload.AsSpan(11, 2))
        == GameProtocolEngine.BlackDiamondBonusFatigue
    && selectCharacterWithPremium.Payload[13] == 1
    && selectCharacterWithPremium.Payload[14] == GameProtocolEngine.BlackDiamondServiceType
    && BinaryPrimitives.ReadUInt32LittleEndian(
        selectCharacterWithPremium.Payload.AsSpan(15, 4)) == 2_592_000,
    "select-character reply carries the account Premium service type and remaining seconds");
var completedQuestLayoutRejected = false;
try
{
    _ = GameProtocolEngine.CreateSelectCharacterReply(
        selectCharacterRequest,
        completedQuestIds: [1, 9]);
}
catch (NotSupportedException)
{
    completedQuestLayoutRejected = true;
}
Check(completedQuestLayoutRejected,
    "select-character reply refuses to guess bitmap positions without a PVF quest mapping");

var currentCharacterInfo = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(1, "123"u8.ToArray(), 0, 0, 1),
    userId: 1);
Check(
    currentCharacterInfo.Type == GameProtocolEngine.NotificationPacketType
    && currentCharacterInfo.ProtocolId == GameProtocolEngine.UserInfoNotification,
    "current character is sent as a USERINFO notification");
Check(currentCharacterInfo.Payload.Length == 56,
    "mode-zero current-character record has the exact mapped layout");
Check(currentCharacterInfo.Payload.AsSpan(0, 5).SequenceEqual(new byte[] { 0, 1, 0, 1, 0 }),
    "current-character USERINFO selects mode zero and matches the local user id");
Check(BinaryPrimitives.ReadUInt32LittleEndian(currentCharacterInfo.Payload.AsSpan(5, 4)) == 3,
    "current-character name is length prefixed");
var awakenedCharacterInfo = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(
        1,
        "123"u8.ToArray(),
        2,
        1,
        54,
        AwakeningType: 1),
    userId: 1);
Check(awakenedCharacterInfo.Payload[13] == 0x11
    && awakenedCharacterInfo.Payload[15] == 0
    && awakenedCharacterInfo.Payload[16] == 0,
    "mode-zero USERINFO keeps awakening packed separately from the default 10级 PVP rank");
var blackDiamondCharacterInfo = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(1, "123"u8.ToArray(), 0, 0, 1),
    userId: 1,
    hasBlackDiamond: true);
Check(blackDiamondCharacterInfo.Payload.Length == currentCharacterInfo.Payload.Length
    && currentCharacterInfo.Payload[33] == 0
    && blackDiamondCharacterInfo.Payload[33] == 1
    && currentCharacterInfo.Payload[43] == 1
    && blackDiamondCharacterInfo.Payload[43] == 1
    && currentCharacterInfo.Payload.Where((value, index) => index != 33)
        .SequenceEqual(blackDiamondCharacterInfo.Payload.Where((value, index) => index != 33)),
    "black diamond changes only USERINFO record +0xB8 while the active-entity gate stays enabled");
var currentCharacterWithCreatureAppearance = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(1, "123"u8.ToArray(), 0, 0, 1),
    userId: 1,
    equippedCreature: new GameCreatureAppearanceEntry(
        63_008,
        "Botis"u8.ToArray()));
Check(currentCharacterWithCreatureAppearance.Payload.Length == 61
    && currentCharacterWithCreatureAppearance.Payload.AsSpan(25, 12).SequenceEqual(
        new byte[]
        {
            0x20, 0xF6,
            5, 0, 0, 0,
            (byte)'B', (byte)'o', (byte)'t', (byte)'i', (byte)'s',
            1
        }),
    "mode-zero USERINFO carries the equipped Creature item id, name, and wire-active state used to register its map object");
Check(GameProtocolEngine.ResponseCreatureCommand == 104,
    "DFLegacy RESPONSE_CREATURE uses old-client command 104 rather than evolute notification 106");

var currentCharacterWithTitleAppearance = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(1, "123"u8.ToArray(), 0, 0, 1),
    userId: 1,
    equippedItems: [new GameInventoryEntry(10, 12_345, 1)]);
Check(currentCharacterWithTitleAppearance.Payload.Length == 60
    && currentCharacterWithTitleAppearance.Payload[18] == 1
    && currentCharacterWithTitleAppearance.Payload.AsSpan(19, 4).SequenceEqual(
        new byte[] { 10, 0x39, 0x30, 0 })
    && BinaryPrimitives.ReadUInt32LittleEndian(
        currentCharacterWithTitleAppearance.Payload.AsSpan(38, 4)) == 0
    && currentCharacterWithTitleAppearance.Payload[42] == 0
    && currentCharacterWithTitleAppearance.Payload[47] == 1,
    "mode-zero USERINFO carries title equipment in compact slot ten and keeps the independent name-grade score zero");

var currentWeaponAppearance = GameProtocolEngine.CreateCurrentCharacterInfo(
    new GameCharacterSummary(1, "123"u8.ToArray(), 0, 0, 1),
    userId: 1,
    equippedItems: [new GameInventoryEntry(9, 27546, 1)]);
Check(currentWeaponAppearance.Payload.Length == 60
    && currentWeaponAppearance.Payload[18] == 1
    && currentWeaponAppearance.Payload.AsSpan(19, 4).SequenceEqual(
        new byte[] { 9, 0x9A, 0x6B, 0 }),
    "mode-zero USERINFO carries one exact DFLegacy weapon appearance record");

var currentCharacterDetails = GameProtocolEngine.CreateCurrentCharacterDetails(userId: 1);
Check(currentCharacterDetails.Type == GameProtocolEngine.NotificationPacketType
    && currentCharacterDetails.ProtocolId == GameProtocolEngine.UserInfoNotification,
    "full current-character data uses USERINFO notification two");
Check(currentCharacterDetails.Payload.Length == 97,
    "mode-one current-character data has the exact DFLegacy layout");
Check(currentCharacterDetails.Payload.AsSpan(0, 5).SequenceEqual(new byte[] { 1, 1, 0, 1, 0 }),
    "full current-character USERINFO selects subtype one and the local user id");
var currentCharacterWithExperience = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    experience: 1_234);
Check(BinaryPrimitives.ReadUInt32LittleEndian(
        currentCharacterWithExperience.Payload.AsSpan(5, 4)) == 1_234,
    "full current-character USERINFO carries the persisted cumulative experience");
Check(BinaryPrimitives.ReadUInt32LittleEndian(currentCharacterDetails.Payload.AsSpan(9, 4)) == 81,
    "full current-character USERINFO prefixes the exact 81-byte stat block");
Check(BinaryPrimitives.ReadUInt32LittleEndian(currentCharacterDetails.Payload.AsSpan(13, 4)) == 11_600,
    "full current-character USERINFO carries a usable maximum HP value");
Check(currentCharacterDetails.Payload[^3..].SequenceEqual(new byte[] { 0, 0, 0 }),
    "full current-character USERINFO starts with empty equipment, skills and creature data");
var weakenedCharacterDetails = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    staminaRecoveryPercentage: 37);
Check(weakenedCharacterDetails.Payload[93] == 37,
    "mode-one current-character data carries the weakness recovery percentage in stat byte 80");

var scriptedCombatStats = new GameCharacterCombatStats(
    MaximumHp: 1_500,
    MaximumMp: 1_500,
    PhysicalAttack: 50,
    PhysicalDefense: 50,
    MagicalAttack: 60,
    MagicalDefense: 50,
    FireResistance: 0,
    WaterResistance: 0,
    DarkResistance: 200,
    LightResistance: -200,
    InventoryLimit: 400_000,
    HpRegeneration: 0,
    MpRegeneration: 150,
    MovementSpeed: 8_500,
    AttackSpeed: 8_500,
    CastSpeed: 7_000,
    HitRecovery: 6_000,
    JumpPower: 4_300,
    Weight: 680_000);
var currentCharacterWithScriptedStats = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    combatStats: scriptedCombatStats);
Check(BinaryPrimitives.ReadUInt32LittleEndian(
        currentCharacterWithScriptedStats.Payload.AsSpan(13, 4)) == 1_500,
    "mode-one current-character data serializes the character-specific maximum HP");
Check(BinaryPrimitives.ReadInt16LittleEndian(
        currentCharacterWithScriptedStats.Payload.AsSpan(33, 2)) == 200
    && BinaryPrimitives.ReadInt16LittleEndian(
        currentCharacterWithScriptedStats.Payload.AsSpan(35, 2)) == -200,
    "mode-one current-character data preserves signed elemental resistances");
Check(BinaryPrimitives.ReadUInt32LittleEndian(
        currentCharacterWithScriptedStats.Payload.AsSpan(71, 4)) == 400_000,
    "mode-one current-character data serializes the script inventory limit");

var currentCharacterWithSkills = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    learnedSkills: [new GameSkillEntry(5, 20), new GameSkillEntry(8, 10)]);
Check(currentCharacterWithSkills.Payload.Length == 101,
    "mode-one current-character data appends compact learned skills");
Check(currentCharacterWithSkills.Payload.AsSpan(94).SequenceEqual(
        new byte[] { 0, 2, 5, 20, 8, 10, 0 }),
    "mode-one learned skills follow the empty equipment count and end before creature level");

var currentCharacterWithEquipment = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    equippedItems:
    [
        new GameInventoryEntry(
            Slot: 9,
            ItemId: 27546,
            CountOrValue: 1,
            State: 0,
            Durability: 1000)
    ]);
Check(currentCharacterWithEquipment.Payload.AsSpan(94).SequenceEqual(
        new byte[]
        {
            1,
            9, 0x9A, 0x6B, 1, 0, 0, 0, 0, 0xE8, 0x03,
            0,
            0
        }),
    "mode-one equipment snapshot uses the exact 10-byte worn-slot record");
var currentCharacterWithAvatar = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    equippedItems:
    [
        new GameInventoryEntry(
            Slot: 0,
            ItemId: 39_428,
            CountOrValue: 0,
            State: 0,
            Durability: 0)
    ]);
Check(currentCharacterWithAvatar.Payload.AsSpan(94).SequenceEqual(
        new byte[]
        {
            1,
            0, 0x04, 0x9A, 0, 0, 0, 0, 0, 0, 0,
            0,
            0
        }),
    "mode-one Avatar snapshot carries permanent lifetime and first ability selection");
var currentCharacterWithCreature = GameProtocolEngine.CreateCurrentCharacterDetails(
    userId: 1,
    equippedItems:
    [
        new GameInventoryEntry(
            Slot: 19,
            ItemId: 63_008,
            CountOrValue: 1)
    ],
    equippedCreatureLevel: 7);
Check(currentCharacterWithCreature.Payload.AsSpan(94).SequenceEqual(
        new byte[]
        {
            1,
            19, 0x20, 0xF6, 1, 0, 0, 0, 0, 0, 0,
            0,
            7
        }),
    "mode-one current-character data carries the equipped Creature item in slot 19 before its trailing level");

var userState = GameProtocolEngine.CreateUserState(userId: 1, state: 0);
Check(userState.Type == GameProtocolEngine.NotificationPacketType
    && userState.ProtocolId == GameProtocolEngine.UserStateNotification,
    "town state uses USER_STATE notification three");
Check(userState.Payload.SequenceEqual(new byte[] { 1, 0, 0 }),
    "town state identifies the local character and keeps its normal state");

var itemList = GameProtocolEngine.CreateItemList(
    0,
    [new GameInventoryEntry(3, 27093, 1, Durability: 1000)]);
Check(itemList.ProtocolId == GameProtocolEngine.ItemListNotification,
    "inventory initialization uses ITEM_LIST notification thirteen");
Check(itemList.Payload.Length == 15,
    "one compact inventory record has the exact mapped length");
Check(itemList.Payload.AsSpan(0, 3).SequenceEqual(new byte[] { 0, 1, 0 }),
    "inventory payload carries list type and item count");
Check(BinaryPrimitives.ReadUInt16LittleEndian(itemList.Payload.AsSpan(5, 2)) == 27093,
    "inventory payload preserves the client item id");
var avatarItemList = GameProtocolEngine.CreateItemList(
    1,
    [new GameInventoryEntry(104, 39_000, 0, Durability: 0)]);
Check(avatarItemList.Payload.Length == 15
    && avatarItemList.Payload.AsSpan(0, 5).SequenceEqual(
        new byte[] { 1, 1, 0, 104, 0 })
    && BinaryPrimitives.ReadUInt16LittleEndian(
        avatarItemList.Payload.AsSpan(5, 2)) == 39_000
    && BinaryPrimitives.ReadUInt32LittleEndian(
        avatarItemList.Payload.AsSpan(7, 4)) == 0
    && BinaryPrimitives.ReadUInt16LittleEndian(
        avatarItemList.Payload.AsSpan(12, 2)) == 0,
    "Avatar inventory uses protocol-13 list one with permanent lifetime and first ability selection");
var creatureInfo = GameProtocolEngine.CreateCreatureInfo(
[
    new GameCreatureInfoEntry(
        0x11223344,
        100,
        0x55667788,
        7,
        "Pet"u8.ToArray(),
        NoCharge: true)
]);
Check(creatureInfo.Type == GameProtocolEngine.NotificationPacketType
    && creatureInfo.ProtocolId == GameProtocolEngine.CreatureInfoNotification
    && creatureInfo.Payload.SequenceEqual(
        new byte[]
        {
            1,
            0x44, 0x33, 0x22, 0x11,
            100,
            0x88, 0x77, 0x66, 0x55,
            7,
            3, 0, 0, 0, (byte)'P', (byte)'e', (byte)'t',
            1
        }),
    "DFLegacy Creature info notification 105 uses the old uid/stomach/experience/level/name/no-charge record");
var creatureState = GameProtocolEngine.CreateCreatureState(
    creatureUid: 0x11223344,
    state: 3);
Check(creatureState.Type == GameProtocolEngine.NotificationPacketType
    && creatureState.ProtocolId == GameProtocolEngine.CreatureStateNotification
    && creatureState.Payload.SequenceEqual(
        new byte[] { 0x44, 0x33, 0x22, 0x11, 3, 0, 0, 0 }),
    "DFLegacy Creature state notification 103 carries the Creature uid and state as u32 values");
var creatureResponse = GameProtocolEngine.CreateCreatureResponse(userId: 1);
Check(creatureResponse.Type == GameProtocolEngine.NotificationPacketType
    && creatureResponse.ProtocolId == GameProtocolEngine.CreatureResponseNotification
    && creatureResponse.Payload.SequenceEqual(new byte[] { 1, 0 }),
    "DFLegacy Creature response notification 104 carries the area user id as u16");
Check(GameProtocolEngine.CreateHatchCreatureReply(success: true).Payload.SequenceEqual(
        new byte[] { 1 })
    && GameProtocolEngine.CreateHatchCreatureReply(success: false, errorCode: 23)
        .Payload.SequenceEqual(new byte[] { 0, 23 }),
    "Creature hatch command 105 uses the old client's result-first command reply");
Check(GameProtocolEngine.CreateRenameCreatureReply(
        success: true,
        cardSlot: 190,
        itemSpace: 7).Payload.SequenceEqual(
        new byte[] { 1, 190, 0, 7 })
    && GameProtocolEngine.CreateRenameCreatureReply(
            success: false,
            errorCode: 4).Payload.SequenceEqual(new byte[] { 0, 4 }),
    "Creature rename command 103 returns success, the consumed card slot and Creature item space");
var creatureRenameNotification = GameProtocolEngine.CreateCreatureRename(
    userId: 1,
    nameBytes: [0xCD, 0xC3, 0xD7, 0xD3]);
Check(creatureRenameNotification.ProtocolId
        == GameProtocolEngine.CreatureRenameNotification
    && creatureRenameNotification.Payload.SequenceEqual(
        new byte[] { 1, 0, 4, 0, 0, 0, 0xCD, 0xC3, 0xD7, 0xD3 }),
    "Creature rename notification 101 carries the area user id and GBK length-prefixed name");
var creatureItemList = GameProtocolEngine.CreateItemList(
    7,
    [new GameInventoryEntry(238, 50_000, 0x11223344)]);
Check(creatureItemList.Payload.Length == 15
    && creatureItemList.Payload[0] == 7
    && BinaryPrimitives.ReadUInt16LittleEndian(
        creatureItemList.Payload.AsSpan(3, 2)) == 238
    && BinaryPrimitives.ReadUInt16LittleEndian(
        creatureItemList.Payload.AsSpan(5, 2)) == 50_000
    && BinaryPrimitives.ReadUInt32LittleEndian(
        creatureItemList.Payload.AsSpan(7, 4)) == 0x11223344,
    "Creature inventory uses protocol-13 list seven and preserves the equipped raw slot and uid");
var fundedMainInventory = GameProtocolEngine.CreateMainItemList(
    1_000_000,
    [new GameInventoryEntry(9, 3037, 1_000)]);
Check(fundedMainInventory.Payload.Length == 39
    && fundedMainInventory.Payload.AsSpan(0, 7).SequenceEqual(
        new byte[] { 0, 3, 0, 0, 0, 0, 0 })
    && BinaryPrimitives.ReadUInt32LittleEndian(
        fundedMainInventory.Payload.AsSpan(7, 4)) == 1_000_000
    && BinaryPrimitives.ReadUInt16LittleEndian(
        fundedMainInventory.Payload.AsSpan(27, 2)) == 9
    && BinaryPrimitives.ReadUInt16LittleEndian(
        fundedMainInventory.Payload.AsSpan(29, 2)) == 3037
    && BinaryPrimitives.ReadUInt32LittleEndian(
        fundedMainInventory.Payload.AsSpan(31, 4)) == 1_000,
    "main inventory updates virtual gold slot zero and keeps colorless cubes in the bag");
var revivalCoinMainInventory = GameProtocolEngine.CreateMainItemList(
    50_000,
    [new GameInventoryEntry(1, 1, 20)]);
Check(revivalCoinMainInventory.Payload.Length == 39
    && BinaryPrimitives.ReadUInt16LittleEndian(
        revivalCoinMainInventory.Payload.AsSpan(1, 2)) == 3
    && BinaryPrimitives.ReadUInt16LittleEndian(
        revivalCoinMainInventory.Payload.AsSpan(27, 2)) == 1
    && BinaryPrimitives.ReadUInt16LittleEndian(
        revivalCoinMainInventory.Payload.AsSpan(29, 2)) == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(
        revivalCoinMainInventory.Payload.AsSpan(31, 4)) == 20,
    "main inventory updates the DFLegacy revival-coin virtual object at slot and item id one");
var victoryPointMainInventory = GameProtocolEngine.CreateMainItemList(
    50_000,
    100,
    [new GameInventoryEntry(1, 1, 20)]);
Check(victoryPointMainInventory.Payload.Length == 39
    && BinaryPrimitives.ReadUInt16LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(1, 2)) == 3
    && BinaryPrimitives.ReadUInt16LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(15, 2)) == 2
    && BinaryPrimitives.ReadUInt16LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(17, 2)) == 2
    && BinaryPrimitives.ReadUInt32LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(19, 4)) == 100
    && BinaryPrimitives.ReadUInt16LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(27, 2)) == 1
    && BinaryPrimitives.ReadUInt16LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(29, 2)) == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(
        victoryPointMainInventory.Payload.AsSpan(31, 4)) == 20,
    "main inventory synthesizes the DFLegacy victory-point object at slot and item id two");
var emptyCargo = GameProtocolEngine.CreateItemList(2, [], listParameter: 8);
Check(emptyCargo.Payload.SequenceEqual(new byte[] { 2, 8, 0, 0, 0 }),
    "personal cargo carries its additional list parameter");
var upgradedCargo = GameProtocolEngine.CreateItemList(2, [], listParameter: 24);
Check(upgradedCargo.ProtocolId == GameProtocolEngine.ItemListNotification
    && upgradedCargo.Payload.SequenceEqual(new byte[] { 2, 24, 0, 0, 0 }),
    "warehouse upgrades refresh target client notification 13 with the absolute u16 capacity");
Check(CharacterWarehouseProgression.CapacitySteps.SequenceEqual(
        new ushort[] { 8, 24, 40, 56, 72, 88, 104, 120 })
    && CharacterWarehouseProgression.Normalize(0) == 8
    && CharacterWarehouseProgression.Normalize(39) == 24
    && CharacterWarehouseProgression.Normalize(ushort.MaxValue) == 120
    && CharacterWarehouseProgression.IsNextUpgrade(8, 24)
    && !CharacterWarehouseProgression.IsNextUpgrade(8, 40)
    && !CharacterWarehouseProgression.TryGetNextCapacity(120, out _),
    "target client warehouse UI remains staged in 16-slot increments and capped at 120");
Check(CharacterWarehouseProgression.TryGetItemTargetCapacity(5, out var copperCapacity)
    && copperCapacity == 24
    && CharacterWarehouseProgression.TryGetItemTargetCapacity(6, out var silverCapacity)
    && silverCapacity == 40
    && CharacterWarehouseProgression.TryGetItemTargetCapacity(7, out var goldCapacity)
    && goldCapacity == 56,
    "obsolete copper, silver and gold kits resolve to the target client's recognized named grades");
var crossLevelWarehouseSettlement =
    CharacterWarehouseProgression.SettleUpgradeItems(
        [
            new CharacterItemRecord(9, 60, 1),
            new CharacterItemRecord(10, 3_037, 5)
        ],
        [new CharacterItemRecord(3, 57, 2)],
        currentCapacity: 8);
var lowerWarehouseSettlement =
    CharacterWarehouseProgression.SettleUpgradeItems(
        [new CharacterItemRecord(11, 50, 1)],
        [],
        currentCapacity: 104);
var goldenWarehouseSettlement =
    CharacterWarehouseProgression.SettleUpgradeItems(
        [new CharacterItemRecord(43, 68, 2)],
        [],
        currentCapacity: 8);
var legacyWarehouseSettlement =
    CharacterWarehouseProgression.SettleUpgradeItems(
        [
            new CharacterItemRecord(44, 5, 1),
            new CharacterItemRecord(45, 6, 1),
            new CharacterItemRecord(46, 7, 1)
        ],
        [],
        currentCapacity: 8);
var lowerLegacyWarehouseSettlement =
    CharacterWarehouseProgression.SettleUpgradeItems(
        [new CharacterItemRecord(47, 7, 1)],
        [],
        currentCapacity: 72);
Check(crossLevelWarehouseSettlement.Capacity == 88
    && crossLevelWarehouseSettlement.Inventory.SequenceEqual(
        [new CharacterItemRecord(10, 3_037, 5)])
    && crossLevelWarehouseSettlement.Warehouse.Count == 0
    && crossLevelWarehouseSettlement.ConsumedInventorySlots.SequenceEqual(
        new ushort[] { 9 })
    && crossLevelWarehouseSettlement.ConsumedWarehouseSlots.SequenceEqual(
        new ushort[] { 3 })
    && lowerWarehouseSettlement.Capacity == 104
    && lowerWarehouseSettlement.Inventory.Count == 0
    && goldenWarehouseSettlement.Capacity == 72
    && goldenWarehouseSettlement.Inventory.Count == 0
    && goldenWarehouseSettlement.ConsumedInventorySlots.SequenceEqual(
        new ushort[] { 43 })
    && legacyWarehouseSettlement.Capacity == 56
    && legacyWarehouseSettlement.Inventory.Count == 0
    && legacyWarehouseSettlement.ConsumedInventorySlots.SequenceEqual(
        new ushort[] { 44, 45, 46 })
    && lowerLegacyWarehouseSettlement.Capacity == 72
    && lowerLegacyWarehouseSettlement.Inventory.Count == 0
    && CharacterWarehouseProgression.ApplyItemTarget(8, 62) == 120,
    "legacy, normal and golden warehouse upgrade items jump directly to their target, never downgrade and are removed from every character item space");
var warehouseSettlementRoot = Path.Combine(
    Path.GetTempPath(),
    $"dflegacy-warehouse-settlement-{Guid.NewGuid():N}");
Directory.CreateDirectory(warehouseSettlementRoot);
try
{
    var warehouseSettlementOptions = new ServerOptions
    {
        DataPath = Path.Combine(warehouseSettlementRoot, "state.json")
    };
    var warehouseSettlementStore = new JsonGameStore(
        warehouseSettlementOptions,
        NullLogger<JsonGameStore>.Instance);
    await warehouseSettlementStore.InitializeAsync();
    var warehouseSettlementCharacter =
        (await warehouseSettlementStore.GetCharactersAsync("test"))[0];
    warehouseSettlementCharacter =
        await warehouseSettlementStore.SaveCharacterInventoryAsync(
            "test",
            warehouseSettlementCharacter.Id,
            warehouseSettlementCharacter.Gold,
            (warehouseSettlementCharacter.Inventory ?? [])
                .Append(new CharacterItemRecord(80, 68, 2)),
            warehouseSettlementCharacter.Equipment ?? [])
        ?? warehouseSettlementCharacter;
    warehouseSettlementCharacter =
        await warehouseSettlementStore.SaveCharacterInventoryAsync(
            "test",
            warehouseSettlementCharacter.Id,
            warehouseSettlementCharacter.Gold,
            warehouseSettlementCharacter.Inventory ?? [],
            warehouseSettlementCharacter.Equipment ?? [],
            generatedMails:
            [
                new CreateCharacterMailRequest(
                    "DFLegacy",
                    "overflow upgrade",
                    Attachment: new CharacterMailAttachmentRecord(61, 1))
            ])
        ?? warehouseSettlementCharacter;
    warehouseSettlementCharacter =
        await warehouseSettlementStore.SaveCharacterItemSpacesAsync(
            "test",
            warehouseSettlementCharacter.Id,
            warehouseSettlementCharacter.Gold,
            warehouseSettlementCharacter.Inventory ?? [],
            warehouseSettlementCharacter.Equipment ?? [],
            [new CharacterItemRecord(3, 50, 1)])
        ?? warehouseSettlementCharacter;
    var reloadedWarehouseSettlementStore = new JsonGameStore(
        warehouseSettlementOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedWarehouseSettlementStore.InitializeAsync();
    var reloadedWarehouseSettlementCharacter =
        (await reloadedWarehouseSettlementStore.GetCharactersAsync("test"))[0];
    Check(warehouseSettlementCharacter.WarehouseCapacity == 104
        && warehouseSettlementCharacter.Inventory?.All(item =>
            !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                item.ItemId,
                out _)) == true
        && warehouseSettlementCharacter.Warehouse?.Count == 0
        && warehouseSettlementCharacter.Mailbox is null or { Count: 0 }
        && reloadedWarehouseSettlementCharacter.WarehouseCapacity == 104
        && reloadedWarehouseSettlementCharacter.Inventory?.All(item =>
            !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                item.ItemId,
                out _)) == true,
        "online persistence consumes acquired and overflow-mailed warehouse kits atomically without writing an item or mail attachment to JSON");
}
finally
{
    Directory.Delete(warehouseSettlementRoot, recursive: true);
}

var mailboxOpen = GameProtocolEngine.CreateMailboxOpenReply();
Check(mailboxOpen.Type == GameProtocolEngine.CommandPacketType
    && mailboxOpen.ProtocolId == GameProtocolEngine.MailboxOpenCommand
    && mailboxOpen.Payload.SequenceEqual(new byte[] { 1, 0, 0 }),
    "mailbox open reply carries success and the signed not-loaded count");
var mailboxList = GameProtocolEngine.CreateMailboxList(
[
    new GameMailEntry(
        MailId: 0x11223344,
        SenderBytes: "Seria"u8.ToArray(),
        TextBytes: "test"u8.ToArray(),
        Gold: 500,
        Attachment: new GameMailAttachment(
            ItemId: 3037,
            CountOrValue: 25,
            State: 3,
            Durability: 400,
            SealState: 1),
        SentAt: 1000,
        ExpiresAt: 2000,
        State: 2)
]);
Check(mailboxList.Type == GameProtocolEngine.NotificationPacketType
    && mailboxList.ProtocolId == GameProtocolEngine.MailboxMailListNotification
    && mailboxList.Payload.Length == 72
    && mailboxList.Payload[0] == 1
    && mailboxList.Payload[1] == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(mailboxList.Payload.AsSpan(2, 4))
        == 0x11223344
    && BinaryPrimitives.ReadUInt32LittleEndian(mailboxList.Payload.AsSpan(6, 4)) == 5
    && BinaryPrimitives.ReadUInt32LittleEndian(mailboxList.Payload.AsSpan(15, 4)) == 500
    && BinaryPrimitives.ReadUInt16LittleEndian(mailboxList.Payload.AsSpan(19, 2)) == 3037
    && mailboxList.Payload[21] == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(mailboxList.Payload.AsSpan(22, 4)) == 25
    && BinaryPrimitives.ReadUInt16LittleEndian(mailboxList.Payload.AsSpan(26, 2)) == 400
    && mailboxList.Payload[28] == 3
    && BinaryPrimitives.ReadUInt16LittleEndian(mailboxList.Payload.AsSpan(39, 2)) == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(mailboxList.Payload.AsSpan(41, 4))
        == 0x11223344
    && BinaryPrimitives.ReadUInt16LittleEndian(mailboxList.Payload.AsSpan(70, 2)) == 2,
    "mailbox list uses the DFLegacy package tuple and correlated text record");
var archivedMailboxList = GameProtocolEngine.CreateMailboxList(
[
    new GameMailEntry(
        MailId: 0x55667788,
        SenderBytes: "Seria"u8.ToArray(),
        TextBytes: "kept"u8.ToArray(),
        Gold: 0,
        Attachment: null,
        SentAt: 3000,
        ExpiresAt: 4000,
        State: 3)
], includeEmptyPackageCarriers: true);
Check(archivedMailboxList.Payload.Length == 72
    && archivedMailboxList.Payload[0] == 1
    && archivedMailboxList.Payload[1] == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        archivedMailboxList.Payload.AsSpan(2, 4)) == 0x55667788
    && BinaryPrimitives.ReadUInt16LittleEndian(
        archivedMailboxList.Payload.AsSpan(39, 2)) == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(
        archivedMailboxList.Payload.AsSpan(41, 4)) == 0x55667788
    && BinaryPrimitives.ReadUInt16LittleEndian(
        archivedMailboxList.Payload.AsSpan(70, 2)) == 3,
    "an isolated archived-mail restore carries its own id into the state-three body");
var mixedMailboxSnapshot = GameProtocolEngine.CreateMailboxSnapshot(
[
    new GameMailEntry(
        MailId: 0x11223344,
        SenderBytes: "Seria"u8.ToArray(),
        TextBytes: "test"u8.ToArray(),
        Gold: 500,
        Attachment: new GameMailAttachment(3037, 25, 3, 400, 1),
        SentAt: 1000,
        ExpiresAt: 2000,
        State: 2),
    new GameMailEntry(
        MailId: 0x55667788,
        SenderBytes: "Seria"u8.ToArray(),
        TextBytes: "kept"u8.ToArray(),
        Gold: 0,
        Attachment: null,
        SentAt: 3000,
        ExpiresAt: 4000,
        State: 3)
]);
Check(mixedMailboxSnapshot.Count == 2
    && mixedMailboxSnapshot[0].Payload.Length == 72
    && mixedMailboxSnapshot[0].Payload[0] == 1
    && mixedMailboxSnapshot[0].Payload[1] == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        mixedMailboxSnapshot[0].Payload.AsSpan(2, 4)) == 0x55667788
    && BinaryPrimitives.ReadUInt32LittleEndian(
        mixedMailboxSnapshot[0].Payload.AsSpan(41, 4)) == 0x55667788
    && BinaryPrimitives.ReadUInt16LittleEndian(
        mixedMailboxSnapshot[0].Payload.AsSpan(70, 2)) == 3
    && mixedMailboxSnapshot[1].Payload.Length == 72
    && mixedMailboxSnapshot[1].Payload[0] == 1
    && mixedMailboxSnapshot[1].Payload[1] == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(
        mixedMailboxSnapshot[1].Payload.AsSpan(2, 4)) == 0x11223344
    && BinaryPrimitives.ReadUInt16LittleEndian(
        mixedMailboxSnapshot[1].Payload.AsSpan(70, 2)) == 2,
    "mailbox snapshot restores archives separately before incrementally loading inbox mail");
var mailboxExtract = GameProtocolEngine.CreateMailboxExtractReply(0x11223344);
Check(mailboxExtract.Payload.SequenceEqual(new byte[] { 1, 0x44, 0x33, 0x22, 0x11 }),
    "mail extraction reply returns the removed mail id");
var mailboxState = GameProtocolEngine.CreateMailboxStateReply(0x11223344, 2);
Check(mailboxState.ProtocolId == GameProtocolEngine.MailboxStateCommand
    && mailboxState.Payload.SequenceEqual(
        new byte[] { 1, 0x44, 0x33, 0x22, 0x11, 2, 0 }),
    "mail state reply echoes the id and two-byte DFLegacy state");
Check(GameProtocolEngine.CreateMailboxAlarm(2).Payload.SequenceEqual(new byte[] { 2, 0 }),
    "mail alarm carries the count consumed by the client's system-message path");
var equipmentUpdate = GameProtocolEngine.CreateUpdateItemList(
    3,
    [new GameInventoryEntry(18, 24032, 1, 0, 1000, 0)]);
Check(equipmentUpdate.ProtocolId == GameProtocolEngine.UpdateItemListNotification
    && equipmentUpdate.Payload.SequenceEqual(
        new byte[] { 3, 1, 0, 18, 0, 0xE0, 0x5D, 1, 0, 0, 0, 0, 0xE8, 3, 0 }),
    "55 list-three update carries one complete worn-equipment record");
var equipmentRemoval = GameProtocolEngine.CreateUpdateItemList(
    3,
    [new GameInventoryEntry(18, ushort.MaxValue, 0)]);
Check(equipmentRemoval.Payload.SequenceEqual(
        new byte[] { 3, 1, 0, 18, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }),
    "55 list-three deletion uses the existing worn slot and sentinel item id");

var moveItemReply = GameProtocolEngine.CreateMoveItemSpaceReply(
    sourceListType: 0,
    sourceSlot: 13,
    movedCount: 1,
    destinationListType: 3,
    destinationSlot: 9);
Check(moveItemReply.ProtocolId == GameProtocolEngine.MoveItemSpaceCommand
    && moveItemReply.Payload.SequenceEqual(
        new byte[] { 1, 0, 13, 0, 1, 0, 0, 0, 3, 9, 0 }),
    "DFLegacy move-item reply carries both list/slot endpoints and the moved count");
var rejectedMoveReply = GameProtocolEngine.CreateMoveItemSpaceReply(
    sourceListType: 0,
    sourceSlot: 76,
    movedCount: 0,
    destinationListType: 0,
    destinationSlot: 8);
Check(rejectedMoveReply.Payload.SequenceEqual(
        new byte[] { 1, 0, 76, 0, 0, 0, 0, 0, 0, 8, 0 }),
    "a rejected DFLegacy inventory move is completed as a zero-count success reply");
var sortItemReply = GameProtocolEngine.CreateSortItemReply(0);
Check(sortItemReply.ProtocolId == GameProtocolEngine.SortItemCommand
    && sortItemReply.Payload.SequenceEqual(new byte[] { 1, 0 }),
    "DFLegacy sort-item success completes the pending main-inventory operation");
Check(GameProtocolEngine.CreateSortItemReply(2).Payload.SequenceEqual(new byte[] { 1, 2 }),
    "DFLegacy sort-item success identifies the character warehouse item space");
Check(GameProtocolEngine.CreateSortItemError(7, 4).Payload.SequenceEqual(
        new byte[] { 0, 4, 7 }),
    "DFLegacy sort-item failure carries the error and item space consumed by the old client");
var inventoryMoveParser = typeof(EntranceService).GetMethod(
    "TryReadInventoryMove",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
object?[] inventoryMoveArguments =
[
    new byte[]
    {
        9, 0,
        0, 10, 0, 0xD5, 0x69, 1, 0, 0, 0,
        3, 9, 0, 0, 0, 0xA7, 0xE4, 0x40, 0x28
    },
    (byte)0,
    (ushort)0,
    0,
    (byte)0,
    (ushort)0
];
var parsedInventoryMove = inventoryMoveParser is not null
    && (bool)inventoryMoveParser.Invoke(null, inventoryMoveArguments)!;
Check(parsedInventoryMove
    && (byte)inventoryMoveArguments[1]! == 0
    && (ushort)inventoryMoveArguments[2]! == 10
    && (int)inventoryMoveArguments[3]! == 1
    && (byte)inventoryMoveArguments[4]! == 3
    && (ushort)inventoryMoveArguments[5]! == 9,
    "DFLegacy move-item request skips the complete source endpoint before reading the destination");
object?[] warehouseMoveArguments =
[
    new byte[]
    {
        0xBA, 0,
        0, 10, 0, 0xAF, 0x1C, 1, 0, 0, 0,
        2, 7, 0, 0, 0, 0xCC, 0xE2, 0x35, 0xF4
    },
    (byte)0,
    (ushort)0,
    0,
    (byte)0,
    (ushort)0
];
var parsedWarehouseMove = inventoryMoveParser is not null
    && (bool)inventoryMoveParser.Invoke(null, warehouseMoveArguments)!;
Check(parsedWarehouseMove
    && (byte)warehouseMoveArguments[1]! == 0
    && (ushort)warehouseMoveArguments[2]! == 10
    && (int)warehouseMoveArguments[3]! == 1
    && (byte)warehouseMoveArguments[4]! == 2
    && (ushort)warehouseMoveArguments[5]! == 7,
    "captured DFLegacy cargo move maps the personal warehouse to item-list type two");
Check(EntranceService.ShouldUseIncrementalMainMoveRefresh(0, 10, 0, 6)
    && EntranceService.ShouldUseIncrementalMainMoveRefresh(0, 3, 0, 41)
    && EntranceService.ShouldUseIncrementalMainMoveRefresh(0, 3, 0, 8)
    && !EntranceService.ShouldUseIncrementalMainMoveRefresh(0, 10, 0, 41)
    && !EntranceService.ShouldUseIncrementalMainMoveRefresh(0, 10, 2, 7),
    "main-list moves touching quick slots use incremental updates instead of a full tab-changing inventory snapshot");
var buyItemReply = GameProtocolEngine.CreateBuyItemReply(
    updatedGold: 0x11223344,
    updatedVictoryPoints: 0x55667788,
    auxiliaryItemSpaceValue: 0x99AABBCC,
    updatedCera: 8_270,
    slot: 140,
    itemId: 63_501,
    countOrValue: 1);
Check(buyItemReply.Payload.Length == 25
    && buyItemReply.Payload[0] == 1
    && BinaryPrimitives.ReadUInt32LittleEndian(
        buyItemReply.Payload.AsSpan(1, 4)) == 0x11223344
    && BinaryPrimitives.ReadUInt32LittleEndian(
        buyItemReply.Payload.AsSpan(5, 4)) == 0x55667788
    && BinaryPrimitives.ReadUInt32LittleEndian(
        buyItemReply.Payload.AsSpan(9, 4)) == 0x99AABBCC
    && BinaryPrimitives.ReadUInt32LittleEndian(
        buyItemReply.Payload.AsSpan(13, 4)) == 8_270
    && BinaryPrimitives.ReadUInt16LittleEndian(
        buyItemReply.Payload.AsSpan(17, 2)) == 140
    && BinaryPrimitives.ReadUInt16LittleEndian(
        buyItemReply.Payload.AsSpan(19, 2)) == 63_501
    && BinaryPrimitives.ReadUInt32LittleEndian(
        buyItemReply.Payload.AsSpan(21, 4)) == 1,
    "DF2008 command-21 success reply carries all four wallets and the purchased item tuple");
Check(GameProtocolEngine.UseStackableItemCommand == 47
    && GameProtocolEngine.DungeonItemActionCommand
        == GameProtocolEngine.UseStackableItemCommand,
    "DFLegacy USE_STACKABLE command id matches the client enum table");
Check(GameProtocolEngine.GetItemCommand == 46
    && GameProtocolEngine.GetItemNotification == 39,
    "DFLegacy ground-item pickup uses command 46 and notification 39");
var getItemRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.GetItemCommand,
    0,
    new byte[] { 0x34, 0x12 });
Check(GameProtocolEngine.CreateGetItemSilentRejection(getItemRequest).Payload.SequenceEqual(
        new byte[] { 0, GameProtocolEngine.GetItemSilentRejectionError })
    && GameProtocolEngine.GetItemSilentRejectionError == 0x15,
    "GET_ITEM overweight rejection uses the client's silent pickup rollback before the formatted weight notice");
Check(GameProtocolEngine.CreateDungeonItemActionReply(3, 0).Payload.SequenceEqual(
        new byte[] { 1, 3, 0, 0 }),
    "dungeon consumable success returns the slot and list expected by the client");
Check(GameProtocolEngine.CreateUseStackableItemReply(41, 0).Payload.SequenceEqual(
        new byte[] { 1, 41, 0, 0 }),
    "USE_STACKABLE success returns result, source slot, and main-list type");
Check(GameProtocolEngine.CreateUseStackableItemError(17, 0).Payload.SequenceEqual(
        new byte[] { 0, 17, 0 }),
    "USE_STACKABLE failure returns error code and main-list type for pending-state cleanup");
var consumeItemsParser = typeof(EntranceService).GetMethod(
    "TryReadConsumeItems",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
object?[] consumeItemsArguments =
[
    new byte[]
    {
        0x57, 0x00, 0x00, 0x01,
        0x02, 0x00, 0x08, 0x00, 0xDD, 0x0B, 0x01, 0x00, 0x00, 0x00,
        0x61, 0x95, 0xBD, 0x7D
    },
    (byte)0,
    null
];
var parsedConsumeItems = consumeItemsParser is not null
    && (bool)consumeItemsParser.Invoke(null, consumeItemsArguments)!;
var parsedConsumeEntries = parsedConsumeItems
    ? (Array)consumeItemsArguments[2]!
    : Array.Empty<object>();
var parsedConsumeEntry = parsedConsumeEntries.Length == 1
    ? parsedConsumeEntries.GetValue(0)
    : null;
var parsedConsumeEntryType = parsedConsumeEntry?.GetType();
Check(parsedConsumeItems
    && (byte)consumeItemsArguments[1]! == 0
    && (ushort)parsedConsumeEntryType!.GetProperty("Operation")!
        .GetValue(parsedConsumeEntry!)! == 2
    && (ushort)parsedConsumeEntryType.GetProperty("Slot")!
        .GetValue(parsedConsumeEntry!)! == 8
    && (ushort)parsedConsumeEntryType.GetProperty("ItemId")!
        .GetValue(parsedConsumeEntry!)! == 3037
    && (uint)parsedConsumeEntryType.GetProperty("RequestedCount")!
        .GetValue(parsedConsumeEntry!)! == 1,
    "DFLegacy material request parses operation, slot, item id and count without field skew");
var ceraShopBuyReply = GameProtocolEngine.CreateCeraShopBuyReply(
    shopCategory: 0,
    commodityNo: 140032,
    rewards:
    [
        new GameCeraShopReward(3037, 50),
        new GameCeraShopReward(18012, 1)
    ]);
Check(ceraShopBuyReply.ProtocolId == GameProtocolEngine.CeraShopBuyCommand
    && ceraShopBuyReply.Payload.Length == 40
    && ceraShopBuyReply.Payload[0] == 1
    && ceraShopBuyReply.Payload[1] == 0
    && BinaryPrimitives.ReadInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(2, 4)) == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(6, 4)) == 140032
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(10, 4)) == 140032
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(14, 4)) == 140032
    && BinaryPrimitives.ReadUInt16LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(18, 2)) == ushort.MaxValue
    && BinaryPrimitives.ReadUInt16LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(20, 2)) == 2
    && BinaryPrimitives.ReadUInt16LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(22, 2)) == 3037
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(24, 4)) == 50
    && BinaryPrimitives.ReadUInt16LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(28, 2)) == 18012
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(30, 4)) == 1
    && BinaryPrimitives.ReadUInt16LittleEndian(
        ceraShopBuyReply.Payload.AsSpan(34, 2)) == ushort.MaxValue,
    "DFLegacy Cera-shop success reply carries the command-67 result-item vector");
var ceraShopBuyError = GameProtocolEngine.CreateCeraShopBuyError(
    errorCode: 4,
    shopCategory: -1,
    commodityNo: 100001);
Check(ceraShopBuyError.Payload.Length == 20
    && ceraShopBuyError.Payload[0] == 0
    && ceraShopBuyError.Payload[1] == 4
    && BinaryPrimitives.ReadInt32LittleEndian(
        ceraShopBuyError.Payload.AsSpan(2, 4)) == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        ceraShopBuyError.Payload.AsSpan(6, 4)) == 100001,
    "DFLegacy Cera-shop error reply keeps the client catalog index in range");
var ceraTicket = GameProtocolEngine.CreateCeraTicketReply("DFLegacy", 7);
Check(ceraTicket.ProtocolId == GameProtocolEngine.GenerateCeraTicketCommand
    && ceraTicket.Payload.SequenceEqual(
        new byte[]
        {
            1,
            8, 0, 0, 0,
            (byte)'D', (byte)'F', (byte)'L', (byte)'e',
            (byte)'g', (byte)'a', (byte)'c', (byte)'y',
            7, 0, 0, 0
        }),
    "DFLegacy command 68 returns the length-prefixed Cera web ticket");
var ceraShopParser = typeof(EntranceService).GetMethod(
    "TryReadCeraShopPurchase",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
object?[] ceraShopParserArguments =
[
    new byte[]
    {
        0x0B, 0x00,
        0x00,
        0x01,
        0x0F, 0x03, 0x00, 0x00, 0x23, 0x02, 0x00
    },
    null,
    false
];
var parsedCeraShopRequest = ceraShopParser is not null
    && (bool)ceraShopParser.Invoke(null, ceraShopParserArguments)!;
var parsedCeraShopProducts = parsedCeraShopRequest
    ? (Array)ceraShopParserArguments[1]!
    : Array.Empty<object>();
var parsedCeraShopProduct = parsedCeraShopProducts.Length == 1
    ? parsedCeraShopProducts.GetValue(0)
    : null;
var parsedCeraShopProductType = parsedCeraShopProduct?.GetType();
Check(parsedCeraShopRequest
    && !(bool)ceraShopParserArguments[2]!
    && parsedCeraShopProducts.Length == 1
    && (byte)parsedCeraShopProductType!.GetProperty("ProductType")!
        .GetValue(parsedCeraShopProduct!)! == 0x0F
    && (ushort)parsedCeraShopProductType.GetProperty("AttributeValue")!
        .GetValue(parsedCeraShopProduct!)! == 3
    && (uint)parsedCeraShopProductType.GetProperty("CommodityNo")!
        .GetValue(parsedCeraShopProduct!)! == 140032,
    "DFLegacy command 67 parses gift mode, product count and seven-byte product rows");
var ceraUpdate = GameProtocolEngine.CreateCeraUpdate(true, 1_000_000);
Check(ceraUpdate.ProtocolId == GameProtocolEngine.CeraUpdateNotification
    && ceraUpdate.Payload.SequenceEqual(
        new byte[] { 1, 0x40, 0x42, 0x0F, 0 }),
    "55 Cera wallet notification carries success and the full 32-bit balance");
Check(GameProtocolEngine.CreateSellItemReply(50_000, 0, 11, 1).Payload.SequenceEqual(
        new byte[]
        {
            1,
            0x50, 0xC3, 0, 0,
            0,
            11, 0,
            1, 0
        }),
    "DFLegacy sell-item reply carries gold, item space, slot and sold count");
var highGoldSellReply = GameProtocolEngine.CreateSellItemReply(
    1_050_000,
    3,
    300,
    500);
Check(highGoldSellReply.Payload.Length == 10
    && BinaryPrimitives.ReadUInt32LittleEndian(
        highGoldSellReply.Payload.AsSpan(1, 4)) == 1_050_000
    && highGoldSellReply.Payload[5] == 3
    && BinaryPrimitives.ReadUInt16LittleEndian(
        highGoldSellReply.Payload.AsSpan(6, 2)) == 300
    && BinaryPrimitives.ReadUInt16LittleEndian(
        highGoldSellReply.Payload.AsSpan(8, 2)) == 500,
    "sell-item reply preserves the full gold balance and exact item mutation");
Check(GameProtocolEngine.RepairEquipmentCommand == 25
    && GameProtocolEngine.CreateRepairEquipmentReply(
            48_650,
            3,
            9)
        .Payload.SequenceEqual(
        new byte[]
        {
            1,
            0x0A, 0xBE, 0, 0,
            3,
            9, 0
        }),
    "DFLegacy repair reply carries success, updated gold, item space and slot");
Check(GameProtocolEngine.DecreaseDurabilityCommand == 51
    && GameProtocolEngine.CreateDecreaseDurabilityReply(9)
        .Payload.SequenceEqual(new byte[] { 1, 9 }),
    "DFLegacy durability-use reply carries the equipped slot selected by the client");
var soldSlotOnlyUpdate = GameProtocolEngine.CreateUpdateItemList(
    0,
    [new GameInventoryEntry(12, ushort.MaxValue, 0)]);
Check(soldSlotOnlyUpdate.Payload.Length == 15
    && soldSlotOnlyUpdate.Payload[0] == 0
    && BinaryPrimitives.ReadUInt16LittleEndian(
        soldSlotOnlyUpdate.Payload.AsSpan(1, 2)) == 1
    && BinaryPrimitives.ReadUInt16LittleEndian(
        soldSlotOnlyUpdate.Payload.AsSpan(3, 2)) == 12
    && BinaryPrimitives.ReadUInt16LittleEndian(
        soldSlotOnlyUpdate.Payload.AsSpan(5, 2)) == ushort.MaxValue,
    "post-sale inventory refresh touches only the sold bag slot, not victory-point or Cera slots");
Check(GameProtocolEngine.CreateSetItemTradeStateReply(28223, 0).Payload.SequenceEqual(
        new byte[] { 1, 0x3F, 0x6E, 0, 0 }),
    "55 item-trade-state reply carries item id, list and an empty mutation list");
Check(GameProtocolEngine.CreateChangeSkillSlotReply(0x34, 7).Payload.SequenceEqual(
        new byte[] { 1, 0x34, 7 })
    && GameProtocolEngine.CreateChangeSkillSlotReply(0x34, 7).ProtocolId == 30,
    "55 skill-slot reply echoes the one-byte source and destination slots");
Check(GameProtocolEngine.CreateBuySkillReply(470, 12, 169, 1).ProtocolId == 31
    && GameProtocolEngine.CreateBuySkillReply(470, 12, 169, 1).Payload.SequenceEqual(
        new byte[] { 1, 0xD6, 0x01, 12, 169, 1 }),
    "55 buy-skill command 31 returns SP, slot, skill id and level after success");
var ceraFixtureRoot = Path.Combine(
    Path.GetTempPath(),
    $"dflegacy-cera-fixture-{Guid.NewGuid():N}");
var ceraFixtureEtc = Path.Combine(ceraFixtureRoot, "etc");
var ceraFixtureEquipment = Path.Combine(ceraFixtureRoot, "equipment");
var ceraFixtureStackable = Path.Combine(ceraFixtureRoot, "stackable");
Directory.CreateDirectory(ceraFixtureEtc);
Directory.CreateDirectory(Path.Combine(ceraFixtureEquipment, "avatar"));
Directory.CreateDirectory(Path.Combine(ceraFixtureStackable, "cash"));
try
{
    File.WriteAllText(
        Path.Combine(ceraFixtureEtc, "newcashshop.etc"),
        """
        [coin]
        100001 1 10 0 0 1400 `coin 10` 0
        [/coin]
        [item]
        110009 8 100 0 0 2900 `recovery 100` 0 0
        100063 50 1 0 0 60 `warehouse 24` 0 0
        100190 190 1 0 0 900 `one million gold` 0 0
        100191 191 2 0 0 800 `two victory-point vouchers` 0 0
        [/item]
        [premium]
        100057 43 1 0 0 1100 `master contract 1 day` 0 0
        [/premium]
        [avatar]
        120000 39000 1 0 0 -1
        120001 39000 2 0 0 -1
        120002 39000 3 0 0 -1
        [/avatar]
        [package]
        130071 71 0 0 500 `test booster`
        130072 72 0 0 600 `test package`
        [/package]
        """);
    File.WriteAllText(
        Path.Combine(ceraFixtureEtc, "premiumlist.etc"),
        """
        [type] 22
        [over equipinfo]
        4
        1 5
        2 5
        3 5
        4 5
        [type] 27
        [over skill] 5
        """);
    File.WriteAllText(
        Path.Combine(ceraFixtureEquipment, "equipment.lst"),
        "39000 `avatar/test.equ`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "stackable.lst"),
        "8 `cash/recovery.stk`\n"
        + "43 `cash/contract_expert3.stk`\n"
        + "50 `cash/safe_upgradekit.stk`\n"
        + "71 `cash/test_booster.stk`\n"
        + "72 `cash/test_package.stk`\n"
        + "190 `cash/gold_voucher.stk`\n"
        + "191 `cash/victorypoint_voucher.stk`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "contract_expert3.stk"),
        "[name] `master contract`\n"
        + "[stackable type] `[etc]`\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "recovery.stk"),
        "[name] `recovery`\n"
        + "[stackable type] `[consume]`\n"
        + "[attach type] `[trade]`\n"
        + "[stack limit] 1000\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "safe_upgradekit.stk"),
        "[name] `warehouse upgrade`\n"
        + "[stackable type] `[etc]`\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "gold_voucher.stk"),
        "[name] `one million gold voucher`\n"
        + "[stackable type] `[etc]`\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "victorypoint_voucher.stk"),
        "[name] `one hundred victory points voucher`\n"
        + "[stackable type] `[etc]`\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "test_booster.stk"),
        "[name] `test booster`\n"
        + "[stackable type] `[cera booster]` 0\n"
        + "[attach type] `[trade]`\n"
        + "[booster info]\n"
        + "[avatar]\n"
        + "1\n"
        + "39000 100 1 30 4\n"
        + "[/avatar]\n"
        + "[etc]\n"
        + "1\n"
        + "8 100 3\n"
        + "[/etc]\n"
        + "[/booster info]\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureStackable, "cash", "test_package.stk"),
        "[name] `test package`\n"
        + "[stackable type] `[cera package]` 0\n"
        + "[attach type] `[trade]`\n"
        + "[package data]\n"
        + "8 4\n"
        + "39000 1\n"
        + "43 1\n"
        + "[/package data]\n");
    File.WriteAllText(
        Path.Combine(ceraFixtureEquipment, "avatar", "test.equ"),
        "[name] `test avatar`\n[equipment type] `[coat avatar]`\n[cash] 450\n");
    var ceraScripts = CreateScripts(new ServerOptions
    {
        ScriptPvfPath = "",
        SkillScriptPath = ceraFixtureRoot
    });
    var ceraItems = new ItemCatalog(
        ceraScripts,
        NullLogger<ItemCatalog>.Instance);
    ceraItems.Initialize();
    Check(ceraItems.CeraBoosterCount == 1
        && ceraItems.TryGetDefinition(71, out var boosterDefinition)
        && boosterDefinition.CeraBooster is { Groups.Count: 2 }
        && boosterDefinition.CeraBooster.Groups[0] is
        {
            Kind: CeraBoosterRewardKind.Avatar,
            DrawCount: 1,
            TotalWeight: 100
        }
        && boosterDefinition.CeraBooster.Groups[0].Rewards[0] is
        {
            ItemId: 39000,
            Count: 1,
            AvatarPeriodDays: 30,
            AvatarAbilityIndex: 4
        },
        "DFLegacy item cache parses Cera booster groups, weights and Avatar metadata");
    Check(ceraItems.CeraPackageCount == 1
        && ceraItems.TryGetDefinition(72, out var packageDefinition)
        && packageDefinition.CeraPackage is { Rewards.Count: 3 }
        && packageDefinition.CeraPackage.Rewards[0] is
        {
            ItemId: 8,
            Count: 4
        }
        && packageDefinition.CeraPackage.Rewards[1] is
        {
            ItemId: 39000,
            Count: 1
        },
        "DFLegacy item cache parses deterministic Cera-package item/count pairs");
    Check(CeraBoosterOpeningPlanner.TryCreate(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [41] = new(41, 71, 1),
                [42] = new(42, 8, 5)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 41,
            ceraItems,
            weightLimit: uint.MaxValue,
            new SequenceDropRandomSource(0, 0),
            out var boosterOpenPlan,
            out _)
        && !boosterOpenPlan.MainInventory.ContainsKey(41)
        && boosterOpenPlan.MainInventory[42].CountOrValue == 8
        && boosterOpenPlan.AvatarInventory.Values.Single() is
        {
            ItemId: 39000,
            AvatarRemainingSeconds: 0,
            AvatarAbilityIndex: 4
        }
        && boosterOpenPlan.OverflowItems.Count == 0,
        "Cera booster opening atomically consumes the box, stacks items and preserves Avatar options");
    Check(CeraShopContentPlanner.TryCreatePurchase(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [42] = new(42, 8, 5)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            ceraItems.Definitions[71],
            containerCount: 1,
            ceraItems,
            weightLimit: uint.MaxValue,
            new SequenceDropRandomSource(0, 0),
            shouldSettleAsEffect: null,
            out var boosterPurchasePlan,
            out _)
        && boosterPurchasePlan.MainInventory[42].CountOrValue == 8
        && boosterPurchasePlan.AvatarInventory.Values.Single() is
        {
            ItemId: 39000,
            AvatarRemainingSeconds: 0,
            AvatarAbilityIndex: 4
        }
        && boosterPurchasePlan.MainInventory.Values.All(item => item.ItemId != 71),
        "Cera-shop booster purchase opens immediately without granting the booster container");
    Check(CeraShopContentPlanner.TryCreatePurchase(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [42] = new(42, 8, 5)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            ceraItems.Definitions[72],
            containerCount: 1,
            ceraItems,
            weightLimit: uint.MaxValue,
            new SequenceDropRandomSource(),
            shouldSettleAsEffect: itemId => itemId == 43,
            out var packagePurchasePlan,
            out _)
        && packagePurchasePlan.MainInventory[42].CountOrValue == 9
        && packagePurchasePlan.AvatarInventory.Values.Single() is
        {
            ItemId: 39000,
            AvatarRemainingSeconds: 0,
            AvatarAbilityIndex: 0
        }
        && packagePurchasePlan.MainInventory.Values.All(item => item.ItemId != 43)
        && packagePurchasePlan.MainInventory.Values.All(item => item.ItemId != 72),
        "Cera-shop package purchase grants cached rewards, delegates special effects and omits the package container");
    var ceraCatalog = new CeraShopCatalog(
        ceraScripts,
        ceraItems,
        NullLogger<CeraShopCatalog>.Instance);
    Check(ceraCatalog.Count == 11
        && ceraCatalog.TryGetProduct(100001, out var coinProduct)
        && coinProduct.ItemId == 1
        && coinProduct.Quantity == 10
        && coinProduct.CashPrice == 1400
        && coinProduct.ClientCategory == 1
        && coinProduct.UsesMainInventory,
        "DFLegacy Cera catalog parses bundled coin quantities and prices");
    Check(ceraCatalog.TryGetProduct(130071, out var boosterProduct)
            && boosterProduct.ItemId == 71
            && boosterProduct.CashPrice == 500
            && ceraCatalog.TryGetProduct(130072, out var packageProduct)
            && packageProduct.ItemId == 72
            && packageProduct.CashPrice == 600,
        "DFLegacy Cera catalog exposes booster and package commodities for immediate settlement");
    Check(ceraCatalog.TryGetProduct(100057, out var contractProduct)
        && contractProduct.ItemId == 43
        && contractProduct.PremiumContract is
        {
            ServiceType: GameProtocolEngine.MasterContractServiceType,
            Days: 1
        },
        "DFLegacy Cera catalog resolves explicit contract scripts to Premium service durations");
    Check(ceraCatalog.TryGetProduct(100190, out var goldVoucherProduct)
            && goldVoucherProduct.DirectCurrencyGrant
                == new CeraShopDirectCurrencyGrant(1_000_000, 0)
            && CeraShopDirectCurrencyPlanner.TryApply(
                50_000,
                20,
                goldVoucherProduct.DirectCurrencyGrant,
                goldVoucherProduct.Quantity,
                out var voucherGold,
                out var voucherGoldVictoryPoints)
            && voucherGold == 1_050_000
            && voucherGoldVictoryPoints == 20
            && ceraCatalog.TryGetProduct(100191, out var victoryVoucherProduct)
            && victoryVoucherProduct.DirectCurrencyGrant
                == new CeraShopDirectCurrencyGrant(0, 100)
            && CeraShopDirectCurrencyPlanner.TryApply(
                voucherGold,
                voucherGoldVictoryPoints,
                victoryVoucherProduct.DirectCurrencyGrant,
                victoryVoucherProduct.Quantity,
                out var combinedVoucherGold,
                out var combinedVoucherVictoryPoints)
            && combinedVoucherGold == 1_050_000
            && combinedVoucherVictoryPoints == 220,
        "Cera vouchers settle their configured quantity directly into gold and victory points without inventory placement");
    Check(!CeraShopDirectCurrencyPlanner.TryApply(
            int.MaxValue,
            uint.MaxValue,
            new CeraShopDirectCurrencyGrant(1, 1),
            1,
            out _,
            out _),
        "Cera voucher settlement rejects currency overflow instead of truncating it");
    Check(ceraCatalog.TryGetProduct(100063, out var warehouseUpgradeProduct)
        && warehouseUpgradeProduct.ItemId == 50
        && warehouseUpgradeProduct.Quantity == 1
        && warehouseUpgradeProduct.CashPrice == 60
        && warehouseUpgradeProduct.WarehouseUpgradeCapacity == 24,
        "DFLegacy Cera catalog recognizes the target client's first exact warehouse-upgrade commodity");
    Check(ceraCatalog.TryGetProduct(120000, out var avatarProduct)
        && avatarProduct.ItemId == 39000
        && avatarProduct.AttributeValue == 1
        && avatarProduct.CashPrice == 2_970
        && avatarProduct.ClientCategory == 0
        && !avatarProduct.UsesMainInventory,
        "DFLegacy Cera catalog charges the permanent price while expiration is unsupported");
    Check(ceraCatalog.TryGetProduct(120001, out var avatar30DayProduct)
            && avatar30DayProduct.AttributeValue == 2
            && avatar30DayProduct.CashPrice == 2_970
            && ceraCatalog.TryGetProduct(120002, out var avatarPermanentProduct)
            && avatarPermanentProduct.AttributeValue == 3
            && avatarPermanentProduct.CashPrice == 2_970,
        "DFLegacy avatar period codes all charge 6.6x until expiration is implemented");
    var premiumBenefits = new PremiumBenefitCatalog(
        ceraScripts,
        NullLogger<PremiumBenefitCatalog>.Instance);
    premiumBenefits.Initialize();
    Check(premiumBenefits.Count == 2
        && premiumBenefits.GetEffectiveSkillLevel(
            50,
            [GameProtocolEngine.MasterContractServiceType]) == 55
        && premiumBenefits.GetEffectiveSkillLevel(
            50,
            [GameProtocolEngine.OverlordContractServiceType]) == 50
        && premiumBenefits.GetOverEquipableLevel(
            [GameProtocolEngine.OverlordContractServiceType],
            equipmentType: 1) == 5
        && premiumBenefits.GetOverEquipableLevel(
            [GameProtocolEngine.MasterContractServiceType],
            equipmentType: 1) == 0
        && ceraItems.TryGetDefinition(39_000, out var premiumAvatar)
        && PremiumBenefitCatalog.GetPremiumEquipmentType(premiumAvatar) == 4,
        "Premium benefits parse independent +5 skill and per-equipment-type levels");
}
finally
{
    Directory.Delete(ceraFixtureRoot, recursive: true);
}

var skillFixtureRoot = Path.Combine(
    Path.GetTempPath(),
    $"dflegacy-skill-fixture-{Guid.NewGuid():N}");
var skillFixtureDirectory = Path.Combine(skillFixtureRoot, "skill", "Swordman");
Directory.CreateDirectory(skillFixtureDirectory);
Directory.CreateDirectory(Path.Combine(skillFixtureRoot, "character", "Swordman"));
try
{
    File.WriteAllText(
        Path.Combine(skillFixtureRoot, "skill", "skilllist.lst"),
        "0 `SwordmanSkill.lst`\n");
    File.WriteAllText(
        Path.Combine(skillFixtureRoot, "skill", "SwordmanSkill.lst"),
        "1 `Swordman/Active.skl`\n"
        + "2 `Swordman/Passive.skl`\n"
        + "3 `Swordman/Common.skl`\n"
        + "4 `Swordman/Guild.skl`\n"
        + "5 `Swordman/Active2.skl`\n"
        + "6 `Swordman/Guild2.skl`\n"
        + "7 `Swordman/Guild10.skl`\n"
        + "8 `Swordman/Awakening.skl`\n"
        + "200 `Swordman/HighId.skl`\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Active.skl"),
        "[purchase cost]\n25\n[/purchase cost]\n"
        + "[required level] 5\n[required level range] 2\n"
        + "[type] `[active]`\n[skill class] 0\n"
        + "[maximum level] 10\n[growtype maximum level] 10 10 10 10 10\n"
        + "[consume item]\n3037 3 3 // colorless cubes\n[/consume item]\n"
        + "[skill fitness growtype]\n0\n1\n2\n3\n4\n[/skill fitness growtype]\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Passive.skl"),
        "[purchase cost]\n20\n[/purchase cost]\n"
        + "[required level] 10\n[type] `[passive]`\n[skill class] 1\n"
        + "[maximum level] 5\n[growtype maximum level] 5 5 5 5 5\n"
        + "[skill fitness growtype]\n0\n1\n2\n3\n4\n[/skill fitness growtype]\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Common.skl"),
        "[type] `[active]`\n[skill class] 4\n[maximum level] 10\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Guild.skl"),
        "[type] `[passive]`\n[skill class] 4\n[purchase gsp] 1\n[maximum level] 5\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Active2.skl"),
        "[type] `[active]`\n[skill class] 0\n[maximum level] 10\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Guild2.skl"),
        "[type] `[passive]`\n[skill class] 4\n[purchase gsp] 2\n[maximum level] 5\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Guild10.skl"),
        "[type] `[passive]`\n[skill class] 2\n[purchase gsp] 10\n[maximum level] 5\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "Awakening.skl"),
        "[type] `[passive]`\n[skill class] 4\n[maximum level] 20\n"
        + "[growtype maximum level] 0 0 0 0 0\n"
        + "[second growtype maximum level] 0 0 10 0 0 0 0 0 0 0\n"
        + "[skill fitness second growtype]\n1\n[/skill fitness second growtype]\n");
    File.WriteAllText(
        Path.Combine(skillFixtureDirectory, "HighId.skl"),
        "[type] `[active]`\n[skill class] 2\n[maximum level] 10\n");
    File.WriteAllText(
        Path.Combine(skillFixtureRoot, "character", "character.lst"),
        "0 `Swordman/Swordman.chr`\n");
    File.WriteAllText(
        Path.Combine(skillFixtureRoot, "character", "Swordman", "Swordman.chr"),
        "[initial value]\n"
        + "[skill]\n1 2\n2 1\n[/skill]\n"
        + "[growtype 1]\n"
        + "[growtype 2]\n"
        + "[skill]\n1 0\n5 3\n[/skill]\n"
        + "[awakening 1]\n"
        + "[awakening skill]\n3 2\n[/awakening skill]\n"
        + "[awakening 2]\n"
        + "[awakening skill]\n1 4\n[/awakening skill]\n");

    var catalog = new SkillCatalog(
        CreateScripts(new ServerOptions
        {
            ScriptPvfPath = "",
            SkillScriptPath = skillFixtureRoot
        }),
        NullLogger<SkillCatalog>.Instance);
    Check(catalog.TryGetDefinition(0, 1, out var activeSkill) && activeSkill.IsActive,
        "skill catalog recognizes the exact active tag");
    Check(catalog.TryGetDefinition(0, 2, out var passiveSkill) && !passiveSkill.IsActive,
        "skill catalog does not classify passive as active");
    var hasCommonSkill = catalog.TryGetDefinition(0, 3, out var commonSkill);
    var hasGuildSkill = catalog.TryGetDefinition(0, 4, out var guildSkill);
    var hasGuildSkill2 = catalog.TryGetDefinition(0, 6, out var guildSkill2);
    var hasGuildSkill10 = catalog.TryGetDefinition(0, 7, out var guildSkill10);
    var hasHighIdSkill = catalog.TryGetDefinition(0, 200, out var highIdSkill);
    Check(hasCommonSkill
        && commonSkill.RawGroup == 4
        && !commonSkill.IsGuildSkill
        && hasGuildSkill
        && guildSkill.IsGuildSkill
        && hasGuildSkill2
        && guildSkill2.IsGuildSkill
        && hasGuildSkill10
        && guildSkill10.IsGuildSkill
        && hasHighIdSkill
        && !highIdSkill.IsGuildSkill,
        "any positive [purchase gsp] marks a guild skill without relying on its id");
    Check(activeSkill.CostFor(0, 1) == 25
        && activeSkill.RequiredCharacterLevelFor(3) == 9
        && activeSkill.ConsumedItemId == 3037
        && activeSkill.ConsumedItemCount == 3,
        "skill catalog parses SP, level and dungeon item-consumption requirements");
    Check(catalog.TryGetDefinition(0, 8, out var awakeningSkill)
        && awakeningSkill.MaximumLevelFor(growType: 1) == 0
        && awakeningSkill.MaximumLevelFor(growType: 1, awakeningType: 1) == 10
        && awakeningSkill.MaximumLevelFor(growType: 1, awakeningType: 2) == 0,
        "second-growth maximum-level tables open only the matching awakened skill branch");
    var normalizedAwakeningSkill = catalog.NormalizeLayout(
        0,
        1,
        1,
        [new CharacterSkillRecord(174, 8, 1)],
        out var normalizedAwakeningSkillChanged);
    Check(!normalizedAwakeningSkillChanged
        && normalizedAwakeningSkill.SequenceEqual(
            [new CharacterSkillRecord(174, 8, 1)])
        && catalog.NormalizeLayout(
            0,
            1,
            0,
            [new CharacterSkillRecord(174, 8, 1)],
            out var removedUnavailableAwakeningSkill).Count == 0
        && removedUnavailableAwakeningSkill,
        "login normalization preserves reached awakening skills and removes them from an unawakened branch");
    var unawakenedGrantedSkills = catalog.GetGrantedSkills(0, 1);
    var grantedSkills = catalog.GetGrantedSkills(0, 1, awakeningType: 1);
    Check(grantedSkills.SequenceEqual(
            [
                new GameSkillEntry(2, 1),
                new GameSkillEntry(5, 3),
                new GameSkillEntry(3, 2)
            ])
        && unawakenedGrantedSkills.All(skill => skill.SkillId != 3)
        && catalog.GetGrantedSkills(0, 1, awakeningType: 2).Any(skill =>
            skill.SkillId == 1 && skill.Level == 4),
        "character state maps to the one-based .chr growtype and applies only reached [awakening skill] stages");
    var classSkillChanges = catalog.GetGrowTypeSkillChanges(0, 1);
    var awakeningSkillChanges = catalog.GetAwakeningSkillChanges(0, 1, 1);
    Check(classSkillChanges.SequenceEqual(
            [
                new CharacterGrantedSkillChange(1, 0, 0),
                new CharacterGrantedSkillChange(5, 3, 0)
            ])
        && awakeningSkillChanges.SequenceEqual(
            [new CharacterGrantedSkillChange(3, 2, 4)])
        && catalog.TryApplyGrowthSkillChanges(
            0,
            [
                new CharacterSkillRecord(6, 1, 5),
                new CharacterSkillRecord(48, 2, 1)
            ],
            classSkillChanges,
            out var classChangedSkills)
        && classChangedSkills.All(skill => skill.SkillId != 1)
        && classChangedSkills.Any(skill =>
            skill.SkillId == 5 && skill.Level == 3),
        "raw .chr growth skill rows preserve removal levels and can be applied atomically to the persisted panel");
    var resetLayout = catalog.CreateResetLayout(
        0,
        1,
        [
            new CharacterSkillRecord(174, 3, 4),
            new CharacterSkillRecord(204, 4, 2)
        ],
        awakeningType: 1);
    Check(resetLayout.Any(skill => skill.SkillId == 2 && skill.Level == 1)
        && resetLayout.Any(skill => skill.SkillId == 5 && skill.Level == 3)
        && resetLayout.Any(skill => skill.SkillId == 3 && skill.Level == 2)
        && resetLayout.Any(skill =>
            skill.SkillId == 4 && skill.Level == 2 && skill.Slot == 204)
        && resetLayout.All(skill => skill.SkillId != 1),
        "skill reset restores base, growtype and reached awakening grants, resets learned levels and preserves [purchase gsp] guild skills");
    Check(catalog.AllocateSlot(activeSkill, new HashSet<byte>()) == 6,
        "new active skills allocate into their [skill class] page");
    Check(catalog.AllocateSlot(passiveSkill, new HashSet<byte>()) == 48,
        "passive skills allocate into their [skill class] page instead of the quick bar");
    Check(catalog.AllocateSlot(commonSkill, new HashSet<byte>()) == 174
        && catalog.AllocateSlot(guildSkill, new HashSet<byte>()) == 204
        && catalog.AllocateSlot(guildSkill2, new HashSet<byte>()) == 204
        && catalog.AllocateSlot(guildSkill10, new HashSet<byte>()) == 204
        && catalog.AllocateSlot(highIdSkill, new HashSet<byte>()) == 90,
        "class-four ordinary, guild and high-id non-guild skills use distinct client ranges");
    var normalized = catalog.NormalizeLayout(
        0,
        0,
        0,
        [
            new CharacterSkillRecord(198, 1, 12),
            new CharacterSkillRecord(5, 2, 7),
            new CharacterSkillRecord(175, 3, 2),
            new CharacterSkillRecord(129, 4, 2),
            new CharacterSkillRecord(2, 5, 3),
            new CharacterSkillRecord(90, 200, 4)
        ],
        out var normalizedChanged);
    Check(normalizedChanged
        && normalized.SequenceEqual(
        [
            new CharacterSkillRecord(2, 5, 3),
            new CharacterSkillRecord(6, 1, 10),
            new CharacterSkillRecord(48, 2, 5),
            new CharacterSkillRecord(90, 200, 4),
            new CharacterSkillRecord(175, 3, 2),
            new CharacterSkillRecord(204, 4, 2)
        ]),
        "skill normalization preserves valid slots and repairs class, passive and guild placement");
    Check(!catalog.TrySwapSlots(0, normalized, 48, 0, out _, out _)
        && !catalog.TrySwapSlots(0, normalized, 204, 174, out _, out _)
        && !catalog.TrySwapSlots(0, normalized, 6, 48, out _, out _)
        && catalog.TrySwapSlots(0, normalized, 6, 0, out var quickMove, out _)
        && quickMove.Any(skill => skill.SkillId == 1 && skill.Slot == 0),
        "skill moves reject passive quick slots, guild rows and cross-class destinations");

    var contractSkills = new[]
    {
        new CharacterSkillRecord(6, 1, 5),
        new CharacterSkillRecord(7, 5, 3),
        new CharacterSkillRecord(48, 2, 3),
        new CharacterSkillRecord(174, 3, 2),
        new CharacterSkillRecord(204, 4, 2)
    };
    var activeContractSkills = catalog.ReconcileOverLevelSkills(
        0,
        1,
        awakeningType: 1,
        effectiveCharacterLevel: 14,
        contractSkills,
        out var activeContractRefund,
        out var activeContractRefunds);
    var expiredContractSkills = catalog.ReconcileOverLevelSkills(
        0,
        1,
        awakeningType: 1,
        effectiveCharacterLevel: 9,
        contractSkills,
        out var expiredContractRefund,
        out var expiredContractRefunds);
    Check(activeContractSkills.SequenceEqual(contractSkills)
        && activeContractRefund == 0
        && activeContractRefunds.Count == 0
        && expiredContractSkills.Any(skill =>
            skill.SkillId == 1 && skill.Level == 3)
        && expiredContractSkills.Any(skill =>
            skill.SkillId == 2 && skill.Level == 1)
        && expiredContractSkills.Any(skill =>
            skill.SkillId == 3 && skill.Level == 2)
        && expiredContractSkills.Any(skill =>
            skill.SkillId == 4 && skill.Level == 2)
        && expiredContractSkills.Any(skill =>
            skill.SkillId == 5 && skill.Level == 3)
        && expiredContractRefund == 90
        && expiredContractRefunds.SequenceEqual(
        [
            new SkillLevelRefund(1, 5, 3, 50),
            new SkillLevelRefund(2, 3, 1, 40)
        ]),
        "expired Master contract lowers only over-level purchased skills, refunds per-level SP, and preserves granted and guild skills");

    var quickSlotLayout = new[]
    {
        new CharacterSkillRecord(5, 10, 1),
        new CharacterSkillRecord(6, 20, 1)
    };
    quickSlotLayout = SkillCatalog.SwapSlots(
        quickSlotLayout,
        12,
        5,
        out var firstQuickSlotChange).ToArray();
    quickSlotLayout = SkillCatalog.SwapSlots(
        quickSlotLayout,
        5,
        6,
        out var secondQuickSlotChange).ToArray();
    Check(firstQuickSlotChange
        && secondQuickSlotChange
        && quickSlotLayout.SequenceEqual(
        [
            new CharacterSkillRecord(5, 20, 1),
            new CharacterSkillRecord(12, 10, 1)
        ]),
        "compound skill quick-slot swaps persist displaced and dragged skills");
}
finally
{
    Directory.Delete(skillFixtureRoot, recursive: true);
}

var questFixtureRoot = Path.Combine(
    Path.GetTempPath(),
    $"dflegacy-quest-fixture-{Guid.NewGuid():N}");
var questFixtureDirectory = Path.Combine(questFixtureRoot, "quest");
Directory.CreateDirectory(Path.Combine(questFixtureDirectory, "Seria"));
Directory.CreateDirectory(Path.Combine(questFixtureDirectory, "CChange"));
Directory.CreateDirectory(Path.Combine(questFixtureDirectory, "Epic Job"));
ItemCatalog? smokeItemCatalog = null;
try
{
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "quest.lst"),
        "1016 `Seria/sq.qst`\n"
        + "1017 `Seria/after.qst`\n"
         + "1018 `Seria/fighter.qst`\n"
         + "1019 `Seria/high.qst`\n"
         + "1020 `Seria/collision.qst`\n"
         + "1021 `Seria/selection.qst`\n"
         + "1022 `Seria/task-drop.qst`\n"
         + "1023 `CChange/probe1.qst`\n"
         + "1024 `CChange/probe2.qst`\n"
         + "1025 `CChange/probe3.qst`\n"
         + "1026 `Epic Job/probe-1.qst`\n"
         + "1027 `Epic Job/probe-2.qst`\n"
         + "1028 `Epic Job/probe-3.qst`\n");
    const string commonQuest =
        "[grade] `[common unique]`\n"
        + "[npc index] 2\n[complete npc index] 2\n"
        + "[job] `[all]`\n[grow type] -1\n[level] 1 99\n"
        + "[type] `[meet npc]`\n[int data]\n2\n[/int data]\n";
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "sq.qst"),
        commonQuest
        + "[reward int data]\n1055 3\n0 200\n[/reward int data]\n"
        + "[reward type] `[item]`\n");
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "after.qst"),
        commonQuest + "[pre required quest]\n1016\n[/pre required quest]\n");
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "fighter.qst"),
        commonQuest.Replace("[job] `[all]`", "[job] `[fighter]`"));
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "high.qst"),
        commonQuest.Replace("[level] 1 99", "[level] 61 99"));
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "collision.qst"),
        commonQuest + "[collision quest]\n1016\n[/collision quest]\n");
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "selection.qst"),
        commonQuest.Replace("[level] 1 99", "[level] 61 99")
        + "[reward int data]\n1000 1\n[/reward int data]\n"
        + "[reward selection int data]\n27093 1\n14873 1\n[/reward selection int data]\n"
        + "[reward type] `[item]`\n");
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Seria", "task-drop.qst"),
        "[grade] `[common unique]`\n"
        + "[npc index] 2\n[complete npc index] 2\n"
        + "[job] `[all]`\n[grow type] -1\n[level] 61 99\n"
        + "[type] `[seeking]`\n[int data]\n4000 2\n[/int data]\n"
        + "[depend give item]\n4000 1\n[/depend give item]\n"
        + "[clear reward item]\n2 3 4000 2 25 4\n[/clear reward item]\n"
        + "[monster reward item]\n77 -1 3 4000 1 50 4\n[/monster reward item]\n");
    const string classChangeQuest =
        "[grade] `[epic]`\n"
        + "[npc index] 7\n[complete npc index] 7\n"
        + "[job change quest] 1\n"
        + "[job] `[swordman]`\n[grow type] -1\n[level] 18 99\n"
        + "[type] `[meet npc]`\n";
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "CChange", "probe1.qst"),
        classChangeQuest);
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "CChange", "probe2.qst"),
        classChangeQuest
        + "[pre required quest]\n65000\n[/pre required quest]\n"
        + "[pre required quest]\n1023\n[/pre required quest]\n");
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "CChange", "probe3.qst"),
        classChangeQuest
        + "[pre required quest]\n1024\n[/pre required quest]\n"
        + "[reward type] `[grow type]`\n"
        + "[reward int data]\n2\n[/reward int data]\n");
    const string awakeningQuest =
        "[grade] `[achievement]`\n"
        + "[npc index] 17\n[complete npc index] 17\n"
        + "[job change quest] 2\n"
        + "[job] `[fighter]`\n[grow type] 3\n[level] 48 99\n"
        + "[type] `[meet npc]`\n";
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Epic Job", "probe-1.qst"),
        awakeningQuest);
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Epic Job", "probe-2.qst"),
        awakeningQuest);
    File.WriteAllText(
        Path.Combine(questFixtureDirectory, "Epic Job", "probe-3.qst"),
        awakeningQuest
        + "[reward type] `[awakening type]`\n"
        + "[reward int data]\n1\n[/reward int data]\n");

    var equipmentFixtureDirectory = Path.Combine(questFixtureRoot, "equipment");
    var stackableFixtureDirectory = Path.Combine(questFixtureRoot, "stackable");
    var etcFixtureDirectory = Path.Combine(questFixtureRoot, "etc");
    Directory.CreateDirectory(equipmentFixtureDirectory);
    Directory.CreateDirectory(stackableFixtureDirectory);
    Directory.CreateDirectory(etcFixtureDirectory);
    Directory.CreateDirectory(Path.Combine(
        stackableFixtureDirectory,
        "cash",
        "creature"));
    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "equipment.lst"),
        "10000 `test-10000.equ`\n"
        + "12007 `test-12007.equ`\n"
        + "14873 `test-14873.equ`\n"
        + "27093 `test-27093.equ`\n"
         + "39000 `test-39000.equ`\n"
         + "50000 `test-50000.equ`\n"
         + "50001 `test-50001.equ`\n"
         + "50003 `test-50003.equ`\n");
    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "pricetable.tbl"),
        "[price rates] 200 150 225\n");
    foreach (var equipmentId in new ushort[] { 10_000, 14_873, 27_093 })
    {
        var rarity = equipmentId switch
        {
            14_873 => 0,
            27_093 => 1,
            _ => 2
        };
        File.WriteAllText(
            Path.Combine(equipmentFixtureDirectory, $"test-{equipmentId}.equ"),
            "[equipment type] `[weapon]`\n"
            + "[attach type] `[free]`\n"
            + $"[rarity] {rarity}\n"
            + "[weight] 10\n"
            + "[durability] 1000\n");
    }

    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "test-12007.equ"),
        "[equipment type] `[pants]`\n"
        + "[attach type] `[free]`\n"
        + "[grade] 10\n"
        + "[rarity] 1\n"
        + "[price] 5000\n"
        + "[weight] 10\n"
        + "[inventory limit] 3000\n"
        + "[durability] 24\n");

    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "test-39000.equ"),
        "[equipment type] `[hair avatar]`\n"
        + "[attach type] `[free]`\n"
        + "[weight] 10\n"
        + "[durability] 1000\n");
    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "test-50000.equ"),
         "[name] `Test Creature`\n"
         + "[equipment type] `[creature]`\n"
         + "[sub type] 0\n"
         + "[creature species] 15\n"
         + "[attach type] `[free]`\n");
    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "test-50001.equ"),
        "[name] `Test Red Artifact`\n"
         + "[equipment type] `[artifact red]`\n"
         + "[creature minimum level] 3\n"
         + "[creature experience amount rate] 10.00\n"
         + "[attach type] `[free]`\n");
    File.WriteAllText(
        Path.Combine(equipmentFixtureDirectory, "test-50003.equ"),
        "[name] `Test Creature Egg`\n"
        + "[equipment type] `[creature]`\n"
        + "[sub type] 1\n"
        + "[output index] 50000\n"
        + "[attach type] `[sealing]`\n");

    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "stackable.lst"),
        "1 `test-1.stk`\n"
        + "8 `test-8.stk`\n"
        + "24 `cash/creature/creature_food.stk`\n"
        + "1000 `test-1000.stk`\n"
        + "1031 `book_skill1.stk`\n"
        + "1034 `book_exp1.stk`\n"
        + "1035 `book_exp2.stk`\n"
        + "1036 `book_exp3.stk`\n"
        + "1037 `book_exp4.stk`\n"
        + "1038 `book_skill2.stk`\n"
        + "1039 `stone_str.stk`\n"
        + "1040 `stone_int.stk`\n"
        + "1041 `stone_helth.stk`\n"
        + "1042 `stone_mind.stk`\n"
        + "1043 `stone_hp.stk`\n"
        + "1044 `stone_mp.stk`\n"
        + "1045 `stone_speed.stk`\n"
        + "1046 `stone_allr.stk`\n"
        + "1055 `test-1055.stk`\n"
        + "3033 `test-3033.stk`\n"
        + "3034 `test-3034.stk`\n"
        + "3035 `test-3035.stk`\n"
        + "3036 `test-3036.stk`\n"
        + "3037 `test-3037.stk`\n"
        + "3166 `test-3166.stk`\n"
        + "3167 `test-3167.stk`\n"
         + "3176 `test-3176.stk`\n"
         + "4000 `test-4000.stk`\n"
         + "7143 `test-7143.stk`\n"
        + "50002 `test-50002.stk`\n"
        + "50004 `cash/creature/rename_card.stk`\n");
    foreach (var stackableId in new ushort[] { 1, 8, 1_000, 1_055 })
    {
        File.WriteAllText(
            Path.Combine(stackableFixtureDirectory, $"test-{stackableId}.stk"),
            "[stackable type] `[consume]`\n"
            + "[attach type] `[trade]`\n"
            + "[stack limit] 1000\n"
             + "[weight] 1\n");
    }

    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "book_skill1.stk"),
        "[name] `SP+5 Skill Book`\n"
        + "[stackable type] `[etc]` 8\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "book_skill2.stk"),
        "[name] `SP+20 Skill Book`\n"
        + "[stackable type] `[etc]` 1\n"
        + "[attach type] `[trade]`\n");

    foreach (var experienceBookScript in new[]
             {
                 "book_exp1.stk",
                 "book_exp2.stk",
                 "book_exp3.stk",
                 "book_exp4.stk"
             })
    {
        File.WriteAllText(
            Path.Combine(stackableFixtureDirectory, experienceBookScript),
            "[stackable type] `[etc]` 1\n"
            + "[attach type] `[trade]`\n");
    }

    foreach (var stoneScript in new[]
             {
                 "stone_str.stk",
                 "stone_int.stk",
                 "stone_helth.stk",
                 "stone_mind.stk",
                 "stone_hp.stk",
                 "stone_mp.stk",
                 "stone_speed.stk",
                 "stone_allr.stk"
             })
    {
        File.WriteAllText(
            Path.Combine(stackableFixtureDirectory, stoneScript),
            "[stackable type] `[etc]`\n"
            + "[attach type] `[trade]`\n"
            + "[stack limit] 1000\n");
    }

    foreach (var materialId in new ushort[]
             {
                 3_033, 3_034, 3_035, 3_036, 3_037, 3_166, 3_167
             })
    {
        File.WriteAllText(
            Path.Combine(stackableFixtureDirectory, $"test-{materialId}.stk"),
            "[stackable type] `[material]`\n"
            + "[attach type] `[free]`\n"
            + "[weight] 1\n");
    }
    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "test-3176.stk"),
        "[stackable type] `[material]`\n"
        + "[attach type] `[sealing]`\n"
        + "[stack limit] 3\n"
        + "[weight] 1\n");
    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "test-4000.stk"),
        "[stackable type] `[quest]`\n"
        + "[attach type] `[free]`\n"
        + "[stack limit] 1000\n");
    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "test-7143.stk"),
        "[stackable type] `[upgradable legacy]` 1\n"
        + "[attach type] `[free]`\n"
        + "[minimum level] 10\n"
        + "[lottery use cost] 200\n"
        + "[int data]\n"
        + "10000 1\n"
        + "14873 25000 1\n"
        + "39000 100 1\n"
        + "[/int data]\n");
    File.WriteAllText(
        Path.Combine(stackableFixtureDirectory, "test-50002.stk"),
        "[name] `Test Feed`\n"
        + "[stackable type] `[feed]`\n"
        + "[attach type] `[free]`\n"
        + "[stack limit] 1000\n");
    File.WriteAllText(
        Path.Combine(
            stackableFixtureDirectory,
            "cash",
            "creature",
            "creature_food.stk"),
        "[name] `Test Creature Food`\n"
        + "[stackable type] `[feed]`\n"
        + "[attach type] `[trade]`\n"
        + "[stack limit] 1000\n");
    File.WriteAllText(
        Path.Combine(
            stackableFixtureDirectory,
            "cash",
            "creature",
            "rename_card.stk"),
        "[name] `Test Rename Card`\n"
        + "[stackable type] `[creature]` 1\n"
        + "[attach type] `[trade]`\n");
    File.WriteAllText(
        Path.Combine(etcFixtureDirectory, "disjoint.etc"),
        "[cube index]\n"
        + "`[no element]`\n"
        + "3037\n"
        + "[/cube index]\n"
        + "[cube creation const] 150 1.0 1.2 1.4 1.6 1.8\n"
        + "[additional result]\n"
        + "0\n"
        + "4 3033 3034 3035 3036\n"
        + "1 3166\n"
        + "1 3167\n"
        + "0\n"
        + "[additional result const]\n"
        + "0.0\n"
        + "0.14 10 5\n"
        + "0.14 10 10\n"
         + "0.14 10 20\n"
         + "0.0\n");
    File.WriteAllText(
        Path.Combine(etcFixtureDirectory, "sptable.etc"),
        "[sp table]\n"
        + "1 0\n"
        + "2 30\n"
        + "3 30\n"
        + "4 60\n"
        + "[/sp table]\n");

    var characterFixtureDirectory = Path.Combine(questFixtureRoot, "character");
    Directory.CreateDirectory(characterFixtureDirectory);
    Directory.CreateDirectory(Path.Combine(characterFixtureDirectory, "Swordman"));
    File.WriteAllText(
        Path.Combine(characterFixtureDirectory, "character.lst"),
        "0 `Swordman/Swordman.chr`\n");
    File.WriteAllText(
        Path.Combine(characterFixtureDirectory, "Swordman", "Swordman.chr"),
        "[initial value]\n"
        + "[skill]\n1 2\n2 1\n[/skill]\n"
        + "[growtype 1]\n"
        + "[growtype 2]\n[skill]\n3 1\n[/skill]\n"
        + "[growtype 3]\n[skill]\n1 0\n5 3\n[/skill]\n");
    File.WriteAllText(
        Path.Combine(characterFixtureDirectory, "exptable.tbl"),
        """
        450
        1464
        3510
        6996
        12362
        20080
        30654
        44620
        62546
        85032
        115070
        151809
        196095
        248816
        310905
        393985
        494179
        614062
        753257
        917214
        1123038
        1363827
        1643977
        1962296
        2328481
        2740543
        3210056
        3743055
        4336134
        5004170
        5883255
        6874123
        7987360
        9217890
        10590791
        12098881
        13771298
        15621583
        17638994
        19859590
        23812958
        28201365
        33059266
        38378231
        44231049
        50602242
        57576816
        65197027
        73435756
        82398442
        97208357
        113383862
        131015224
        150072973
        170753230
        193007209
        217061521
        243023296
        270812393
        """);

    var creatureFixtureDirectory = Path.Combine(questFixtureRoot, "creature");
    var botisFixtureDirectory = Path.Combine(creatureFixtureDirectory, "Botis");
    Directory.CreateDirectory(botisFixtureDirectory);
    File.WriteAllText(
        Path.Combine(creatureFixtureDirectory, "exptable.tbl"),
        "2\n5\n9\n15\n");
    File.WriteAllText(
        Path.Combine(creatureFixtureDirectory, "creature.lst"),
        "15 `Botis/Botis.cre`\n"
        + "1001 `Botis/Botis.cre`\n");
    File.WriteAllText(
        Path.Combine(botisFixtureDirectory, "Botis.cre"),
        "[max level] 4\n");

    var questScripts = CreateScripts(new ServerOptions
    {
        ScriptPvfPath = "",
        SkillScriptPath = questFixtureRoot
    });
    var questCatalog = new QuestCatalog(
        questScripts,
        NullLogger<QuestCatalog>.Instance);
    smokeItemCatalog = CreateItems(questScripts);
    smokeItemCatalog.Initialize();
    var creatureExperienceCatalog = new CreatureExperienceCatalog(
        questScripts,
        NullLogger<CreatureExperienceCatalog>.Instance);
    creatureExperienceCatalog.Initialize();
    var creatureExperienceGrant = creatureExperienceCatalog.Grant(
        currentExperience: 1,
        currentLevel: 1,
        creatureSpecies: 15,
        baseExperience: 10,
        bonusRatePercent: 10m);
    var maximumLevelCreatureGrant = creatureExperienceCatalog.Grant(
        currentExperience: creatureExperienceGrant.Experience,
        currentLevel: creatureExperienceGrant.Level,
        creatureSpecies: 15,
        baseExperience: 10,
        bonusRatePercent: 10m);
    Check(creatureExperienceCatalog.ThresholdCount == 4
        && creatureExperienceCatalog.SpeciesCount == 2
        && creatureExperienceCatalog.GetMaximumLevel(15) == 4
        && creatureExperienceCatalog.GetMaximumLevel(1_001) == 4
        && creatureExperienceCatalog.GetLevel(1, 15) == 1
        && creatureExperienceCatalog.GetLevel(2, 15) == 2
        && creatureExperienceGrant.PreviousExperience == 1
        && creatureExperienceGrant.Experience == 12
        && creatureExperienceGrant.BaseExperience == 10
        && creatureExperienceGrant.BonusExperience == 1
        && creatureExperienceGrant.Level == 4
        && creatureExperienceGrant.LeveledUp
        && maximumLevelCreatureGrant.GrantedExperience == 0
        && maximumLevelCreatureGrant.Experience == 12,
        "Creature fatigue experience uses cumulative thresholds, truncates artifact percentage bonuses and stops at the species maximum level");
    Check(smokeItemCatalog.SkillPointBookCount == 2
        && smokeItemCatalog.ExperienceBookCount == 4
        && smokeItemCatalog.AttributeStoneCount == 8
        && smokeItemCatalog.IncreaseStatusItemCount == 14
        && smokeItemCatalog.TryGetDefinition(1_034, out var hundredExperienceBook)
        && hundredExperienceBook.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.Experience,
            100)
        && smokeItemCatalog.TryGetDefinition(1_037, out var hundredThousandExperienceBook)
        && hundredThousandExperienceBook.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.Experience,
            100_000)
        && smokeItemCatalog.TryGetDefinition(1_031, out var fiveSpBook)
        && fiveSpBook.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.SkillPoints,
            5)
        && smokeItemCatalog.TryGetDefinition(1_038, out var twentySpBook)
        && twentySpBook.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.SkillPoints,
            20)
        && smokeItemCatalog.TryGetDefinition(1_043, out var hpStone)
        && hpStone.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.MaximumHp,
            25)
        && smokeItemCatalog.TryGetDefinition(1_046, out var resistanceStone)
        && resistanceStone.IncreaseStatus == new IncreaseStatusEffect(
            IncreaseStatusType.AllElementResistance,
            1),
        "legacy CMD 32 items cache their server-authoritative effects by script identity");
    var smokeDisjointCatalog = new DisjointCatalog(
        questScripts,
        NullLogger<DisjointCatalog>.Instance);
    smokeDisjointCatalog.Initialize();
    Check(smokeDisjointCatalog.IsAvailable
            && smokeDisjointCatalog.PrimaryItemId == 3_037
            && smokeDisjointCatalog.CubeCreationConstant == 150
            && smokeDisjointCatalog.RarityCount == 5
            && smokeItemCatalog.TryGetDefinition(12_007, out var disjointSource)
            && smokeDisjointCatalog.TryGenerate(
                disjointSource,
                additionalItemRoll: 0,
                jackpotRoll: 500,
                smokeItemCatalog,
                out var normalDisjointRewards)
            && normalDisjointRewards.SequenceEqual(
            [
                new DisjointRewardDefinition(3_037, 8),
                new DisjointRewardDefinition(3_033, 1)
            ])
            && smokeDisjointCatalog.TryGenerate(
                disjointSource,
                additionalItemRoll: 3,
                jackpotRoll: 0,
                smokeItemCatalog,
                out var jackpotDisjointRewards)
            && jackpotDisjointRewards.SequenceEqual(
            [
                new DisjointRewardDefinition(3_037, 8),
                new DisjointRewardDefinition(3_036, 71)
            ]),
        "disjoint rules cache rarity multipliers and normal/jackpot material outcomes");
    var disjointInventory = new Dictionary<ushort, CharacterItemRecord>
    {
        [9] = new(9, 12_007, 1, Durability: 24),
        [73] = new(73, 3_037, 1_000)
    };
    Check(DisjointItemPlanner.TryCreate(
            disjointInventory,
            sourceSlot: 9,
            itemSpace: 0,
            weightLimit: uint.MaxValue,
            additionalItemRoll: 0,
            jackpotRoll: 500,
            smokeItemCatalog,
            smokeDisjointCatalog,
            out var disjointPlan,
            out var disjointFailure)
        && disjointFailure == DisjointItemFailure.None
        && !disjointPlan.MainInventory.ContainsKey(9)
        && disjointPlan.MainInventory[73].CountOrValue == 1_008
        && disjointPlan.MainInventory[74]
            is { ItemId: 3_033, CountOrValue: 1 }
        && disjointPlan.ResponseRewards.SequenceEqual(
        [
            new GameDisjointRewardEntry(73, 3_037, 8),
            new GameDisjointRewardEntry(74, 3_033, 1)
        ])
        && disjointInventory.ContainsKey(9)
        && disjointInventory[73].CountOrValue == 1_000,
        "disjoint planning removes the source and atomically stacks all generated materials");
    var fullDisjointInventory = Enumerable.Range(
            CharacterInventoryLayout.MaterialSlotStart,
            CharacterInventoryLayout.MaterialSlotEnd
                - CharacterInventoryLayout.MaterialSlotStart)
        .Concat(Enumerable.Range(
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd
                - CharacterInventoryLayout.QuickSlotStart))
        .ToDictionary(
            slot => checked((ushort)slot),
            slot => new CharacterItemRecord(checked((ushort)slot), 3_176, 3));
    fullDisjointInventory[9] = new CharacterItemRecord(9, 12_007, 1, Durability: 24);
    Check(!DisjointItemPlanner.TryCreate(
            fullDisjointInventory,
            sourceSlot: 9,
            itemSpace: 0,
            weightLimit: uint.MaxValue,
            additionalItemRoll: 0,
            jackpotRoll: 500,
            smokeItemCatalog,
            smokeDisjointCatalog,
            out _,
            out var fullDisjointFailure)
        && fullDisjointFailure == DisjointItemFailure.InventoryFull
        && fullDisjointInventory.ContainsKey(9)
        && !DisjointItemPlanner.TryCreate(
            disjointInventory,
            sourceSlot: 9,
            itemSpace: 1,
            weightLimit: uint.MaxValue,
            additionalItemRoll: 0,
            jackpotRoll: 500,
            smokeItemCatalog,
            smokeDisjointCatalog,
            out _,
            out var invalidSpaceFailure)
        && invalidSpaceFailure == DisjointItemFailure.InvalidRequest,
        "disjoint rejects a full material/quick inventory and non-main item spaces without consuming equipment");
    Check(smokeItemCatalog.LotteryCount == 1
        && smokeItemCatalog.TryGetDefinition(7_143, out var cachedLottery)
        && cachedLottery.Lottery is not null
        && cachedLottery.Lottery.Select(0)
            == new LotteryRewardDefinition(14_873, 1, 25_000)
        && cachedLottery.Lottery.Select(24_999).ItemId == 14_873
        && cachedLottery.Lottery.Select(25_000).ItemId == 39_000
        && cachedLottery.Lottery.Select(25_100).ItemId == 10_000,
        "upgradable-legacy int data is cached as fixed-100000 weighted rewards with a fallback");
    Check(LotteryItemPlanner.TryCreate(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [43] = new(43, 7_143, 2)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 43,
            characterLevel: 60,
            currentGold: 1_000,
            weightLimit: uint.MaxValue,
            roll: 0,
            catalog: smokeItemCatalog,
            out var lotteryPlan,
            out var lotteryFailure)
        && lotteryFailure == LotteryUseFailure.None
        && lotteryPlan.SourceSlot == 43
        && lotteryPlan.DestinationSlot == CharacterInventoryLayout.EquipmentSlotStart
        && lotteryPlan.MainInventory[43].CountOrValue == 1
        && lotteryPlan.Gold == 800
        && lotteryPlan.MainInventory[CharacterInventoryLayout.EquipmentSlotStart]
            is { ItemId: 14_873, CountOrValue: 1, Durability: 1_000 },
        "lottery planning places the reward before atomically consuming one source item");
    Check(!LotteryItemPlanner.TryCreate(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [43] = new(43, 7_143, 1)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 43,
            characterLevel: 9,
            currentGold: 1_000,
            weightLimit: uint.MaxValue,
            roll: 0,
            catalog: smokeItemCatalog,
            out _,
            out var lowLevelLotteryFailure)
        && lowLevelLotteryFailure == LotteryUseFailure.LevelTooLow,
        "lottery planning rejects a source item above the character level without consuming it");
    Check(!LotteryItemPlanner.TryCreate(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [43] = new(43, 7_143, 1)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 43,
            characterLevel: 60,
            currentGold: 199,
            weightLimit: uint.MaxValue,
            roll: 0,
            catalog: smokeItemCatalog,
            out _,
            out var lotteryGoldFailure)
        && lotteryGoldFailure == LotteryUseFailure.InsufficientGold,
        "lottery use cost is validated before the source item is consumed");
    Check(LotteryItemPlanner.TryCreate(
            new Dictionary<ushort, CharacterItemRecord>
            {
                [43] = new(43, 7_143, 1)
            },
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 43,
            characterLevel: 60,
            currentGold: 1_000,
            weightLimit: uint.MaxValue,
            roll: 25_000,
            catalog: smokeItemCatalog,
            out var avatarLotteryPlan,
            out _)
        && !avatarLotteryPlan.MainInventory.ContainsKey(43)
        && avatarLotteryPlan.DestinationSlot == 0
        && avatarLotteryPlan.ResponseCountOrValue == 0
        && avatarLotteryPlan.AvatarInventory[0]
            is
            {
                ItemId: 39_000,
                CountOrValue: 1,
                AvatarRemainingSeconds: 0,
                AvatarAbilityIndex: 0
            },
        "lottery Avatar rewards enter permanent Avatar storage with ability index zero");
    var fullLotteryInventory = Enumerable.Range(
            CharacterInventoryLayout.EquipmentSlotStart,
            CharacterInventoryLayout.EquipmentSlotEnd
                - CharacterInventoryLayout.EquipmentSlotStart)
        .Concat(Enumerable.Range(
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd
                - CharacterInventoryLayout.QuickSlotStart))
        .ToDictionary(
            slot => checked((ushort)slot),
            slot => new CharacterItemRecord(checked((ushort)slot), 10_000, 1));
    fullLotteryInventory[43] = new CharacterItemRecord(43, 7_143, 1);
    Check(!LotteryItemPlanner.TryCreate(
            fullLotteryInventory,
            new Dictionary<ushort, CharacterItemRecord>(),
            new Dictionary<ushort, CharacterItemRecord>(),
            sourceSlot: 43,
            characterLevel: 60,
            currentGold: 1_000,
            weightLimit: uint.MaxValue,
            roll: 0,
            catalog: smokeItemCatalog,
            out _,
            out var fullLotteryFailure)
        && fullLotteryFailure == LotteryUseFailure.InventoryFull
        && fullLotteryInventory[43].CountOrValue == 1,
        "a full lottery destination rejects the draw without consuming the source item");
    var questCharacter = new CharacterRecord(
        Guid.NewGuid(), "QuestTest", 0, 2, 60, 500, 50_000);
    Check(questCatalog.GetAcceptableQuestIds(questCharacter, [], [], 2)
            .SequenceEqual(new ushort[] { 1020 }),
        "quest catalog hides the tutorial handoff quest from ordinary NPC acceptance");
    Check(questCatalog.GetAcceptableQuestIds(
            questCharacter,
            [new CharacterQuestRecord(1016)],
            [],
            2).Count == 0,
        "quest catalog excludes active quests and their collisions");
    Check(questCatalog.GetAcceptableQuestIds(questCharacter, [], [1016], 2)
            .SequenceEqual(new ushort[] { 1017 }),
        "quest catalog unlocks prerequisites and excludes completed collisions");
    var baseSwordman = new CharacterRecord(
        Guid.NewGuid(), "ClassProbe", 0, 0, 18, 0, 0);
    Check(questCatalog.GetAcceptableQuestIds(baseSwordman, [], [])
            .Contains((ushort)1023)
        && !questCatalog.GetAcceptableQuestIds(baseSwordman, [], [])
            .Contains((ushort)1024)
        && questCatalog.GetAcceptableQuestIds(baseSwordman, [], [1023])
            .Contains((ushort)1024)
        && questCatalog.GetAcceptableQuestIds(baseSwordman, [], [1024])
            .Contains((ushort)1025)
        && !questCatalog.GetAcceptableQuestIds(
                baseSwordman with { GrowType = 2 },
                [],
                [])
            .Any(questId => questId is >= 1023 and <= 1025),
        "class-change chains are base-class only and repeated prerequisite blocks are alternative prerequisite groups");
    var advancedFighter = new CharacterRecord(
        Guid.NewGuid(), "AwakeningProbe", 1, 3, 48, 0, 0);
    Check(questCatalog.GetAcceptableQuestIds(advancedFighter, [], [])
            .Contains((ushort)1026)
        && !questCatalog.GetAcceptableQuestIds(advancedFighter, [], [])
            .Contains((ushort)1027)
        && questCatalog.GetAcceptableQuestIds(advancedFighter, [], [1026])
            .Contains((ushort)1027)
        && questCatalog.GetAcceptableQuestIds(advancedFighter, [], [1027])
            .Contains((ushort)1028)
        && !questCatalog.GetAcceptableQuestIds(
                advancedFighter with { AwakeningType = 1 },
                [],
                [])
            .Any(questId => questId is >= 1026 and <= 1028),
        "awakening chains require an advanced unawakened class and infer missing numbered predecessors from script paths");
    Check(questCatalog.TryGetDefinition(1025, out var classChangeRewardQuest)
        && classChangeRewardQuest.JobChangeQuest == 1
        && classChangeRewardQuest.GrowthReward == 2
        && classChangeRewardQuest.TryGetGrowthReward(
            out var classChainType,
            out var classGrowNumber)
        && classChainType == 1
        && classGrowNumber == 2
        && questCatalog.TryGetDefinition(1028, out var awakeningRewardQuest)
        && awakeningRewardQuest.JobChangeQuest == 2
        && awakeningRewardQuest.TryGetGrowthReward(
            out var awakeningChainType,
            out var awakeningGrowNumber)
        && awakeningChainType == 2
        && awakeningGrowNumber == 1,
        "quest catalog caches PVF class and awakening reward branches for command 36");
    Check(questCatalog.TryGetDefinition(1016, out var seriaQuest)
        && QuestCatalog.IsTutorialCompletionQuestDefinition(seriaQuest)
        && seriaQuest.NpcIndex == 2
        && seriaQuest.CompleteNpcIndex == 2
        && seriaQuest.Type == "meet npc"
        && seriaQuest.FixedRewardItems.SequenceEqual(
            [new QuestRewardItemDefinition(1055, 3)])
        && seriaQuest.GoldReward == 200,
        "quest catalog identifies the DFLegacy tutorial-completion meet-NPC task metadata");
    Check(questCatalog.TryGetDefinition(1021, out var selectionQuest)
        && selectionQuest.FixedRewardItems.SequenceEqual(
            [new QuestRewardItemDefinition(1000, 1)])
        && selectionQuest.SelectableRewardItems.SequenceEqual(
        [
            new QuestRewardItemDefinition(27093, 1),
            new QuestRewardItemDefinition(14873, 1)
        ]),
        "quest catalog parses fixed, gold-marker and selectable rewards");
    Check(questCatalog.TryGetDefinition(1022, out var taskDropQuest)
        && taskDropQuest.Type == "seeking"
        && taskDropQuest.ConditionRows.Single().SequenceEqual(new[] { 4000, 2 })
        && taskDropQuest.DependGiveItems.SequenceEqual(
        [
            new QuestDependGiveItemDefinition(4000, 1)
        ])
        && taskDropQuest.ClearRewardItems.SequenceEqual(
        [
            new QuestClearRewardItemDefinition(2, 3, 4000, 2, 25, 4)
        ])
        && taskDropQuest.MonsterRewardItems.SequenceEqual(
        [
            new QuestMonsterRewardItemDefinition(77, -1, 3, 4000, 1, 50, 4)
        ])
        && smokeItemCatalog.TryGetDefinition(4000, out var taskItemDefinition)
        && taskItemDefinition.InventoryCategory == ItemInventoryCategory.Quest,
        "quest catalog parses generic clear and monster task-item drop rows");

    var acceptedTaskItemPlan = QuestAcceptancePlanner.Create(
        new Dictionary<ushort, CharacterItemRecord>
        {
            [CharacterInventoryLayout.QuestSlotStart] = new(
                CharacterInventoryLayout.QuestSlotStart,
                4000,
                1)
        },
        taskDropQuest,
        smokeItemCatalog,
        uint.MaxValue);
    Check(acceptedTaskItemPlan.Success
        && acceptedTaskItemPlan.Inventory[CharacterInventoryLayout.QuestSlotStart]
            .CountOrValue == 2
        && acceptedTaskItemPlan.InsertedItems.SequenceEqual(
        [
            new QuestInsertedReward(
                CharacterInventoryLayout.QuestSlotStart,
                4000,
                1)
        ])
        && QuestItemRequirementPlanner.GetInitialTrigger(
            taskDropQuest,
            acceptedTaskItemPlan.Inventory.Values,
            gold: 0,
            victoryPoints: 0) == 0,
        "accepting a seeking quest stacks depend-give items before calculating its initial trigger");

    var fullTaskInventory = Enumerable.Range(
            CharacterInventoryLayout.QuickSlotStart,
            CharacterInventoryLayout.QuickSlotEnd
                - CharacterInventoryLayout.QuickSlotStart)
        .Select(slot => new CharacterItemRecord((ushort)slot, 3037, 1))
        .Concat(Enumerable.Range(
                CharacterInventoryLayout.QuestSlotStart,
                CharacterInventoryLayout.QuestSlotEnd
                    - CharacterInventoryLayout.QuestSlotStart)
            .Select(slot => new CharacterItemRecord((ushort)slot, 4000, 1000)))
        .ToDictionary(item => item.Slot);
    var rejectedTaskItemPlan = QuestAcceptancePlanner.Create(
        fullTaskInventory,
        taskDropQuest,
        smokeItemCatalog,
        uint.MaxValue);
    Check(!rejectedTaskItemPlan.Success
        && rejectedTaskItemPlan.Failure == InventoryPlacementFailure.Full
        && rejectedTaskItemPlan.Inventory.SequenceEqual(fullTaskInventory),
        "depend-give item allocation rejects the whole quest plan when its category and quick slots are full");

    var existingTaskItems = new CharacterItemRecord[]
    {
        new(CharacterInventoryLayout.QuestSlotStart, 4000, 3)
    };
    var observedMonsterChance = 0;
    var plannedMonsterTaskDrops = QuestItemDropPlanner.PlanMonster(
        taskDropQuest,
        dungeonId: 99,
        difficulty: 3,
        monsterIndex: 77,
        existingTaskItems,
        chance =>
        {
            observedMonsterChance = chance;
            return true;
        });
    Check(observedMonsterChance == 50
        && plannedMonsterTaskDrops.SequenceEqual([new QuestItemDrop(4000, 1)])
        && QuestItemDropPlanner.PlanMonster(
                taskDropQuest,
                dungeonId: 99,
                difficulty: 2,
                monsterIndex: 77,
                [],
                _ => true).Count == 0
        && QuestItemDropPlanner.PlanMonster(
                taskDropQuest,
                dungeonId: 99,
                difficulty: 3,
                monsterIndex: 78,
                [],
                _ => true).Count == 0
        && QuestItemDropPlanner.PlanMonster(
                taskDropQuest,
                dungeonId: 99,
                difficulty: 3,
                monsterIndex: 77,
                [],
                _ => false).Count == 0,
        "monster task-item drops apply monster, dungeon, difficulty and PVF chance filters");

    var cappedDropRolled = false;
    var cappedTaskItems = new CharacterItemRecord[]
    {
        new(CharacterInventoryLayout.QuestSlotStart, 4000, 4)
    };
    Check(QuestItemDropPlanner.PlanMonster(
                taskDropQuest,
                dungeonId: 99,
                difficulty: 3,
                monsterIndex: 77,
                cappedTaskItems,
                _ => cappedDropRolled = true).Count == 0
        && !cappedDropRolled
        && QuestItemDropPlanner.PlanClear(
                taskDropQuest,
                dungeonId: 2,
                difficulty: 3,
                existingTaskItems,
                _ => true).SequenceEqual([new QuestItemDrop(4000, 1)])
        && QuestItemDropPlanner.PlanClear(
                taskDropQuest,
                dungeonId: 3,
                difficulty: 3,
                [],
                _ => true).Count == 0,
        "task-item planning caps the aggregate held count before rolling and truncates clear rewards to the remaining capacity");

    var oneTaskItem = new Dictionary<ushort, CharacterItemRecord>
    {
        [CharacterInventoryLayout.QuestSlotStart] =
            new(CharacterInventoryLayout.QuestSlotStart, 4000, 1)
    };
    var threeTaskItems = new Dictionary<ushort, CharacterItemRecord>
    {
        [CharacterInventoryLayout.QuestSlotStart] =
            new(CharacterInventoryLayout.QuestSlotStart, 4000, 3)
    };
    Check(QuestItemRequirementPlanner.GetInitialTrigger(
                taskDropQuest,
                oneTaskItem.Values,
                gold: 0,
                victoryPoints: 0) == 1
        && QuestItemRequirementPlanner.GetInitialTrigger(
                taskDropQuest,
                threeTaskItems.Values,
                gold: 0,
                victoryPoints: 0) == 0
        && QuestItemRequirementPlanner.TryConsume(
                taskDropQuest,
                threeTaskItems,
                gold: 0,
                victoryPoints: 0,
                out var taskConsumption)
        && taskConsumption.Inventory[CharacterInventoryLayout.QuestSlotStart]
            .CountOrValue == 1
        && taskConsumption.ChangedItems.SequenceEqual(
        [
            new QuestConsumedItemChange(CharacterInventoryLayout.QuestSlotStart, 1)
        ]),
        "seeking task progress is derived from server inventory and completion consumes the required stack count");

    var compositeSeekingQuest = taskDropQuest with
    {
        ConditionRows =
        [
            [0, 200],
            [2, 50],
            [4000, 2]
        ]
    };
    Check(!QuestItemRequirementPlanner.IsSatisfied(
            compositeSeekingQuest,
            threeTaskItems.Values,
            gold: 199,
            victoryPoints: 50)
        && !QuestItemRequirementPlanner.IsSatisfied(
            compositeSeekingQuest,
            threeTaskItems.Values,
            gold: 200,
            victoryPoints: 49)
        && QuestItemRequirementPlanner.TryConsume(
            compositeSeekingQuest,
            threeTaskItems,
            gold: 200,
            victoryPoints: 50,
            out var compositeConsumption)
        && compositeConsumption.GoldCost == 200
        && compositeConsumption.VictoryPointCost == 50
        && compositeConsumption.Inventory[CharacterInventoryLayout.QuestSlotStart]
            .CountOrValue == 1,
        "generic seeking completion validates item, gold and victory-point requirements in one plan");

    var rewardCharacter = new CharacterRecord(
        Guid.NewGuid(),
        "RewardTest",
        0,
        2,
        1,
        500,
        50_000,
        Gold: 1_000,
        Inventory: [new CharacterItemRecord(5, 1055, 2)],
        Experience: 440);
    var rewardPlan = QuestRewardPlanner.Create(
        rewardCharacter,
        seriaQuest,
        -1,
        smokeItemCatalog,
        uint.MaxValue);
    Check(rewardPlan.Success
        && rewardPlan.Gold == 1_200
        && rewardPlan.Inventory.Single(item => item.ItemId == 1055).CountOrValue == 5
        && rewardPlan.InsertedItems.SequenceEqual(
            [new QuestInsertedReward(5, 1055, 3)]),
        "quest reward planner stacks fixed items, records the exact inserted delta and adds scripted gold");
    var warehouseUpgradeRewardPlan = QuestRewardPlanner.Create(
        rewardCharacter with { WarehouseCapacity = 8 },
        seriaQuest with
        {
            FixedRewardItems = [new QuestRewardItemDefinition(60, 1)],
            GoldReward = 0
        },
        -1,
        smokeItemCatalog,
        uint.MaxValue);
    Check(warehouseUpgradeRewardPlan.Success
        && warehouseUpgradeRewardPlan.WarehouseCapacity == 88
        && warehouseUpgradeRewardPlan.Inventory.All(item => item.ItemId != 60)
        && warehouseUpgradeRewardPlan.MailedItems.All(item => item.ItemId != 60),
        "quest rewards settle warehouse kits as direct capacity effects without requiring bag or mailbox space");
    var selectablePlan = QuestRewardPlanner.Create(
        rewardCharacter,
        selectionQuest,
        1,
        smokeItemCatalog,
        uint.MaxValue);
    Check(selectablePlan.Success
        && selectablePlan.Inventory.Any(item =>
            item.ItemId == 14873 && item.CountOrValue == 1 && item.Durability == 1_000)
        && selectablePlan.Inventory.All(item => item.ItemId != 27093),
        "quest reward planner grants only the selected equipment reward");
    var fullInventoryCharacter = rewardCharacter with
    {
        Inventory = Enumerable.Range(3, 38)
            .Select(slot => new CharacterItemRecord((ushort)slot, 27_093, 1, Durability: 1_000))
            .ToList()
    };
    var equipmentRewardQuest = seriaQuest with
    {
        FixedRewardItems = [new QuestRewardItemDefinition(27093, 1)],
        GoldReward = 0
    };
    var mailedQuestReward = QuestRewardPlanner.Create(
            fullInventoryCharacter,
            equipmentRewardQuest,
            -1,
            smokeItemCatalog,
            uint.MaxValue);
    Check(mailedQuestReward.Success
        && mailedQuestReward.MailedItems.SingleOrDefault() is
        {
            ItemId: 27_093,
            CountOrValue: 1,
            Durability: 1_000,
            InstanceId: var mailedQuestRewardInstanceId
        }
        && mailedQuestRewardInstanceId != Guid.Empty
        && mailedQuestReward.Inventory.Count == fullInventoryCharacter.Inventory.Count,
        "quest completion sends a reward to mail without mutating a full town inventory");

    var experienceCatalog = new CharacterExperienceCatalog(
        questScripts,
        NullLogger<CharacterExperienceCatalog>.Instance);
    var spCatalog = new CharacterSpCatalog(
        questScripts,
        NullLogger<CharacterSpCatalog>.Instance);
    spCatalog.Initialize();
    Check(spCatalog.Count == 4
        && spCatalog.GetRewardForLevel(1) == 0
        && spCatalog.GetRewardForLevel(2) == 30
        && spCatalog.CalculateLevelUpReward(1, 2) == 30
        && spCatalog.CalculateLevelUpReward(1, 4) == 120
        && spCatalog.CalculateTotalReward(4) == 120
        && spCatalog.CalculateLevelUpReward(4, 4) == 0,
        "SP table is cached and multi-level progression sums each entered level");
    var levelUp = experienceCatalog.Grant(rewardCharacter, 20);
    Check(levelUp.Level == 2 && levelUp.Experience == 460,
        "cumulative experience crosses the level-one threshold");
    Check(experienceCatalog.GetMinimumCumulativeExperience(1) == 0
        && experienceCatalog.GetMinimumCumulativeExperience(2) == 450
        && experienceCatalog.GetMinimumCumulativeExperience(4) == 3_510
        && experienceCatalog.NormalizeCumulativeExperience(4, 1_964) == 4_010,
        "character-level lower bounds use the preceding cumulative threshold and retain progress from an off-by-one baseline");
    var multiLevelUp = experienceCatalog.Grant(rewardCharacter with { Experience = 0 }, 4_000);
    Check(multiLevelUp.Level == 4 && multiLevelUp.Experience == 4_000,
        "one experience grant can advance several levels");
    var maximumLevelGrant = experienceCatalog.Grant(
        rewardCharacter with { Level = CharacterExperienceCatalog.MaximumLevel },
        10_000);
    Check(maximumLevelGrant.Level == CharacterExperienceCatalog.MaximumLevel
        && maximumLevelGrant.GrantedExperience == 0,
        "maximum-level characters do not progress beyond level sixty");
    var levelSixtyGrant = experienceCatalog.Grant(
        rewardCharacter with
        {
            Level = 59,
            Experience = 243_023_296
        },
        27_789_097);
    Check(levelSixtyGrant.Level == 60
        && levelSixtyGrant.Experience == 270_812_393,
        "DFLegacy cumulative threshold advances level fifty-nine to sixty");
    var maximumTransferCharacters = MaximumTransferRoster.Create();
    Check(maximumTransferCharacters.Length == 20
        && MaximumTransferRoster.CharacterLevel == 60
        && maximumTransferCharacters.Select(character => character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 20
        && maximumTransferCharacters.All(character =>
            character.Level == MaximumTransferRoster.CharacterLevel
            && character.Sp == MaximumTransferRoster.CharacterSkillPoints
            && character.Gold == MaximumTransferRoster.CharacterGold
            && character.Inventory?.Count(item =>
                item.ItemId == MaximumTransferRoster.ColorlessCubeFragmentItemId
                && item.CountOrValue == MaximumTransferRoster.ColorlessCubeFragmentCount
                && item.Slot >= 9) == 1),
        "maximum transfer roster creates twenty unique level-sixty funded characters with colorless cubes");
    Check(Enumerable.Range(0, 5).All(job =>
            maximumTransferCharacters.Count(character => character.Job == job) == 4
            && Enumerable.Range(1, 4).All(growType =>
                maximumTransferCharacters.Count(character =>
                    character.Job == job && character.GrowType == growType) == 1)),
        "maximum transfer roster covers all four transfers for all five DFLegacy jobs");
    var maximumTransferUserInfo = GameProtocolEngine.CreateUserInfo(
        maximumTransferCharacters
            .Select((character, slot) => new GameCharacterSummary(
                (ushort)slot,
                Encoding.ASCII.GetBytes(character.Name),
                (byte)character.Job,
                (byte)character.GrowType,
                (byte)character.Level))
            .ToArray());
    Check(BinaryPrimitives.ReadUInt16LittleEndian(
            maximumTransferUserInfo.Payload.AsSpan(1, sizeof(ushort))) == 20,
        "character-selection packet carries the complete twenty-character transfer roster");
    Check(experienceCatalog.CalculateQuestExperience(
            rewardCharacter with { Experience = 0 },
            seriaQuest) == 45,
        "level-one non-repeatable quest experience uses the DFLegacy threshold table");

    var experienceStore = new JsonGameStore(
        new ServerOptions
        {
            DataPath = Path.Combine(questFixtureRoot, "experience-state.json")
        },
        NullLogger<JsonGameStore>.Instance,
        experienceCatalog);
    await experienceStore.InitializeAsync();
    var thresholdCharacterCreation = await experienceStore.CreateCharacterAsync(
        "test",
        new CreateCharacterRequest("ThresholdLevelFour", Level: 4));
    Check(thresholdCharacterCreation.Created
        && thresholdCharacterCreation.Character?.Experience == 3_510,
        "creating a high-level character initializes cumulative experience at that level's lower bound");
    if (thresholdCharacterCreation.Character is { } thresholdCharacter)
    {
        await experienceStore.SaveCharacterProgressionAsync(
            "test",
            thresholdCharacter.Id,
            level: 4,
            experience: 1_964,
            skillPoints: thresholdCharacter.Sp);
        var normalizedExperienceStore = new JsonGameStore(
            new ServerOptions
            {
                DataPath = Path.Combine(questFixtureRoot, "experience-state.json")
            },
            NullLogger<JsonGameStore>.Instance,
            experienceCatalog);
        await normalizedExperienceStore.InitializeAsync();
        var normalizedThresholdCharacter =
            (await normalizedExperienceStore.GetCharactersAsync("test"))
            .Single(character => character.Id == thresholdCharacter.Id);
        Check(normalizedThresholdCharacter.Experience == 4_010,
            "loading persisted state repairs an off-by-one level baseline without discarding earned progress");
    }

    var experienceBookCharacterCreation = await experienceStore.CreateCharacterAsync(
        "test",
        new CreateCharacterRequest("ExperienceBookUser", Level: 1));
    if (experienceBookCharacterCreation.Character is { } experienceBookCharacter)
    {
        var preparedExperienceBookCharacter =
            await experienceStore.SaveCharacterInventoryAsync(
                "test",
                experienceBookCharacter.Id,
                experienceBookCharacter.Gold,
                (experienceBookCharacter.Inventory ?? [])
                    .Append(new CharacterItemRecord(41, 1_035, 2)),
                experienceBookCharacter.Equipment ?? []);
        var experienceBookResult = await experienceStore.UseExperienceBookItemAsync(
            "test",
            experienceBookCharacter.Id,
            sourceSlot: 41,
            expectedItemId: 1_035,
            experienceAmount: 1_000,
            experienceCatalog,
            spCatalog);
        var rejectedChangedExperienceBook =
            await experienceStore.UseExperienceBookItemAsync(
                "test",
                experienceBookCharacter.Id,
                sourceSlot: 41,
                expectedItemId: 1_034,
                experienceAmount: 100,
                experienceCatalog,
                spCatalog);
        var reloadedExperienceBookStore = new JsonGameStore(
            new ServerOptions
            {
                DataPath = Path.Combine(questFixtureRoot, "experience-state.json")
            },
            NullLogger<JsonGameStore>.Instance,
            experienceCatalog);
        await reloadedExperienceBookStore.InitializeAsync();
        var reloadedExperienceBookCharacter =
            (await reloadedExperienceBookStore.GetCharactersAsync("test"))
            .Single(character => character.Id == experienceBookCharacter.Id);
        Check(preparedExperienceBookCharacter is not null
            && experienceBookResult is
            {
                Success: true,
                RemainingItemCount: 1,
                GrantedSkillPoints: 30,
                Failure: ExperienceBookItemUseFailure.None,
                Progression:
                {
                    PreviousLevel: 1,
                    Level: 2,
                    Experience: 1_000,
                    GrantedExperience: 1_000
                }
            }
            && rejectedChangedExperienceBook.Failure
                == ExperienceBookItemUseFailure.ItemStateChanged
            && reloadedExperienceBookCharacter.Level == 2
            && reloadedExperienceBookCharacter.Experience == 1_000
            && reloadedExperienceBookCharacter.Sp == experienceBookCharacter.Sp + 30
            && reloadedExperienceBookCharacter.Inventory?.Single(item => item.Slot == 41)
                is { ItemId: 1_035, CountOrValue: 1 },
            "experience books atomically consume one item, grant cumulative experience, cross levels and persist SP-table rewards");
    }
    else
    {
        Check(false, "experience-book fixture character is created");
    }

    var levelUpCouponCharacterCreation = await experienceStore.CreateCharacterAsync(
        "test",
        new CreateCharacterRequest("LevelUpCouponUser", Level: 1));
    if (levelUpCouponCharacterCreation.Character is { } levelUpCouponCharacter)
    {
        var preparedLevelUpCouponCharacter =
            await experienceStore.SaveCharacterInventoryAsync(
                "test",
                levelUpCouponCharacter.Id,
                levelUpCouponCharacter.Gold,
                (levelUpCouponCharacter.Inventory ?? [])
                    .Append(new CharacterItemRecord(41, 7_519, 2)),
                levelUpCouponCharacter.Equipment ?? []);
        var levelUpCouponResult = await experienceStore.UseLevelUpCouponItemAsync(
            "test",
            levelUpCouponCharacter.Id,
            sourceSlot: 41,
            expectedItemId: 7_519,
            experienceCatalog,
            spCatalog);
        var persistedLevelUpCouponCharacter =
            (await experienceStore.GetCharactersAsync("test"))
            .Single(character => character.Id == levelUpCouponCharacter.Id);
        Check(preparedLevelUpCouponCharacter is not null
            && levelUpCouponResult is
            {
                Success: true,
                RemainingItemCount: 1,
                GrantedSkillPoints: 30,
                Failure: ExperienceBookItemUseFailure.None,
                Progression:
                {
                    PreviousLevel: 1,
                    Level: 2,
                    Experience: 450,
                    GrantedExperience: 450
                }
            }
            && persistedLevelUpCouponCharacter.Level == 2
            && persistedLevelUpCouponCharacter.Experience == 450
            && persistedLevelUpCouponCharacter.Sp == levelUpCouponCharacter.Sp + 30
            && persistedLevelUpCouponCharacter.Inventory?.Single(item => item.Slot == 41)
                is { ItemId: 7_519, CountOrValue: 1 },
            "level-up coupons atomically consume one item, reach exactly the next cumulative threshold and persist SP-table rewards");
    }
    else
    {
        Check(false, "level-up coupon fixture character is created");
    }

    var storeOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "state.json")
    };
    var questStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance,
        experienceCatalog);
    await questStore.InitializeAsync();
    var storedCharacter = (await questStore.GetCharactersAsync("test"))[0];
    Check(storedCharacter.Name == JsonGameStore.PresetCharacterName
        && storedCharacter.Job == JsonGameStore.PresetCharacterJob
        && storedCharacter.GrowType == JsonGameStore.PresetCharacterGrowType
        && storedCharacter.Level == CharacterExperienceCatalog.MaximumLevel
        && storedCharacter.Experience == JsonGameStore.PresetCharacterExperience
        && storedCharacter.Experience == 270_812_393,
        "preset level-sixty Berserker starts with the DFLegacy cumulative experience");
    var initialSp = storedCharacter.Sp;
    var permanentStatusItems = new (ushort Slot, ushort ItemId, IncreaseStatusEffect Effect)[]
    {
        (45, 1_038, new(IncreaseStatusType.SkillPoints, 20)),
        (46, 1_043, new(IncreaseStatusType.MaximumHp, 25)),
        (47, 1_044, new(IncreaseStatusType.MaximumMp, 25)),
        (48, 1_039, new(IncreaseStatusType.Strength, 5)),
        (49, 1_041, new(IncreaseStatusType.Vitality, 5)),
        (50, 1_040, new(IncreaseStatusType.Intelligence, 5)),
        (51, 1_042, new(IncreaseStatusType.Spirit, 5)),
        (52, 1_045, new(IncreaseStatusType.MovementSpeed, 1)),
        (53, 1_046, new(IncreaseStatusType.AllElementResistance, 1))
    };
    var permanentStatusSlots = permanentStatusItems
        .Select(item => item.Slot)
        .ToHashSet();
    var inventoryWithPermanentStatusItems = (storedCharacter.Inventory ?? [])
        .Where(item => !permanentStatusSlots.Contains(item.Slot))
        .Concat(permanentStatusItems.Select(item =>
            new CharacterItemRecord(item.Slot, item.ItemId, 2)))
        .ToArray();
    var preparedPermanentStatusCharacter = await questStore.SaveCharacterInventoryAsync(
        "test",
        storedCharacter.Id,
        storedCharacter.Gold,
        inventoryWithPermanentStatusItems,
        storedCharacter.Equipment ?? [],
        CancellationToken.None);
    var permanentStatusResults = new List<IncreaseStatusItemUseResult>();
    foreach (var item in permanentStatusItems)
    {
        permanentStatusResults.Add(await questStore.UseIncreaseStatusItemAsync(
            "test",
            storedCharacter.Id,
            item.Slot,
            item.ItemId,
            item.Effect));
    }

    var rejectedChangedStatusItem = await questStore.UseIncreaseStatusItemAsync(
        "test",
        storedCharacter.Id,
        sourceSlot: 45,
        expectedItemId: 1_031,
        new IncreaseStatusEffect(IncreaseStatusType.SkillPoints, 5));
    var reloadedSpStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedSpStore.InitializeAsync();
    var reloadedSpCharacter = (await reloadedSpStore.GetCharactersAsync("test"))[0];
    Check(preparedPermanentStatusCharacter is not null
        && permanentStatusResults.Count == permanentStatusItems.Length
        && permanentStatusResults.All(result => result is
        {
            Success: true,
            RemainingItemCount: 1,
            Failure: IncreaseStatusItemUseFailure.None
        })
        && rejectedChangedStatusItem.Failure
            == IncreaseStatusItemUseFailure.ItemStateChanged
        && reloadedSpCharacter.Sp == initialSp + 20
        && reloadedSpCharacter.BonusSp == 20
        && reloadedSpCharacter.BonusMaximumHp == 25
        && reloadedSpCharacter.BonusMaximumMp == 25
        && reloadedSpCharacter.BonusStrength == 5
        && reloadedSpCharacter.BonusVitality == 5
        && reloadedSpCharacter.BonusIntelligence == 5
        && reloadedSpCharacter.BonusSpirit == 5
        && reloadedSpCharacter.BonusMovementSpeed == 1
        && reloadedSpCharacter.BonusAllElementResistance == 1
        && permanentStatusItems.All(expected =>
            reloadedSpCharacter.Inventory?.Single(item => item.Slot == expected.Slot)
                is { CountOrValue: 1 } remaining
            && remaining.ItemId == expected.ItemId),
        "CMD 32 atomically consumes one item and persists SP and all eight legacy attribute-stone totals");

    var skillResetSource = (await questStore.GetCharactersAsync("test"))[0];
    var resetSkillLayout = new[]
    {
        new CharacterSkillRecord(6, 5, 1),
        new CharacterSkillRecord(204, 200, 3)
    };
    var characterWithLearnedSkills = await questStore.SaveCharacterSkillsAsync(
        "test",
        skillResetSource.Id,
        remainingSp: 1,
        [
            new CharacterSkillRecord(6, 1, 10),
            new CharacterSkillRecord(48, 2, 5),
            new CharacterSkillRecord(204, 200, 3)
        ]);
    var skillResetInventory = (characterWithLearnedSkills?.Inventory ?? [])
        .Where(item => item.Slot != 60)
        .Append(new CharacterItemRecord(60, 3, 2))
        .ToArray();
    var characterWithSkillResetItem = await questStore.SaveCharacterInventoryAsync(
        "test",
        skillResetSource.Id,
        skillResetSource.Gold,
        skillResetInventory,
        characterWithLearnedSkills?.Equipment ?? [],
        CancellationToken.None);
    var accumulatedLevelSkillPoints = spCatalog.CalculateTotalReward(
        skillResetSource.Level);
    var skillResetResult = await questStore.UseSkillResetItemAsync(
        "test",
        skillResetSource.Id,
        sourceSlot: 60,
        expectedItemId: 3,
        accumulatedLevelSkillPoints,
        resetSkillLayout);
    var rejectedChangedSkillResetItem = await questStore.UseSkillResetItemAsync(
        "test",
        skillResetSource.Id,
        sourceSlot: 60,
        expectedItemId: 28,
        accumulatedLevelSkillPoints,
        resetSkillLayout);
    var skillResetReloadStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance);
    await skillResetReloadStore.InitializeAsync();
    var skillResetCharacter = (await skillResetReloadStore.GetCharactersAsync("test"))[0];
    Check(characterWithSkillResetItem is not null
        && skillResetResult is
        {
            Success: true,
            RemainingItemCount: 1,
            Failure: SkillResetItemUseFailure.None
        }
        && rejectedChangedSkillResetItem.Failure
            == SkillResetItemUseFailure.ItemStateChanged
        && skillResetCharacter.Sp
            == accumulatedLevelSkillPoints + skillResetCharacter.BonusSp
        && skillResetCharacter.BonusSp == 20
        && skillResetCharacter.Skills?.SequenceEqual(resetSkillLayout) == true
        && skillResetCharacter.Inventory?.Single(item => item.Slot == 60)
            is { ItemId: 3, CountOrValue: 1 },
        "River Lethe atomically consumes one item, recomputes level SP plus bonus SP and persists the reset layout");

    var fallbackStatCatalog = new CharacterStatCatalog(
        CreateScripts(new ServerOptions
        {
            ScriptPvfPath = "",
            SkillScriptPath = ""
        }),
        NullLogger<CharacterStatCatalog>.Instance);
    var parsedCharacterGrowth = CharacterStatCatalog.Parse(
        """
        [initial value]
        [HP MAX] 100
        [MP MAX] 80
        [physical attack] 7
        [physical defense] 6
        [magical attack] 5
        [magical defense] 4
        [inventory limit] 30000
        [skill]
        [/skill]
        [growtype 1]
        [HP MAX] 10
        [physical attack] 1.25
        [growtype 2]
        [HP MAX] 20
        [physical attack] 2.5
        [skill]
        [/skill]
        [awakening name] `class`
        [awakening 1]
        [HP MAX] 30
        [physical attack] 3.5
        [awakening skill]
        [/awakening skill]
        [awakening 2]
        [HP MAX] 40
        [physical attack] 4.5
        [awakening skill]
        [/awakening skill]
        """,
        "synthetic/character.chr");
    Check(parsedCharacterGrowth.Growth[1].MaximumHp == 10m
        && parsedCharacterGrowth.Growth[1].Strength == 1.25m
        && parsedCharacterGrowth.Growth[2].MaximumHp == 20m
        && parsedCharacterGrowth.AwakeningGrowth[(2, 1)].MaximumHp == 30m
        && parsedCharacterGrowth.AwakeningGrowth[(2, 2)].Strength == 4.5m,
        "character .chr parser isolates initial, growtype and each awakening stat section without reading skill blocks");
    Check(fallbackStatCatalog.Get(2, 0, 9) is
        {
            MaximumHp: 4_800,
            MaximumMp: 4_000,
            PhysicalAttack: 300,
            PhysicalDefense: 300,
            MagicalAttack: 290,
            MagicalDefense: 290,
            InventoryLimit: 354_000,
            MpRegeneration: 700,
            MovementSpeed: 8_200,
            Weight: 600_000
        },
        "character-stat fallback preserves the DF60A1 level-nine Gunner baseline when no PVF is available");
    var characterWithoutStoneBonuses = reloadedSpCharacter with
    {
        BonusMaximumHp = 0,
        BonusMaximumMp = 0,
        BonusStrength = 0,
        BonusVitality = 0,
        BonusIntelligence = 0,
        BonusSpirit = 0,
        BonusMovementSpeed = 0,
        BonusAllElementResistance = 0
    };
    var combatStatsWithoutStoneBonuses = fallbackStatCatalog.Get(
        characterWithoutStoneBonuses);
    var combatStatsWithStoneBonuses = fallbackStatCatalog.Get(
        reloadedSpCharacter);
    Check(combatStatsWithStoneBonuses.MaximumHp
            - combatStatsWithoutStoneBonuses.MaximumHp == 250
        && combatStatsWithStoneBonuses.MaximumMp
            - combatStatsWithoutStoneBonuses.MaximumMp == 250
        && combatStatsWithStoneBonuses.PhysicalAttack
            - combatStatsWithoutStoneBonuses.PhysicalAttack == 50
        && combatStatsWithStoneBonuses.PhysicalDefense
            - combatStatsWithoutStoneBonuses.PhysicalDefense == 50
        && combatStatsWithStoneBonuses.MagicalAttack
            - combatStatsWithoutStoneBonuses.MagicalAttack == 50
        && combatStatsWithStoneBonuses.MagicalDefense
            - combatStatsWithoutStoneBonuses.MagicalDefense == 50
        && combatStatsWithStoneBonuses.FireResistance
            - combatStatsWithoutStoneBonuses.FireResistance == 10
        && combatStatsWithStoneBonuses.WaterResistance
            - combatStatsWithoutStoneBonuses.WaterResistance == 10
        && combatStatsWithStoneBonuses.DarkResistance
            - combatStatsWithoutStoneBonuses.DarkResistance == 10
        && combatStatsWithStoneBonuses.LightResistance
            - combatStatsWithoutStoneBonuses.LightResistance == 10
        && combatStatsWithStoneBonuses.MovementSpeed
            - combatStatsWithoutStoneBonuses.MovementSpeed == 10,
        "persisted old-client stone units are projected once into the existing x10 character-stat wire fields");
    Check(storedCharacter.Equipment?.Select(item => (item.Slot, item.ItemId))
            .SequenceEqual(
            [
                ((ushort)11, (ushort)11271),
                ((ushort)12, (ushort)15270),
                ((ushort)13, (ushort)13271),
                ((ushort)14, (ushort)19271),
                ((ushort)15, (ushort)17270)
            ]) == true,
        "preset Berserker wears the requested heavy-armor set in client slot order");
    var expectedPresetAvatarItemIds = new ushort[]
    {
        39_428, 39_008, 39_811, 40_207, 40_633,
        42_206, 41_407, 41_016, 41_817
    };
    Check(storedCharacter.AvatarInventory?.Count == 9
        && storedCharacter.AvatarInventory
            .OrderBy(item => item.Slot)
            .Select(item => item.ItemId)
            .SequenceEqual(expectedPresetAvatarItemIds)
        && storedCharacter.AvatarInventory.All(item =>
            item.CountOrValue == 1
            && item.Durability == 0
            && item.AvatarRemainingSeconds == 0
            && item.AvatarAbilityIndex == 0),
        "preset Berserker receives the nine requested Avatar test items as independent bag records");
    var expectedPresetCreatureItems = new Dictionary<ushort, uint>
    {
        [63013] = 1,
        [24] = 100,
        [63501] = 1,
        [64001] = 1,
        [64501] = 1
    };
    Check(storedCharacter.CreatureInventory?.Count == expectedPresetCreatureItems.Count
        && storedCharacter.CreatureInventory.All(item =>
            expectedPresetCreatureItems.TryGetValue(item.ItemId, out var expectedValue)
            && (item.ItemId == 63013
                ? item.CountOrValue != 0
                    && item.SealState == 1
                    && item.Slot < CharacterCreatureInventoryLayout.CreatureBagEnd
                : item.CountOrValue == expectedValue))
        && storedCharacter.CreatureInventory.Single(item => item.ItemId == 24).Slot
            is >= CharacterCreatureInventoryLayout.StackableBagStart
                and < CharacterCreatureInventoryLayout.StackableBagEnd
        && storedCharacter.CreatureInventory
            .Where(item => item.ItemId is 63501 or 64001 or 64501)
            .All(item => item.Slot is >= CharacterCreatureInventoryLayout.ArtifactBagStart
                and < CharacterCreatureInventoryLayout.ArtifactBagEnd),
        "preset Berserker receives the requested Creature, feed and three Artifact test items in their native bag regions");
    var expectedBerserkerSkillIds = new byte[]
    {
        1, 5, 8, 11, 12, 13, 14, 15, 16, 17, 19, 20, 23, 24, 25, 26,
        31, 34, 40, 45, 46, 48, 54, 56, 58, 59, 63, 64, 65, 66, 70, 76,
        77, 78, 79, 81, 169, 170, 171, 173, 175, 176, 177, 178, 179, 180,
        181, 182, 184, 186, 188, 189, 200
    };
    Check(storedCharacter.Skills?.Count == expectedBerserkerSkillIds.Length
        && storedCharacter.Skills.Select(skill => skill.SkillId)
            .Order()
            .SequenceEqual(expectedBerserkerSkillIds)
        && storedCharacter.Skills.Select(skill => skill.Slot).Distinct().Count()
            == expectedBerserkerSkillIds.Length
        && storedCharacter.Skills.All(skill => skill.Level is > 0 and <= 5),
        "preset Berserker learns every grow-type-three swordman skill with valid unique slots");

    var identityStoreOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "identity-state.json")
    };
    var identityStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await identityStore.InitializeAsync();
    var defaultAccount = (await identityStore.GetAccountsAsync()).Single();
    var defaultCharacter = (await identityStore.GetCharactersAsync(defaultAccount.AccountUid)).Single();
    Check(defaultAccount.AccountUid == 1
        && defaultAccount.Kind == AccountKind.Test
        && defaultCharacter.CharacterNo == 1
        && defaultCharacter.AccountUid == defaultAccount.AccountUid
        && defaultCharacter.Slot == 0
        && defaultCharacter.IsPreset
        && defaultCharacter.TutorialStarted,
        "the built-in test account starts at UID one with CharacterNo one and an explicit preset role");
    var fatigueUpdate = await identityStore.SetCharacterUsedFatigueAsync(
        defaultCharacter.Id,
        usedFatigue: 88);
    var fatigueReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await fatigueReloadStore.InitializeAsync();
    var reloadedFatigueCharacter = (await fatigueReloadStore.GetCharactersAsync(
        defaultAccount.AccountUid)).Single(character => character.Id == defaultCharacter.Id);
    Check(fatigueUpdate?.UsedFatigue == 88
        && reloadedFatigueCharacter.UsedFatigue == 88,
        "used fatigue is persisted per character for select and town notifications");
    var roomFatigueConsumption =
        await identityStore.ConsumeDungeonRoomFatigueAsync(
            defaultCharacter.Id,
            GameProtocolEngine.BlackDiamondMaximumFatigue);
    var cappedRoomFatigueConsumption =
        await identityStore.ConsumeDungeonRoomFatigueAsync(
            defaultCharacter.Id,
            maximumFatigue: 89);
    var roomFatigueReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await roomFatigueReloadStore.InitializeAsync();
    var reloadedRoomFatigueCharacter =
        (await roomFatigueReloadStore.GetCharactersAsync(defaultAccount.AccountUid))
        .Single(character => character.Id == defaultCharacter.Id);
    Check(roomFatigueConsumption is
        {
            Consumed: true,
            PreviousUsedFatigue: 88,
            UsedFatigue: 89,
            Character.UsedFatigue: 89
        }
        && cappedRoomFatigueConsumption is
        {
            Consumed: false,
            PreviousUsedFatigue: 89,
            UsedFatigue: 89,
            Character.UsedFatigue: 89
        }
        && reloadedRoomFatigueCharacter.UsedFatigue == 89,
        "entering a dungeon room atomically consumes one fatigue and clamps at the active maximum");
    fatigueUpdate = await identityStore.SetCharacterUsedFatigueAsync(
        defaultCharacter.Id,
        usedFatigue: 88);
    const ushort fatiguePotionSlot = 41;
    const ushort fatiguePotionItemId = 7_518;
    var fatiguePotionInventory = (fatigueUpdate!.Inventory ?? [])
        .Where(item => item.Slot != fatiguePotionSlot)
        .Append(new CharacterItemRecord(
            fatiguePotionSlot,
            fatiguePotionItemId,
            2));
    var preparedFatiguePotionCharacter = await identityStore.SaveCharacterInventoryAsync(
        defaultAccount.UserName,
        defaultCharacter.Id,
        fatigueUpdate.Gold,
        fatiguePotionInventory,
        fatigueUpdate.Equipment ?? [],
        CancellationToken.None);
    var fatiguePotionUse = await identityStore.UseFatigueRecoveryItemAsync(
        defaultAccount.UserName,
        defaultCharacter.Id,
        fatiguePotionSlot,
        fatiguePotionItemId,
        maximumFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue,
        maximumRemainingFatigue: 126,
        recoveryAmount: 30);
    var fatiguePotionReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await fatiguePotionReloadStore.InitializeAsync();
    var reloadedFatiguePotionCharacter = (await fatiguePotionReloadStore.GetCharactersAsync(
        defaultAccount.AccountUid)).Single(character => character.Id == defaultCharacter.Id);
    Check(preparedFatiguePotionCharacter is not null
        && fatiguePotionUse is
        {
            Success: true,
            RemainingItemCount: 1,
            PreviousUsedFatigue: 88,
            UsedFatigue: 58,
            Failure: FatigueRecoveryItemUseFailure.None
        }
        && reloadedFatiguePotionCharacter.UsedFatigue == 58
        && reloadedFatiguePotionCharacter.Inventory?.Single(
            item => item.Slot == fatiguePotionSlot) is
        {
            ItemId: fatiguePotionItemId,
            CountOrValue: 1
        },
        "item 7518 atomically restores 30 fatigue and consumes exactly one stack entry");
    var fatigueThresholdCharacter = await identityStore.SetCharacterUsedFatigueAsync(
        defaultCharacter.Id,
        usedFatigue: 62);
    var rejectedFatiguePotion = await identityStore.UseFatigueRecoveryItemAsync(
        defaultAccount.UserName,
        defaultCharacter.Id,
        fatiguePotionSlot,
        fatiguePotionItemId,
        maximumFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue,
        maximumRemainingFatigue: 126,
        recoveryAmount: 30);
    var fatigueThresholdReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await fatigueThresholdReloadStore.InitializeAsync();
    var reloadedFatigueThresholdCharacter =
        (await fatigueThresholdReloadStore.GetCharactersAsync(defaultAccount.AccountUid))
        .Single(character => character.Id == defaultCharacter.Id);
    Check(fatigueThresholdCharacter?.UsedFatigue == 62
        && rejectedFatiguePotion is
        {
            Success: false,
            RemainingItemCount: 1,
            PreviousUsedFatigue: 62,
            UsedFatigue: 62,
            Failure: FatigueRecoveryItemUseFailure.FatigueNotLowEnough
        }
        && reloadedFatigueThresholdCharacter.UsedFatigue == 62
        && reloadedFatigueThresholdCharacter.Inventory?.Single(
            item => item.Slot == fatiguePotionSlot).CountOrValue == 1,
        "item 7518 rejects the exact 126-fatigue boundary without consuming or mutating state");
    var sameFatigueDay = await identityStore.ResetFatigueForCurrentDayAsync(
        DateTimeOffset.Now);
    var nowForReset = DateTimeOffset.Now;
    var nextResetDate = nowForReset.TimeOfDay < TimeSpan.FromHours(6)
        ? nowForReset.Date
        : nowForReset.Date.AddDays(1);
    var nextReset = new DateTimeOffset(
        DateTime.SpecifyKind(
            nextResetDate.AddHours(6),
            DateTimeKind.Unspecified),
        nowForReset.Offset);
    var nextFatigueDay = await identityStore.ResetFatigueForCurrentDayAsync(nextReset);
    Check(!sameFatigueDay.NewFatigueDay
        && nextFatigueDay.NewFatigueDay
        && nextFatigueDay.ResetCharacterCount == 1
        && nextFatigueDay.CharacterIds.SequenceEqual([defaultCharacter.Id])
        && JsonGameStore.GetFatigueDayKey(new DateTimeOffset(
            2026, 8, 10, 5, 59, 59, TimeSpan.FromHours(8))) == 20260809
        && JsonGameStore.GetFatigueDayKey(new DateTimeOffset(
            2026, 8, 10, 6, 0, 0, TimeSpan.FromHours(8))) == 20260810,
        "the local fatigue day changes at 06:00 and resets persisted character usage once");
    Check(AccountUidRules.IsTest(10_000_000)
        && !AccountUidRules.IsTest(10_000_001)
        && AccountUidRules.IsReserved(10_000_001)
        && AccountUidRules.IsNormal(AccountUidRules.NormalUidStart),
        "UID boundary classification keeps the inclusive test limit and reserved interval explicit");
    var firstTestUidStarterItems = EntranceService.GetStarterInventoryItems(1);
    var lastTestUidStarterItems = EntranceService.GetStarterInventoryItems(
        AccountUidRules.MaximumTestUid);
    var reservedUidStarterItems = EntranceService.GetStarterInventoryItems(
        AccountUidRules.MaximumTestUid + 1);
    var normalUidStarterItems = EntranceService.GetStarterInventoryItems(
        AccountUidRules.NormalUidStart);
    Check(firstTestUidStarterItems.Count == 13
        && lastTestUidStarterItems.SequenceEqual(firstTestUidStarterItems)
        && reservedUidStarterItems.Count == 0
        && normalUidStarterItems.SequenceEqual(
            new (ushort ItemId, uint Count)[] { (26043, 1), (8, 100) }),
        "starter inventory keeps the full debug grant in the test UID range and gives normal accounts only title 26043 plus one stack of 100 item 8");

    var normalAccountCreation = await identityStore.CreateAccountAsync(
        new CreateAccountRequest(
            "normal-identity",
            "normal-password",
            OicqCode: "10001"));
    Check(normalAccountCreation.Created
        && normalAccountCreation.Account?.AccountUid == AccountUidRules.NormalUidStart
        && normalAccountCreation.Account.Kind == AccountKind.Normal
        && normalAccountCreation.Account.Characters.Count == 0,
        "normal accounts allocate from UID 18000000 and do not receive preset roles");
    var normalAccountUid = normalAccountCreation.Account!.AccountUid;
    var normalCharacter = await identityStore.CreateCharacterAsync(
        normalAccountUid,
        new CreateCharacterRequest("NormalIdentityOne", Level: 1));
    Check(normalCharacter.Created
        && normalCharacter.Character?.CharacterNo == 2
        && normalCharacter.Character.AccountUid == normalAccountUid
        && normalCharacter.Character.Slot == 0
        && normalCharacter.Character.Sp == 0
        && !normalCharacter.Character.TutorialStarted,
        "normal role creation starts with zero SP, allocates the next global CharacterNo at slot zero and requires the tutorial");
    var completedTutorialCharacter = await identityStore.CompleteCharacterTutorialAsync(
        "normal-identity",
        normalCharacter.Character!.Id,
        new CharacterQuestRecord(QuestCatalog.TutorialCompletionQuestId, Trigger: 0),
        level: 2,
        experience: 45,
        skillPoints: 530);
    var repeatedTutorialCompletion = await identityStore.CompleteCharacterTutorialAsync(
        "normal-identity",
        normalCharacter.Character.Id,
        new CharacterQuestRecord(QuestCatalog.TutorialCompletionQuestId, Trigger: 0));
    var tutorialReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await tutorialReloadStore.InitializeAsync();
    var reloadedTutorialCharacter = (await tutorialReloadStore.GetCharactersAsync(
            normalAccountUid))
        .Single(character => character.Id == normalCharacter.Character.Id);
    Check(completedTutorialCharacter is
        {
            TutorialStarted: true,
            Level: 2,
            Experience: 45,
            Sp: 530,
            Quests: [{ QuestId: QuestCatalog.TutorialCompletionQuestId, Trigger: 0 }]
        }
        && repeatedTutorialCompletion?.Quests?.Count(quest =>
            quest.QuestId == QuestCatalog.TutorialCompletionQuestId) == 1
        && reloadedTutorialCharacter.TutorialStarted
        && reloadedTutorialCharacter is { Level: 2, Experience: 45, Sp: 530 }
        && reloadedTutorialCharacter.Quests is
            [{ QuestId: QuestCatalog.TutorialCompletionQuestId, Trigger: 0 }],
        "tutorial completion, quest 1016 and earned progression persist atomically and remain idempotent");

    var testAccountCreation = await identityStore.CreateAccountAsync(
        new CreateAccountRequest(
            "test-identity",
            "test-password",
            TestAccount: true,
            OicqCode: "10002"));
    Check(testAccountCreation.Created
        && testAccountCreation.Account?.AccountUid == 2
        && testAccountCreation.Account.Kind == AccountKind.Test
        && testAccountCreation.Account.Characters is [{ IsPreset: true, CharacterNo: 3, Slot: 0 }],
        "test account creation allocates a test UID and one idempotent preset role");
    var testAccountUid = testAccountCreation.Account!.AccountUid;
    var testCharacter = await identityStore.CreateCharacterAsync(
        testAccountUid,
        new CreateCharacterRequest("TestIdentityOne", Level: 1));
    Check(testCharacter.Created
        && testCharacter.Character?.CharacterNo == 4
        && testCharacter.Character.Slot == 1
        && testCharacter.Character.Sp == 0
        && !testCharacter.Character.TutorialStarted,
        "test account custom roles start with zero SP, share the global CharacterNo sequence and require the tutorial");

    var weakenedCharacter = await identityStore.SetCharacterWeaknessRecoveryAsync(
        testCharacter.Character!.Id,
        recovery: 0);
    var weaknessMinuteUpdates =
        await identityStore.AdvanceCharacterWeaknessRecoveryAsync(
            DateTimeOffset.UtcNow);
    var minuteRecoveredCharacter =
        (await identityStore.GetCharactersAsync(testAccountUid))
        .Single(character => character.Id == testCharacter.Character.Id);
    var rejectedWeaknessRecovery = await identityStore.RecoverCharacterWeaknessAsync(
        testCharacter.Character.Id,
        recoveryCost: weakenedCharacter!.Gold + 1);
    var weaknessRecovery = await identityStore.RecoverCharacterWeaknessAsync(
        testCharacter.Character.Id,
        recoveryCost: 505);
    var weaknessReloadStore = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await weaknessReloadStore.InitializeAsync();
    var reloadedWeaknessCharacter =
        (await weaknessReloadStore.GetCharactersAsync(testAccountUid))
        .Single(character => character.Id == testCharacter.Character.Id);
    Check(weakenedCharacter.IsWeakened
        && weakenedCharacter.WeaknessRecovery == DungeonWeaknessPolicy.InitialRecovery
        && weaknessMinuteUpdates is [{ Recovery: 19 }]
        && minuteRecoveredCharacter.WeaknessRecovery
            == 19
        && rejectedWeaknessRecovery is
        {
            Success: false,
            Failure: WeaknessRecoveryFailure.InsufficientGold,
            Character.IsWeakened: true
        }
        && weaknessRecovery is
        {
            Success: true,
            Failure: WeaknessRecoveryFailure.None,
            Character.IsWeakened: false
        }
        && weaknessRecovery.Character.Gold == weakenedCharacter.Gold - 505
        && !reloadedWeaknessCharacter.IsWeakened
        && reloadedWeaknessCharacter.Gold == weakenedCharacter.Gold - 505,
        "the global minute tick advances offline weakness from the ten-percent floor by nine and gold recovery persists full recovery atomically without charging a rejected request");

    var authenticatedNormal = await identityStore.AuthenticateAsync(
        "normal-identity",
        "normal-password");
    var rejectedNormal = await identityStore.AuthenticateAsync(
        "normal-identity",
        "wrong-password");
    Check(authenticatedNormal is
            { AccountUid: var authenticatedUid }
        && authenticatedUid == normalAccountUid
        && rejectedNormal is null,
        "password authentication resolves the server-owned AccountUid and rejects an incorrect password");

    var rejectedPasswordChange = await identityStore.ChangePasswordAsync(
        "normal-identity",
        new ChangeAccountPasswordRequest("wrong-password", "changed-password"));
    var changedPassword = await identityStore.ChangePasswordAsync(
        "normal-identity",
        new ChangeAccountPasswordRequest("normal-password", "changed-password"));
    var rejectedOldPassword = await identityStore.AuthenticateAsync(
        "normal-identity",
        "normal-password");
    var authenticatedChangedPassword = await identityStore.AuthenticateAsync(
        "normal-identity",
        "changed-password");
    Check(rejectedPasswordChange.Failure == AccountPasswordUpdateFailure.InvalidCredentials
        && changedPassword.Success
        && rejectedOldPassword is null
        && authenticatedChangedPassword?.AccountUid == normalAccountUid,
        "password change requires the current password and immediately replaces the credential");

    var rejectedPasswordReset = await identityStore.ResetPasswordAsync(
        "normal-identity",
        new ResetAccountPasswordRequest("wrong-qq", "reset-password"));
    var resetPassword = await identityStore.ResetPasswordAsync(
        "normal-identity",
        new ResetAccountPasswordRequest("10001", "reset-password"));
    var rejectedChangedPassword = await identityStore.AuthenticateAsync(
        "normal-identity",
        "changed-password");
    var authenticatedResetPassword = await identityStore.AuthenticateAsync(
        "normal-identity",
        "reset-password");
    Check(rejectedPasswordReset.Failure == AccountPasswordUpdateFailure.InvalidRecoveryInformation
        && resetPassword.Success
        && rejectedChangedPassword is null
        && authenticatedResetPassword?.AccountUid == normalAccountUid,
        "password reset requires the persisted QQ recovery value and replaces the credential");

    var deletedNormalCharacter = await identityStore.DeleteCharacterAsync(normalAccountUid, 0);
    var recreatedNormalCharacter = await identityStore.CreateCharacterAsync(
        normalAccountUid,
        new CreateCharacterRequest("NormalIdentityTwo", Level: 1));
    Check(deletedNormalCharacter.Deleted
        && deletedNormalCharacter.Character?.CharacterNo == 2
        && recreatedNormalCharacter.Created
        && recreatedNormalCharacter.Character?.CharacterNo == 5,
        "deleting a role compacts slots without reusing its CharacterNo");
    var deletedHighestNormalCharacter = await identityStore.DeleteCharacterAsync(normalAccountUid, 0);
    var recreatedHighestNormalCharacter = await identityStore.CreateCharacterAsync(
        normalAccountUid,
        new CreateCharacterRequest("NormalIdentityThree", Level: 1));
    Check(deletedHighestNormalCharacter.Deleted
        && deletedHighestNormalCharacter.Character?.CharacterNo == 5
        && recreatedHighestNormalCharacter.Created
        && recreatedHighestNormalCharacter.Character?.CharacterNo == 6,
        "the persisted CharacterNo sequence does not reuse the deleted highest value after reload-free deletion");

    var deletedPreset = await identityStore.DeleteCharacterAsync(testAccountUid, 0);
    var identityReload = new JsonGameStore(
        identityStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await identityReload.InitializeAsync();
    var reloadedTestCharacters = await identityReload.GetCharactersAsync(testAccountUid);
    Check(deletedPreset.Deleted
        && deletedPreset.Character?.CharacterNo == 3
        && reloadedTestCharacters is [{ CharacterNo: 4, AccountUid: var reloadedTestUid, Slot: 0 }]
        && reloadedTestUid == testAccountUid,
        "deleting a test preset role is not undone by the next initialization");

    var mailStoreOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "mail-state.json")
    };
    var mailStore = new JsonGameStore(
        mailStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await mailStore.InitializeAsync();
    var mailSender = (await mailStore.GetCharactersAsync("test"))[0];
    var recipientCreation = await mailStore.CreateCharacterAsync(
        "test",
        new CreateCharacterRequest("MailboxTarget", Level: 1));
    var mailRecipient = recipientCreation.Character!;
    var adminDungeonOpen = await mailStore.SetCharacterDungeonDifficultyAsync(
        mailRecipient.Id,
        101,
        3);
    var adminDungeonLower = await mailStore.SetCharacterDungeonDifficultyAsync(
        mailRecipient.Id,
        101,
        1);
    Check(adminDungeonOpen?.UnlockedDungeonIds?.Contains(101) == true
        && DungeonDifficultyProgression.GetMaximumDifficulty(
            adminDungeonOpen.DungeonProgress,
            101) == 3
        && DungeonDifficultyProgression.GetMaximumDifficulty(
            adminDungeonLower?.DungeonProgress,
            101) == 1,
        "admin dungeon permissions persist the unlocked DGN and replace its exact maximum difficulty");
    var adminMailCreation = await mailStore.SendCharacterMailAsync(
        mailRecipient.Id,
        new CreateCharacterMailRequest(
            "DFLegacy",
            "system delivery",
            Gold: 123,
            Attachment: new CharacterMailAttachmentRecord(3037, 4, Durability: 12)));
    var adminMails = await mailStore.GetCharacterMailsAsync(mailRecipient.Id);
    Check(adminMailCreation.Created
        && adminMails.Length == 1
        && adminMails[0].IsNew
        && adminMails[0].Gold == 123
        && adminMails[0].Attachment is { ItemId: 3037, CountOrValue: 4 },
        "system mail persists one client-compatible attachment, gold and text");
    Check(await mailStore.AcknowledgeCharacterMailNotificationsAsync(mailRecipient.Id) == 1
        && !(await mailStore.GetCharacterMailsAsync(mailRecipient.Id))[0].IsNew,
        "mail alarms are acknowledged without removing the package");
    var claimedInventory = new[]
    {
        new CharacterItemRecord(9, 3037, 4, Durability: 12)
    };
    var adminClaim = await mailStore.ClaimCharacterMailAsync(
        "test",
        mailRecipient.Id,
        adminMails[0].Id,
        mailRecipient.Gold + 123,
        claimedInventory,
        [],
        CancellationToken.None);
    Check(adminClaim is not null
        && adminClaim.Character.Gold == mailRecipient.Gold + 123
        && adminClaim.Character.Inventory?.SequenceEqual(claimedInventory) == true
        && (await mailStore.GetCharacterMailsAsync(mailRecipient.Id)) is
        [
            {
                Gold: 0,
                Attachment: null,
                State: 1,
                IsNew: false
            }
        ]
        && await mailStore.ClaimCharacterMailAsync(
            "test",
            mailRecipient.Id,
            adminMails[0].Id,
            mailRecipient.Gold + 246,
            claimedInventory,
            [],
            CancellationToken.None) is null,
        "mail extraction atomically grants contents, preserves text and rejects duplicate claims");
    Check(await mailStore.SetCharacterMailStateAsync(
            mailRecipient.Id,
            adminMails[0].Id,
            3)
        && (await mailStore.GetCharacterMailsAsync(mailRecipient.Id)) is [{ State: 3 }],
        "mail archive state survives a fresh mailbox read");
    mailStore = new JsonGameStore(
        mailStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await mailStore.InitializeAsync();
    Check((await mailStore.GetCharacterMailsAsync(mailRecipient.Id)) is
        [
            {
                Gold: 0,
                Attachment: null,
                State: 3,
                IsNew: false
            }
        ],
        "mail archive state survives store reload and keeps only the text shell");
    Check(await mailStore.DeleteCharacterMailAsync(
            mailRecipient.Id,
            adminMails[0].Id)
        && (await mailStore.GetCharacterMailsAsync(mailRecipient.Id)).Length == 0,
        "archived mail remains stored until an explicit delete");

    var upgradeMailCreation = await mailStore.SendCharacterMailAsync(
        mailRecipient.Id,
        new CreateCharacterMailRequest(
            "DFLegacy",
            "warehouse upgrade",
            Attachment: new CharacterMailAttachmentRecord(62, 1)));
    var upgradeMail = upgradeMailCreation.Mail!;
    var recipientBeforeUpgradeClaim = (await mailStore.GetCharactersAsync("test"))
        .Single(character => character.Id == mailRecipient.Id);
    var upgradeMailClaim = await mailStore.ClaimCharacterMailAsync(
        "test",
        mailRecipient.Id,
        upgradeMail.Id,
        recipientBeforeUpgradeClaim.Gold,
        recipientBeforeUpgradeClaim.Inventory ?? [],
        recipientBeforeUpgradeClaim.Equipment ?? [],
        CancellationToken.None,
        warehouseCapacity: 120,
        expectedWarehouseCapacity: 8);
    Check(upgradeMailCreation.Created
        && upgradeMailClaim is { Character.WarehouseCapacity: 120 }
        && upgradeMailClaim.Character.Inventory?.All(item => item.ItemId != 62) == true
        && (await mailStore.GetCharacterMailsAsync(mailRecipient.Id)) is
        [
            {
                Attachment: null,
                State: 1,
                IsNew: false
            }
        ]
        && await mailStore.DeleteCharacterMailAsync(
            mailRecipient.Id,
            upgradeMail.Id),
        "claiming a high-level warehouse kit jumps directly to its target capacity and consumes the attachment without placing it in inventory");

    var disposableMail = await mailStore.SendCharacterMailAsync(
        mailRecipient.Id,
        new CreateCharacterMailRequest("DFLegacy", "delete me"));
    Check(disposableMail is { Created: true, Mail: not null }
        && await mailStore.DeleteCharacterMailAsync(
            mailRecipient.Id,
            disposableMail.Mail.Id)
        && !await mailStore.DeleteCharacterMailAsync(
            mailRecipient.Id,
            disposableMail.Mail.Id),
        "mail deletion is persisted and reports duplicate removal");

    var senderInventory = new[] { new CharacterItemRecord(20, 3037, 10) };
    mailSender = await mailStore.SaveCharacterInventoryAsync(
        "test",
        mailSender.Id,
        1_000,
        senderInventory,
        mailSender.Equipment ?? []) ?? mailSender;
    var plannedSenderInventory = new[] { new CharacterItemRecord(20, 3037, 7) };
    var playerMail = await mailStore.SendCharacterMailFromCharacterAsync(
        "test",
        mailSender.Id,
        mailRecipient.Name,
        new CreateCharacterMailRequest(
            mailSender.Name,
            "player delivery",
            Gold: 250,
            Attachment: new CharacterMailAttachmentRecord(3037, 3)),
        senderGold: 750,
        plannedSenderInventory,
        mailSender.Equipment ?? [],
        CancellationToken.None);
    var playerMails = await mailStore.GetCharacterMailsAsync(mailRecipient.Id);
    Check(playerMail.Sent
        && playerMail.Sender is { Gold: 750 }
        && playerMail.Sender.Inventory?.Single() is { Slot: 20, CountOrValue: 7 }
        && playerMails.Single().Attachment is { ItemId: 3037, CountOrValue: 3 },
        "player mail atomically debits sender gold and stack quantity before delivery");
    var stalePlayerMail = await mailStore.SendCharacterMailFromCharacterAsync(
        "test",
        mailSender.Id,
        mailRecipient.Name,
        new CreateCharacterMailRequest(mailSender.Name, "stale", Gold: 250),
        senderGold: 700,
        plannedSenderInventory,
        mailSender.Equipment ?? [],
        CancellationToken.None);
    var reloadedMailStore = new JsonGameStore(
        mailStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedMailStore.InitializeAsync();
    var reloadedMailSender = (await reloadedMailStore.GetCharactersAsync("test"))
        .Single(character => character.Id == mailSender.Id);
    Check(!stalePlayerMail.Sent
        && stalePlayerMail.Failure == CharacterMailSendFailure.SenderStateChanged
        && (await reloadedMailStore.GetCharacterMailsAsync(mailRecipient.Id)).Length == 1
        && reloadedMailSender.Gold == 750,
        "stale player-mail transactions cannot duplicate gold or attachments");

    var warehouseItem = new CharacterItemRecord(
        Slot: 7,
        ItemId: 7343,
        CountOrValue: 1,
        Durability: 1_000);
    var avatarInventoryItem = new CharacterItemRecord(
        Slot: 104,
        ItemId: 39_000,
        CountOrValue: 1,
        AvatarRemainingSeconds: 0,
        AvatarAbilityIndex: 0);
    var avatarCompatibilityJson = JsonSerializer.Serialize(avatarInventoryItem);
    var reloadedAvatarCompatibilityItem =
        JsonSerializer.Deserialize<CharacterItemRecord>(avatarCompatibilityJson);
    Check(avatarCompatibilityJson.Contains(
            "\"AvatarExpireAt\":0",
            StringComparison.Ordinal)
        && !avatarCompatibilityJson.Contains(
            "AvatarRemainingSeconds",
            StringComparison.Ordinal)
        && reloadedAvatarCompatibilityItem?.AvatarRemainingSeconds == 0,
        "Avatar remaining seconds retain the legacy JSON key without exposing expiration-timestamp semantics in code");
    var creatureInventoryItem = new CharacterItemRecord(
        Slot: CharacterCreatureInventoryLayout.EquippedCreatureSlot,
        ItemId: 50_000,
        CountOrValue: 0x11223344,
        CreatureStomach: 100,
        CreatureExperience: 1234,
        CreatureLevel: 7,
        CreatureName: "Stored Creature",
        CreatureNoCharge: false,
        CreatureStomachRemainderSeconds: 42);
    storedCharacter = await questStore.SaveCharacterItemSpacesAsync(
        "test",
        storedCharacter.Id,
        storedCharacter.Gold,
        storedCharacter.Inventory ?? [],
        storedCharacter.Equipment ?? [],
        [warehouseItem],
        avatarInventory: [avatarInventoryItem],
        creatureInventory: [creatureInventoryItem],
        warehouseCapacity: 24) ?? storedCharacter;
    Check(storedCharacter.AvatarInventory?.Single() == avatarInventoryItem,
        "the character store persists the independent 105-slot Avatar inventory");
    Check(storedCharacter.CreatureInventory?.Single() == creatureInventoryItem,
        "the character store persists Creature uid, growth state, name and equipped raw slot");
    Check(storedCharacter.WarehouseCapacity == 24,
        "the character store persists the upgraded warehouse capacity with its item spaces");

    var presetRefreshOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "preset-refresh-state.json")
    };
    var preservedAccessory = new CharacterItemRecord(
        Slot: 16,
        ItemId: 20093,
        CountOrValue: 1,
        Durability: 1_000);
    await File.WriteAllTextAsync(
        presetRefreshOptions.DataPath,
        JsonSerializer.Serialize(
        new AccountRecord[]
        {
            new AccountRecord(
                Guid.NewGuid(),
                "test",
                "test-hash",
                [
                    new CharacterRecord(
                        Guid.NewGuid(),
                        "测试角色",
                        0,
                        2,
                        55,
                        500,
                        12_345,
                        Equipment:
                        [
                            new CharacterItemRecord(
                                Slot: 11,
                                ItemId: 10874,
                                CountOrValue: 1,
                                Durability: 1_000),
                            preservedAccessory
                        ],
                        Skills: [new CharacterSkillRecord(0, 5, 20)]),
                    new CharacterRecord(
                        Guid.NewGuid(),
                        JsonGameStore.PresetCharacterName,
                        JsonGameStore.PresetCharacterJob,
                        JsonGameStore.PresetCharacterGrowType,
                        55,
                        500,
                        99_999,
                        Equipment: [preservedAccessory],
                        Skills: [new CharacterSkillRecord(0, 5, 20)])
                ])
        }));
    var presetRefreshStore = new JsonGameStore(
        presetRefreshOptions,
        NullLogger<JsonGameStore>.Instance);
    await presetRefreshStore.InitializeAsync();
    var refreshedCharacters = await presetRefreshStore.GetCharactersAsync("test");
    var historicalNamedCharacter = refreshedCharacters[0];
    Check(historicalNamedCharacter.Name == "测试角色"
        && historicalNamedCharacter.CharacterNo == 1
        && historicalNamedCharacter.AccountUid == 1
        && historicalNamedCharacter.Slot == 0
        && historicalNamedCharacter.Job == 0
        && historicalNamedCharacter.GrowType == 2
         && historicalNamedCharacter.Level == 55
         && historicalNamedCharacter.Cash == 12_345
        && historicalNamedCharacter.Experience == 0
        && historicalNamedCharacter.Equipment?.Count == 2
        && historicalNamedCharacter.Skills?.Count == 1,
        "historical preset names no longer trigger character migration");
    var stalePreset = refreshedCharacters[1];
    Check(stalePreset.Name == JsonGameStore.PresetCharacterName
        && stalePreset.CharacterNo == 2
        && stalePreset.AccountUid == 1
        && stalePreset.Slot == 1
        && stalePreset.Job == JsonGameStore.PresetCharacterJob
        && stalePreset.GrowType == JsonGameStore.PresetCharacterGrowType
         && stalePreset.Level == CharacterExperienceCatalog.MaximumLevel
         && stalePreset.Cash == 12_345
        && stalePreset.Experience == JsonGameStore.PresetCharacterExperience
        && stalePreset.Equipment?.Count == 1
        && stalePreset.Equipment.Single() == preservedAccessory
        && expectedPresetAvatarItemIds.All(itemId =>
            stalePreset.AvatarInventory?.Any(item => item.ItemId == itemId) == true)
        && expectedPresetCreatureItems.All(expected =>
            stalePreset.CreatureInventory?.Any(item => item.ItemId == expected.Key) == true)
        && stalePreset.Skills?.Count == 1,
        "existing preset refresh preserves character-owned data, synchronizes the legacy slot-zero account wallet and adds all Avatar and Creature test items");
    await presetRefreshStore.SaveCharacterProgressionAsync(
        "test",
        stalePreset.Id,
        CharacterExperienceCatalog.MaximumLevel,
        0,
        stalePreset.Sp);
    var reloadedPresetRefreshStore = new JsonGameStore(
        presetRefreshOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedPresetRefreshStore.InitializeAsync();
    Check((await reloadedPresetRefreshStore.GetCharactersAsync("test"))[1].Experience
          == JsonGameStore.PresetCharacterExperience,
        "existing preset character refreshes to the correct cumulative experience");

    var persistedQuestDropInventory = (storedCharacter.Inventory ?? [])
        .Where(item => item.Slot != CharacterInventoryLayout.QuestSlotStart)
        .Append(new CharacterItemRecord(
            CharacterInventoryLayout.QuestSlotStart,
            4000,
            1))
        .ToArray();
    storedCharacter = await questStore.SaveCharacterQuestProgressAsync(
        "test",
        storedCharacter.Id,
        persistedQuestDropInventory,
        [new CharacterQuestRecord(1016, 1)],
        []) ?? storedCharacter;
    Check(storedCharacter.Quests?.SequenceEqual([new CharacterQuestRecord(1016, 1)]) == true
        && storedCharacter.Inventory?.Any(item =>
            item.Slot == CharacterInventoryLayout.QuestSlotStart
            && item.ItemId == 4000
            && item.CountOrValue == 1) == true,
        "quest drop persistence commits inventory and active quest trigger in one transaction");
    storedCharacter = await questStore.SaveCharacterDungeonProgressAsync(
        "test",
        storedCharacter.Id,
        [new CharacterDungeonProgressRecord(1, 0)]) ?? storedCharacter;
    storedCharacter = await questStore.UnlockNextDungeonDifficultyAsync(
        "test",
        storedCharacter.Id,
        dungeonId: 1,
        clearedDifficulty: 0) ?? storedCharacter;
    storedCharacter = await questStore.UnlockNextDungeonDifficultyAsync(
        "test",
        storedCharacter.Id,
        dungeonId: 1,
        clearedDifficulty: 1) ?? storedCharacter;
    storedCharacter = await questStore.UnlockNextDungeonDifficultyAsync(
        "test",
        storedCharacter.Id,
        dungeonId: 1,
        clearedDifficulty: 0) ?? storedCharacter;
    storedCharacter = await questStore.SaveCharacterDungeonUnlocksAsync(
        "test",
        storedCharacter.Id,
        [1_500, 17, 17]) ?? storedCharacter;
    var completedCharacter = await questStore.CompleteCharacterQuestAsync(
        "test",
        storedCharacter.Id,
        1016,
        levelUp.Level,
        levelUp.Experience,
        storedCharacter.Sp + spCatalog.CalculateLevelUpReward(
            levelUp.PreviousLevel,
            levelUp.Level),
        rewardPlan.Gold,
        rewardPlan.Inventory,
        [],
        [1016],
        growType: 3,
        awakeningType: 1,
        victoryPoints: 321);
    if (completedCharacter is not null)
    {
        completedCharacter = await questStore.SaveCharacterCeraShopPurchaseAsync(
            "test",
            completedCharacter.Id,
            cash: 765_432,
            gold: completedCharacter.Gold,
            inventory: completedCharacter.Inventory ?? [],
            equipment: completedCharacter.Equipment ?? [],
            premiumServiceGrants:
            [
                new(
                    GameProtocolEngine.MasterContractServiceType,
                    15)
            ],
            victoryPoints: 421,
            warehouseCapacity: 40,
            expectedWarehouseCapacity: 24)
            ?? completedCharacter;
    }

    var staleWarehouseUpgrade = completedCharacter is null
        ? null
        : await questStore.SaveCharacterCeraShopPurchaseAsync(
            "test",
            completedCharacter.Id,
            completedCharacter.Cash,
            completedCharacter.Gold,
            completedCharacter.Inventory ?? [],
            completedCharacter.Equipment ?? [],
            warehouseCapacity: 56,
            expectedWarehouseCapacity: 24);
    var crossLevelWarehouseUpgrade = completedCharacter is null
        ? null
        : await questStore.SaveCharacterCeraShopPurchaseAsync(
            "test",
            completedCharacter.Id,
            completedCharacter.Cash,
            completedCharacter.Gold,
            completedCharacter.Inventory ?? [],
            completedCharacter.Equipment ?? [],
            warehouseCapacity: 72,
            expectedWarehouseCapacity: 40);
    Check(staleWarehouseUpgrade is null
        && crossLevelWarehouseUpgrade is { WarehouseCapacity: 72 },
        "Cera-shop persistence rejects stale state but permits an acquired upgrade item to jump directly to its absolute target");

    await questStore.SaveCharacterLocationAsync(
        "test",
        storedCharacter.Id,
        townId: 3,
        areaId: 4,
        x: 612,
        y: 287,
        direction: 2);
    var reloadedStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedStore.InitializeAsync();
    var reloadedCharacter = (await reloadedStore.GetCharactersAsync("test"))[0];
    Check(completedCharacter is not null
        && reloadedCharacter.Level == 2
        && reloadedCharacter.Experience == 460
        && reloadedCharacter.Sp == storedCharacter.Sp + 30
        && reloadedCharacter.GrowType == 3
        && reloadedCharacter.AwakeningType == 1
        && reloadedCharacter.Gold == 1_200
        && reloadedCharacter.Cash == 765_432
        && reloadedCharacter.VictoryPoints == 421
        && reloadedCharacter.Inventory?.Any(item =>
            item.ItemId == 1055 && item.CountOrValue == 5) == true
        && reloadedCharacter.Warehouse?.SequenceEqual([warehouseItem]) == true
        && reloadedCharacter.WarehouseCapacity == 72
        && reloadedCharacter.Quests?.Count == 0
        && reloadedCharacter.CompletedQuestIds?.Contains(1016) == true
        && reloadedCharacter.UnlockedDungeonIds?.SequenceEqual(
            new ushort[] { 17, 1_500 }) == true
        && DungeonDifficultyProgression.GetMaximumDifficulty(
            reloadedCharacter.DungeonProgress,
            1) == 2
        && DungeonDifficultyProgression.GetMaximumDifficulty(
            reloadedCharacter.DungeonProgress,
            17) == 0
        && DungeonDifficultyProgression.GetMaximumDifficulty(
            reloadedCharacter.DungeonProgress,
            1_500) == 0,
        "quest, growth, hidden-dungeon, difficulty and Cera-shop transactions preserve character state and atomically advance warehouse capacity without lowering prior difficulty progress");
    Check(reloadedCharacter.TownId == 3
        && reloadedCharacter.AreaId == 4
        && reloadedCharacter.X == 612
        && reloadedCharacter.Y == 287
        && reloadedCharacter.Direction == 2,
        "character town, area and coordinates persist across store reloads");
    var purchasedContractServices = await reloadedStore.GetPremiumServicesAsync("test");
    Check(purchasedContractServices.Any(service =>
            service.ServiceType == GameProtocolEngine.MasterContractServiceType
            && service.Active
            && service.RemainingSeconds >= 1_295_990),
        "Cera-shop persistence extends the purchased contract in the same transaction as the cash deduction");
    Check(reloadedCharacter.RewardedQuestIds?.Contains(1016) == true,
        "quest completion records its reward settlement to prevent duplicate grants");

    var cashUpdatedCharacters = await reloadedStore.AddCashToAllCharactersAsync(1_000_000);
    reloadedCharacter = (await reloadedStore.GetCharactersAsync("test"))
        .Single(character => character.Id == completedCharacter!.Id);
    Check(cashUpdatedCharacters == 1
        && reloadedCharacter.Cash == 1_765_432,
        "bulk Cera grant adds to every persisted character without replacing the existing balance");

    var preDiamondWeakness = await reloadedStore.SetCharacterWeaknessRecoveryAsync(
        completedCharacter!.Id,
        recovery: 37);
    var accountCeraGrant = await reloadedStore.GrantCeraToAccountAsync("test", 200);
    var accountDiamondGrant = await reloadedStore.GrantDiamondToAccountAsync("test", 30);
    var reloadedPremiumStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedPremiumStore.InitializeAsync();
    var reloadedPremiumCharacter = (await reloadedPremiumStore.GetCharactersAsync("test"))[0];
    var premiumState = await reloadedPremiumStore.GetPremiumServiceAsync("test");
    Check(accountCeraGrant.Success
        && accountCeraGrant.CharacterCount == 1
        && reloadedPremiumCharacter.Cash == 1_765_632,
        "account Cera grant updates every character and persists the new balance");
    Check(accountDiamondGrant.Success
        && accountDiamondGrant.Days == 30
        && accountDiamondGrant.RemainingSeconds >= 2_591_990
        && accountDiamondGrant.WeaknessRecoveredCharacterIds?.SequenceEqual(
            [completedCharacter.Id]) == true
        && preDiamondWeakness?.WeaknessRecovery == 37
        && reloadedPremiumCharacter.WeaknessRecovery
            == DungeonWeaknessPolicy.FullStamina
        && premiumState.Active
        && premiumState.ExpiresAt == accountDiamondGrant.ExpiresAt,
        "activating account black diamond persists its expiry and immediately restores every weakened character on that account");
    await reloadedPremiumStore.SetCharacterWeaknessRecoveryAsync(
        reloadedPremiumCharacter.Id,
        recovery: 25);
    var blackDiamondWeaknessTick =
        await reloadedPremiumStore.AdvanceCharacterWeaknessRecoveryAsync(
            DateTimeOffset.UtcNow);
    var blackDiamondRecoveredCharacter =
        (await reloadedPremiumStore.GetCharactersAsync("test"))
        .Single(character => character.Id == reloadedPremiumCharacter.Id);
    Check(blackDiamondWeaknessTick is
            [{ CharacterId: var recoveredId, Recovery: DungeonWeaknessPolicy.FullStamina }]
        && recoveredId == reloadedPremiumCharacter.Id
        && blackDiamondRecoveredCharacter.WeaknessRecovery
            == DungeonWeaknessPolicy.FullStamina,
        "the shared minute tick repairs any non-full character on an active black-diamond account directly to 100");
    var sharedWalletCharacterCreation = await reloadedPremiumStore.CreateCharacterAsync(
        "test",
        new CreateCharacterRequest("SharedWallet", Job: 2, GrowType: 1, Level: 54));
    var sharedWalletCharacter = sharedWalletCharacterCreation.Character;
    Check(sharedWalletCharacterCreation.Created
        && sharedWalletCharacter?.Cash == reloadedPremiumCharacter.Cash,
        "new characters inherit the existing account Cera wallet instead of receiving a private default balance");
    var sharedWalletPurchase = sharedWalletCharacter is null
        ? null
        : await reloadedPremiumStore.SaveCharacterCeraShopPurchaseAsync(
            "test",
            sharedWalletCharacter.Id,
            cash: sharedWalletCharacter.Cash - 500,
            gold: sharedWalletCharacter.Gold,
            inventory: sharedWalletCharacter.Inventory ?? [],
            equipment: sharedWalletCharacter.Equipment ?? [],
            expectedCash: sharedWalletCharacter.Cash);
    var sharedWalletCharacters = await reloadedPremiumStore.GetCharactersAsync("test");
    Check(sharedWalletPurchase is not null
        && sharedWalletCharacters.Length == 2
        && sharedWalletCharacters.All(character =>
            character.Cash == sharedWalletCharacter!.Cash - 500),
        "a Cera-shop deduction on one character atomically updates the shared account wallet and every character mirror");
    var staleSharedWalletPurchase = await reloadedPremiumStore.SaveCharacterCeraShopPurchaseAsync(
        "test",
        reloadedPremiumCharacter.Id,
        cash: reloadedPremiumCharacter.Cash - 100,
        gold: reloadedPremiumCharacter.Gold,
        inventory: reloadedPremiumCharacter.Inventory ?? [],
        equipment: reloadedPremiumCharacter.Equipment ?? [],
        expectedCash: reloadedPremiumCharacter.Cash);
    Check(staleSharedWalletPurchase is null,
        "a concurrent Cera-shop write based on an obsolete account balance is rejected");
    var overlordContractGrant =
        await reloadedPremiumStore.GrantPremiumServiceToAccountAsync(
            "test",
            GameProtocolEngine.OverlordContractServiceType,
            30);
    var masterContractGrant =
        await reloadedPremiumStore.GrantPremiumServiceToAccountAsync(
            "test",
            GameProtocolEngine.MasterContractServiceType,
            30);
    var reloadedPremiumServicesStore = new JsonGameStore(
        storeOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedPremiumServicesStore.InitializeAsync();
    var premiumServices = await reloadedPremiumServicesStore.GetPremiumServicesAsync("test");
    Check(overlordContractGrant.Success
        && masterContractGrant.Success
        && premiumServices.Any(service =>
            service.ServiceType == GameProtocolEngine.OverlordContractServiceType
            && service.Active
            && service.RemainingSeconds > 0)
        && premiumServices.Any(service =>
            service.ServiceType == GameProtocolEngine.MasterContractServiceType
            && service.Active
            && service.RemainingSeconds > 0),
        "霸王 and 达人 contracts persist independent Premium expiries");
    var legacyContractOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "legacy-contract-state.json")
    };
    var legacyContractExpiry = checked(
        (uint)DateTimeOffset.UtcNow.AddDays(15).ToUnixTimeSeconds());
    var legacyDiamondExpiry = checked(
        (uint)DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
    await File.WriteAllTextAsync(
        legacyContractOptions.DataPath,
        JsonSerializer.Serialize(
        new[]
        {
            new
            {
                Id = Guid.NewGuid(),
                UserName = "legacy-contract",
                PasswordHash = "hash",
                Characters = new[]
                {
                    new
                    {
                        Id = Guid.NewGuid(),
                        Name = "legacy",
                        Job = 0,
                        GrowType = 0,
                        Level = 50,
                        Sp = 0,
                        Cash = 0,
                        IsWeakened = true,
                        IsPremium = true,
                        PremiumExpireAt = legacyDiamondExpiry
                    }
                },
                PremiumExpireAt = legacyDiamondExpiry,
                PremiumServices = new[]
                {
                    new AccountPremiumServiceRecord(17, legacyContractExpiry)
                }
            }
        }));
    var legacyContractStore = new JsonGameStore(
        legacyContractOptions,
        NullLogger<JsonGameStore>.Instance);
    await legacyContractStore.InitializeAsync();
    var migratedContractServices =
        await legacyContractStore.GetPremiumServicesAsync("legacy-contract");
    var migratedLegacyWeakness =
        (await legacyContractStore.GetCharactersAsync("legacy-contract")).Single();
    var migratedPremiumJson = await File.ReadAllTextAsync(legacyContractOptions.DataPath);
    Check(migratedContractServices.Any(service =>
            service.ServiceType == GameProtocolEngine.BlackDiamondServiceType
            && service.Active
            && service.ExpiresAt == legacyDiamondExpiry)
        && migratedContractServices.Any(service =>
            service.ServiceType == GameProtocolEngine.OverlordContractServiceType
            && service.Active
            && service.ExpiresAt == legacyContractExpiry)
        && migratedLegacyWeakness.WeaknessRecovery
            == DungeonWeaknessPolicy.InitialRecovery
        && migratedLegacyWeakness.IsWeakened
        && !migratedPremiumJson.Contains("\"IsWeakened\"", StringComparison.Ordinal)
        && !migratedPremiumJson.Contains("\"IsPremium\"", StringComparison.Ordinal)
        && !migratedPremiumJson.Contains("\"PremiumExpireAt\"", StringComparison.Ordinal),
        "legacy account black-diamond expiry and Overlord service 17 migrate into the normalized account Premium-service table");
    var unsupportedDiamondGrant = await reloadedPremiumStore.GrantDiamondToAccountAsync("test", 15);
    Check(!unsupportedDiamondGrant.Success,
        "account black-diamond grant rejects disabled service durations");

    var rosterStoreOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "roster-state.json")
    };
    var rosterStore = new JsonGameStore(
        rosterStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await rosterStore.InitializeAsync();
    var rosterReplacement = await rosterStore.ReplaceAllCharactersAsync(
        "test",
        MaximumTransferRoster.Create());
    var reloadedRosterStore = new JsonGameStore(
        rosterStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await reloadedRosterStore.InitializeAsync();
    var reloadedRoster = await reloadedRosterStore.GetCharactersAsync("test");
    Check(rosterReplacement.Replaced
        && rosterReplacement.CharacterCount == 20
        && reloadedRoster.Length == 20
        && reloadedRoster.All(character =>
            character.Level == MaximumTransferRoster.CharacterLevel
            && character.Inventory?.Count(item =>
                item.ItemId == MaximumTransferRoster.ColorlessCubeFragmentItemId
                && item.CountOrValue == MaximumTransferRoster.ColorlessCubeFragmentCount) == 1)
        && reloadedRoster.Select(character => (character.Job, character.GrowType))
            .Distinct()
            .Count() == 20,
        "maximum transfer roster atomically replaces and persists every existing character");
    var deletedCharacterId = reloadedRoster[7].Id;
    var characterAfterDeletedSlotId = reloadedRoster[8].Id;
    var deletedRosterEntry = await reloadedRosterStore.DeleteCharacterAsync("test", 7);
    var deleteReloadStore = new JsonGameStore(
        rosterStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await deleteReloadStore.InitializeAsync();
    var rosterAfterDelete = await deleteReloadStore.GetCharactersAsync("test");
    Check(deletedRosterEntry.Deleted
        && deletedRosterEntry.Character?.Id == deletedCharacterId
        && rosterAfterDelete.Length == 19
        && rosterAfterDelete.All(character => character.Id != deletedCharacterId)
        && rosterAfterDelete[7].Id == characterAfterDeletedSlotId,
        "character deletion removes exactly the selected slot and persists the compacted roster");
    var invalidDelete = await deleteReloadStore.DeleteCharacterAsync("test", 23);
    Check(!invalidDelete.Deleted && rosterAfterDelete.Length == 19,
        "deleting an empty character slot is rejected without changing the roster");

    var legacyLocationPath = Path.Combine(questFixtureRoot, "locationless-state.json");
    File.WriteAllText(
        legacyLocationPath,
        """
        [
          {
            "Id": "11111111-1111-1111-1111-111111111111",
            "UserName": "test",
            "PasswordHash": "legacy",
            "Characters": [
              {
                "Id": "22222222-2222-2222-2222-222222222222",
                "Name": "Legacy",
                "Job": 0,
                "GrowType": 0,
                "Level": 1,
                "Sp": 0,
                "Cash": 0
              }
            ]
          }
        ]
        """);
    var locationlessStore = new JsonGameStore(
        new ServerOptions { DataPath = legacyLocationPath },
        NullLogger<JsonGameStore>.Instance);
    await locationlessStore.InitializeAsync();
    var locationlessCharacter = (await locationlessStore.GetCharactersAsync("test"))[0];
    Check(locationlessCharacter.TownId == 1
        && locationlessCharacter.CharacterNo == 1
        && locationlessCharacter.AccountUid == 1
        && locationlessCharacter.Slot == 0
        && locationlessCharacter.AreaId == 1
        && locationlessCharacter.X == 474
        && locationlessCharacter.Y == 234
        && locationlessCharacter.Direction == 5
        && locationlessCharacter.TutorialStarted,
        "old state files retain Elvengard defaults and do not force migrated characters through the tutorial");

    var legacyStoreOptions = new ServerOptions
    {
        DataPath = Path.Combine(questFixtureRoot, "legacy-state.json"),
        SkillScriptPath = questFixtureRoot
    };
    var legacyStore = new JsonGameStore(
        legacyStoreOptions,
        NullLogger<JsonGameStore>.Instance);
    await legacyStore.InitializeAsync();
    var legacyCharacter = (await legacyStore.GetCharactersAsync("test"))[0];
    legacyCharacter = await legacyStore.SaveCharacterProgressionAsync(
        "test",
        legacyCharacter.Id,
        1,
        0,
        legacyCharacter.Sp) ?? legacyCharacter;
    legacyCharacter = await legacyStore.SaveCharacterQuestsAsync(
        "test",
        legacyCharacter.Id,
        [],
        [1016]) ?? legacyCharacter;
    var migratedRewards = await legacyStore.MigrateLegacyQuestRewardsAsync(
        questCatalog,
        experienceCatalog,
        spCatalog,
        CreateItems(questScripts));
    var migratedCharacter = (await legacyStore.GetCharactersAsync("test"))[0];
    Check(migratedRewards == 1
        && migratedCharacter.Experience == 45
        && migratedCharacter.Inventory?.Any(item =>
            item.ItemId == 1055 && item.CountOrValue == 3) == true
        && migratedCharacter.RewardedQuestIds?.Contains(1016) == true,
        "legacy completed quests receive missing rewards exactly once");
    Check(await legacyStore.MigrateLegacyQuestRewardsAsync(
            questCatalog,
            experienceCatalog,
            spCatalog,
            CreateItems(questScripts)) == 0,
        "legacy reward migration is idempotent across restarts");

    var legacyGrowthPath = Path.Combine(
        questFixtureRoot,
        "legacy-growth-state.json");
    File.WriteAllText(
        legacyGrowthPath,
        """
        [
          {
            "Id": "33333333-3333-3333-3333-333333333333",
            "UserName": "growth",
            "PasswordHash": "legacy",
            "Characters": [
              {
                "Id": "44444444-4444-4444-4444-444444444444",
                "Name": "GrowthLegacy",
                "Job": 0,
                "GrowType": 0,
                "Level": 18,
                "Sp": 100,
                "Cash": 0,
                "CompletedQuestIds": [1025],
                "RewardedQuestIds": [1025],
                "Skills": [
                  { "Slot": 6, "SkillId": 1, "Level": 5 },
                  { "Slot": 48, "SkillId": 2, "Level": 1 }
                ]
              }
            ]
          }
        ]
        """);
    var legacyGrowthStore = new JsonGameStore(
        new ServerOptions
        {
            DataPath = legacyGrowthPath,
            SkillScriptPath = questFixtureRoot
        },
        NullLogger<JsonGameStore>.Instance);
    await legacyGrowthStore.InitializeAsync();
    var legacyGrowthSkillCatalog = new SkillCatalog(
        questScripts,
        NullLogger<SkillCatalog>.Instance);
    Check(await legacyGrowthStore.MigrateLegacyQuestRewardsAsync(
            questCatalog,
            experienceCatalog,
            spCatalog,
            CreateItems(questScripts),
            skillCatalog: legacyGrowthSkillCatalog) == 0,
        "already rewarded legacy class quests do not grant duplicate ordinary rewards");
    var restoredGrowthCharacter =
        (await legacyGrowthStore.GetCharactersAsync("growth"))[0];
    Check(restoredGrowthCharacter.GrowType == 2
        && restoredGrowthCharacter.AwakeningType == 0
        && restoredGrowthCharacter.Sp == 100
        && restoredGrowthCharacter.Skills?.All(skill => skill.SkillId != 1) == true
        && restoredGrowthCharacter.Skills?.Any(skill =>
            skill.SkillId == 2 && skill.Level == 1) == true
        && restoredGrowthCharacter.Skills?.Any(skill =>
            skill.SkillId == 5 && skill.Level == 3) == true,
        "legacy completion repair restores the unique class reward and PVF skill changes without resetting purchased SP");
}
finally
{
    Directory.Delete(questFixtureRoot, recursive: true);
}


var skillInfo = GameProtocolEngine.CreateSkillInfo(
    500,
    [new GameSkillEntry(5, 20), new GameSkillEntry(8, 20)]);
Check(skillInfo.ProtocolId == GameProtocolEngine.SkillInfoNotification,
    "skill initialization uses SKILLINFO notification nineteen");
Check(skillInfo.Payload.SequenceEqual(new byte[] { 0xF4, 0x01, 2, 0, 5, 20, 1, 8, 20, 0 }),
    "skill initialization preserves SP and slot/id/level records");
Check(GameProtocolEngine.CreateStamina(100).Payload.SequenceEqual(new byte[] { 100 }),
    "DFLegacy stamina notification carries a one-byte recovery percentage");
var deadState = GameProtocolEngine.CreateDieState(0x1234, isAlive: false);
var revivedState = GameProtocolEngine.CreateDieState(0x1234, isAlive: true);
Check(deadState.Type == GameProtocolEngine.NotificationPacketType
        && deadState.ProtocolId == GameProtocolEngine.DieStateNotification
        && deadState.Payload.SequenceEqual(new byte[] { 0x34, 0x12, 0 }),
    "DFLegacy DIE_STATE encodes the target user and dead state");
Check(revivedState.Payload.SequenceEqual(new byte[] { 0x34, 0x12, 1 }),
    "DFLegacy DIE_STATE encodes the target user and revived state");
Check(GameProtocolEngine.TryParseUseCoinCommand(
        new byte[] { 0x1A, 0, 0x34, 0x12 },
        out var useCoinCommand)
        && useCoinCommand.TargetUserId == 0x1234,
    "DFLegacy USE_COIN parses the captured sequence and target user id");
Check(!GameProtocolEngine.TryParseUseCoinCommand(
        new byte[] { 0x1A, 0, 0x34 },
        out _),
    "DFLegacy USE_COIN rejects truncated requests");
Check(GameProtocolEngine.CreateUseCoinReply(0x1234).Payload.SequenceEqual(
        new byte[] { 1, 0x34, 0x12 }),
    "DFLegacy USE_COIN success returns the revived target user id");
var revivalCoinInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [CharacterInventoryLayout.RevivalCoinSlot] = new(
        CharacterInventoryLayout.RevivalCoinSlot,
        CharacterInventoryLayout.RevivalCoinItemId,
        20),
    [9] = new(9, 3037, 50)
};
Check(CharacterInventoryPlanner.TryConsumeRevivalCoin(
        revivalCoinInventory,
        out var inventoryAfterRevival)
        && revivalCoinInventory[CharacterInventoryLayout.RevivalCoinSlot].CountOrValue == 20
        && inventoryAfterRevival[CharacterInventoryLayout.RevivalCoinSlot].CountOrValue == 19
        && inventoryAfterRevival[9] == revivalCoinInventory[9],
    "revival coin consumption plans one authoritative decrement without mutating live inventory");
var lastRevivalCoinInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [CharacterInventoryLayout.RevivalCoinSlot] = new(
        CharacterInventoryLayout.RevivalCoinSlot,
        CharacterInventoryLayout.RevivalCoinItemId,
        1)
};
Check(CharacterInventoryPlanner.TryConsumeRevivalCoin(
        lastRevivalCoinInventory,
        out var inventoryAfterLastRevival)
        && !inventoryAfterLastRevival.ContainsKey(CharacterInventoryLayout.RevivalCoinSlot),
    "consuming the last revival coin removes its virtual inventory object");
Check(!CharacterInventoryPlanner.TryConsumeRevivalCoin(
        new Dictionary<ushort, CharacterItemRecord>(),
        out _),
    "revival coin consumption rejects an empty virtual slot");
Check(GameProtocolEngine.CreateStamina(100).Encode().SequenceEqual(
        new byte[] { 0, 4, 7, 0, 0, 0, 0x47 }),
    "DFLegacy stamina notification has the exact encrypted seven-byte wire layout");
Check(GameProtocolEngine.FailClearDungeonNotification == 33,
    "DFLegacy notification thirty-three is FAIL_CLEAR_DUNGEON, not weakness state");
Check(GameProtocolEngine.CreateFailClearDungeon(100).Payload.SequenceEqual(new byte[] { 100 })
    && GameProtocolEngine.CreateFailClearDungeon(100).ProtocolId == 33,
    "FAIL_CLEAR_DUNGEON carries the silent stamina state used after town actor rebuilds");
var recoverStaminaReply = GameProtocolEngine.CreateRecoverStaminaReply(8_457);
var recoverStaminaError = GameProtocolEngine.CreateRecoverStaminaError(22);
Check(GameProtocolEngine.RecoverStaminaCommand == 9
    && recoverStaminaReply.Type == GameProtocolEngine.CommandPacketType
    && recoverStaminaReply.ProtocolId == GameProtocolEngine.RecoverStaminaCommand
    && recoverStaminaReply.Payload.SequenceEqual(
        new byte[] { 1, 0x09, 0x21, 0, 0 })
    && recoverStaminaError.Payload.SequenceEqual(new byte[] { 0, 22 }),
    "DFLegacy CMD 9 returns the remaining gold or the old-client recovery error code");
Check(!DungeonWeaknessPolicy.ShouldApply(
        characterLevel: DungeonWeaknessPolicy.MinimumLevel - 1,
        forcedDungeonExit: true,
        dungeonActive: true,
        dungeonCleared: false,
        hasBlackDiamond: false)
    && DungeonWeaknessPolicy.ShouldApply(
        characterLevel: DungeonWeaknessPolicy.MinimumLevel,
        forcedDungeonExit: true,
        dungeonActive: true,
        dungeonCleared: false,
        hasBlackDiamond: false)
    && !DungeonWeaknessPolicy.ShouldApply(
        characterLevel: 60,
        forcedDungeonExit: false,
        dungeonActive: true,
        dungeonCleared: false,
        hasBlackDiamond: false)
    && !DungeonWeaknessPolicy.ShouldApply(
        characterLevel: 60,
        forcedDungeonExit: true,
        dungeonActive: false,
        dungeonCleared: false,
        hasBlackDiamond: false)
    && !DungeonWeaknessPolicy.ShouldApply(
        characterLevel: 60,
        forcedDungeonExit: true,
        dungeonActive: true,
        dungeonCleared: true,
        hasBlackDiamond: false)
    && !DungeonWeaknessPolicy.ShouldApply(
        characterLevel: 60,
        forcedDungeonExit: true,
        dungeonActive: true,
        dungeonCleared: false,
        hasBlackDiamond: true)
    && DungeonWeaknessPolicy.MinimumRecovery == 10
    && DungeonWeaknessPolicy.InitialRecovery == 10
    && DungeonWeaknessPolicy.RecoveryPerMinute == 9
    && DungeonWeaknessPolicy.FullStamina == 100,
    "failed or forced dungeon exits apply weakness only to eligible non-black-diamond characters");
var parsedStaminaRecoveryCosts = StaminaRecoveryCatalog.Parse(
    "[stamina recovery cost]\n0\n505 // level 6\n11543\n[/stamina recovery cost]");
Check(parsedStaminaRecoveryCosts.SequenceEqual(new[] { 0, 505, 11_543 }),
    "stamina recovery costs parse from the DF2008 server-parameter section");
var fatigue = GameProtocolEngine.CreateFatigue();
Check(fatigue.ProtocolId == GameProtocolEngine.FatigueNotification
    && fatigue.Payload.SequenceEqual(new byte[] { 0, 0, 0x9C, 0, 0, 0 }),
    "DFLegacy fatigue notification carries used, maximum, and premium values");
var blackDiamondFatigue = GameProtocolEngine.CreateFatigue(
    maximumFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue,
    premiumFatigue: GameProtocolEngine.BlackDiamondBonusFatigue);
Check(blackDiamondFatigue.Payload.SequenceEqual(new byte[] { 0, 0, 0xBC, 0, 0x20, 0 }),
    "black diamond adds 32 to the total fatigue maximum and premium field");
var enterDungeonSelectionFatigueError =
    GameProtocolEngine.CreateEnterDungeonSelectionInsufficientFatigueReply();
Check(enterDungeonSelectionFatigueError.Type == GameProtocolEngine.CommandPacketType
        && enterDungeonSelectionFatigueError.ProtocolId == GameProtocolEngine.StartGameCommand
        && enterDungeonSelectionFatigueError.Payload.SequenceEqual(new byte[] { 0, 0x16, 0 }),
    "DFLegacy CMD 15 rejects an exhausted solo member with the old-client fatigue layout");
Check(DungeonFatigueRules.CanEnterNewDungeon(
        usedFatigue: GameProtocolEngine.DefaultMaximumFatigue - 1,
        maximumFatigue: GameProtocolEngine.DefaultMaximumFatigue)
    && !DungeonFatigueRules.CanEnterNewDungeon(
        usedFatigue: GameProtocolEngine.DefaultMaximumFatigue,
        maximumFatigue: GameProtocolEngine.DefaultMaximumFatigue)
    && !DungeonFatigueRules.CanEnterNewDungeon(
        usedFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue,
        maximumFatigue: GameProtocolEngine.BlackDiamondMaximumFatigue),
    "new dungeon entry requires at least one remaining normal or black-diamond fatigue point");
var eventInfo = GameProtocolEngine.CreateEventInfo();
Check(eventInfo.ProtocolId == GameProtocolEngine.EventInfoNotification
    && eventInfo.Payload.SequenceEqual(new byte[] { 0, 0, 0, 0 }),
    "DFLegacy event-info notification initializes the ordinary fatigue-bar mode");
var dungeonPermissions = GameProtocolEngine.CreateDungeonPermissions(
    [new GameDungeonPermissionEntry(1, 1), new GameDungeonPermissionEntry(9, 3)]);
Check(dungeonPermissions.ProtocolId == GameProtocolEngine.DungeonPermissionNotification,
    "dungeon initialization uses notification five");
Check(dungeonPermissions.Payload.SequenceEqual(
        new byte[] { 2, 0, 1, 0, 1, 9, 0, 3 }),
    "dungeon permissions preserve the u16 count and id/state records");
var incrementalDungeonDifficulty = GameProtocolEngine.CreateDungeonPermissions(
    [new GameDungeonPermissionEntry(17, 2)]);
Check(incrementalDungeonDifficulty.Payload.SequenceEqual(
        new byte[] { 1, 0, 17, 0, 2 }),
    "DF2008 dungeon difficulty unlock uses a one-record NOTI 5 with an u8 difficulty");
Check(GameProtocolEngine.CreateDungeonPermissions(
        [new GameDungeonPermissionEntry(GameProtocolEngine.TutorialDungeonId, 0)])
    .Payload.SequenceEqual(new byte[] { 1, 0, 0x10, 0x27, 0 }),
    "incomplete characters can receive the server-owned tutorial permission for id 10000");
Check(DungeonDifficultyProgression.TryGetNextDifficulty(0, 0, out var difficultyOne)
        && difficultyOne == 1
        && !DungeonDifficultyProgression.TryGetNextDifficulty(1, 45, out _)
        && DungeonDifficultyProgression.TryGetNextDifficulty(1, 55, out var difficultyTwo)
        && difficultyTwo == 2
        && !DungeonDifficultyProgression.TryGetNextDifficulty(2, 70, out _)
        && DungeonDifficultyProgression.TryGetNextDifficulty(2, 85, out var difficultyThree)
        && difficultyThree == 3
        && !DungeonDifficultyProgression.TryGetNextDifficulty(3, 105, out _),
    "dungeon difficulty progression applies clear, B-rank and S-rank gates across difficulties zero through three");
Check(DungeonClearScore.TryParse(
            Convert.FromHexString(
                "29011E0000000C0006000A000600FBFFFFFF2A0017003700"),
            out var clearScore)
        && clearScore.StyleComponents.SequenceEqual(new ushort[] { 30, 0, 12 })
        && clearScore.TechniqueComponents.SequenceEqual(new ushort[] { 6, 10, 6 })
        && clearScore.HitPenalty == -5
        && clearScore.CalculatedScore == 60
        && clearScore.CalculatedResultCode == 55
        && clearScore.IsWireResultCodeConsistent
        && DungeonClearScore.ResultCodeFromScore(84) == 70
        && DungeonClearScore.ResultCodeFromScore(85) == 85
        && !DungeonClearScore.TryParse(new byte[23], out _),
    "command 49 score parsing calculates the DFLegacy result code used by difficulty rank gates");
Check(GameProtocolEngine.CreateEmptyAcceptableQuestList().Payload.SequenceEqual(new byte[] { 0 }),
    "town initialization clears the acceptable quest list safely");
Check(GameProtocolEngine.CreateAcceptableQuestList([1, 1016]).Payload.SequenceEqual(
        new byte[] { 2, 1, 0, 0xF8, 0x03 }),
    "acceptable quest notification uses a u8 count followed by u16 ids");
Check(GameProtocolEngine.AcceptQuestCommand == 33
    && GameProtocolEngine.GiveupQuestCommand == 34
    && GameProtocolEngine.SetQuestTriggerCommand == 35
    && GameProtocolEngine.FinishQuestCommand == 36,
    "quest command ids match the DFLegacy command-name table");
Check(GameProtocolEngine.SellItemCommand == 24
    && GameProtocolEngine.SetItemTradeStateCommand == 26
    && GameProtocolEngine.DisjointItemCommand == 28
    && GameProtocolEngine.ChangeSkillSlotCommand == 30
    && GameProtocolEngine.StartGameCommand == 15
    && GameProtocolEngine.DeleteItemCommand == 18,
    "DFLegacy conflicting item and skill command ids are selected");
Check(GameProtocolEngine.CreateAcceptQuestReply(1016, 3).Payload.SequenceEqual(
        new byte[] { 1, 0xF8, 0x03, 3, 0, 0, 0, 0 }),
    "accept-quest reply uses the DFLegacy u32 trigger and empty event-item layout");
Check(GameProtocolEngine.CreateAcceptQuestReply(
        201,
        0,
        [new GameQuestInsertedItem(105, 3080, 1)]).Payload.SequenceEqual(
    new byte[]
    {
        1, 0xC9, 0x00, 0, 0, 0, 0, 1,
        0x69, 0x00, 0x08, 0x0C, 1, 0, 0, 0
    }),
    "accept-quest reply carries each depend-give item as slot, item id and inserted count");
Check(GameProtocolEngine.CreateGiveupQuestReply(1016).Payload.SequenceEqual(
        new byte[] { 1, 0xF8, 0x03 }),
    "giveup-quest reply echoes the accepted quest id");
Check(GameProtocolEngine.CreateSetQuestTriggerReply(1016, 4).Payload.SequenceEqual(
        new byte[] { 1, 0xF8, 0x03, 4, 0, 0, 0 }),
    "set-trigger reply carries the DFLegacy u32 trigger");
Check(GameProtocolEngine.CreateFinishQuestReply(1016).Payload.SequenceEqual(
        new byte[] { 1, 0xF8, 0x03, 0, 0, 0, 0 }),
    "finish-quest reply emits a complete empty-reward state block");
Check(GameProtocolEngine.CreateFinishQuestReply(
        1016,
        [new GameQuestConsumedItem(105, 1), new GameQuestConsumedItem(106, 0)])
    .Payload.SequenceEqual(
    new byte[]
    {
        1, 0xF8, 0x03, 0, 2,
        0, 105, 0, 1, 0, 0, 0,
        0, 106, 0, 0, 0, 0, 0,
        0, 0
    }),
    "finish-quest reply reports each consumed task-item slot and its remaining count");
Check(GameProtocolEngine.CreateFinishQuestReply(
        1016,
        insertedItems:
        [
            new GameQuestInsertedItem(105, 1055, 3),
            new GameQuestInsertedItem(0, 0, 200)
        ])
    .Payload.SequenceEqual(
    new byte[]
    {
        1, 0xF8, 0x03, 0, 0, 0, 2,
        105, 0, 0x1F, 0x04, 3, 0, 0, 0,
        0, 0, 0, 0, 200, 0, 0, 0
    }),
    "normal finish-quest reply carries each inserted inventory reward and the gold pseudo-item");
Check(GameProtocolEngine.CreateFinishQuestReply(
        1016,
        chainType: 1,
        growNumber: 2,
        growthSkillChanges:
        [
            new GameQuestGrowthSkillChange(181, 4, 0),
            new GameQuestGrowthSkillChange(35, 0, 1)
        ])
    .Payload.SequenceEqual(
    new byte[]
    {
        1, 0xF8, 0x03, 0, 0, 1, 2, 2,
        4, 181, 0,
        0, 35, 1
    }),
    "class-change finish reply uses the client chain branch and includes PVF skill removals and grants");
Check(GameProtocolEngine.SetUserPositionCommand == 37
    && GameProtocolEngine.SetUserAreaCommand == 38
    && GameProtocolEngine.FinishLoadingCommand == 40
    && GameProtocolEngine.GiveupGameCommand == 45
    && GameProtocolEngine.BackToVillageCommand == 142,
    "town command ids match the DFLegacy command-name table");
var townUser = new GameTownUser(1, 474, 234, 5);
var userPosition = GameProtocolEngine.CreateUserPosition(townUser);
Check(userPosition.ProtocolId == GameProtocolEngine.UserPositionNotification,
    "town movement uses USER_POSITION notification twenty-two");
Check(userPosition.Payload.SequenceEqual(
        new byte[] { 1, 0, 0xDA, 0x01, 0xEA, 0, 5, 100, 0 }),
    "USER_POSITION uses uid/x/y/direction/movement-value layout");
var userArea = GameProtocolEngine.CreateUserArea(2, 5, townUser);
Check(userArea.ProtocolId == GameProtocolEngine.UserAreaNotification,
    "town transition uses USER_AREA notification twenty-three");
Check(userArea.Payload.SequenceEqual(
        new byte[] { 1, 0, 2, 5, 0xDA, 0x01, 0xEA, 0, 5, 1 }),
    "USER_AREA uses the DFLegacy uid/town/area/x/y/direction/active layout");
var areaUsers = GameProtocolEngine.CreateAreaUsers(2, 5, [townUser]);
Check(areaUsers.ProtocolId == GameProtocolEngine.AreaUsersNotification,
    "town initialization uses AREA_USERS notification twenty-four");
Check(areaUsers.Payload.SequenceEqual(
        new byte[] { 2, 5, 1, 0, 1, 0, 0xDA, 0x01, 0xEA, 0, 5, 1 }),
    "AREA_USERS initializes the local town roster with the exact DFLegacy layout");
Check(GameProtocolEngine.CreateUdpHost().Payload.SequenceEqual(new byte[] { 0 }),
    "solo dungeon selection assigns the local actor as UDP host");
var natCommandBody = new byte[]
{
    0x34, 0x12,
    3,
    192, 168, 1, 10,
    1, 2, 3, 4,
    0x13, 0xC7,
    0xC0, 0x05, 0, 0
};
Check(GameDatagramProtocol.TryParseNatReport(natCommandBody, out var natReport)
        && natReport.NatType == 3
        && natReport.LocalAddress.Equals(IPAddress.Parse("192.168.1.10"))
        && natReport.PublicAddress.Equals(IPAddress.Parse("1.2.3.4"))
        && natReport.PublicPort == 5063
        && natReport.Mtu == 1472,
    "CMD 2 parses the exact DFLegacy NAT endpoint layout and mixed port endian");
Check(GameDatagramProtocol.TryCreateNatProbeResponse(
        new byte[] { 1 },
        new IPEndPoint(IPAddress.Loopback, 5063),
        out var natResponse)
        && natResponse.SequenceEqual(new byte[] { 2, 1, 0, 0, 127, 0xC7, 0x13 }),
    "NAT probe response uses reversed IPv4 and little-endian reflected port");
Check(GameDatagramProtocol.IsNatKeepAlive(
        new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })
        && !GameDatagramProtocol.IsNatKeepAlive(new byte[] { 0 }),
    "NAT keepalive is the exact ten-byte DFLegacy secondary-reflector packet");
var udpPeerInfo = GameProtocolEngine.CreateUdpPeerInfo(
    [
        new GamePeerInfo(
            1,
            IPAddress.Parse("192.168.1.10"),
            IPAddress.Parse("1.2.3.4"),
            5063,
            1,
            3,
            1472)
    ]);
Check(udpPeerInfo.ProtocolId == GameProtocolEngine.UdpPeerInfoNotification
        && udpPeerInfo.Payload.SequenceEqual(
            new byte[]
            {
                1,
                1, 0,
                192, 168, 1, 10,
                1, 2, 3, 4,
                0xC7, 0x13,
                1, 0, 0, 0,
                3,
                0xC0, 0x05, 0, 0
            }),
    "NOTI 11 serializes the DFLegacy peer endpoint layout");
var bossDieCheckBody = new byte[]
{
    0x42, 0x00,
    0x01, 0x00,
    0x04, 0x00,
    0x12, 0xB7, 0x34, 0x45
};
Check(GameProtocolEngine.TryParseBossDieCheckCommand(
        bossDieCheckBody,
        out var bossDieCheck)
        && bossDieCheck.ParticipantId == 1
        && bossDieCheck.ParticipantId != 5
        && bossDieCheck.BossId == 4
        && bossDieCheck.SkillChecksum == 0x4534B712,
    "CMD 127 parses the participant independently from the selected dungeon, boss and skill checksum");
var bossDieCheckPending = GameProtocolEngine.CreateBossDieCheck(
    success: true,
    allBossesReported: false,
    bossId: 4);
var bossDieCheckComplete = GameProtocolEngine.CreateBossDieCheck(
    success: true,
    allBossesReported: true,
    bossId: 4);
var bossDieCheckRejected = GameProtocolEngine.CreateBossDieCheck(
    success: false,
    allBossesReported: false,
    errorCode: 2);
Check(bossDieCheckPending.ProtocolId == GameProtocolEngine.BossDieCheckNotification
        && bossDieCheckPending.Payload.SequenceEqual(new byte[] { 1, 0, 4, 0 })
        && bossDieCheckComplete.Payload.SequenceEqual(new byte[] { 1, 1, 4, 0 })
        && bossDieCheckRejected.Payload.SequenceEqual(new byte[] { 0, 2 }),
    "NOTI 127 matches the DFLegacy success, all-bosses-reported and boss-id layout");
var setHpApplication = GameDatagramProtocol.CreateSetActiveObjectHpApplication(
    objectType: 2,
    objectId: 3,
    hp: 0);
Check(setHpApplication.SequenceEqual(
        new byte[]
        {
            1, 12, 0, 0xF3, 0xE3, 0xD9, 0x7C,
            0x85, 0x05, 0xC5, 0x05,
            0x05, 0x05, 0x05, 0x05,
            0x05, 0x05, 0x05, 0x05
        }),
    "UDP application protocol 12 matches the DFLegacy CRC and 0x2F1214 cipher bytes");
Check(GameDatagramProtocol.TryParseApplication(
        setHpApplication,
        out var parsedSetHp)
        && parsedSetHp.Mode == 1
        && parsedSetHp.ProtocolId == 12
        && parsedSetHp.Payload.SequenceEqual(
            new byte[] { 2, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
    "UDP application protocol 12 decrypts and validates CRC");
var setHpFrame = GameDatagramProtocol.CreateUnreliableFrame(
    sequence: 7,
    senderPartyIndex: 0,
    flag: 0,
    setHpApplication);
Check(GameDatagramProtocol.TryReadBlock(setHpFrame, out var parsedSetHpFrame, out var consumed)
        && consumed == setHpFrame.Length
        && parsedSetHpFrame is not null
        && parsedSetHpFrame.BlockType == GameDatagramProtocol.UnreliableBlockType
        && parsedSetHpFrame.Sequence == 7
        && parsedSetHpFrame.SenderPartyIndex == 0
        && parsedSetHpFrame.Application.SequenceEqual(setHpApplication),
    "MTUPD type 2 frame round-trips the DFLegacy sequence/length/sender header");
var enterSelectDungeon = GameProtocolEngine.CreateEnterSelectDungeon(true, []);
Check(enterSelectDungeon.ProtocolId == GameProtocolEngine.EnterSelectDungeonNotification
    && enterSelectDungeon.Payload.SequenceEqual(new byte[] { 1, 0 }),
    "dungeon-gate entry uses the exact DFLegacy quest/item-party-slot layout");
var dungeonInfo = GameProtocolEngine.CreateDungeonInfo(1, 0, 0, 2, 1);
Check(dungeonInfo.ProtocolId == GameProtocolEngine.DungeonInfoNotification
    && dungeonInfo.Payload.SequenceEqual(new byte[] { 1, 0, 0, 0, 2, 1, 0, 0 }),
    "DUNGEON_INFO uses the fixed eight-byte DFLegacy layout");
var alternateMazeDungeonInfo = GameProtocolEngine.CreateDungeonInfo(6, 1, 1, 4, 3);
Check(alternateMazeDungeonInfo.Payload.SequenceEqual(
        new byte[] { 6, 0, 1, 1, 4, 3, 0, 0 }),
    "DUNGEON_INFO carries the selected maze and boss coordinate without changing its layout");
var tutorialDungeonInfo = GameProtocolEngine.CreateTutorialDungeonInfo(2);
Check(GameProtocolEngine.TutorialDungeonId == 10_000
        && tutorialDungeonInfo.Payload.SequenceEqual(
            new byte[] { 0x10, 0x27, 2, 0, 0xFF, 0xFF, 0, 0 }),
    "tutorial DUNGEON_INFO uses reserved id 10000 without a PVF dungeon lookup");
var tutorialStartMap = GameProtocolEngine.CreateTutorialStartMap();
Check(GameProtocolEngine.TutorialMapId == 61_000
        && tutorialStartMap.ProtocolId == GameProtocolEngine.StartMapNotification
        && tutorialStartMap.Payload.SequenceEqual(
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 1, 0x48, 0xEE, 4,
            0, 0, 0, 1, 0, 1, 0, 0, 0,
            1, 1, 0, 1, 0, 1, 0, 0, 0,
            2, 2, 0, 1, 0, 1, 0, 0, 0,
            3, 3, 0, 1, 0, 1, 0, 0, 0,
            0, 0
        }),
    "tutorial START_MAP is the exact 48-byte four-monster layout and uses map.lst scene id 61000");
var tutorialMonsters = GameProtocolEngine.CreateTutorialMonsters();
Check(tutorialMonsters.Select(monster => monster.UniqueId)
        .SequenceEqual(new ushort[] { 0, 1, 2, 3 })
        && tutorialMonsters.All(monster => monster.MonsterIndex == 1 && monster.Level == 1)
        && tutorialMonsters.Select(monster => (monster.X, monster.Y)).SequenceEqual(
        [
            ((ushort)1078, (ushort)177),
            ((ushort)557, (ushort)208),
            ((ushort)487, (ushort)332),
            ((ushort)1026, (ushort)334)
        ]),
    "tutorial runtime state keeps the four actor ids and drop coordinates independent of PVF");
Check(GameProtocolEngine.TutorialCompletionCommand == 153
        && GameProtocolEngine.IsTutorialCompletionModule(
            new byte[] { 0, 0, 31, 0, 0, 0 })
        && !GameProtocolEngine.IsTutorialCompletionModule(
            new byte[] { 0, 0, 11, 0, 0, 0 })
        && !GameProtocolEngine.IsTutorialCompletionModule(
            new byte[] { 31, 0, 0, 0 }),
    "tutorial completion recognizes only command 153 module 31 at the client body offset");
var emptyStartMap = GameProtocolEngine.CreateEmptyStartMap(0, 0, 1);
Check(emptyStartMap.ProtocolId == GameProtocolEngine.StartMapNotification
    && emptyStartMap.Payload.SequenceEqual(
        new byte[] { 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0 }),
    "START_MAP carries a complete first-visit room with empty runtime lists");
var cachedStartMap = GameProtocolEngine.CreateCachedStartMap(
    3,
    4,
    0x1234_5678);
Check(cachedStartMap.ProtocolId == GameProtocolEngine.StartMapNotification
        && cachedStartMap.Payload.SequenceEqual(
            new byte[] { 3, 4, 0x78, 0x56, 0x34, 0x12, 0 }),
    "START_MAP state zero reuses the client's coordinate-scoped room snapshot");
var hellStartMap = GameProtocolEngine.CreateStartMap(
    2,
    3,
    60_001,
    [],
    roomStateFlag: 2,
    hellPartyMode: 2);
Check(hellStartMap.Payload[6] == 2
        && hellStartMap.Payload[^1] == 2,
    "START_MAP carries the hell-room state flag and hell-party mode tail byte");
Check(HiddenDungeonUnlockCatalog.TryGetDungeonId(4, out var lorienDeepDungeon)
        && lorienDeepDungeon == 9
        && HiddenDungeonUnlockCatalog.TryGetDungeonId(428, out var hiddenDungeon)
        && hiddenDungeon == 1_500
        && HiddenDungeonUnlockCatalog.ResolveVisibleDungeonIds(
                [1, 9, 17, 51, 1_000, 1_500],
                [17, 1_500])
            .SequenceEqual(new ushort[] { 1, 17, 1_500 }),
    "hidden dungeon permissions expose only quest-unlocked dungeon ids");
var monsterStartMap = GameProtocolEngine.CreateStartMap(
    0,
    0,
    1,
    [
        new GameDungeonMonster(0, 0, 1, 1),
        new GameDungeonMonster(1, 1, 1, 1),
        new GameDungeonMonster(2, 2, 1, 1)
    ]);
Check(monsterStartMap.Payload.Length == 39,
    "three compact START_MAP monsters add three nine-byte records");
Check(monsterStartMap.Payload.AsSpan(10, 27).SequenceEqual(
        new byte[]
        {
            0, 0, 0, 1, 0, 1, 0, 0, 0,
            1, 1, 0, 1, 0, 1, 0, 0, 0,
            2, 2, 0, 1, 0, 1, 0, 0, 0
        }),
    "Lorien monster records match the client's map-row, actor-id, monster-index and level layout");
Check(GameDungeonMonsterTypes.Normal == 0
    && GameDungeonMonsterTypes.Champion == 1
    && GameDungeonMonsterTypes.SuperChampion == 2
    && GameDungeonMonsterTypes.Boss == 3,
    "DFLegacy monster runtime types match the client map parser");
var championPromotionSource = new[]
{
    new GameDungeonMonster(0, 0, 1, 1),
    new GameDungeonMonster(1, 1, 1, 1),
    new GameDungeonMonster(2, 2, 1, 1),
    new GameDungeonMonster(3, 3, 1, 1, GameDungeonMonsterTypes.Boss),
    new GameDungeonMonster(4, 4, 1, 1, GameDungeonMonsterTypes.Champion),
    new GameDungeonMonster(5, 5, 1, 1, GameDungeonMonsterTypes.SuperChampion),
    new GameDungeonMonster(6, 6, 1, 1, IsBoxMonster: 1),
    new GameDungeonMonster(7, 7, 1, 1, IsHellHidden: true)
};
var promotedChampions = DungeonCatalog.PromoteChampions(
    championPromotionSource,
    count: 2,
    new ZeroDropRandomSource());
Check(promotedChampions.SequenceEqual(DungeonCatalog.PromoteChampions(
            championPromotionSource,
            count: 2,
            new ZeroDropRandomSource()))
        && promotedChampions.Count(monster =>
            monster.Type == GameDungeonMonsterTypes.Champion) == 3
        && promotedChampions[3].Type == GameDungeonMonsterTypes.Boss
        && promotedChampions[5].Type == GameDungeonMonsterTypes.SuperChampion
        && promotedChampions[6].Type == GameDungeonMonsterTypes.Normal
        && promotedChampions[7].Type == GameDungeonMonsterTypes.Normal
        && championPromotionSource.Take(3).All(monster =>
            monster.Type == GameDungeonMonsterTypes.Normal),
    "champion promotion selects without replacement and excludes bosses, existing elites, box monsters and hell-hidden demons");
var typedMonsterStartMap = GameProtocolEngine.CreateStartMap(
    0,
    0,
    1,
    [
        new GameDungeonMonster(0, 0, 1, 1, GameDungeonMonsterTypes.Normal),
        new GameDungeonMonster(1, 1, 1, 1, GameDungeonMonsterTypes.Champion),
        new GameDungeonMonster(2, 2, 1, 1, GameDungeonMonsterTypes.SuperChampion),
        new GameDungeonMonster(3, 3, 1, 1, GameDungeonMonsterTypes.Boss)
    ],
    randomSeed: 0x1234_5678u);
Check(new[] { 0, 1, 2, 3 }.Select(index =>
        typedMonsterStartMap.Payload[16 + index * 9]).SequenceEqual(
        new byte[] { 0, 1, 2, 3 })
      && BinaryPrimitives.ReadUInt32LittleEndian(
          typedMonsterStartMap.Payload.AsSpan(2, 4)) == 0x1234_5678u,
    "START_MAP preserves the client random seed and normal, champion, super-champion and boss type bytes");
var passiveDropStartMap = GameProtocolEngine.CreateStartMap(
    0,
    0,
    1,
    [],
    [new GamePassiveObjectDrop(3, 7, 3176, 1, 1)]);
Check(passiveDropStartMap.Payload.SequenceEqual(
        new byte[]
        {
            0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
            1,
            3, 7, 0, 0x68, 0x0C, 1, 0, 0, 0, 1, 0,
            0
        }),
    "START_MAP serializes the DFLegacy passive item association and eleven-byte record");
var passiveDieBody = Convert.FromHexString(
    "35000100010000000000000000004B4000000800000001");
Check(GameProtocolEngine.TryParseDieMonsterCommand(passiveDieBody, out var passiveDie)
        && passiveDie.EntityId == 1
        && passiveDie.IsPassiveObject,
    "command 42 identifies the DFLegacy passive-object discriminator at byte 22");
passiveDieBody[22] = 0;
Check(GameProtocolEngine.TryParseDieMonsterCommand(passiveDieBody, out var ordinaryDie)
        && ordinaryDie.EntityId == 1
        && !ordinaryDie.IsPassiveObject,
    "command 42 keeps ordinary monster death on the existing branch");
Check(GameProtocolEngine.CreateFinishLoading().Payload.Length == 0,
    "finish-loading notification has no payload in this client build");
var enterGameWorldComplete = GameProtocolEngine.CreateEnterGameWorldComplete();
Check(enterGameWorldComplete.ProtocolId
        == GameProtocolEngine.EnterGameWorldCompleteNotification
    && enterGameWorldComplete.Payload.Length == 0,
    "DFLegacy enter-game-world-complete notification 136 releases town input locks");
var monsterDie = GameProtocolEngine.CreateMonsterDie(2);
Check(monsterDie.ProtocolId == GameProtocolEngine.MonsterDieNotification
    && monsterDie.Payload.SequenceEqual(
        new byte[] { 2, 0, 0, 0, byte.MaxValue, 0 }),
    "MONSTER_DIE commits a runtime actor death with DFLegacy empty drop metadata");
var monsterDieWithDrop = GameProtocolEngine.CreateMonsterDie(
    2,
    [new GameDungeonDrop(7, 3176, 1, 0, 1)]);
Check(monsterDieWithDrop.Payload.SequenceEqual(
        new byte[]
        {
            2, 0, 1,
            7, 0, 0x68, 0x0C, 1, 0, 0, 0, 0, 0, 1, 0,
            0, byte.MaxValue, 0
        }),
    "MONSTER_DIE serializes the compact DFLegacy five-field ground-item record");
var championMonsterDie = GameProtocolEngine.CreateMonsterDie(
    0x1234,
    championRewardOrdinal: 1);
Check(championMonsterDie.Payload.SequenceEqual(
        new byte[] { 0x34, 0x12, 0, 0, 1, 0 }),
    "MONSTER_DIE carries the 13338 Champion clear-reward animation ordinal");
var noChampionRewardOrdinalRejected = false;
try
{
    _ = GameProtocolEngine.CreateMonsterDie(
        1,
        championRewardOrdinal: (sbyte)(GameProtocolEngine.MaximumChampionRewardOrdinal + 1));
}
catch (ArgumentOutOfRangeException)
{
    noChampionRewardOrdinalRejected = true;
}
Check(noChampionRewardOrdinalRejected,
    "MONSTER_DIE rejects Champion reward animation ordinals above the 13338 limit");
var goldPickup = GameProtocolEngine.CreateGoldItemPickup(7, 1, 1_050);
Check(goldPickup.ProtocolId == GameProtocolEngine.GetItemNotification
    && goldPickup.Payload.SequenceEqual(
        new byte[]
        {
            7, 0, 1, 0,
            0x1A, 0x04, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0
        }),
    "GET_ITEM gold notification carries four DFLegacy party settlement records");
var inventoryPickup = GameProtocolEngine.CreateInventoryItemPickup(7, 1, 9);
Check(inventoryPickup.ProtocolId == GameProtocolEngine.GetItemNotification
    && inventoryPickup.Payload.SequenceEqual(
        new byte[] { 7, 0, 1, 0, 0, 0, 0, 0, 1, 0, 9, 0 }),
    "GET_ITEM direct item pickup carries zero routing rolls, recipient and inventory slot");
var groundItems = new DungeonGroundItemState();
var firstGroundItem = groundItems.Add(
    new DungeonGeneratedDrop(false, 3176, 1),
    ownerUserId: 1,
    sourceMonsterId: 2,
    roomX: 3,
    roomY: 4,
    x: 535,
    y: 180);
Check(firstGroundItem.GroundId == 1
    && firstGroundItem.X == 535
    && firstGroundItem.Y == 180
    && groundItems.TryGet(1, 3, 4, out _)
    && !groundItems.TryGet(1, 4, 3, out _),
    "ground items are addressable only in their originating dungeon room");
Check(groundItems.GetRoomItems(4, 4).Count == 0
        && groundItems.GetRoomItems(3, 4).Single().GroundId
            == firstGroundItem.GroundId,
    "querying another room does not consume or migrate unclaimed ground items");
Check(groundItems.Remove(1) && !groundItems.Remove(1),
    "ground-item removal is idempotent for duplicate pickup requests");
var secondGroundItem = groundItems.Add(
    new DungeonGeneratedDrop(true, 0, 100),
    ownerUserId: 1,
    sourceMonsterId: 3,
    roomX: 3,
    roomY: 4);
var thirdGroundItem = groundItems.Add(
    new DungeonGeneratedDrop(true, 0, 100),
    ownerUserId: 1,
    sourceMonsterId: 4,
    roomX: 4,
    roomY: 4);
Check(secondGroundItem.GroundId == 2
    && thirdGroundItem.GroundId == 3
    && groundItems.GetRoomItems(3, 4).Single().GroundId == 2
    && groundItems.GetRoomItems(4, 4).Single().GroundId == 3,
    "dungeon ground-item state preserves independent per-room drops across transitions");
var passiveGroundItem = groundItems.AddPassive(
    new DungeonGeneratedDrop(false, 1000, 1),
    ownerUserId: 1,
    passiveObjectIndex: 2,
    passiveItemSlot: 0,
    roomX: 4,
    roomY: 4);
Check(groundItems.TryGet(passiveGroundItem.GroundId, 4, 4, out _)
        && groundItems.ActivatePassiveObject(2, 4, 4) == 0,
    "passive ground items trust the DFLegacy client-side reveal and remain activation-idempotent");
Check(groundItems.DetachPassiveObjectItems(2, 4, 4) == 1
        && groundItems.GetPassiveObjectItems(4, 4).Count == 0
        && groundItems.GetRoomItems(4, 4).Any(item =>
            item.GroundId == passiveGroundItem.GroundId
            && item.PassiveObjectIndex == byte.MaxValue),
    "cleared hell rooms detach stale passive associations while retaining their ground drops");
groundItems.ResetDungeon();
Check(groundItems.Add(
        new DungeonGeneratedDrop(true, 0, 100),
        1,
        5,
        0,
        0).GroundId == 1,
    "new dungeon sessions reset the ground-id sequence");
var stackInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [73] = new CharacterItemRecord(73, 3176, 2)
};
var stackGroundItem = new DungeonGroundItem(
    1,
    3176,
    1,
    false,
    1,
    2,
    0,
    0);
Check(DungeonPickupPlanner.TryPlanItem(
        stackInventory,
        stackGroundItem,
        smokeItemCatalog!,
        uint.MaxValue,
        out var stackPlan,
        out var stackFailure)
    && stackFailure == InventoryPlacementFailure.None
    && stackPlan.DestinationSlot == 73
    && stackPlan.Inventory[73].CountOrValue == 3,
    "stackable ground items reuse and increment an existing inventory slot");
Check(DungeonPickupPlanner.TryPlanItem(
        stackInventory,
        stackGroundItem with { ItemId = 10_000 },
        smokeItemCatalog!,
        uint.MaxValue,
        out var equipmentPlan,
        out _)
    && equipmentPlan.DestinationSlot == 9
    && equipmentPlan.Inventory[9].CountOrValue == 1
    && equipmentPlan.Inventory[9].Durability == 1_000,
    "equipment ground items allocate the first equipment-category slot");
var fullInventory = Enumerable.Range(73, 32)
    .Concat(Enumerable.Range(3, 6))
    .ToDictionary(
        slot => (ushort)slot,
        slot => new CharacterItemRecord((ushort)slot, 3176, 3));
var fullInventorySnapshot = fullInventory.ToDictionary(pair => pair.Key, pair => pair.Value);
Check(!DungeonPickupPlanner.TryPlanItem(
        fullInventory,
        stackGroundItem,
        smokeItemCatalog!,
        uint.MaxValue,
        out _,
        out var fullFailure)
    && fullFailure == InventoryPlacementFailure.Full
    && fullInventory.OrderBy(pair => pair.Key)
        .SequenceEqual(fullInventorySnapshot.OrderBy(pair => pair.Key)),
    "full material and quick-slot regions reject pickup without mutating inventory");
var fullMaterialCategory = Enumerable.Range(73, 32)
    .ToDictionary(
        slot => (ushort)slot,
        slot => new CharacterItemRecord((ushort)slot, 3176, 3));
Check(DungeonPickupPlanner.TryPlanItem(
        fullMaterialCategory,
        stackGroundItem,
        smokeItemCatalog!,
        uint.MaxValue,
        out var quickFallbackPlan,
        out _)
    && quickFallbackPlan.DestinationSlot == CharacterInventoryLayout.QuickSlotStart
    && quickFallbackPlan.Inventory[CharacterInventoryLayout.QuickSlotStart].ItemId == 3176,
    "a full 32-slot category falls back to the first empty quick slot");
var overflowInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [73] = new CharacterItemRecord(73, 3176, 3)
};
Check(DungeonPickupPlanner.TryPlanItem(
        overflowInventory,
        stackGroundItem,
        smokeItemCatalog!,
        uint.MaxValue,
        out var splitStackPlan,
        out _)
    && splitStackPlan.Inventory[73].CountOrValue == 3
    && splitStackPlan.Inventory[74].CountOrValue == 1,
    "a stack at its script limit allocates the next category slot instead of overflowing");
Check(!DungeonPickupPlanner.TryPlanItem(
        stackInventory,
        stackGroundItem,
        smokeItemCatalog!,
        weightLimit: 2,
        out _,
        out var weightFailure)
    && weightFailure == InventoryPlacementFailure.Overweight
    && stackInventory[73].CountOrValue == 2,
    "an overweight pickup is rejected without mutating the source inventory");

var carriedMainInventory = new[]
{
    new CharacterItemRecord(73, 3176, 2)
};
var carriedEquipment = new[]
{
    new CharacterItemRecord(2, 12007, 1, Durability: 24)
};
var carriedCreatureInventory = new[]
{
    new CharacterItemRecord(14, 3176, 3)
};
Check(smokeItemCatalog!.TryGetDefinition(12007, out var weightLimitEquipment)
    && weightLimitEquipment.InventoryLimit == 3_000
    && CharacterInventoryPlanner.CalculateCarriedWeight(
        carriedMainInventory,
        carriedEquipment,
        carriedCreatureInventory,
        smokeItemCatalog) == 15
    && CharacterInventoryPlanner.CalculateCarriedWeightLimit(
        combatStatInventoryLimit: 400_000,
        carriedEquipment,
        smokeItemCatalog) == 43_000
    && CharacterInventoryPlanner.CalculateMainInventoryWeightAllowance(
        combatStatInventoryLimit: 400_000,
        carriedEquipment,
        carriedCreatureInventory,
        smokeItemCatalog) == 42_987,
    "carried weight includes main, equipped and Creature spaces while the x10 combat limit and equipped inventory-limit bonuses use item-script units");
Check(!CharacterInventoryPlanner.CanCarryAdditionalItem(
        carriedMainInventory,
        [new CharacterItemRecord(2, 10_000, 1, Durability: 1_000)],
        carriedCreatureInventory,
        itemId: 3176,
        count: 1,
        combatStatInventoryLimit: 140,
        smokeItemCatalog,
        out var fullCarriedWeight,
        out var nextPickupWeight,
        out var fullCarriedLimit)
    && fullCarriedWeight == 15
    && nextPickupWeight == 1
    && fullCarriedLimit == 14,
    "server-authoritative pickup rejects weight across every carried item space");

var occupiedEquipment = new Dictionary<ushort, CharacterItemRecord>
{
    [9] = new CharacterItemRecord(9, 10_000, 1, Durability: 1_000)
};
Check(CharacterInventoryPlanner.TryPlace(
        occupiedEquipment,
        new CharacterMailAttachmentRecord(14_873, 1, Durability: 1_000),
        smokeItemCatalog!,
        uint.MaxValue,
        out var occupiedEquipmentPlan,
        out _)
    && occupiedEquipmentPlan.DestinationSlot == 10
    && occupiedEquipmentPlan.Inventory[9].ItemId == 10_000
    && occupiedEquipmentPlan.Inventory[10].ItemId == 14_873,
    "item allocation never overwrites an occupied category slot");

var unlimitedDefaultStack = new Dictionary<ushort, CharacterItemRecord>
{
    [76] = new CharacterItemRecord(76, 3_037, 1_000)
};
Check(CharacterInventoryPlanner.TryPlace(
        unlimitedDefaultStack,
        new CharacterMailAttachmentRecord(3_037, 58),
        smokeItemCatalog!,
        uint.MaxValue,
        out var unlimitedDefaultStackPlan,
        out _)
    && unlimitedDefaultStackPlan.DestinationSlot == 76
    && unlimitedDefaultStackPlan.Inventory.Count == 1
    && unlimitedDefaultStackPlan.Inventory[76].CountOrValue == 1_058,
    "a stackable item without an explicit stack limit defaults to Int32.MaxValue");

var misplacedRevivalCoins = new Dictionary<ushort, CharacterItemRecord>
{
    [44] = new CharacterItemRecord(44, 1, 10)
};
Check(CharacterInventoryPlanner.TryPlace(
        misplacedRevivalCoins,
        new CharacterMailAttachmentRecord(1, 10),
        smokeItemCatalog!,
        uint.MaxValue,
        out var revivalCoinPurchasePlan,
        out _)
    && revivalCoinPurchasePlan.DestinationSlot
        == CharacterInventoryLayout.RevivalCoinSlot
    && revivalCoinPurchasePlan.Inventory.Count == 1
    && revivalCoinPurchasePlan.Inventory[CharacterInventoryLayout.RevivalCoinSlot]
        == new CharacterItemRecord(1, 1, 20),
    "revival-coin grants merge into virtual slot one and migrate legacy bag entries");

var warehouseWithdrawalInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [74] = new CharacterItemRecord(74, 3_037, 558)
};
Check(CharacterInventoryPlanner.TryPlace(
        warehouseWithdrawalInventory,
        new CharacterMailAttachmentRecord(3_037, 500),
        smokeItemCatalog!,
        uint.MaxValue,
        preferredEmptySlot: 82,
        out var warehouseWithdrawalPlan,
        out _)
    && warehouseWithdrawalPlan.DestinationSlot == 74
    && warehouseWithdrawalPlan.Inventory.Count == 1
    && warehouseWithdrawalPlan.Inventory[74].CountOrValue == 1_058,
    "a warehouse withdrawal fills an existing compatible stack before using the client target slot");

var limitedWarehouseWithdrawalInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [80] = new CharacterItemRecord(80, 3_176, 2)
};
Check(CharacterInventoryPlanner.TryPlace(
        limitedWarehouseWithdrawalInventory,
        new CharacterMailAttachmentRecord(3_176, 4),
        smokeItemCatalog!,
        uint.MaxValue,
        preferredEmptySlot: 82,
        out var limitedWarehouseWithdrawalPlan,
        out _)
    && limitedWarehouseWithdrawalPlan.Inventory[80].CountOrValue == 3
    && limitedWarehouseWithdrawalPlan.Inventory[82].CountOrValue == 3
    && !limitedWarehouseWithdrawalPlan.Inventory.ContainsKey(73),
    "warehouse withdrawal fills old stacks and places the remainder in the client-selected empty slot");

Check(CharacterCreatureInventoryLayout.CreatureBagEnd == 140
    && CharacterCreatureInventoryLayout.ArtifactBagStart == 140
    && CharacterCreatureInventoryLayout.StackableBagStart == 189
    && CharacterCreatureInventoryLayout.EquippedCreatureSlot == 238
    && CharacterCreatureInventoryLayout.EquippedArtifactRedSlot == 239
    && CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot == 240
    && CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot == 241
    && CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot == 19
    && CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot == 20
    && CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot == 21
    && CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot == 22
    && CharacterCreatureInventoryLayout.Capacity == 242,
    "Creature raw inventory and old-client equipment endpoints preserve the four native equipped-slot mappings");
var creatureExperienceNotification =
    GameProtocolEngine.CreateCreatureGainExperience(7, 0x11223344);
Check(creatureExperienceNotification.Type == GameProtocolEngine.NotificationPacketType
    && creatureExperienceNotification.ProtocolId
        == GameProtocolEngine.CreatureGainExperienceNotification
    && creatureExperienceNotification.Payload.SequenceEqual(
        new byte[] { 7, 0x44, 0x33, 0x22, 0x11 }),
    "DF2008 NOTI 102 serializes only Creature level u8 and cumulative experience u32");
var diedCreatureNotification = GameProtocolEngine.CreateDiedCreature(1);
var revivalCreatureNotification = GameProtocolEngine.CreateRevivalCreature(1);
Check(diedCreatureNotification.Type == GameProtocolEngine.NotificationPacketType
    && diedCreatureNotification.ProtocolId
        == GameProtocolEngine.DiedCreatureNotification
    && diedCreatureNotification.Payload.SequenceEqual(new byte[] { 1, 0 })
    && revivalCreatureNotification.Type
        == GameProtocolEngine.NotificationPacketType
    && revivalCreatureNotification.ProtocolId
        == GameProtocolEngine.RevivalCreatureNotification
    && revivalCreatureNotification.Payload.SequenceEqual(new byte[] { 1, 0 }),
    "DF2008 NOTI 100/107 carry only the area-user id u16 used by the old Creature map object");
var characterExperienceNotification =
    GameProtocolEngine.CreateExperienceGained(
        level: 57,
        cumulativeExperience: 0x12345678,
        extraExperienceA: 0,
        extraExperienceB: 0,
        skillPoints: 5_220);
Check(characterExperienceNotification.Type == GameProtocolEngine.NotificationPacketType
    && characterExperienceNotification.ProtocolId
        == GameProtocolEngine.ExperienceGainedNotification
    && characterExperienceNotification.Payload.SequenceEqual(
        new byte[]
        {
            57,
            0x78, 0x56, 0x34, 0x12,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0x64, 0x14
        }),
    "DF2008 NOTI 37 serializes level, cumulative EXP, two reserved EXP fields and SP");
var creatureEvolutionNotification =
    GameProtocolEngine.CreateCreatureEvolution(2, 0x1234);
Check(creatureEvolutionNotification.Type == GameProtocolEngine.NotificationPacketType
    && creatureEvolutionNotification.ProtocolId
        == GameProtocolEngine.CreatureEvolutionNotification
    && creatureEvolutionNotification.Payload.SequenceEqual(
        new byte[] { 2, 0x34, 0x12 }),
    "DF2008 NOTI 106 serializes evolution Creature id u8 before user id u16");
Check(smokeItemCatalog!.TryGetDefinition(50_003, out var eggDefinition)
    && !CharacterCreatureInventoryLayout.TryGetEquippedSlot(
        eggDefinition,
        out _)
    && smokeItemCatalog.TryGetDefinition(50_000, out var hatchedCreatureDefinition)
    && CharacterCreatureInventoryLayout.TryGetEquippedSlot(
        hatchedCreatureDefinition,
        out var hatchedCreatureSlot)
    && hatchedCreatureSlot == CharacterCreatureInventoryLayout.EquippedCreatureSlot
    && hatchedCreatureDefinition.CreatureSpecies == 15,
    "Creature eggs cannot enter the equipped pet endpoint while hatched Creature instances can");
Check(AdminMailComposer.TryCompose(
        new AdminSendMailRequest(
            "DFLegacy",
            "stackable",
            AttachmentKind: AdminMailAttachmentKind.Item,
            ItemId: 8,
            Quantity: 100),
        smokeItemCatalog,
        out var adminStackableMail,
        out _)
    && adminStackableMail.Attachment is
    {
        ItemId: 8,
        CountOrValue: 100,
        AvatarRemainingSeconds: null
    }
    && AdminMailComposer.TryCompose(
        new AdminSendMailRequest(
            "DFLegacy",
            "avatar",
            AttachmentKind: AdminMailAttachmentKind.Avatar,
            ItemId: 39_000,
            AvatarAbilityIndex: 4),
        smokeItemCatalog,
        out var adminAvatarMail,
        out _)
    && adminAvatarMail.Attachment is
    {
        ItemId: 39_000,
        CountOrValue: 1,
        AvatarRemainingSeconds: 0,
        AvatarAbilityIndex: 4
    }
    && AdminMailComposer.TryCompose(
        new AdminSendMailRequest(
            "DFLegacy",
            "creature egg",
            AttachmentKind: AdminMailAttachmentKind.CreatureEgg,
            ItemId: 50_003),
        smokeItemCatalog,
        out var adminCreatureEggMail,
        out _)
    && adminCreatureEggMail.Attachment is
    {
        ItemId: 50_003,
        CountOrValue: 1,
        SealState: 1
    }
    && !AdminMailComposer.TryCompose(
        new AdminSendMailRequest(
            "DFLegacy",
            "hatched creature",
            AttachmentKind: AdminMailAttachmentKind.Item,
            ItemId: 50_000),
        smokeItemCatalog,
        out _,
        out _),
    "admin mail constructs ordinary stacks, permanent Avatar equipment and sealed Creature eggs while rejecting hatched Creatures");
Check(smokeItemCatalog.TryGetDefinition(50_001, out var artifactDefinition)
    && artifactDefinition.CreatureMinimumLevel == 3
    && artifactDefinition.CreatureExperienceAmountRate == 10m,
    "Creature artifact eligibility and experience-rate tags are cached as typed metadata");
Check(smokeItemCatalog.TryGetDefinition(CreatureHungerPlanner.FoodItemId, out var foodDefinition)
    && foodDefinition.ScriptPath.Equals(
        "stackable/cash/creature/creature_food.stk",
        StringComparison.OrdinalIgnoreCase)
    && foodDefinition.TypeTag == "feed"
    && foodDefinition.InventoryCategory == ItemInventoryCategory.Creature,
    "Creature food item 24 is cached in the Creature stackable space");
var hungerInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [CharacterCreatureInventoryLayout.EquippedCreatureSlot] = new(
        CharacterCreatureInventoryLayout.EquippedCreatureSlot,
        50_000,
        0x11223344,
        CreatureStomach: 2,
        CreatureExperience: 5,
        CreatureLevel: 3,
        CreatureStomachRemainderSeconds: 30),
    [CharacterCreatureInventoryLayout.StackableBagStart] = new(
        CharacterCreatureInventoryLayout.StackableBagStart,
        CreatureHungerPlanner.FoodItemId,
        2)
};
Check(CreatureHungerPlanner.TrySettle(
        hungerInventory,
        elapsedSeconds: 30,
        smokeItemCatalog,
        out var ordinaryHungerPlan)
    && ordinaryHungerPlan.PreviousStomach == 2
    && ordinaryHungerPlan.Stomach == 1
    && ordinaryHungerPlan.RemainderSeconds == 0
    && ordinaryHungerPlan.ConsumedFoodCount == 0
    && ordinaryHungerPlan.Inventory[
        CharacterCreatureInventoryLayout.StackableBagStart].CountOrValue == 2,
    "Creature hunger combines persisted partial seconds and loses one point per 60 fighting seconds without premature feeding");
Check(CreatureHungerPlanner.TrySettle(
        ordinaryHungerPlan.Inventory,
        elapsedSeconds: 60,
        smokeItemCatalog,
        out var autoFeedPlan)
    && autoFeedPlan.PreviousStomach == 1
    && autoFeedPlan.Stomach == CreatureHungerPlanner.FoodRecovery
    && autoFeedPlan.ConsumedFoodCount == 1
    && autoFeedPlan.ChangedFoodSlots.SequenceEqual(
        [CharacterCreatureInventoryLayout.StackableBagStart])
    && autoFeedPlan.Inventory[
        CharacterCreatureInventoryLayout.StackableBagStart].CountOrValue == 1,
    "Creature food item 24 is atomically consumed exactly when hunger would reach zero and restores 30 points");
var noFoodHungerInventory = hungerInventory
    .Where(pair => pair.Value.ItemId != CreatureHungerPlanner.FoodItemId)
    .ToDictionary(pair => pair.Key, pair => pair.Value with
    {
        CreatureStomach = 1,
        CreatureStomachRemainderSeconds = 0
    });
Check(CreatureHungerPlanner.TrySettle(
        noFoodHungerInventory,
        elapsedSeconds: 60,
        smokeItemCatalog,
        out var starvedPlan)
    && starvedPlan.Stomach == 0
    && starvedPlan.RemainderSeconds == 0
    && starvedPlan.ConsumedFoodCount == 0
    && starvedPlan.BecameDead,
    "a Creature reaches zero hunger when no item 24 is available");
Check(CreatureHungerPlanner.TrySettle(
        hungerInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                CreatureStomach = pair.Key
                    == CharacterCreatureInventoryLayout.EquippedCreatureSlot
                        ? (byte)1
                        : pair.Value.CreatureStomach,
                CreatureStomachRemainderSeconds = 0
            }),
        elapsedSeconds: 31 * CreatureHungerPlanner.SecondsPerStomachPoint,
        smokeItemCatalog,
        out var repeatedAutoFeedPlan)
    && repeatedAutoFeedPlan.Stomach == CreatureHungerPlanner.FoodRecovery
    && repeatedAutoFeedPlan.ConsumedFoodCount == 2
    && !repeatedAutoFeedPlan.Inventory.ContainsKey(
        CharacterCreatureInventoryLayout.StackableBagStart),
    "long Creature hunger settlements consume one food at each zero boundary without unbounded per-second iteration");
Check(CharacterCreatureInventoryPlanner.TryHatch(
        new Dictionary<ushort, CharacterItemRecord>
        {
            [5] = new CharacterItemRecord(
                5,
                50_003,
                0x11223344,
                CreatureStomach: 27,
                CreatureExperience: 999,
                CreatureLevel: 8,
                CreatureName: "Egg")
        },
        5,
        smokeItemCatalog!,
        out var hatchPlan,
        out _)
    && hatchPlan.EggItemId == 50_003
    && hatchPlan.CreatureItemId == 50_000
    && hatchPlan.Inventory[5].CountOrValue == 0x11223344
    && hatchPlan.Inventory[5].CreatureStomach == 100
    && hatchPlan.Inventory[5].CreatureStomachRemainderSeconds == 0
    && hatchPlan.Inventory[5].CreatureExperience == 0
    && hatchPlan.Inventory[5].CreatureLevel == 1
    && hatchPlan.Inventory[5].CreatureName == "Test Creature"
    && hatchPlan.Inventory[5].SealState == 0,
    "Creature hatching follows cached sub-type/output-index metadata while preserving the instance uid");
Check(CharacterCreatureInventoryPlanner.TryRename(
        new Dictionary<ushort, CharacterItemRecord>
        {
            [CharacterCreatureInventoryLayout.StackableBagStart] =
                new(
                    CharacterCreatureInventoryLayout.StackableBagStart,
                    50_004,
                    2),
            [CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
                new(
                    CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                    50_000,
                    77,
                    CreatureName: "Old Name",
                    CreatureLevel: 3)
        },
        CharacterCreatureInventoryLayout.StackableBagStart,
        "Rabbit",
        smokeItemCatalog!,
        out var renamePlan,
        out _)
    && renamePlan.CreatureUid == 77
    && renamePlan.CreatureName == "Rabbit"
    && renamePlan.Inventory[
        CharacterCreatureInventoryLayout.StackableBagStart].CountOrValue == 1
    && renamePlan.Inventory[
        CharacterCreatureInventoryLayout.EquippedCreatureSlot].CreatureName == "Rabbit",
    "Creature rename consumes one rename card and persists the name on the equipped Creature instance");
var normalizedEggInventory = CharacterCreatureInventoryPlanner.Normalize(
[
    new CharacterItemRecord(
        Slot: 5,
        ItemId: 50_003,
        CountOrValue: 0x11223344,
        CreatureStomach: 100,
        CreatureExperience: 999,
        CreatureLevel: 8,
        CreatureName: "Incorrectly initialized egg",
        CreatureNoCharge: false)
],
    smokeItemCatalog);
var normalizedEgg = normalizedEggInventory.Inventory[5];
Check(normalizedEgg.CountOrValue == 0x11223344
    && normalizedEgg.SealState == 1
    && normalizedEgg.CreatureStomach == 100
    && normalizedEgg.CreatureExperience == 999
    && normalizedEgg.CreatureLevel == 8
    && normalizedEgg.CreatureName == "Incorrectly initialized egg"
    && normalizedEgg.CreatureNoCharge == false,
    "Creature normalization preserves the egg uid and status object required by the old Creature window");
var normalizedCreatureInventory = CharacterCreatureInventoryPlanner.Normalize(
[
    new CharacterItemRecord(
        Slot: 200,
        ItemId: 50_000,
        CountOrValue: 77,
        CreatureLevel: 5,
        CreatureName: "First"),
    new CharacterItemRecord(
        Slot: 201,
        ItemId: 50_000,
        CountOrValue: 77,
        CreatureLevel: 2,
        CreatureName: "Second"),
    new CharacterItemRecord(
        Slot: CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
        ItemId: 50_001,
        CountOrValue: 1),
    new CharacterItemRecord(
        Slot: CharacterCreatureInventoryLayout.StackableBagStart,
        ItemId: 50_002,
        CountOrValue: 1_250)
],
    smokeItemCatalog!);
var normalizedCreaturePets = normalizedCreatureInventory.Inventory.Values
    .Where(item => item.ItemId == 50_000)
    .OrderBy(item => item.Slot)
    .ToArray();
Check(normalizedCreatureInventory.OverflowItems.Count == 0
    && normalizedCreaturePets.Length == 2
    && normalizedCreaturePets.All(item => item.Slot < 140)
    && normalizedCreaturePets.Select(item => item.CountOrValue).Distinct().Count() == 2
    && normalizedCreaturePets.All(item => item.CreatureStomach == 100)
    && normalizedCreatureInventory.Inventory[
        CharacterCreatureInventoryLayout.EquippedArtifactRedSlot].ItemId == 50_001
    && normalizedCreatureInventory.Inventory[
        CharacterCreatureInventoryLayout.StackableBagStart].CountOrValue == 1_000
    && normalizedCreatureInventory.Inventory[
        CharacterCreatureInventoryLayout.StackableBagStart + 1].CountOrValue == 250,
    "Creature normalization assigns unique pet uids, keeps fixed Artifact equipment, and splits feed at its cached stack limit");
var sortedCreatureInventory = CharacterCreatureInventorySorter.Sort(
    new Dictionary<ushort, CharacterItemRecord>
    {
        [3] = new CharacterItemRecord(3, 50_000, 9, CreatureLevel: 3),
        [8] = new CharacterItemRecord(8, 50_000, 4, CreatureLevel: 2),
        [145] = new CharacterItemRecord(145, 50_001, 3),
        [150] = new CharacterItemRecord(150, 50_001, 1),
        [195] = new CharacterItemRecord(195, 50_002, 9),
        [200] = new CharacterItemRecord(200, 50_002, 4),
        [238] = new CharacterItemRecord(238, 50_000, 20, CreatureLevel: 7)
    });
Check(sortedCreatureInventory[0].CountOrValue == 4
    && sortedCreatureInventory[1].CountOrValue == 9
    && sortedCreatureInventory[140].CountOrValue == 1
    && sortedCreatureInventory[141].CountOrValue == 3
    && sortedCreatureInventory[189].CountOrValue == 4
    && sortedCreatureInventory[190].CountOrValue == 9
    && sortedCreatureInventory[238].CreatureLevel == 7,
    "Creature sorting compacts each native region independently and never moves equipped slots 238 through 241");

var renameCreatureParser = typeof(EntranceService).GetMethod(
    "TryReadRenameCreature",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
object?[] renameCreatureArguments =
[
    new byte[]
    {
        0x0E, 0x00, 0xBE, 0x00, 0x07, 0x04, 0x00, 0x00, 0x00,
        0xCD, 0xC3, 0xD7, 0xD3
    },
    (ushort)0,
    (byte)0,
    Array.Empty<byte>()
];
Check(renameCreatureParser is not null
    && (bool)renameCreatureParser.Invoke(null, renameCreatureArguments)!
    && (ushort)renameCreatureArguments[1]! == 190
    && (byte)renameCreatureArguments[2]! == 7
    && ((byte[])renameCreatureArguments[3]!).SequenceEqual(
        new byte[] { 0xCD, 0xC3, 0xD7, 0xD3 }),
    "DFLegacy Creature rename parser reads card slot 190, item space 7 and the GBK name from CMD 103");

var sortItemParser = typeof(EntranceService).GetMethod(
    "TryReadSortItem",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
object?[] sortItemArguments = [new byte[] { 0x0E, 0x00, 0x00 }, (byte)0xFF];
Check(sortItemParser is not null
    && (bool)sortItemParser.Invoke(null, sortItemArguments)!
    && (byte)sortItemArguments[1]! == 0,
    "DFLegacy sort-item parser reads the item-space byte after the request sequence");

var unsortedMainInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [3] = new CharacterItemRecord(3, 1_055, 9),
    [12] = new CharacterItemRecord(12, 10_000, 1, Durability: 1_000),
    [15] = new CharacterItemRecord(15, 14_873, 1, Durability: 1_000),
    [80] = new CharacterItemRecord(80, 3_176, 2),
    [84] = new CharacterItemRecord(84, 3_037, 1),
    [86] = new CharacterItemRecord(86, 3_037, 58)
};
var sortedMainInventory = CharacterInventorySorter.SortMain(
    unsortedMainInventory,
    smokeItemCatalog!);
Check(sortedMainInventory[3].ItemId == 1_055
    && sortedMainInventory[9].ItemId == 14_873
    && sortedMainInventory[10].ItemId == 10_000
    && sortedMainInventory[73].ItemId == 3_037
    && sortedMainInventory[73].CountOrValue == 1
    && sortedMainInventory[74].ItemId == 3_037
    && sortedMainInventory[74].CountOrValue == 58
    && sortedMainInventory[75].ItemId == 3_176
    && sortedMainInventory.Count == unsortedMainInventory.Count,
    "main-inventory sorting keeps quick slots, orders equipment by rarity/id, and does not merge stacks");

var sortedWarehouse = CharacterInventorySorter.SortWarehouse(
    new Dictionary<ushort, CharacterItemRecord>
    {
        [1] = new CharacterItemRecord(1, 3_176, 2),
        [3] = new CharacterItemRecord(3, 10_000, 1, Durability: 1_000),
        [5] = new CharacterItemRecord(5, 1_055, 7),
        [6] = new CharacterItemRecord(6, 3_037, 1),
        [7] = new CharacterItemRecord(7, 3_037, 58)
    },
    smokeItemCatalog!,
    capacity: 8);
Check(sortedWarehouse.Keys.SequenceEqual(new ushort[] { 0, 1, 2, 3, 4 })
    && sortedWarehouse[0].ItemId == 10_000
    && sortedWarehouse[1].ItemId == 1_055
    && sortedWarehouse[2].ItemId == 3_037
    && sortedWarehouse[2].CountOrValue == 1
    && sortedWarehouse[3].ItemId == 3_037
    && sortedWarehouse[3].CountOrValue == 58
    && sortedWarehouse[4].ItemId == 3_176,
    "character-warehouse sorting compacts by item category/id without changing stack boundaries");

var nonFullStackAfterEmptySlot = new Dictionary<ushort, CharacterItemRecord>
{
    [80] = new CharacterItemRecord(80, 3_176, 2)
};
Check(CharacterInventoryPlanner.TryPlace(
        nonFullStackAfterEmptySlot,
        new CharacterMailAttachmentRecord(3_176, 4),
        smokeItemCatalog!,
        uint.MaxValue,
        out var multiStackPlan,
        out _)
    && multiStackPlan.DestinationSlot == 80
    && multiStackPlan.Inventory[80].CountOrValue == 3
    && multiStackPlan.Inventory[73].CountOrValue == 3,
    "automatic allocation fills every partial stack before selecting an empty slot");

var normalizedLayout = CharacterInventoryPlanner.Normalize(
    [
        new CharacterItemRecord(9, 10_000, 1, Durability: 1_000),
        new CharacterItemRecord(9, 3_037, 1),
        new CharacterItemRecord(0, 3_037, 1)
    ],
    smokeItemCatalog!);
Check(normalizedLayout.Changed
    && normalizedLayout.OverflowItems.Count == 0
    && normalizedLayout.Inventory.Count == 2
    && normalizedLayout.Inventory[9].ItemId == 10_000
    && normalizedLayout.Inventory[73].ItemId == 3_037
    && normalizedLayout.Inventory[73].CountOrValue == 2,
    "login normalization repairs reserved, wrong-category and duplicate slots without losing items");
var normalizedRevivalCoins = CharacterInventoryPlanner.Normalize(
    [
        new CharacterItemRecord(41, 1, 7),
        new CharacterItemRecord(44, 1, 10)
    ],
    smokeItemCatalog!);
Check(normalizedRevivalCoins.Changed
    && normalizedRevivalCoins.OverflowItems.Count == 0
    && normalizedRevivalCoins.Inventory.Count == 1
    && normalizedRevivalCoins.Inventory[CharacterInventoryLayout.RevivalCoinSlot]
        == new CharacterItemRecord(1, 1, 17),
    "login normalization consolidates old revival-coin bag records into virtual slot one");
var normalizedWarehouse = CharacterInventoryPlanner.NormalizeWarehouse(
    [
        new CharacterItemRecord(0, 3_176, 7),
        new CharacterItemRecord(0, 10_000, 1, Durability: 1_000)
    ],
    smokeItemCatalog!,
    capacity: 2);
Check(normalizedWarehouse.Changed
    && normalizedWarehouse.Warehouse.Count == 2
    && normalizedWarehouse.Warehouse.Values.All(item => item.CountOrValue == 3)
    && normalizedWarehouse.OverflowItems.Sum(item => item.CountOrValue) == 2
    && normalizedWarehouse.Warehouse.Keys.Distinct().Count() == 2,
    "login warehouse normalization splits oversized stacks and mails every full-space remainder");
var warehouseWithoutAvatar = CharacterInventoryPlanner.NormalizeWarehouse(
    [new CharacterItemRecord(0, 39_000, 1, Durability: 1_000)],
    smokeItemCatalog!,
    capacity: 8);
Check(warehouseWithoutAvatar.Changed
    && warehouseWithoutAvatar.Warehouse.Count == 0
    && warehouseWithoutAvatar.OverflowItems.SingleOrDefault() is
    {
        ItemId: 39_000,
        CountOrValue: 1,
        Durability: 1_000,
        InstanceId: var overflowAvatarInstanceId
    }
    && overflowAvatarInstanceId != Guid.Empty,
    "login warehouse normalization rejects avatar items without losing them");
Check(!CharacterInventoryPlanner.TryPlace(
        new Dictionary<ushort, CharacterItemRecord>(),
        new CharacterMailAttachmentRecord(39_000, 1, Durability: 1_000),
        smokeItemCatalog!,
        uint.MaxValue,
        out _,
        out var mainAvatarFailure)
    && mainAvatarFailure == InventoryPlacementFailure.UnsupportedCategory,
    "Avatar items cannot fall through into a main-inventory quick slot");
Check(CharacterAvatarInventoryPlanner.TryPlace(
        new Dictionary<ushort, CharacterItemRecord>(),
        new CharacterMailAttachmentRecord(39_000, 1, Durability: 1_000),
        smokeItemCatalog!,
        preferredEmptySlot: 104,
        out var avatarPlacement,
        out _)
    && avatarPlacement.DestinationSlot == 104
    && avatarPlacement.Inventory[104].ItemId == 39_000
    && avatarPlacement.Inventory[104].Durability == 0
    && avatarPlacement.Inventory[104].AvatarRemainingSeconds == 0
    && avatarPlacement.Inventory[104].AvatarAbilityIndex == 0,
    "Avatar allocation honors an empty client-selected slot in the 105-slot bag");
var normalizedAvatarInventory = CharacterAvatarInventoryPlanner.Normalize(
    [new CharacterItemRecord(0, 39_000, 2)],
    smokeItemCatalog!);
Check(normalizedAvatarInventory.Changed
    && normalizedAvatarInventory.OverflowItems.Count == 0
    && normalizedAvatarInventory.Inventory.Count == 2
    && normalizedAvatarInventory.Inventory[0].CountOrValue == 1
    && normalizedAvatarInventory.Inventory[1].CountOrValue == 1
    && normalizedAvatarInventory.Inventory.Values.All(item =>
        item.Durability == 0
        && item.AvatarRemainingSeconds == 0
        && item.AvatarAbilityIndex == 0)
    && smokeItemCatalog.TryGetDefinition(39_000, out var hairAvatar)
    && CharacterAvatarInventoryLayout.TryGetWornSlot(hairAvatar, out var hairSlot)
    && hairSlot == 1
    && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(hairAvatar, 1)
    && !CharacterAvatarInventoryLayout.IsCompatibleWornSlot(hairAvatar, 0),
    "Avatar login normalization splits instances and maps hair Avatar only to worn slot one");
await ItemIdentitySmokeTests.RunAsync(smokeItemCatalog!, Check);
Check(smokeItemCatalog!.TryGetDefinition(3_037, out var freeDefinition)
    && freeDefinition.CanDropOnGround
    && smokeItemCatalog.TryGetDefinition(3_176, out var sealingDefinition)
    && sealingDefinition.CanDropOnGround
    && smokeItemCatalog.TryGetDefinition(1_055, out var tradeDefinition)
    && !tradeDefinition.CanDropOnGround,
    "only free and sealing attachment types use dungeon overflow ground drops");

var reinforcedWireItem = CharacterItemWireProjection.Project(
    new CharacterItemRecord(
        CharacterInventoryLayout.EquipmentSlotStart,
        10_000,
        CountOrValue: 1,
        State: 5,
        Durability: 777,
        EquipmentQualitySeed: 987_654_321),
    smokeItemCatalog);
var stackableWireItem = CharacterItemWireProjection.Project(
    new CharacterItemRecord(
        CharacterInventoryLayout.MaterialSlotStart,
        3_176,
        CountOrValue: 2,
        State: 4),
    smokeItemCatalog);
Check(reinforcedWireItem is
        { AddInfo: 987_654_321, ItemAttr: 5, Durability: 777 }
    && stackableWireItem is { AddInfo: 2, ItemAttr: 4 },
    "ordinary equipment projects quality into add-info and reinforcement into item-attr while stackables keep their quantity");

var dropItemNotification = GameProtocolEngine.CreateDropItem(
    1,
    474,
    234,
    new GameDungeonDrop(7, 3_176, 2, 0, 1, ItemAttr: 4));
Check(dropItemNotification.ProtocolId == GameProtocolEngine.DropItemNotification
    && dropItemNotification.Payload.SequenceEqual(
    new byte[]
    {
        0x01, 0x00, 0xDA, 0x01, 0xEA, 0x00,
        0x07, 0x00, 0x68, 0x0C, 0x04,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00
    }),
    "DROP_ITEM keeps the DFLegacy item-attr byte before stack count/add-info");
var capturedDropItemBody = Convert.FromHexString(
    "1E000001F000001A0001000000");
Check(GameProtocolEngine.DropItemCommand == 50
    && GameProtocolEngine.TryParseDropItemCommand(
        capturedDropItemBody,
        out var capturedDropItem)
    && capturedDropItem.X == 256
    && capturedDropItem.Y == 240
    && capturedDropItem.ItemSpace == 0
    && capturedDropItem.Slot == 26
    && capturedDropItem.Count == 1,
    "CMD 50 parses the captured DFLegacy dungeon item-drop request");
var dropItemReply = GameProtocolEngine.CreateDropItemReply(0, 26, 1);
Check(dropItemReply.ProtocolId == GameProtocolEngine.DropItemCommand
    && dropItemReply.Payload.SequenceEqual(
        new byte[] { 1, 0, 0x1A, 0, 1, 0, 0, 0 }),
    "CMD 50 success echoes item space, source slot and removed count");
var dropItemError = GameProtocolEngine.CreateDropItemErrorReply(23, 0);
Check(dropItemError.ProtocolId == GameProtocolEngine.DropItemCommand
    && dropItemError.Payload.SequenceEqual(new byte[] { 0, 23, 0 }),
    "CMD 50 failure includes the item space needed to release the client lock");

var droppableInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [CharacterInventoryLayout.MaterialSlotStart] = new(
        CharacterInventoryLayout.MaterialSlotStart,
        3_176,
        3,
        State: 5,
        Durability: 7,
        SealState: 3)
};
Check(DungeonItemDropPlanner.TryPlan(
        droppableInventory,
        itemSpace: 0,
        CharacterInventoryLayout.MaterialSlotStart,
        count: 2,
        smokeItemCatalog!,
        out var dungeonDropPlan,
        out var dungeonDropFailure)
    && dungeonDropFailure == DungeonItemDropFailure.None
    && dungeonDropPlan.Inventory[
        CharacterInventoryLayout.MaterialSlotStart].CountOrValue == 1
    && dungeonDropPlan.DroppedItem.CountOrValue == 2
    && dungeonDropPlan.DroppedItem.State == 5
    && dungeonDropPlan.DroppedItem.Durability == 7
    && dungeonDropPlan.DroppedItem.SealState == 3
    && droppableInventory[
        CharacterInventoryLayout.MaterialSlotStart].CountOrValue == 3,
    "dungeon drops remove only the requested stack count without mutating source data");
var rejectedDropInventory = new Dictionary<ushort, CharacterItemRecord>
{
    [CharacterInventoryLayout.ConsumableSlotStart] = new(
        CharacterInventoryLayout.ConsumableSlotStart,
        1_055,
        1)
};
Check(!DungeonItemDropPlanner.TryPlan(
        rejectedDropInventory,
        itemSpace: 0,
        CharacterInventoryLayout.ConsumableSlotStart,
        count: 1,
        smokeItemCatalog!,
        out _,
        out var rejectedDungeonDropFailure)
    && rejectedDungeonDropFailure == DungeonItemDropFailure.NotTradeable
    && rejectedDropInventory.ContainsKey(
        CharacterInventoryLayout.ConsumableSlotStart),
    "dungeon drops reject attach types other than free and sealing");

var playerDropGroundState = new DungeonGroundItemState();
var generatedEquipmentGroundItem = playerDropGroundState.Add(
    new DungeonGeneratedDrop(false, 10_000, 1),
    ownerUserId: 1,
    sourceMonsterId: 2,
    roomX: 2,
    roomY: 3);
Check(generatedEquipmentGroundItem.GetClientAddInfo(smokeItemCatalog!) == 1
    && generatedEquipmentGroundItem.GetClientDurability(smokeItemCatalog!) == 1_000,
    "a generated equipment drop sends DF60A1's initial quality seed one");
var playerDropGroundItem = playerDropGroundState.AddPlayerDropped(
    new CharacterItemRecord(
        ushort.MaxValue,
        10_000,
        CountOrValue: 1,
        State: 5,
        Durability: 7,
        SealState: 3,
        EquipmentQualitySeed: 456),
    ownerUserId: 1,
    roomX: 2,
    roomY: 3);
Check(playerDropGroundItem.PreservedItem is
        {
            State: 5,
            Durability: 7,
            SealState: 3,
            EquipmentQualitySeed: 456
        }
    && playerDropGroundItem.GetClientAddInfo(smokeItemCatalog!) == 456
    && playerDropGroundItem.GetClientItemAttr(smokeItemCatalog!) == 5
    && playerDropGroundItem.GetClientDurability(smokeItemCatalog!) == 7
    && DungeonPickupPlanner.TryPlanItem(
        new Dictionary<ushort, CharacterItemRecord>(),
        playerDropGroundItem,
        smokeItemCatalog!,
        uint.MaxValue,
        out var playerDropPickupPlan,
        out _)
    && playerDropPickupPlan.Inventory.Values.Single() is
        {
            CountOrValue: 1,
            State: 5,
            Durability: 7,
            SealState: 3,
            EquipmentQualitySeed: 456
        },
    "a quality-456 +5 player item keeps quality, reinforcement, durability and seal state through a ground round trip");
Check(GameProtocolEngine.CreateEnableClearDungeon().Payload.Length == 0,
    "ENABLE_CLEAR_DUNGEON has no payload in the DFLegacy client");
var normalBossActor = new GameDungeonMonster(
    0,
    0,
    100,
    10,
    GameDungeonMonsterTypes.Boss);
var normalActor = new GameDungeonMonster(
    1,
    1,
    101,
    10,
    GameDungeonMonsterTypes.Normal);
var normalMapBossDecision = DungeonRoomClearRules.Evaluate(
    CreateDungeonRoom(DungeonMapType.Normal, normalBossActor, normalActor),
    normalBossActor,
    new HashSet<ushort> { normalActor.UniqueId });
var dummyMapBossDecision = DungeonRoomClearRules.Evaluate(
    CreateDungeonRoom(DungeonMapType.Dummy, normalBossActor, normalActor),
    normalBossActor,
    new HashSet<ushort> { normalActor.UniqueId });
Check(!normalMapBossDecision.ShouldEnableClear
        && !dummyMapBossDecision.ShouldEnableClear,
    "normal and dummy maps ignore boss actors for server clear business");

var secondBossActor = new GameDungeonMonster(
    1,
    1,
    102,
    10,
    GameDungeonMonsterTypes.Boss);
var trailingNormalActor = normalActor with { MapListIndex = 2, UniqueId = 2 };
var secondTrailingNormalActor = normalActor with { MapListIndex = 3, UniqueId = 3 };
var multiBossMap = CreateDungeonRoom(
    DungeonMapType.Boss,
    normalBossActor,
    secondBossActor,
    trailingNormalActor,
    secondTrailingNormalActor);
var firstBossDecision = DungeonRoomClearRules.Evaluate(
    multiBossMap,
    normalBossActor,
    new HashSet<ushort>
    {
        secondBossActor.UniqueId,
        trailingNormalActor.UniqueId,
        secondTrailingNormalActor.UniqueId
    });
var lastBossDecision = DungeonRoomClearRules.Evaluate(
    multiBossMap,
    secondBossActor,
    new HashSet<ushort>
    {
        trailingNormalActor.UniqueId,
        secondTrailingNormalActor.UniqueId
    });
Check(!firstBossDecision.ShouldEnableClear,
    "a boss map waits for every real boss actor before clearing");
Check(lastBossDecision.Reason == DungeonRoomClearReason.LastBossDefeated
        && !lastBossDecision.ShouldEnableClear
        && lastBossDecision.ForcedMonsterIds.SequenceEqual(
            new[]
            {
                trailingNormalActor.UniqueId,
                secondTrailingNormalActor.UniqueId
            }),
    "the last real boss selects the remaining monsters but waits for their native death confirmations");
Check(bossDieCheckComplete.ProtocolId == GameProtocolEngine.BossDieCheckNotification
        && bossDieCheckComplete.Payload.SequenceEqual(new byte[] { 1, 1, 4, 0 }),
    "boss cascade uses NOTI 127 so the solo client runs its native ALLDIE_MONSTER path");
var finalCascadeDeathDecision = DungeonRoomClearRules.Evaluate(
    multiBossMap,
    secondTrailingNormalActor,
    new HashSet<ushort>());
Check(finalCascadeDeathDecision.Reason
        == DungeonRoomClearReason.AllMonstersDefeated
        && finalCascadeDeathDecision.ShouldEnableClear,
    "the boss room becomes clear only after every cascaded monster confirms normal death");

var noBossMap = CreateDungeonRoom(
    DungeonMapType.Boss,
    normalActor with { MapListIndex = 0, UniqueId = 0 },
    trailingNormalActor);
var noBossPartialDecision = DungeonRoomClearRules.Evaluate(
    noBossMap,
    noBossMap.Monsters[0],
    new HashSet<ushort> { trailingNormalActor.UniqueId });
var noBossCompleteDecision = DungeonRoomClearRules.Evaluate(
    noBossMap,
    trailingNormalActor,
    new HashSet<ushort>());
Check(!noBossPartialDecision.ShouldEnableClear
        && noBossCompleteDecision.Reason
            == DungeonRoomClearReason.AllMonstersDefeated
        && noBossCompleteDecision.ForcedMonsterIds.Length == 0,
    "a boss map without a real boss requires every monster to die normally");
var playResult = GameProtocolEngine.CreatePlayResult(1, 25, 31, 33);
Check(playResult.ProtocolId == GameProtocolEngine.PlayResultNotification
    && playResult.Payload.SequenceEqual(
        new byte[] { 1, 0, 0, 0, 0 }),
    "PLAY_RESULT follows the five-byte DFLegacy score-flag layout");
var clearExperienceBreakdown = new GameDungeonClearExperienceBreakdown
{
    BaseExperience = 1_000,
    RankBonus = 200,
    PartyBonus = 300,
    AvatarBonus = 40,
    EventBonus = 50,
    BlackDiamondBonus = 60,
    ChannelBonus = 70,
    MentorBonus = 80,
    CreatureBonus = 90
};
var clearExperiencePacket = GameProtocolEngine.CreateClearDungeonReward(
    1,
    100,
    experience: clearExperienceBreakdown);
var clearExperienceWireValues = Enumerable.Range(0, 9)
    .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
        clearExperiencePacket.Payload.AsSpan(1 + index * sizeof(uint), sizeof(uint))))
    .ToArray();
Check(clearExperienceBreakdown.TotalExperience == 1_890
        && clearExperienceWireValues.SequenceEqual(
            new uint[] { 1_300, 200, 300, 40, 50, 60, 70, 80, 90 }),
    "CLEAR_DUNGEON_REWARD writes all nine EXP fields and encodes wire base as base plus party bonus");
var clearDungeonReward = GameProtocolEngine.CreateClearDungeonReward(
    1,
    100,
    freeGoldAmount: 345,
    freeItemId: 12054,
    goldCardEnabled: true,
    goldItemId: 12055);
Check(clearDungeonReward.ProtocolId == GameProtocolEngine.ClearDungeonRewardNotification
    && clearDungeonReward.Payload.Length == 102
    && clearDungeonReward.Payload[0] == 1
    && clearDungeonReward.Payload[38] == 2
    && BinaryPrimitives.ReadUInt16LittleEndian(
        clearDungeonReward.Payload.AsSpan(39, sizeof(ushort))) == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        clearDungeonReward.Payload.AsSpan(41, sizeof(uint))) == 345
    && BinaryPrimitives.ReadUInt16LittleEndian(
        clearDungeonReward.Payload.AsSpan(45, sizeof(ushort))) == 12054
    && BinaryPrimitives.ReadUInt32LittleEndian(
        clearDungeonReward.Payload.AsSpan(47, sizeof(uint))) == 0
    && clearDungeonReward.Payload[51] == 2
    && clearDungeonReward.Payload[64] == 2
    && clearDungeonReward.Payload[77] == 2
    && BinaryPrimitives.ReadUInt32LittleEndian(
        clearDungeonReward.Payload.AsSpan(90, sizeof(uint))) == 100
    && clearDungeonReward.Payload.AsSpan(94, 8).ToArray().All(value => value == 0),
    "CLEAR_DUNGEON_REWARD keeps four visible columns, puts gold before optional free equipment, and hides paid rewards");
var blankClearDungeonReward = GameProtocolEngine.CreateClearDungeonReward(
    1,
    100,
    freeGoldAmount: 345,
    goldCardEnabled: false);
Check(blankClearDungeonReward.Payload.Length == 78
    && blankClearDungeonReward.Payload[38] == 1
    && BinaryPrimitives.ReadUInt16LittleEndian(
        blankClearDungeonReward.Payload.AsSpan(39, sizeof(ushort))) == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        blankClearDungeonReward.Payload.AsSpan(41, sizeof(uint))) == 345
    && blankClearDungeonReward.Payload[45] == 1
    && blankClearDungeonReward.Payload[52] == 1
    && blankClearDungeonReward.Payload[59] == 1
    && blankClearDungeonReward.Payload.AsSpan(66, 12).ToArray().All(value => value == 0),
    "free cards always retain four visible gold records when no equipment was rolled");
var blackDiamondClearDungeonReward = GameProtocolEngine.CreateClearDungeonReward(
    1,
    100,
    freeItemId: 12054,
    goldItemId: 12055,
    premiumCardEnabled: true,
    premiumItemId: 12056);
Check(blackDiamondClearDungeonReward.Payload.Length == 114
    && blackDiamondClearDungeonReward.Payload.AsSpan(94, 4).ToArray().All(value => value == 0)
    && blackDiamondClearDungeonReward.Payload[98] == 2
    && BinaryPrimitives.ReadUInt16LittleEndian(
        blackDiamondClearDungeonReward.Payload.AsSpan(99, sizeof(ushort))) == 0
    && BinaryPrimitives.ReadUInt32LittleEndian(
        blackDiamondClearDungeonReward.Payload.AsSpan(101, sizeof(uint))) == 0
    && BinaryPrimitives.ReadUInt16LittleEndian(
        blackDiamondClearDungeonReward.Payload.AsSpan(105, sizeof(ushort))) == 12056
    && BinaryPrimitives.ReadUInt32LittleEndian(
        blackDiamondClearDungeonReward.Payload.AsSpan(107, sizeof(uint))) == 0
    && blackDiamondClearDungeonReward.Payload.AsSpan(111, 3).ToArray().All(value => value == 0),
    "black diamond uses a zero-gold first record and the automatic equipment bonus as its second record");
var scoreScrollState = GameProtocolEngine.CreateScoreScrollStateReply();
Check(scoreScrollState.Type == GameProtocolEngine.CommandPacketType
    && scoreScrollState.ProtocolId == GameProtocolEngine.ScoreScrollStateCommand
    && scoreScrollState.Payload.SequenceEqual(new byte[] { 1 }),
    "SCORE_SCROLL_STATE acknowledges the DFLegacy card-start request");
var cardSelectRightState = GameProtocolEngine.CreateCardSelectRightState();
Check(cardSelectRightState.Type == GameProtocolEngine.CommandPacketType
    && cardSelectRightState.ProtocolId == GameProtocolEngine.CardSelectRightStateCommand
    && cardSelectRightState.Payload.SequenceEqual(
        new byte[] { 1, 1, 0, 255, 255, 255, 255, 255, 255 }),
    "CARD_SELECT_RIGHT_STATE returns success followed by all four DFLegacy party-slot rights");
var selectCardReply = GameProtocolEngine.CreateSelectCardReply(
    freeCardIndex: 3,
    goldCardIndex: null);
Check(selectCardReply.Type == GameProtocolEngine.CommandPacketType
    && selectCardReply.ProtocolId == GameProtocolEngine.SelectCardCommand
    && selectCardReply.Payload.SequenceEqual(
        new byte[]
        {
            1,
            255, 255, 0,
            255, 255, 0,
            255, 255, 0,
            0, 255, 0
        }),
    "SELECT_CARD returns success followed by the complete DFLegacy free-card ownership snapshot");
var goldCardReply = GameProtocolEngine.CreateSelectCardReply(
    freeCardIndex: 3,
    goldCardIndex: 2,
    goldRewardRevealed: true,
    goldRewardItemId: 12055);
Check(goldCardReply.Payload.SequenceEqual(
        new byte[]
        {
            1,
            255, 255, 0,
            255, 255, 0,
            255, 0, 2,
            0, 0, 0, 0, 0, 0,
            0x17, 0x2F, 0, 0, 0, 0,
            0, 255, 0
        }),
    "SELECT_CARD reveals paid equipment after the required zero-gold group-3 record");
var blankGoldCardReply = GameProtocolEngine.CreateSelectCardReply(
    freeCardIndex: 3,
    goldCardIndex: 2,
    goldRewardRevealed: true);
Check(blankGoldCardReply.Payload.SequenceEqual(
        new byte[]
        {
            1,
            255, 255, 0,
            255, 255, 0,
            255, 0, 1, 0, 0, 0, 0, 0, 0,
            0, 255, 0
        }),
    "a blank paid card retains the zero-gold record expected by the client renderer");
var automaticFreeCardIndex = ClearRewardCardPlanner.FindLowestAvailableFreeCardIndex(
    GameProtocolEngine.ClearRewardCardColumnCount,
    [0, 2]);
Check(automaticFreeCardIndex == 1,
    "clear-reward timeout selects the lowest free card column not already occupied");
var noAutomaticFreeCardIndex = ClearRewardCardPlanner.FindLowestAvailableFreeCardIndex(
    GameProtocolEngine.ClearRewardCardColumnCount,
    [0, 1, 2, 3]);
Check(!noAutomaticFreeCardIndex.HasValue,
    "clear-reward timeout does not invent a card when all four free columns are occupied");
var eplpReply = GameProtocolEngine.CreateEplpReply(1, 2);
Check(eplpReply.Type == GameProtocolEngine.CommandPacketType
    && eplpReply.ProtocolId == GameProtocolEngine.EplpCommand
    && eplpReply.Payload.SequenceEqual(new byte[] { 1, 1, 2 }),
    "EPLP confirms the DFLegacy return-to-town state and option");

var challenge = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
var checkConnection = GameProtocolEngine.CreateCheckConnection(challenge);
Check(checkConnection.Type == 0 && checkConnection.ProtocolId == 0, "check-connection is notification zero");
Check(checkConnection.TotalLength == 22, "check-connection has six-byte header plus sixteen-byte challenge");
var encodedCheckConnection = checkConnection.Encode();
Check(encodedCheckConnection.AsSpan(0, 6).SequenceEqual(new byte[] { 0, 0, 22, 0, 0, 0 }), "notification header is exact");
var decodedChallenge = encodedCheckConnection.AsSpan(6).ToArray();
GamePayloadCipher.DecryptServerPayload(decodedChallenge);
Check(decodedChallenge.SequenceEqual(challenge), "notification transform preserves the exact challenge");

var checkCommand = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.CheckConnectionCommand,
    0,
    [0, 0, .. challenge]);
var channelInfo = GameProtocolEngine.Handle(checkCommand, challenge);
Check(channelInfo?.Type == GameProtocolEngine.NotificationPacketType,
    "check-connection command produces a notification");
Check(channelInfo?.ProtocolId == GameProtocolEngine.ChannelInfoNotification,
    "check-connection command produces CHANNELINFO notification one");
Check(channelInfo?.Payload.Length == 58,
    "CHANNELINFO contains the complete DFLegacy channel descriptor");
if (channelInfo is not null)
{
    var channelInfoPayload = channelInfo.Payload.AsSpan();
    var channelNameLength = BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload);
    Check(channelNameLength == 15
        && channelInfoPayload.Slice(4, 15).SequenceEqual("Local Channel 1"u8),
        "CHANNELINFO starts with the length-prefixed display name");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(19, 4)) == 1
        && BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(23, 4)) == 1,
        "CHANNELINFO enables the channel type and successful result");
    Check(channelInfoPayload[27] == 1 && channelInfoPayload[28] == 1,
        "CHANNELINFO selects server one and channel one");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(33, 4)) == 1
        && BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(37, 4)) == 9
        && channelInfoPayload.Slice(41, 9).SequenceEqual("127.0.0.1"u8),
        "CHANNELINFO carries one length-prefixed loopback host");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(50, 4)) == 2311
        && BinaryPrimitives.ReadUInt32LittleEndian(channelInfoPayload.Slice(54, 4)) == 2311,
        "CHANNELINFO advertises both DFLegacy datagram ports");
}

var clientSeed = 0x4321u;
var loginPlainBody = new byte[] { 0x78, 0x56, 4, 0, 0, 0, (byte)'t', (byte)'e', (byte)'s', (byte)'t' };
var metadataLoginBody = Convert.FromHexString(
    "01000600000073616D706C65060000007365637265740008000000000102030405060700000000");
Check(
    EntranceService.TryReadLoginCredentials(
        metadataLoginBody,
        out var metadataLoginName,
        out var metadataLoginPassword)
    && metadataLoginName == "sample"
    && metadataLoginPassword == "secret",
    "channel login accepts launcher credentials followed by client machine/session metadata");
Check(
    EntranceService.TryReadLoginCredentials(
        loginPlainBody,
        out var legacyLoginName,
        out var legacyLoginPassword)
    && legacyLoginName == "test"
    && legacyLoginPassword is null,
    "legacy channel login without a password remains supported only when the account field ends the packet");
var loginWireBody = loginPlainBody.ToArray();
GameClientPayloadCipher.Encrypt(loginWireBody.AsSpan(2), ref clientSeed);
Check(loginWireBody.AsSpan(2).SequenceEqual(
        new byte[] { 0x31, 0x39, 0x39, 0x39, 0xD1, 0xF3, 0xDF, 0xD1 }),
    "DFLegacy client transform matches the recovered rotate-right vector");
var encryptedLogin = new PacketFrame(1, 1, Crc32.Compute(loginPlainBody), loginWireBody);
var clientCipherState = new GameClientCipherState();
var rawCheckBody = new byte[10];
var rawCheck = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.CheckConnectionCommand,
    Crc32.Compute(rawCheckBody),
    rawCheckBody);
Check(clientCipherState.TryDecode(rawCheck, out var decodedRawCheck)
    && decodedRawCheck.Body.SequenceEqual(rawCheckBody),
    "direct CHECK_CONNECTION remains plaintext");
Check(clientCipherState.CandidateCount == 0x8000,
    "direct CHECK_CONNECTION does not advance the client rolling seed");
Check(clientCipherState.TryDecode(encryptedLogin, out var decodedLogin), "client rolling seed recovered from CRC32");
Check(decodedLogin.Body.SequenceEqual(loginPlainBody), "client command payload decrypts exactly");
Check(decodedLogin.HasValidCrc32, "decrypted client command restores valid CRC32");
Check(clientCipherState.CandidateCount is > 0 and < 0x8000, "rolling-seed candidates narrow after a payload");

var encryptedCheckPlainBody = new byte[10];
var encryptedCheckWireBody = encryptedCheckPlainBody.ToArray();
var checkCipherSeed = 0x1234u;
GameClientPayloadCipher.Encrypt(encryptedCheckWireBody.AsSpan(2), ref checkCipherSeed);
var encryptedRawCheck = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.CheckConnectionCommand,
    Crc32.Compute(encryptedCheckPlainBody),
    encryptedCheckWireBody);
var encryptedCheckCipherState = new GameClientCipherState();
Check(encryptedCheckCipherState.TryDecode(encryptedRawCheck, out var decodedEncryptedCheck)
    && decodedEncryptedCheck.Body.SequenceEqual(encryptedCheckPlainBody),
    "network-transformed CHECK_CONNECTION decrypts to its plaintext");
Check(encryptedCheckCipherState.CandidateCount == 0x8000,
    "network-transformed CHECK_CONNECTION does not consume the command rolling seed");
Check(encryptedCheckCipherState.TryDecode(encryptedLogin, out var decodedLoginAfterCheck)
    && decodedLoginAfterCheck.Body.SequenceEqual(loginPlainBody),
    "LOGIN recovers from its independent initial seed after CHECK_CONNECTION");

var capturedLoginBody = Convert.FromHexString(
    "010052727272D159E9D152727272D159E9D17232727272EBE90E5A2DC2F4416349E37A");
var capturedClientCipherState = new GameClientCipherState();
Check(capturedClientCipherState.TryDecode(rawCheck, out _),
    "captured session accepts the direct CHECK_CONNECTION");
var capturedLogin = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.LoginCommand,
    0xFD14BAC5,
    capturedLoginBody);
Check(capturedClientCipherState.TryDecode(capturedLogin, out var decodedCapturedLogin),
    "captured DFLegacy LOGIN recovers its client seed");
Check(decodedCapturedLogin.Body.AsSpan(2, 8).SequenceEqual(
        new byte[] { 4, 0, 0, 0, (byte)'t', (byte)'e', (byte)'s', (byte)'t' }),
    "captured DFLegacy LOGIN decrypts the account field");
var capturedProtocol161 = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    161,
    0x8621D867,
    Convert.FromHexString(
        "020013767676764654C70F15CFECDC8B2FE2E9C260618EB011362B79861976367695CBB97805464A740CC5E871338E5DEC1136007E86F6E0770385CA839979D4C4BB77CF19767676FF1FA84FB3E2C695A41AEE1311673D5759A91376"));
Check(capturedClientCipherState.TryDecode(capturedProtocol161, out var decodedProtocol161)
    && decodedProtocol161.HasValidCrc32
    && decodedProtocol161.Body.AsSpan(0, 6).SequenceEqual(
        new byte[] { 2, 0, 0x56, 0, 0, 0 }),
    "captured DFLegacy post-login command re-anchors a per-packet seed");
var capturedProtocol8 = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    8,
    0x4230257E,
    Convert.FromHexString("03004545BB"));
Check(capturedClientCipherState.TryDecode(capturedProtocol8, out var decodedProtocol8)
    && decodedProtocol8.Body.SequenceEqual(new byte[] { 3, 0, 0xFF, 0xFF, 2 }),
    "captured DFLegacy short command re-anchors its independent seed");

var loginReply = GameProtocolEngine.Handle(decodedLogin);
Check(loginReply?.ProtocolId == GameProtocolEngine.LoginCommand, "login command receives a reply");
Check(loginReply?.Payload.SequenceEqual(new byte[] { 1, 0, 0, 0, 0 }) == true,
    "login reply selects the normal character-list path");

var userInfoRequest = new PacketFrame(
    GameProtocolEngine.CommandPacketType,
    GameProtocolEngine.GetUserInfoCommand,
    0,
    [0x34, 0x12, 0xFF, 0xFF, 0x02]);
var emptyUserInfo = GameProtocolEngine.Handle(userInfoRequest);
Check(emptyUserInfo?.Type == GameProtocolEngine.NotificationPacketType,
    "GET_USERINFO produces a notification");
Check(emptyUserInfo?.ProtocolId == GameProtocolEngine.UserInfoNotification,
    "GET_USERINFO produces USERINFO notification two");
Check(emptyUserInfo?.Payload.SequenceEqual(new byte[] { 2, 0, 0 }) == true,
    "empty USERINFO selects the visible character-list mode");

var populatedUserInfo = GameProtocolEngine.CreateUserInfo(
    [new GameCharacterSummary(0, "123"u8.ToArray(), 0, 0, 1)]);
Check(populatedUserInfo.Payload.Length == 27, "one-character USERINFO has the mapped legacy layout");
Check(populatedUserInfo.Payload.AsSpan(0, 5).SequenceEqual(new byte[] { 2, 1, 0, 0, 0 }),
    "USERINFO carries mode, character count and slot");
Check(BinaryPrimitives.ReadUInt32LittleEndian(populatedUserInfo.Payload.AsSpan(5, 4)) == 3,
    "USERINFO character name is length prefixed");
Check(populatedUserInfo.Payload.AsSpan(9, 3).SequenceEqual("123"u8),
    "USERINFO preserves encoded character-name bytes");
Check(populatedUserInfo.Payload[12] == 0 && populatedUserInfo.Payload[13] == 0
    && populatedUserInfo.Payload[14] == 1,
    "USERINFO carries job, grow type and level");
Check(populatedUserInfo.Payload.AsSpan(23, 4).SequenceEqual(new byte[] { 0, 0, 0, 1 }),
    "USERINFO ends with the complete selectable-card state");
var awakenedUserInfo = GameProtocolEngine.CreateUserInfo(
    [new GameCharacterSummary(0, "123"u8.ToArray(), 2, 1, 54, AwakeningType: 1)]);
Check(awakenedUserInfo.Payload[13] == 0x11
    && awakenedUserInfo.Payload[15] == 0
    && awakenedUserInfo.Payload[16] == 0,
    "character-selection USERINFO keeps awakening packed separately from the default 10级 PVP rank");
var avatarUserInfo = GameProtocolEngine.CreateUserInfo(
    [new GameCharacterSummary(
        0,
        "123"u8.ToArray(),
        0,
        0,
        1,
        [new GameInventoryEntry(1, 39_000, 1, State: 2, Durability: 1_000)])]);
Check(avatarUserInfo.Payload.Length == 31
    && avatarUserInfo.Payload[17] == 1
    && avatarUserInfo.Payload.AsSpan(18, 4).SequenceEqual(
        new byte[] { 1, 0x58, 0x98, 2 })
    && avatarUserInfo.Payload.AsSpan(27, 4).SequenceEqual(
        new byte[] { 0, 0, 0, 1 }),
    "character-selection USERINFO projects Avatar appearance as compact slot/id/state records");

var championDropFixtureRoot = Path.Combine(
    Path.GetTempPath(),
    $"dflegacy-champion-drops-{Guid.NewGuid():N}");
try
{
    var championDropMonsterRoot = Path.Combine(championDropFixtureRoot, "monster");
    var championDropHunterRoot = Path.Combine(championDropMonsterRoot, "hunter");
    Directory.CreateDirectory(championDropHunterRoot);
    File.WriteAllText(
        Path.Combine(championDropMonsterRoot, "monster.lst"),
        "200 `hunter/hunter.mob`\n"
        + "201 `hunter/boss.mob`\n"
        + "70000 `hunter/invalid.mob`\n");
    File.WriteAllText(
        Path.Combine(championDropHunterRoot, "hunter.mob"),
        "[item] // every row is an independent roll for all monster types\n"
        + "3001 10000\n"
        + "3002 2500\n"
        + "0 10000\n"
        + "[/item]\n"
        + "[common champion drop item] // shared by boss and both champion types\n"
        + "3171 7000\n"
        + "0 10000\n"
        + "[/common champion drop item]\n"
        + "[super champion drop item]\n"
        + "3166 4000\n"
        + "[/super champion drop item]\n");
    File.WriteAllText(
        Path.Combine(championDropHunterRoot, "boss.mob"),
        "[item]\n"
        + "3222 10000\n"
        + "[/item]\n"
        + "[common champion drop item]\n"
        + "3062 12000 // clamped to 10000\n"
        + "[/common champion drop item]\n"
        + "[super champion drop item]\n"
        + "3166 10000\n"
        + "[/super champion drop item]\n");

    var championDropOptions = new ServerOptions
    {
        ScriptPvfPath = "",
        SkillScriptPath = championDropFixtureRoot
    };
    var championDropCatalog = new MonsterChampionDropCatalog(
        championDropOptions,
        CreateScripts(championDropOptions),
        NullLogger<MonsterChampionDropCatalog>.Instance);
    Check(championDropCatalog.DefinitionCount == 2
            && championDropCatalog.ItemDropEntryCount == 3
            && championDropCatalog.CommonDropEntryCount == 2
            && championDropCatalog.SuperDropEntryCount == 2
            && championDropCatalog.TryGetDefinition(200, out var hunterDrops)
            && hunterDrops.ItemDrops.SequenceEqual(
                [
                    new MonsterChampionDropEntry(3_001, 10_000),
                    new MonsterChampionDropEntry(3_002, 2_500)
                ])
            && hunterDrops.CommonDrops.SequenceEqual(
                [new MonsterChampionDropEntry(3_171, 7_000)])
            && hunterDrops.SuperDrops.SequenceEqual(
                [new MonsterChampionDropEntry(3_166, 4_000)])
            && championDropCatalog.TryGetDefinition(201, out var bossDrops)
            && bossDrops.ItemDrops.SequenceEqual(
                [new MonsterChampionDropEntry(3_222, 10_000)])
            && bossDrops.CommonDrops.SequenceEqual(
                [new MonsterChampionDropEntry(3_062, 10_000)]),
        "MOB drop catalog loads independent item plus common/super champion tables by monster.lst index and clamps probabilities to 10000");

    var normalSpecificDrops = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.Normal,
        new SequenceDropRandomSource(9_999, 2_499));
    var normalSpecificMiss = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.Normal,
        new SequenceDropRandomSource(9_999, 2_500));
    var bossSpecificDrops = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.Boss,
        new SequenceDropRandomSource(9_999, 2_500, 6_999));
    var championSpecificMiss = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.Champion,
        new SequenceDropRandomSource(9_999, 2_500, 7_000));
    var superSpecificDrops = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.SuperChampion,
        new SequenceDropRandomSource(9_999, 2_500, 6_999, 3_999));
    var superSpecificMiss = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.SuperChampion,
        new SequenceDropRandomSource(9_999, 2_500, 7_000, 4_000));
    Check(normalSpecificDrops.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001, 3_002 })
            && normalSpecificMiss.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001 })
            && bossSpecificDrops.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001, 3_171 })
            && championSpecificMiss.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001 })
            && superSpecificDrops.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001, 3_171, 3_166 })
            && superSpecificDrops.All(drop => drop.CountOrValue == 1)
            && superSpecificMiss.Select(drop => drop.ItemId)
                .SequenceEqual(new ushort[] { 3_001 }),
        "all monster types independently roll MOB item rows while boss/champion and super-champion types append their existing specific tables");

    championDropOptions.Drop.ForceDrops = true;
    var forcedChampionDrops = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.SuperChampion,
        new SequenceDropRandomSource());
    championDropOptions.Drop.Enabled = false;
    var disabledChampionDrops = championDropCatalog.Roll(
        200,
        GameDungeonMonsterTypes.SuperChampion,
        new ZeroDropRandomSource());
    Check(forcedChampionDrops.Select(drop => drop.ItemId).SequenceEqual(
                new ushort[] { 3_001, 3_002, 3_171, 3_166 })
            && disabledChampionDrops.Count == 0,
        "all MOB-specific drops honor the server drop enable and force-drop controls");
}
finally
{
    Directory.Delete(championDropFixtureRoot, recursive: true);
}

var realPvfPath = Environment.GetEnvironmentVariable("DF2008_REAL_PVF_PATH");
if (string.IsNullOrWhiteSpace(realPvfPath))
{
    // Same layout convention as server.json: "..\" from the DFLegacy.Server.exe
    // directory is the client directory that contains Script.pvf.
    realPvfPath = Path.GetFullPath(@"..\Script.pvf", AppContext.BaseDirectory);
}

if (!string.IsNullOrWhiteSpace(realPvfPath) && File.Exists(realPvfPath))
{
    var protocolPvf = new PvfArchiveReader(realPvfPath);
    Check(protocolPvf.FileCount >= 48_341,
        "shared PVF reader indexes the DFLegacy archive");
    var realChannelInfo = protocolPvf.ReadAllBytes("etc/channel_info.etc");
    Check(protocolPvf.FileExists("etc/channel_info.etc")
        && realChannelInfo.Length > 0,
        "server can read channel_info.etc directly from DFLegacy Script.pvf");
    Check(realChannelInfo.AsSpan().IndexOf((byte)0) < 0
        && realChannelInfo.AsSpan().StartsWith("[dungeon]"u8),
        "DFLegacy channel script is plain null-free text for protocol 10");

    var pvfScripts = CreateScripts(new ServerOptions
    {
        ScriptPvfPath = realPvfPath,
        SkillScriptPath = ""
    });
    Check(pvfScripts.IsPvf, "DFLegacy Script.pvf is selected instead of a directory");
    Check(pvfScripts.FileCount >= 48_341,
        "DFLegacy Script.pvf indexes the complete baseline archive and local additions");
    var pvfStaminaRecoveryCatalog = new StaminaRecoveryCatalog(
        pvfScripts,
        NullLogger<StaminaRecoveryCatalog>.Instance);
    pvfStaminaRecoveryCatalog.Initialize();
    Check(pvfStaminaRecoveryCatalog.Count == 99
        && pvfStaminaRecoveryCatalog.GetCost(18) == 11_543
        && pvfStaminaRecoveryCatalog.GetCost(60) == 75_262,
        "real PVF caches all 99 character-level stamina recovery costs");
    foreach (var requiredPath in new[]
             {
                 "skill/skilllist.lst",
                 "character/character.lst",
                 "dungeon/dungeon.lst",
                 "map/map.lst",
                 "monster/monster.lst",
                 "quest/quest.lst",
                 "equipment/equipment.lst",
                  "stackable/stackable.lst",
                   "creature/creature.lst",
                   "creature/exptable.tbl",
                   "etc/newcashshop.etc",
                   "etc/premiumlist.etc",
                   "etc/sptable.etc"
             })
    {
        Check(pvfScripts.FileExists(requiredPath)
            && pvfScripts.ReadAllBytes(requiredPath).Length > 0,
            $"DFLegacy Script.pvf reads {requiredPath} without extraction");
    }

    var pvfSpCatalog = new CharacterSpCatalog(
        pvfScripts,
        NullLogger<CharacterSpCatalog>.Instance);
    pvfSpCatalog.Initialize();
    Check(pvfSpCatalog.Count == 200
        && pvfSpCatalog.GetRewardForLevel(1) == 0
        && pvfSpCatalog.GetRewardForLevel(2) == 30
        && pvfSpCatalog.GetRewardForLevel(10) == 30
        && pvfSpCatalog.GetRewardForLevel(11) == 60
        && pvfSpCatalog.CalculateLevelUpReward(9, 11) == 90
        && pvfSpCatalog.CalculateLevelUpReward(50, 51) == 150
        && pvfSpCatalog.CalculateLevelUpReward(59, 60) == 150
        && pvfSpCatalog.CalculateTotalReward(60) > 0,
        "real PVF SP cache applies the target-level reward and sums multi-level gains");

    var pvfCharacterStatCatalog = new CharacterStatCatalog(
        pvfScripts,
        NullLogger<CharacterStatCatalog>.Instance);
    pvfCharacterStatCatalog.Initialize();
    var pvfSwordmanLevelOneStats = pvfCharacterStatCatalog.Get(0, 0, 1);
    var pvfGunnerLevelNineStats = pvfCharacterStatCatalog.Get(2, 0, 9);
    var pvfSoulBringerLevelTwentyStats = pvfCharacterStatCatalog.Get(0, 1, 20);
    Check(pvfCharacterStatCatalog.Count == 5
        && pvfSwordmanLevelOneStats is
        {
            MaximumHp: 1_800,
            MaximumMp: 1_400,
            PhysicalAttack: 70,
            PhysicalDefense: 70,
            MagicalAttack: 40,
            MagicalDefense: 40,
            DarkResistance: 200,
            LightResistance: -200,
            InventoryLimit: 400_000,
            MpRegeneration: 500,
            MovementSpeed: 8_500,
            AttackSpeed: 8_500,
            CastSpeed: 7_000,
            HitRecovery: 6_000,
            JumpPower: 4_300,
            Weight: 680_000
        }
        && pvfGunnerLevelNineStats is
        {
            MaximumHp: 4_800,
            MaximumMp: 4_000,
            PhysicalAttack: 300,
            PhysicalDefense: 300,
            MagicalAttack: 290,
            MagicalDefense: 290,
            InventoryLimit: 354_000,
            MpRegeneration: 700,
            MovementSpeed: 8_200,
            AttackSpeed: 9_500,
            Weight: 600_000
        }
        && pvfSoulBringerLevelTwentyStats is
        {
            MaximumHp: 11_300,
            MaximumMp: 5_200,
            PhysicalAttack: 735,
            PhysicalDefense: 735,
            MagicalAttack: 515,
            MagicalDefense: 515,
            InventoryLimit: 457_000,
            MpRegeneration: 975,
            HitRecovery: 6_380
        },
        "real PVF character/*.chr initial values and zero-based job branches drive every level of the authoritative stat projection");
    Check(pvfCharacterStatCatalog.HasAwakeningGrowth(1, 1)
        && pvfCharacterStatCatalog.HasAwakeningGrowth(2, 4, awakeningType: 2)
        && pvfCharacterStatCatalog.GetBase(new CharacterRecord(
            Guid.NewGuid(),
            "PackedAwakeningProbe",
            Job: 1,
            GrowType: 0x11,
            Level: 20,
            Sp: 0,
            Cash: 0)) == pvfCharacterStatCatalog.Get(
                job: 1,
                growType: 1,
                awakeningType: 1,
                level: 20),
        "real PVF awakening growth supports both split dfls state and DF60A1 packed grow-type state");

    var pvfBonusStatCharacter = new CharacterRecord(
        Guid.NewGuid(),
        "StatValidationProbe",
        Job: 2,
        GrowType: 0,
        Level: 9,
        Sp: 0,
        Cash: 0,
        BonusStrength: 5,
        BonusVitality: 15,
        BonusIntelligence: 10,
        BonusSpirit: 20,
        BonusMaximumHp: 25,
        BonusMaximumMp: 50,
        BonusMovementSpeed: 1,
        BonusAllElementResistance: 2);
    var pvfBonusStats = pvfCharacterStatCatalog.Get(pvfBonusStatCharacter);
    var pvfBonusStatsWithEquipment = pvfCharacterStatCatalog.Get(
        pvfBonusStatCharacter with
        {
            Equipment = [new CharacterItemRecord(9, 27_546, 1)]
        });
    var stalePvfStats = pvfCharacterStatCatalog.Get(
        pvfBonusStatCharacter with { Level = 8 });
    var stalePvfValidation = pvfCharacterStatCatalog.Validate(
        pvfBonusStatCharacter,
        stalePvfStats);
    var currentPvfValidation = pvfCharacterStatCatalog.Validate(
        pvfBonusStatCharacter,
        pvfBonusStats);
    Check(pvfBonusStats is
        {
            MaximumHp: 5_050,
            MaximumMp: 4_500,
            PhysicalAttack: 350,
            PhysicalDefense: 450,
            MagicalAttack: 390,
            MagicalDefense: 490,
            FireResistance: 20,
            WaterResistance: 20,
            DarkResistance: 20,
            LightResistance: 20,
            MovementSpeed: 8_210
        }
        && pvfBonusStatsWithEquipment == pvfBonusStats
        && !stalePvfValidation.IsCorrect
        && stalePvfValidation.Expected == pvfBonusStats
        && stalePvfValidation.MismatchedFields.Contains("MaximumHp")
        && stalePvfValidation.MismatchedFields.Contains("InventoryLimit")
        && currentPvfValidation.IsCorrect
        && currentPvfValidation.MismatchedFields.Count == 0,
        "level-up stat validation ignores equipment, detects a stale unequipped snapshot and corrects it to PVF base growth plus persisted permanent bonuses");

    var pvfCreatureExperienceCatalog = new CreatureExperienceCatalog(
        pvfScripts,
        NullLogger<CreatureExperienceCatalog>.Instance);
    pvfCreatureExperienceCatalog.Initialize();
    Check(pvfCreatureExperienceCatalog.ThresholdCount == 55
        && pvfCreatureExperienceCatalog.SpeciesCount > 0
        && pvfCreatureExperienceCatalog.GetMaximumLevel(15) == 50
        && pvfCreatureExperienceCatalog.GetLevel(1, 15) == 1
        && pvfCreatureExperienceCatalog.GetLevel(2, 15) == 2,
        "real PVF Creature experience and every species currently listed in creature.lst are cached with cumulative threshold semantics");

    var pvfGuildSkillCatalog = new SkillCatalog(
        pvfScripts,
        NullLogger<SkillCatalog>.Instance);
    var pvfGuildLayout = pvfGuildSkillCatalog.NormalizeLayout(
        0,
        0,
        0,
        [new CharacterSkillRecord(129, 200, 5)],
        out var pvfGuildLayoutChanged);
    Check(pvfGuildSkillCatalog.TryGetDefinition(0, 200, out var pvfGuildSkill)
        && pvfGuildSkill.IsGuildSkill
        && pvfGuildSkill.RawGroup == 4
        && pvfGuildLayoutChanged
        && pvfGuildLayout.SequenceEqual([new CharacterSkillRecord(204, 200, 5)]),
        "real PVF [purchase gsp] 1 migrates the guild skill into the common-page guild rows");
    var pvfGrantedSwordmanSkills = pvfGuildSkillCatalog.GetGrantedSkills(0, 1);
    var pvfResetSwordmanSkills = pvfGuildSkillCatalog.CreateResetLayout(
        0,
        1,
        [
            new CharacterSkillRecord(6, 8, 20),
            new CharacterSkillRecord(204, 200, 5)
        ]);
    Check(pvfGrantedSwordmanSkills.Count == 11
        && pvfGrantedSwordmanSkills.Any(skill =>
            skill.SkillId == 181 && skill.Level == 2)
        && pvfGrantedSwordmanSkills.Any(skill =>
            skill.SkillId == 27 && skill.Level == 1)
        && pvfResetSwordmanSkills.Any(skill =>
            skill.SkillId == 200 && skill.Level == 5)
        && pvfResetSwordmanSkills.All(skill => skill.SkillId != 8),
        "real PVF reset maps the zero-based character branch to its one-based .chr growtype while retaining guild skills only");
    var pvfUnawakenedRangerSkills = pvfGuildSkillCatalog.GetGrantedSkills(2, 1);
    var pvfAwakenedRangerSkills = pvfGuildSkillCatalog.GetGrantedSkills(
        2,
        1,
        awakeningType: 1);
    Check(pvfUnawakenedRangerSkills.Any(skill =>
            skill.SkillId == 22 && skill.Level == 1)
        && pvfUnawakenedRangerSkills.All(skill => skill.SkillId != 66)
        && pvfAwakenedRangerSkills.Any(skill =>
            skill.SkillId == 66 && skill.Level == 1),
        "real PVF Ranger grants include [growtype 2] skills and gate [awakening skill] 66 on awakening stage one");
    Check(pvfGuildSkillCatalog.TryGetDefinition(2, 66, out var suddenDeathSkill)
        && suddenDeathSkill.MaximumLevelFor(growType: 1) == 0
        && suddenDeathSkill.MaximumLevelFor(growType: 1, awakeningType: 1) == 10,
        "real PVF Ranger awakening skill 66 is learnable only through its second-growth maximum-level row");

    var pvfCeraItemCatalog = new ItemCatalog(
        pvfScripts,
        NullLogger<ItemCatalog>.Instance);
    pvfCeraItemCatalog.Initialize();
    Check(pvfCeraItemCatalog.CeraBoosterCount == 19
        && pvfCeraItemCatalog.TryGetDefinition(71, out var seaHeroGunnerBooster)
        && seaHeroGunnerBooster.CeraBooster is { Groups.Count: 10 }
        && seaHeroGunnerBooster.CeraBooster.Groups.Sum(group => group.DrawCount) == 10,
        "real PVF caches every Cera booster and all ten independent Sea Hero reward draws");
    Check(pvfCeraItemCatalog.CeraPackageCount >= 2
        && pvfCeraItemCatalog.TryGetDefinition(27, out var challengerPackage)
        && challengerPackage.CeraPackage is { Rewards.Count: 5 }
        && challengerPackage.CeraPackage.Rewards.Any(reward =>
            reward.ItemId == 1 && reward.Count == 100)
        && pvfCeraItemCatalog.TryGetDefinition(49, out var expertEventPackage)
        && expertEventPackage.CeraPackage is { Rewards.Count: 2 },
        "real PVF caches deterministic [cera package] contents for purchase-time expansion");
    Check(pvfCeraItemCatalog.TryGetDefinition(1, out var revivalCoin)
        && revivalCoin.Name == "复活币"
        && pvfCeraItemCatalog.TryGetDefinition(47_108, out var seaHeroAvatar)
        && seaHeroAvatar.Name == "紫罗兰色沙滩太阳镜",
        "real PVF item names are decoded from the client CP936 script encoding");
    var expectedPvfIncreaseStatusEffects = new Dictionary<ushort, IncreaseStatusEffect>
    {
        [1_031] = new(IncreaseStatusType.SkillPoints, 5),
        [1_034] = new(IncreaseStatusType.Experience, 100),
        [1_035] = new(IncreaseStatusType.Experience, 1_000),
        [1_036] = new(IncreaseStatusType.Experience, 10_000),
        [1_037] = new(IncreaseStatusType.Experience, 100_000),
        [1_038] = new(IncreaseStatusType.SkillPoints, 20),
        [1_039] = new(IncreaseStatusType.Strength, 5),
        [1_040] = new(IncreaseStatusType.Intelligence, 5),
        [1_041] = new(IncreaseStatusType.Vitality, 5),
        [1_042] = new(IncreaseStatusType.Spirit, 5),
        [1_043] = new(IncreaseStatusType.MaximumHp, 25),
        [1_044] = new(IncreaseStatusType.MaximumMp, 25),
        [1_045] = new(IncreaseStatusType.MovementSpeed, 1),
        [1_046] = new(IncreaseStatusType.AllElementResistance, 1)
    };
    Check(pvfCeraItemCatalog.SkillPointBookCount == 2
        && pvfCeraItemCatalog.ExperienceBookCount == 4
        && pvfCeraItemCatalog.AttributeStoneCount == 8
        && pvfCeraItemCatalog.IncreaseStatusItemCount == 14
        && pvfCeraItemCatalog.TryGetDefinition(1_031, out var pvfFiveSpBook)
        && pvfFiveSpBook.ScriptPath.Equals(
            "stackable/book_skill1.stk",
            StringComparison.OrdinalIgnoreCase)
        && pvfFiveSpBook.IncreaseStatus == expectedPvfIncreaseStatusEffects[1_031]
        && pvfCeraItemCatalog.TryGetDefinition(1_038, out var pvfTwentySpBook)
        && pvfTwentySpBook.ScriptPath.Equals(
            "stackable/book_skill2.stk",
            StringComparison.OrdinalIgnoreCase)
        && pvfTwentySpBook.IncreaseStatus == expectedPvfIncreaseStatusEffects[1_038]
        && expectedPvfIncreaseStatusEffects.All(expected =>
            pvfCeraItemCatalog.TryGetDefinition(expected.Key, out var definition)
            && definition.IncreaseStatus == expected.Value),
        "real PVF item cache identifies both SP books and all eight old-client attribute stones without parsing localized text");
    Check(pvfCeraItemCatalog.TryGetSellPrice(29_301, out var pvfSellPrice)
            && pvfSellPrice == 640,
        "DFLegacy item cache applies the client price-table rate to PVF [price]");
    Check(pvfCeraItemCatalog.TryGetPurchasePrice(63_502, out var babyDragonArtifactPrice)
            && babyDragonArtifactPrice == 5_000
            && pvfCeraItemCatalog.TryGetPurchasePrice(63_505, out var wyvernArtifactPrice)
            && wyvernArtifactPrice == 10_000
            && pvfCeraItemCatalog.TryGetPurchasePrice(3_037, out var materialPurchasePrice)
            && materialPurchasePrice == 100,
        "DFLegacy NPC-shop purchases use the raw cached PVF [price] without the sell-rate table");
    var pricelessPvfItem = pvfCeraItemCatalog.Definitions.Values.First(definition =>
        definition.Price is null);
    Check(pvfCeraItemCatalog.TryGetPurchasePrice(pricelessPvfItem.Id, out var freePurchasePrice)
            && freePurchasePrice == 0,
        "DFLegacy NPC-shop items without a valid PVF [price] remain purchasable for zero gold");
    Check(pvfCeraItemCatalog.GetInitialDurability(11_207) == 40
            && pvfCeraItemCatalog.NormalizeDurability(11_207, 1_000) == 40
            && pvfCeraItemCatalog.NormalizeDurability(11_207, 0) == 0
            && pvfCeraItemCatalog.TryCalculateSellPrice(
                11_207,
                durability: 40,
                out var fullDurabilitySellPrice)
            && fullDurabilitySellPrice == 1_800
            && pvfCeraItemCatalog.TryCalculateSellPrice(
                11_207,
                durability: 20,
                out var halfDurabilitySellPrice)
            && halfDurabilitySellPrice == 900,
        "DFLegacy equipment applies the client price-table rate before durability scaling");
    Check(pvfCeraItemCatalog.TryGetMaximumDurability(11_207, out var maximumDurability)
            && maximumDurability == 40
            && pvfCeraItemCatalog.TryCalculateRepairPrice(
                11_207,
                durability: 0,
                inDungeon: false,
                out var fullTownRepairPrice)
            && fullTownRepairPrice == 1_350
            && pvfCeraItemCatalog.TryCalculateRepairPrice(
                11_207,
                durability: 0,
                inDungeon: true,
                out var fullDungeonRepairPrice)
            && fullDungeonRepairPrice == 1_485
            && pvfCeraItemCatalog.TryCalculateRepairPrice(
                11_207,
                durability: 20,
                inDungeon: false,
                out var halfTownRepairPrice)
            && halfTownRepairPrice == 675
            && pvfCeraItemCatalog.TryCalculateRepairPrice(
                11_207,
                durability: 20,
                inDungeon: true,
                out var halfDungeonRepairPrice)
            && halfDungeonRepairPrice == 742,
        "DFLegacy repair prices match the 2008 client grade, durability and town/dungeon formula");
    var pvfCeraCatalog = new CeraShopCatalog(
        pvfScripts,
        pvfCeraItemCatalog,
        NullLogger<CeraShopCatalog>.Instance);
    var expectedWarehouseUpgradeProducts = new Dictionary<uint, (ushort ItemId, ushort Capacity)>
    {
        [100_063] = (50, 24),
        [100_064] = (57, 40),
        [100_065] = (58, 56),
        [100_066] = (59, 72),
        [100_067] = (60, 88),
        [100_068] = (61, 104),
        [100_069] = (62, 120)
    };
    Check(expectedWarehouseUpgradeProducts.All(expected =>
            pvfCeraCatalog.TryGetProduct(expected.Key, out var product)
            && product.ItemId == expected.Value.ItemId
            && product.Quantity == 1
            && product.WarehouseUpgradeCapacity == expected.Value.Capacity),
        "real PVF maps the seven target-client Cera commodities to exact 24-through-120 warehouse stages");
    Check(pvfCeraCatalog.Count > 0
            && pvfCeraCatalog.TryGetProduct(140_002, out var pvfAvatarProduct)
            && pvfAvatarProduct.ItemId == 40_689
            && pvfAvatarProduct.AttributeValue == 3
            && pvfAvatarProduct.CashPrice == 2_970
            && pvfCeraCatalog.TryGetProduct(100_042, out var pvfOverlordContract)
            && pvfOverlordContract.PremiumContract is
            {
                ServiceType: GameProtocolEngine.OverlordContractServiceType,
                Days: 15
            }
            && pvfCeraCatalog.TryGetProduct(100_060, out var pvfMasterContract)
            && pvfMasterContract.PremiumContract is
            {
                ServiceType: GameProtocolEngine.MasterContractServiceType,
                Days: 15
            },
        "DFLegacy Cera catalog resolves avatar cash prices from item definitions");
    Check(pvfCeraCatalog.Products.Any(product =>
                product.ItemId == 190
                && product.DirectCurrencyGrant
                    == new CeraShopDirectCurrencyGrant(1_000_000, 0))
            && pvfCeraCatalog.Products.Any(product =>
                product.ItemId == 191
                && product.DirectCurrencyGrant
                    == new CeraShopDirectCurrencyGrant(0, 100)),
        "real PVF Cera products settle item 190 as one million gold and item 191 as one hundred victory points");
    Check(pvfCeraCatalog.TryGetProduct(110_100, out var pvfGoldVoucherProduct)
            && pvfGoldVoucherProduct.ItemId == 190
            && pvfGoldVoucherProduct.Quantity == 1
            && pvfGoldVoucherProduct.CashPrice == 1_000
            && pvfCeraCatalog.TryGetProduct(105_001, out var pvfCreatureFoodProduct)
            && pvfCreatureFoodProduct.ItemId == 24
            && pvfCreatureFoodProduct.Quantity == 100
            && pvfCreatureFoodProduct.CashPrice == 900,
        "real PVF Cera prices account for the observed 1000-Cera gold voucher and 900-Cera creature-food purchases exactly once per commodity");
    var pvfPremiumBenefits = new PremiumBenefitCatalog(
        pvfScripts,
        NullLogger<PremiumBenefitCatalog>.Instance);
    pvfPremiumBenefits.Initialize();
    Check(pvfPremiumBenefits.GetOverSkillLevel(
            [GameProtocolEngine.MasterContractServiceType]) == 5
        && Enumerable.Range(1, 4).All(equipmentType =>
            pvfPremiumBenefits.GetOverEquipableLevel(
                [GameProtocolEngine.OverlordContractServiceType],
                equipmentType) == 5),
        "DFLegacy premiumlist grants Master +5 skill level and Overlord +5 for four equipment types");
    var fiveLevelHigherWeapon = pvfCeraItemCatalog.Definitions.Values
        .Where(definition => definition.TypeTag == "weapon"
            && definition.MinimumLevel is >= 5)
        .MaxBy(definition => definition.MinimumLevel) !;
    var fiveLevelsBelowRequirement = fiveLevelHigherWeapon.MinimumLevel!.Value - 5;
    Check(!pvfPremiumBenefits.CanEquipByLevel(
            fiveLevelsBelowRequirement,
            [],
            fiveLevelHigherWeapon)
        && pvfPremiumBenefits.CanEquipByLevel(
            fiveLevelsBelowRequirement,
            [GameProtocolEngine.OverlordContractServiceType],
            fiveLevelHigherWeapon),
        "active Overlord contract permits a character to equip a weapon five levels above the normal requirement only while the benefit is active");
    var pvfGoldDrops = new GoldDropCatalog(
        pvfScripts,
        NullLogger<GoldDropCatalog>.Instance);
    var pvfObjectDrops = new ObjectDropCatalog(
        pvfScripts,
        NullLogger<ObjectDropCatalog>.Instance);
    var pvfClearRewards = new ClearRewardCatalog(
        pvfScripts,
        NullLogger<ClearRewardCatalog>.Instance);
    var hasLevel55CommonRewardTable = pvfClearRewards.TryGetWeightedTableStats(
        generationLevel: 55,
        rarity: 0,
        out var level55CommonCandidateCount,
        out var level55CommonTotalWeight);
    Check(pvfClearRewards.EquipmentCandidateCount > 2_000
            && pvfClearRewards.WeightedTableCount > 0
            && hasLevel55CommonRewardTable
            && level55CommonCandidateCount > 0
            && level55CommonTotalWeight > 0
            && pvfClearRewards.GetGoldCardCost(55) == 9_700,
        "DFLegacy clear-reward tables and weighted level/rarity creation tables load from Script.pvf");
    var clearRewardEquipment = pvfClearRewards.GenerateEquipment(
        ClearRewardProbabilityCategory.Default,
        generationLevel: 55,
        dungeonDifficulty: 0,
        partyMemberCount: 1,
        random: new ZeroDropRandomSource());
    var blankClearReward = pvfClearRewards.GenerateEquipment(
        ClearRewardProbabilityCategory.Default,
        generationLevel: 55,
        dungeonDifficulty: 0,
        partyMemberCount: 1,
        random: new SequenceDropRandomSource(9_999));
    Check(clearRewardEquipment.HasValue && !blankClearReward.HasValue,
        "clear-reward generation supports weighted equipment and explicit blank outcomes");
    var pvfClearRewardGenerator = new ClearRewardGenerator(
        pvfClearRewards,
        pvfGoldDrops);
    var generatedClearReward = pvfClearRewardGenerator.Generate(
        generationLevel: 55,
        dungeonDifficulty: 0,
        isPremium: false,
        goldCardEnabled: true,
        partyMemberCount: 1,
        normalMonsterKills: 4,
        championMonsterKills: 1,
        bossMonsterKills: 1,
        random: new ZeroDropRandomSource());
Check(generatedClearReward.FreeGoldAmount == 388,
        "clear-reward uses the DFLegacy common level table and 0.5/1/2 monster weights for the local party member");
    var pvfObjectDropGenerator = new ObjectDropGenerator(
        new ServerOptions(),
        pvfObjectDrops,
        pvfGoldDrops);
    var pvfDungeonCatalog = new DungeonCatalog(
        pvfScripts,
        pvfObjectDropGenerator,
        NullLogger<DungeonCatalog>.Instance);
    var pvfDungeonExperience = new DungeonExperienceCatalog(
        new ServerOptions(),
        pvfScripts,
        NullLogger<DungeonExperienceCatalog>.Instance);
    Check(pvfDungeonCatalog.DungeonIds.Count == 43,
        "all DFLegacy dungeon definitions are parsed directly from Script.pvf");
    Check(pvfDungeonExperience.MonsterBaseRewardCount == 100
            && pvfDungeonExperience.GetMonsterBaseReward(1) == 30
            && pvfDungeonExperience.GetMonsterBaseReward(55) == 1_254
            && pvfDungeonExperience.MonsterBonusRate == 1
            && pvfDungeonExperience.ClearBonusRate == 1
            && pvfDungeonExperience.GetPartyRate(4) == 2.5
            && pvfDungeonExperience.GetDifficultyRate(3) == 2.2
            && pvfDungeonExperience.GetRankRate(105) == 0.3
            && pvfDungeonExperience.GetMonsterKindRate(
                GameDungeonMonsterTypes.Boss) == 4,
        $"DFLegacy EXP cache combines monster_reward_ref with target serverparameter rates "
        + $"(count={pvfDungeonExperience.MonsterBaseRewardCount}, lv1={pvfDungeonExperience.GetMonsterBaseReward(1)}, "
        + $"lv55={pvfDungeonExperience.GetMonsterBaseReward(55)}, monster={pvfDungeonExperience.MonsterBonusRate}, "
        + $"clear={pvfDungeonExperience.ClearBonusRate}, party4={pvfDungeonExperience.GetPartyRate(4)}, "
        + $"difficulty3={pvfDungeonExperience.GetDifficultyRate(3)}, rank={pvfDungeonExperience.GetRankRate(105)}, "
        + $"boss={pvfDungeonExperience.GetMonsterKindRate(GameDungeonMonsterTypes.Boss)})");
    var championDungeonDefinitions = pvfDungeonCatalog.DungeonIds
        .Select(dungeonId => pvfDungeonCatalog.TryGetDefinition(
            dungeonId,
            out var definition)
                ? definition
                : null)
        .Where(definition => definition?.ChampionCounts.Any(count => count > 0) == true)
        .Cast<DungeonDefinition>()
        .ToArray();
    Check(championDungeonDefinitions.Length > 0
            && championDungeonDefinitions.All(definition =>
                definition.ChampionCounts.Length == 4
                && definition.ChampionCounts.All(count => count >= 0)),
        "DFLegacy DGN [champion] rows load as four non-negative per-difficulty values");
    Check(DungeonCatalog.ParseStandaloneMapChampionCount("""
              [monster]
                  1 0 0 100 100 [champion]
              [/monster]
              [champion] 2
              """) == 2
            && DungeonCatalog.ParseStandaloneMapChampionCount("""
              [monster]
                  1 0 0 100 100 [champion]
              [/monster]
              """) == 0
            && DungeonCatalog.ParseStandaloneMapChampionCount("[champion] -1") == 0,
        "MAP standalone [champion] is parsed independently from inline monster type tags");
    Check(DungeonCatalog.CalculateAdditionalChampionChance(4, 8, 4) == 12
            && DungeonCatalog.CalculateAdditionalChampionChance(40, 2, 2) == 100
            && DungeonCatalog.CalculateAdditionalChampionChance(-1, 0, 0) == 0,
        "DGN champion values scale to one per-room roll by maze area and clamp to a percentage");

    DungeonDefinition? championRunDefinition = null;
    DungeonLayout? championRunLayout = null;
    DungeonRoomDefinition? rawChampionStartRoom = null;
    byte championRunDifficulty = 0;
    var championStartTarget = 0;
    var championStartChance = 0;
    foreach (var candidateDefinition in championDungeonDefinitions)
    {
        if (!pvfDungeonCatalog.TryCreateLayout(
                candidateDefinition.DungeonId,
                out var candidateLayout,
                random: new ZeroDropRandomSource()))
        {
            continue;
        }

        for (byte difficulty = 0; difficulty <= DungeonDifficultyProgression.HighestDifficulty; difficulty++)
        {
            var chance = DungeonCatalog.CalculateAdditionalChampionChance(
                candidateDefinition.ChampionCounts[difficulty],
                candidateLayout.Width,
                candidateLayout.Height);
            if (chance <= 0
                || !pvfDungeonCatalog.TryCreateRoom(
                    candidateLayout,
                    candidateLayout.StartX,
                    candidateLayout.StartY,
                    out var candidateStartRoom,
                    difficulty,
                    new ZeroDropRandomSource()))
            {
                continue;
            }

            var normalCandidateCount = candidateStartRoom.Monsters.Count(monster =>
                monster.Type == GameDungeonMonsterTypes.Normal
                && monster.IsBoxMonster == 0
                && !monster.IsHellHidden);
            if (normalCandidateCount == 0)
            {
                continue;
            }

            var target = pvfDungeonCatalog.GetChampionTargetCount(
                candidateLayout,
                difficulty,
                candidateStartRoom,
                new ZeroDropRandomSource());

            championRunDefinition = candidateDefinition;
            championRunLayout = candidateLayout;
            rawChampionStartRoom = candidateStartRoom;
            championRunDifficulty = difficulty;
            championStartTarget = target;
            championStartChance = chance;
            break;
        }

        if (championRunLayout is not null)
        {
            break;
        }
    }

    var championGenerationValid = championRunDefinition is not null
        && championRunLayout is not null
        && rawChampionStartRoom is not null;
    if (championGenerationValid)
    {
        var normalCandidateCount = rawChampionStartRoom!.Monsters.Count(monster =>
            monster.Type == GameDungeonMonsterTypes.Normal
            && monster.IsBoxMonster == 0
            && !monster.IsHellHidden);
        var existingChampionCount = rawChampionStartRoom.Monsters.Count(monster =>
            monster.Type == GameDungeonMonsterTypes.Champion);
        var expectedHitTarget = rawChampionStartRoom.BaseChampionCount + 1;
        var missRoll = 99;
        var expectedMissTarget = rawChampionStartRoom.BaseChampionCount
            + (missRoll < championStartChance ? 1 : 0);
        var missTarget = pvfDungeonCatalog.GetChampionTargetCount(
            championRunLayout!,
            championRunDifficulty,
            rawChampionStartRoom,
            new SequenceDropRandomSource(missRoll));
        var championRunState = new DungeonRunState(pvfDungeonCatalog);
        championGenerationValid = championStartTarget == expectedHitTarget
            && missTarget == expectedMissTarget
            && championRunState.TryStart(
                championRunLayout!,
                championRunDifficulty,
                dungeonOption: 0,
                out var championStartTransition,
                new ZeroDropRandomSource())
            && championStartTransition.Room.Monsters.Count(monster =>
                monster.Type == GameDungeonMonsterTypes.Champion)
            == existingChampionCount
                + Math.Min(
                    championStartTarget,
                    normalCandidateCount)
            && championStartTransition.Room.ClientRandomSeed != 0
            && championRunState.CurrentRoom?.ClientRandomSeed
                == championStartTransition.Room.ClientRandomSeed;
    }

    Check(championGenerationValid,
        "MAP fixed champions plus the DGN area-scaled room roll promote random ordinary monsters and retain a non-zero client seed");
    var invalidHellDungeons = new List<string>();
    foreach (var hellDungeonId in pvfDungeonCatalog.HellDungeonIds)
    {
        if (!pvfDungeonCatalog.TryGetDefinition(hellDungeonId, out var hellDungeon)
            || hellDungeon.SealDoorMapId == 0
            || !pvfDungeonCatalog.TryCreateLayout(
                hellDungeonId,
                out var hellLayout,
                random: new ZeroDropRandomSource())
            || !pvfDungeonCatalog.TryCreateHellRoom(
                hellLayout,
                dungeonDifficulty: 0,
                out var hellRoom,
                random: new ZeroDropRandomSource())
            || !hellRoom.IsHellRoom
            || hellRoom.MapId != hellDungeon.SealDoorMapId
            || hellRoom.Monsters.Count(monster => monster.IsHellHidden) != 1)
        {
            invalidHellDungeons.Add(hellDungeonId.ToString());
        }
    }

    Check(pvfDungeonCatalog.HellDungeonIds.Count == 19
            && invalidHellDungeons.Count == 0,
        $"all 19 DFLegacy hell dungeons resolve their seal-door map and one cosmofiend ({string.Join(',', invalidHellDungeons)})");
    var hellLayoutCount = 0;
    var invalidHellLayouts = new List<string>();
    foreach (var hellDungeonId in pvfDungeonCatalog.HellDungeonIds)
    {
        _ = pvfDungeonCatalog.TryGetDefinition(hellDungeonId, out var hellDungeon);
        if (hellDungeon.WarRoomMapId != 0)
        {
            continue;
        }

        foreach (var maze in hellDungeon.Mazes)
        {
            foreach (var start in maze.StartPositions)
            {
                var bosses = DungeonCatalog.GetSelectableBossPositions(
                    hellDungeon,
                    maze,
                    start);
                if (bosses.Length == 0)
                {
                    bosses = [new DungeonRoomPosition(byte.MaxValue, byte.MaxValue)];
                }

                foreach (var boss in bosses)
                {
                    hellLayoutCount++;
                    var layout = new DungeonLayout(hellDungeonId, maze, start, boss);
                    if (!pvfDungeonCatalog.TryCreateRoom(
                            layout,
                            hellDungeon.SealDoorPosition.X,
                            hellDungeon.SealDoorPosition.Y,
                            out var ordinarySealRoom,
                            random: new ZeroDropRandomSource())
                        || ordinarySealRoom.MapId == hellDungeon.SealDoorMapId
                        || !pvfDungeonCatalog.TryCreateHellRoom(
                            layout,
                            dungeonDifficulty: 0,
                            out var layoutHellRoom,
                            random: new ZeroDropRandomSource())
                        || layoutHellRoom.MapId != hellDungeon.SealDoorMapId
                        || layoutHellRoom.Monsters.Count(monster => monster.IsHellHidden) != 1)
                    {
                        invalidHellLayouts.Add(
                            $"{hellDungeonId}:{maze.MazeIndex}:{start.X},{start.Y}:{boss.X},{boss.Y}");
                    }
                }
            }
        }
    }

    Check(hellLayoutCount == 78 && invalidHellLayouts.Count == 0,
        $"all 78 selectable hell layouts retain distinct ordinary and seal-door rooms (count={hellLayoutCount}; {string.Join(',', invalidHellLayouts)})");
    var hellRunStateValid = pvfDungeonCatalog.TryCreateLayout(
        11,
        out var skyCastleLayout,
        random: new SequenceDropRandomSource(0, 1, 0));
    var hellRunState = new DungeonRunState(pvfDungeonCatalog);
    DungeonRoomTransition firstHellRunRoom = null!;
    hellRunStateValid = hellRunStateValid
        && hellRunState.TryStart(
            skyCastleLayout,
            difficulty: 0,
            dungeonOption: 1,
            out firstHellRunRoom,
            new ZeroDropRandomSource())
        && !firstHellRunRoom.Room.IsHellRoom
        && hellRunState.CurrentHellPartyMode == 1;
    if (hellRunStateValid)
    {
        var defeatedStartMonster = firstHellRunRoom.Room.Monsters.FirstOrDefault();
        hellRunStateValid = defeatedStartMonster is not null
            && hellRunState.MarkMonsterDefeated(defeatedStartMonster.UniqueId)
            && hellRunState.TryMoveRoom(
                2,
                1,
                out _,
                new ZeroDropRandomSource())
            && hellRunState.TryMoveRoom(
                2,
                0,
                out var revisitedStart,
                new ZeroDropRandomSource())
            && revisitedStart.Revisit
            && revisitedStart.ReuseClientRoomState
            && revisitedStart.Room.Monsters.All(monster =>
                monster.UniqueId != defeatedStartMonster.UniqueId)
            && hellRunState.TryMoveRoom(
                2,
                1,
                out _,
                new ZeroDropRandomSource())
            && hellRunState.TryMoveRoom(
                1,
                1,
                out _,
                new ZeroDropRandomSource())
            && hellRunState.TryMoveRoom(
                1,
                2,
                out _,
                new ZeroDropRandomSource())
            && hellRunState.TryMoveRoom(
                0,
                2,
                out var enteredHellRoom,
                new ZeroDropRandomSource())
            && enteredHellRoom.Room.IsHellRoom
            && enteredHellRoom.Room.MapId == 60_001
            && enteredHellRoom.Room.Monsters.Count(monster => monster.IsHellHidden) == 1
            && hellRunState.RoomStateFlag == 2
            && hellRunState.CurrentHellPartyMode == 1
            && enteredHellRoom.Room.PassiveObjects.FirstOrDefault()
                is { } destroyedHellPassive
            && hellRunState.TryMarkPassiveObjectDestroyed(
                destroyedHellPassive.PassiveObjectId,
                out var destroyedHellPassiveIndex)
            && hellRunState.CurrentRoom!.PassiveObjects.All(passiveObject =>
                passiveObject.ObjectIndex != destroyedHellPassiveIndex)
            && hellRunState.TryMoveRoom(
                1,
                2,
                out var leftHellRoom,
                new ZeroDropRandomSource())
            && leftHellRoom.CompletedHellRoomOnExit
            && hellRunState.CurrentHellPartyMode == 1
            && hellRunState.TryMoveRoom(
                0,
                2,
                out var revisitedHellRoom,
                new ZeroDropRandomSource())
            && revisitedHellRoom.Revisit
            && !revisitedHellRoom.ReuseClientRoomState
            && !revisitedHellRoom.Room.IsHellRoom
            && revisitedHellRoom.Room.MapId != 60_001
            && revisitedHellRoom.Room.Monsters.Length == 0
            && revisitedHellRoom.Room.PassiveObjects.Length == 0
            && hellRunState.RoomStateFlag == 2
            && hellRunState.CurrentHellPartyMode == 0;
    }

    Check(hellRunStateValid,
        "dungeon-run state preserves defeated actors, validates connected moves and restores a cleared hell room as an empty base map");
    Check(pvfDungeonCatalog.TryGetDefinition(6, out var sunderlandPoison)
            && sunderlandPoison.Mazes.Length == 2
            && sunderlandPoison.Mazes[0].StartPositions.SequenceEqual(
                new DungeonRoomPosition[]
                {
                    new(0, 0),
                    new(4, 4),
                    new(0, 4),
                    new(4, 0)
                })
            && sunderlandPoison.Mazes[0].BossPositions.SequenceEqual(
                new DungeonRoomPosition[] { new(1, 2), new(3, 2) })
            && sunderlandPoison.Mazes[1].Width == 6
            && sunderlandPoison.Mazes[1].Height == 4,
        "DFLegacy dungeons retain every maze and all random start/boss candidates");
    Check(pvfDungeonCatalog.TryCreateLayout(
                6,
                out var firstPoisonLayout,
                random: new ZeroDropRandomSource())
            && firstPoisonLayout.MazeIndex == 0
            && firstPoisonLayout.StartX == 0
            && firstPoisonLayout.StartY == 0
            && firstPoisonLayout.BossX == 1
            && firstPoisonLayout.BossY == 2
            && pvfDungeonCatalog.TryCreateRoom(
                firstPoisonLayout,
                firstPoisonLayout.BossX,
                firstPoisonLayout.BossY,
                out var firstPoisonBossRoom,
                random: new ZeroDropRandomSource())
            && firstPoisonBossRoom.MapType == DungeonMapType.Boss
            && firstPoisonBossRoom.MapId != 845,
        "a normal map specification cannot override the selected boss-room role");
    Check(pvfDungeonCatalog.TryCreateLayout(
                6,
                out var alternatePoisonLayout,
                random: new SequenceDropRandomSource(1, 1, 2))
            && alternatePoisonLayout.MazeIndex == 1
            && alternatePoisonLayout.StartX == 1
            && alternatePoisonLayout.StartY == 0
            && alternatePoisonLayout.BossX == 2
            && alternatePoisonLayout.BossY == 2,
        "dungeon layout selection independently randomizes maze, start and boss candidates");
    var invalidDungeonLayouts = new List<string>();
    foreach (var parsedDungeonId in pvfDungeonCatalog.DungeonIds)
    {
        if (!pvfDungeonCatalog.TryGetDefinition(parsedDungeonId, out var parsedDungeon))
        {
            invalidDungeonLayouts.Add($"{parsedDungeonId}:definition");
            continue;
        }

        for (var mazeOffset = 0; mazeOffset < parsedDungeon.Mazes.Length; mazeOffset++)
        {
            if (!pvfDungeonCatalog.TryCreateLayout(
                    parsedDungeonId,
                    out var parsedLayout,
                    random: new SequenceDropRandomSource(mazeOffset, 0, 0)))
            {
                invalidDungeonLayouts.Add($"{parsedDungeonId}:{mazeOffset}:layout");
                continue;
            }

            if (!pvfDungeonCatalog.TryCreateRoom(
                    parsedLayout,
                    parsedLayout.StartX,
                    parsedLayout.StartY,
                    out _,
                    random: new ZeroDropRandomSource()))
            {
                invalidDungeonLayouts.Add($"{parsedDungeonId}:{mazeOffset}:start");
            }

            var hasDistinctBoss = parsedLayout.BossX != byte.MaxValue
                && parsedLayout.BossY != byte.MaxValue
                && (parsedLayout.BossX != parsedLayout.StartX
                    || parsedLayout.BossY != parsedLayout.StartY);
            if (hasDistinctBoss
                && (!pvfDungeonCatalog.TryCreateRoom(
                        parsedLayout,
                        parsedLayout.BossX,
                        parsedLayout.BossY,
                        out var parsedBossRoom,
                        random: new ZeroDropRandomSource())
                    || parsedBossRoom.MapType != DungeonMapType.Boss))
            {
                invalidDungeonLayouts.Add($"{parsedDungeonId}:{mazeOffset}:boss");
            }
        }
    }

    Check(invalidDungeonLayouts.Count == 0,
        $"every DFLegacy maze resolves its selected start and boss roles ({string.Join(",", invalidDungeonLayouts)})");
    Check(pvfDungeonCatalog.TryGetMapType(5013, out var magmaBossMapType)
            && magmaBossMapType == DungeonMapType.Boss
            && pvfDungeonCatalog.TryGetMapType(3802, out var bossMapWithoutBossType)
            && bossMapWithoutBossType == DungeonMapType.Boss
            && pvfDungeonCatalog.TryGetMapType(6418, out var temptationDummyMapType)
            && temptationDummyMapType == DungeonMapType.Dummy,
        "DFLegacy map [type] preserves boss and dummy independently from monster roles");
    Check(pvfDungeonCatalog.TryGetDefinition(25, out var firstBackboneDungeon)
        && pvfDungeonCatalog.TryCreateRoom(
            25,
            firstBackboneDungeon.BossX,
            firstBackboneDungeon.BossY,
            out var firstBackboneBossRoom,
            random: new ZeroDropRandomSource())
        && firstBackboneBossRoom.HasScriptBossActor
        && firstBackboneBossRoom.Monsters.Any(monster =>
            monster.Type == GameDungeonMonsterTypes.Boss
            && monster.Level == firstBackboneDungeon.BasisLevel + 2),
        "DFLegacy map [monster] rows preserve boss type and dungeon-relative level offsets");
    Check(pvfDungeonCatalog.TryGetDefinition(2, out var lorienInsideDungeon)
        && pvfDungeonCatalog.TryCreateRoom(
            2,
            lorienInsideDungeon.BossX,
            lorienInsideDungeon.BossY,
            out var lorienInsideBossRoom,
            random: new ZeroDropRandomSource())
        && lorienInsideBossRoom.Monsters.Any(monster =>
            monster.Type == GameDungeonMonsterTypes.Boss
            && monster.Level == 4),
        "DFLegacy map [monster] rows preserve fixed monster levels");
    pvfDungeonCatalog.TryGetDefinition(1, out var lorienDungeon);
    Check(pvfDungeonCatalog.TryGetDefinition(33, out var kingsRuinsDungeon)
            && kingsRuinsDungeon.ExperienceMultiplier == 3.5,
        "DGN experience increasing point is cached independently for each dungeon");
    var lorienNormalExperience = pvfDungeonExperience.CalculateMonsterExperience(
        characterLevel: 1,
        monsterLevel: 1,
        monsterType: GameDungeonMonsterTypes.Normal,
        monsterIndex: 1,
        dungeon: lorienDungeon,
        difficulty: 0);
    var lorienBossExperience = pvfDungeonExperience.CalculateMonsterExperience(
        characterLevel: 1,
        monsterLevel: 1,
        monsterType: GameDungeonMonsterTypes.Boss,
        monsterIndex: 1,
        dungeon: lorienDungeon,
        difficulty: 0);
    var lorienNamedExperience = pvfDungeonExperience.CalculateMonsterExperience(
        characterLevel: 1,
        monsterLevel: 1,
        monsterType: GameDungeonMonsterTypes.Normal,
        monsterIndex: 50_000,
        dungeon: lorienDungeon,
        difficulty: 0);
    var lorienClearExperience = pvfDungeonExperience.CalculateClearExperience(
        characterLevel: 1,
        dungeon: lorienDungeon,
        difficulty: 0,
        resultCode: 55,
        totalKilledMonsterCount: 10);
    var lorienPartyClearExperience = pvfDungeonExperience.CalculateClearExperience(
        characterLevel: 1,
        dungeon: lorienDungeon,
        difficulty: 0,
        resultCode: 55,
        totalKilledMonsterCount: 10,
        partyMemberCount: 2);
    var scaledDungeonExperience = new DungeonExperienceCatalog(
        new ServerOptions
        {
            Experience = new ExperienceOptions
            {
                MonsterMultiplier = 2,
                ClearMultiplier = 2
            }
        },
        pvfScripts,
        NullLogger<DungeonExperienceCatalog>.Instance);
    Check(lorienNormalExperience == 30
            && lorienBossExperience == 120
            && lorienNamedExperience == 90
            && lorienClearExperience.BaseExperience == 150
            && lorienClearExperience.RankBonus == 7
            && lorienClearExperience.PartyBonus == 0
            && lorienClearExperience.TotalExperience == 157
            && lorienPartyClearExperience.BaseExperience == 75
            && lorienPartyClearExperience.PartyBonus == 37
            && lorienPartyClearExperience.RankBonus == 5
            && lorienPartyClearExperience.TotalExperience == 117
            && scaledDungeonExperience.CalculateMonsterExperience(
                characterLevel: 1,
                monsterLevel: 1,
                monsterType: GameDungeonMonsterTypes.Normal,
                monsterIndex: 1,
                dungeon: lorienDungeon,
                difficulty: 0) == 60
            && pvfDungeonExperience.CalculateMonsterExperience(
                characterLevel: 57,
                monsterLevel: 1,
                monsterType: GameDungeonMonsterTypes.Normal,
                monsterIndex: 1,
                dungeon: lorienDungeon,
                difficulty: 0) == 1
            && scaledDungeonExperience.CalculateClearExperience(
                characterLevel: 1,
                dungeon: lorienDungeon,
                difficulty: 0,
                resultCode: 55,
                totalKilledMonsterCount: 10).TotalExperience == 315
            && DungeonExperienceCatalog.GetLevelPenalty(7, 1) == 0.2
            && DungeonExperienceCatalog.GetLevelPenalty(1, 4) == 1.12,
        "monster, boss, named-monster, party, clear-base and rank EXP remain distinct and honor reference penalties plus operator multipliers");
    Check(pvfDungeonCatalog.TryCreateRoom(
                1,
                lorienDungeon.StartX,
                lorienDungeon.StartY,
                out var positionedLorienRoom,
                random: new ZeroDropRandomSource())
            && positionedLorienRoom.Monsters.Length > 0
            && positionedLorienRoom.Monsters.All(monster =>
                monster.X > 0 && monster.Y > 0),
        "map monster rows retain PVF coordinates for exact ground-drop replay on room revisit");
    DungeonRoomDefinition? parsedPassiveRoom = null;
    var parsedPassiveItemSpawn = false;
    var parsedPassiveMonsterSpawn = false;
    foreach (var parsedDungeonId in pvfDungeonCatalog.DungeonIds)
    {
        if (!pvfDungeonCatalog.TryGetDefinition(parsedDungeonId, out var parsedDungeon))
        {
            continue;
        }

        for (var roomY = 0; roomY < parsedDungeon.Height; roomY++)
        {
            for (var roomX = 0; roomX < parsedDungeon.Width; roomX++)
            {
                if (pvfDungeonCatalog.TryCreateRoom(
                        parsedDungeonId,
                        checked((byte)roomX),
                        checked((byte)roomY),
                        out var candidateRoom,
                        random: new ZeroDropRandomSource())
                    && candidateRoom.PassiveObjects.Length > 0)
                {
                    parsedPassiveRoom ??= candidateRoom;
                    parsedPassiveItemSpawn |= candidateRoom.PassiveItemSpawns.Length > 0;
                    parsedPassiveMonsterSpawn |= candidateRoom.Monsters.Any(monster =>
                        monster.IsBoxMonster == 1);
                }
            }

            if (parsedPassiveItemSpawn && parsedPassiveMonsterSpawn)
            {
                break;
            }
        }

        if (parsedPassiveItemSpawn && parsedPassiveMonsterSpawn)
        {
            break;
        }
    }

    Check(parsedPassiveRoom is not null
            && parsedPassiveRoom.PassiveObjects.Select(passiveObject => passiveObject.ObjectIndex)
                .SequenceEqual(Enumerable.Range(0, parsedPassiveRoom.PassiveObjects.Length)
                    .Select(index => (byte)index)),
        "DFLegacy map special passive objects produce ordered object associations and runtime actions");
    Check(parsedPassiveItemSpawn && parsedPassiveMonsterSpawn,
        "DFLegacy passive actions generate both hidden item records and linked monsters");
    var pvfItemCatalog = CreateItems(pvfScripts);
    pvfItemCatalog.Initialize();
    EquipmentReinforcementSmokeTests.CheckRealPvf(
        Check,
        pvfScripts,
        pvfItemCatalog);
    AvatarCompoundSmokeTests.CheckRealPvf(
        Check,
        pvfScripts,
        pvfItemCatalog);
    CompoundItemSmokeTests.CheckRealPvf(
        Check,
        pvfScripts,
        pvfItemCatalog);
    EquipmentQualitySmokeTests.CheckRealPvf(
        Check,
        pvfScripts,
        pvfItemCatalog);
    Check(pvfItemCatalog.TryGetDefinition(
            CreatureHungerPlanner.FoodItemId,
            out var pvfCreatureFood)
        && pvfCreatureFood.ScriptPath.Equals(
            "stackable/cash/creature/creature_food.stk",
            StringComparison.OrdinalIgnoreCase)
        && pvfCreatureFood.TypeTag == "feed"
        && pvfCreatureFood.InventoryCategory == ItemInventoryCategory.Creature,
        "real PVF item 24 is the fixed Creature food consumed by automatic hunger settlement");
    var pvfDisjointCatalog = new DisjointCatalog(
        pvfScripts,
        NullLogger<DisjointCatalog>.Instance);
    pvfDisjointCatalog.Initialize();
    Check(pvfDisjointCatalog.IsAvailable
            && pvfDisjointCatalog.PrimaryItemId == 3_037
            && pvfItemCatalog.TryGetDefinition(12_007, out var pvfDisjointSource)
            && pvfDisjointCatalog.TryGenerate(
                pvfDisjointSource,
                additionalItemRoll: 0,
                jackpotRoll: 500,
                pvfItemCatalog,
                out var pvfDisjointRewards)
            && pvfDisjointRewards.SequenceEqual(
            [
                new DisjointRewardDefinition(3_037, 8),
                new DisjointRewardDefinition(3_033, 1)
            ]),
        "the real DFLegacy PVF produces the proven level-10 uncommon disjoint result");
    Check(pvfItemCatalog.TryGetDefinition(7_143, out var pvfLottery)
            && pvfLottery.Lottery is not null
            && pvfLottery.Lottery.FallbackReward
                == new LotteryRewardDefinition(29_901, 1, 0)
            && pvfLottery.Lottery.WeightedRewards.Count == 41
            && pvfLottery.Lottery.WeightedRewards.Sum(reward => reward.Weight) == 93_618,
        "the real Fighter weapon jar caches its 41 weighted outcomes and 6.382 percent fallback");
    Check(pvfItemCatalog.Count >= 10_942
        && pvfItemCatalog.TryGetDefinition(10_000, out var cachedEquipment)
        && cachedEquipment is
        {
            ScriptKind: ItemScriptKind.Equipment,
            InventoryCategory: ItemInventoryCategory.Equipment,
            Grade: 1,
            AttachType: ItemAttachType.Free
        }
        && pvfItemCatalog.TryGetDefinition(3_037, out var cachedMaterial)
        && cachedMaterial is
        {
            ScriptKind: ItemScriptKind.Stackable,
            InventoryCategory: ItemInventoryCategory.Material,
            Grade: 5,
            AttachType: ItemAttachType.Free,
            Price: 100
        }
        && pvfItemCatalog.TryGetDefinition(39_000, out var cachedAvatar)
        && cachedAvatar.InventoryCategory == ItemInventoryCategory.Avatar,
        "all DFLegacy equ/stk scripts are cached by id with pickup classification and hot tags");
    var pvfSkillCatalog = new SkillCatalog(
        pvfScripts,
        NullLogger<SkillCatalog>.Instance);
    Check(pvfSkillCatalog.TryGetDefinition(0, 1, out var pvfSkill)
        && pvfSkill.MaximumLevel == 10,
        "DFLegacy skill definitions are parsed directly from Script.pvf");
    Check(pvfSkillCatalog.TryGetDefinition(0, 81, out var contractSkill)
        && contractSkill.RequiredCharacterLevelFor(9) == 61
        && pvfPremiumBenefits.GetEffectiveSkillLevel(
            60,
            [GameProtocolEngine.MasterContractServiceType]) >=
            contractSkill.RequiredCharacterLevelFor(9),
        "active Master contract permits the observed level-60 character to learn skill 81 level 9");
    var pvfQuestCatalog = new QuestCatalog(
        pvfScripts,
        NullLogger<QuestCatalog>.Instance);
    Check(pvfQuestCatalog.CompletionMapping.ContainsKey(116)
            && pvfQuestCatalog.CompletionMapping.Values.All(index =>
                index >= 0 && index / 512 <= 8 && index % 512 < 256),
        "real PVF maps the hidden-dungeon quest into a valid DF2008 completion bitmap");
    Check(new ushort[] { 1, 2, 10 }.All(id => pvfQuestCatalog.TryGetDefinition(id, out _)),
        "DFLegacy quest definitions are parsed directly from Script.pvf");
    Check(pvfQuestCatalog.TryGetDefinition(
            QuestCatalog.TutorialCompletionQuestId,
            out var pvfTutorialCompletionQuest)
        && QuestCatalog.IsTutorialCompletionQuestDefinition(pvfTutorialCompletionQuest)
        && !pvfQuestCatalog.GetAcceptableQuestIds(
                new CharacterRecord(Guid.NewGuid(), "TutorialProbe", 0, 0, 1, 0, 0),
                [],
                [],
                preferredNpcIndex: 2)
            .Contains(QuestCatalog.TutorialCompletionQuestId),
        "target PVF quest 1016 is reserved for server-side tutorial completion activation");
    Check(pvfQuestCatalog.TryGetDefinition(201, out var lostHammerQuest)
        && lostHammerQuest.DependGiveItems.SequenceEqual(
        [
            new QuestDependGiveItemDefinition(3080, 1)
        ])
        && pvfItemCatalog.TryGetDefinition(3080, out var lostHammerItem)
        && lostHammerItem.ScriptKind == ItemScriptKind.Stackable
        && lostHammerItem.InventoryCategory == ItemInventoryCategory.Quest
        && QuestAcceptancePlanner.Create(
                new Dictionary<ushort, CharacterItemRecord>(),
                lostHammerQuest,
                pvfItemCatalog,
                uint.MaxValue) is
            {
                Success: true,
                InsertedItems:
                [
                    {
                        Slot: CharacterInventoryLayout.QuestSlotStart,
                        ItemId: 3080,
                        CountOrSeed: 1
                    }
                ]
            },
        "target PVF quest 201 caches its depend-give item 3080 as an acceptance grant");
    Check(pvfQuestCatalog.TryGetDefinition(102, out var startAdventureQuest)
        && startAdventureQuest.Type == "condition under clear"
        && startAdventureQuest.SubType == QuestDungeonClearRequirement.SimpleClearSubType
        && startAdventureQuest.InitialTrigger == 1
        && startAdventureQuest.ConditionRows.Length == 1
        && startAdventureQuest.ConditionRows[0].SequenceEqual(new[] { 1, -1 })
        && QuestDungeonClearRequirement.Matches(
            startAdventureQuest,
            dungeonId: 1,
            difficulty: 0)
        && QuestDungeonClearRequirement.Matches(
            startAdventureQuest,
            dungeonId: 1,
            difficulty: 3)
        && !QuestDungeonClearRequirement.Matches(
            startAdventureQuest,
            dungeonId: 2,
            difficulty: 0)
        && startAdventureQuest.TryApplyTriggerAction(
            startAdventureQuest.InitialTrigger,
            0x10,
            out var completedStartAdventureTrigger)
        && startAdventureQuest.IsTriggerComplete(completedStartAdventureTrigger),
        "quest 102 starts incomplete and only a Lorien clear can satisfy its simple-clear condition");
    Check(pvfQuestCatalog.TryGetDefinition(416, out var valueMeetNpcQuest)
        && valueMeetNpcQuest.Type == "meet npc"
        && valueMeetNpcQuest.NpcIndex == 25
        && valueMeetNpcQuest.CompleteNpcIndex == 9
        && valueMeetNpcQuest.InitialTrigger == 1
        && valueMeetNpcQuest.ConditionRows.Length == 1
        && valueMeetNpcQuest.ConditionRows[0].SequenceEqual(new[] { 9 })
        && QuestMeetNpcRequirement.TryApplyClientCompletion(
            valueMeetNpcQuest,
            valueMeetNpcQuest.InitialTrigger,
            QuestMeetNpcRequirement.CompleteTriggerAction,
            out var completedMeetNpcTrigger)
        && valueMeetNpcQuest.IsTriggerComplete(completedMeetNpcTrigger)
        && !QuestMeetNpcRequirement.TryApplyClientCompletion(
            valueMeetNpcQuest,
            valueMeetNpcQuest.InitialTrigger,
            action: 1,
            out var rejectedMeetNpcTrigger)
        && rejectedMeetNpcTrigger == valueMeetNpcQuest.InitialTrigger,
        "quest 416 accepts DF2008's action-zero meet-NPC completion without changing its NPC definitions");
    Check(pvfQuestCatalog.TryGetDefinition(1005, out var linusLowHitQuest)
        && linusLowHitQuest.Type == "condition under clear"
        && linusLowHitQuest.SubType == 1
        && linusLowHitQuest.InitialTrigger == 1
        && linusLowHitQuest.ConditionRows.Length == 1
        && linusLowHitQuest.ConditionRows[0].SequenceEqual(new[] { 2, -1, 12 })
        && QuestDungeonClearRequirement.Matches(
            linusLowHitQuest,
            dungeonId: 2,
            difficulty: 0)
        && QuestDungeonClearRequirement.TryApplyClientCompletion(
            linusLowHitQuest,
            QuestDungeonClearRequirement.CompleteTriggerAction,
            out var completedLowHitTrigger)
        && linusLowHitQuest.IsTriggerComplete(completedLowHitTrigger)
        && !QuestDungeonClearRequirement.TryApplyClientCompletion(
            linusLowHitQuest,
            action: 0x10,
            out _),
        "quest 1005 accepts DF2008's action-zero conditional-clear completion without treating it as a packed hunt counter");
    var minimumDifficultyClearQuest = linusLowHitQuest with
    {
        SubType = QuestDungeonClearRequirement.SimpleClearSubType,
        ConditionRows = [new[] { 2, 2 }]
    };
    Check(!QuestDungeonClearRequirement.Matches(
            minimumDifficultyClearQuest,
            dungeonId: 2,
            difficulty: 1)
        && QuestDungeonClearRequirement.Matches(
            minimumDifficultyClearQuest,
            dungeonId: 2,
            difficulty: 2)
        && QuestDungeonClearRequirement.Matches(
            minimumDifficultyClearQuest,
            dungeonId: 2,
            difficulty: 3),
        "DF2008 conditional-clear difficulty is a minimum threshold while -1 accepts every difficulty");
    Check(Enumerable.Range(0, 8).All(subType =>
        QuestDungeonClearRequirement.IsDungeonClearCondition(
            linusLowHitQuest with { SubType = subType }))
        && !QuestDungeonClearRequirement.IsDungeonClearCondition(
            linusLowHitQuest with { SubType = 8 }),
        "conditional-clear policy recognizes all eight DF2008 subtypes and rejects unknown subtypes");
    Check(pvfQuestCatalog.TryGetDefinition(2601, out var skyCastleHellOpeningQuest)
        && skyCastleHellOpeningQuest.Type == "seeking"
        && skyCastleHellOpeningQuest.RewardType == "hell challenge"
        && skyCastleHellOpeningQuest.MonsterRewardItems.Length == 24
        && skyCastleHellOpeningQuest.MonsterRewardItems[0]
            == new QuestMonsterRewardItemDefinition(53, -1, 3, 4134, 1, 100, 1)
        && skyCastleHellOpeningQuest.ConditionRows.Length == 6
        && skyCastleHellOpeningQuest.ConditionRows[0].SequenceEqual(new[] { 4134, 1 })
        && pvfItemCatalog.TryGetDefinition(4134, out var selionSoulOrb)
        && selionSoulOrb.InventoryCategory == ItemInventoryCategory.Quest
        && QuestItemDropPlanner.PlanMonster(
                skyCastleHellOpeningQuest,
                dungeonId: 999,
                difficulty: 3,
                monsterIndex: 53,
                inventory: [],
                _ => false).SequenceEqual([new QuestItemDrop(4134, 1)])
        && QuestItemDropPlanner.PlanMonster(
                skyCastleHellOpeningQuest,
                dungeonId: 999,
                difficulty: 0,
                monsterIndex: 53,
                inventory: [],
                _ => false).Count == 0
        && QuestItemDropPlanner.PlanMonster(
                skyCastleHellOpeningQuest,
                dungeonId: 999,
                difficulty: 0,
                monsterIndex: 53,
                inventory: [],
                chance => chance == 10).SequenceEqual([new QuestItemDrop(4134, 1)]),
        "real hell-opening quest 2601 resolves its boss soul-orb STK rows through the generic quest drop planner");
    Check(pvfQuestCatalog.TryGetDefinition(917, out var berserkerAwakeningTaskQuest)
        && berserkerAwakeningTaskQuest.Type == "seeking"
        && berserkerAwakeningTaskQuest.MonsterRewardItems.Length == 4
        && berserkerAwakeningTaskQuest.MonsterRewardItems[0]
            == new QuestMonsterRewardItemDefinition(17, -1, -1, 4116, 1, 20, 3)
        && QuestItemDropPlanner.PlanMonster(
                berserkerAwakeningTaskQuest,
                dungeonId: 17,
                difficulty: 0,
                monsterIndex: 17,
                inventory:
                [
                    new CharacterItemRecord(
                        CharacterInventoryLayout.QuestSlotStart,
                        4116,
                        2)
                ],
                chance => chance == 20).SequenceEqual([new QuestItemDrop(4116, 1)]),
        "ordinary awakening quests use the same monster task-item planner and aggregate maximum-count rule as hell quests");
    Check(pvfQuestCatalog.TryGetDefinition(116, out var hiddenLorienQuest)
            && hiddenLorienQuest.ConditionRows.Length == 1
            && hiddenLorienQuest.ConditionRows[0].SequenceEqual(new[] { 9, -1 })
            && hiddenLorienQuest.AppearMap.SequenceEqual(new[] { -1, -1, -1, -1 })
            && HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
                    116,
                    hiddenLorienQuest,
                    pvfDungeonCatalog)
                .SequenceEqual(new ushort[] { 9 }),
        "hidden-dungeon migration resolves both the explicit quest map and parsed quest condition rows");
    Check(!HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
                9999, hiddenLorienQuest, pvfDungeonCatalog).Contains((ushort)9),
        "a quest merely referencing a hidden dungeon does not replace its unlock quest");
    Check(pvfQuestCatalog.TryGetDefinition(10, out var rescueLorianQuest)
            && rescueLorianQuest.DeleteNpcIndex == 10
            && rescueLorianQuest.InitialTrigger == 1
            && QuestMapRequirement.GetTargetMapId(rescueLorianQuest) == 2224
            && rescueLorianQuest.AppearMap.SequenceEqual(new[] { 14, -1, 2224, 100 }),
        "current PVF Lorian rescue links NPC 10 to dungeon 14 and clear-map 2224");
    Check(pvfQuestCatalog.TryGetDefinition(1702, out var pvpInitiationQuest)
            && QuestPvpRankRequirement.IsPvpRank(pvpInitiationQuest)
            && pvpInitiationQuest.ConditionRows[0].SequenceEqual(new[] { 1 })
            && QuestPvpRankRequirement.GetTrigger(pvpInitiationQuest, 2) == 0,
        "real PVF quest 1702 is satisfied by internal PvP grade 2 (8th)");
    var realClassChangeRewards = new (ushort QuestId, byte GrowType)[]
    {
        (803, 2), (806, 1), (809, 3), (812, 1), (816, 2), (819, 3),
        (822, 1), (825, 2), (831, 3), (834, 4), (837, 4), (840, 4),
        (844, 1), (848, 3), (853, 2), (856, 1), (859, 2), (862, 3)
    };
    var realAwakeningFinalQuestIds = new ushort[]
    {
        906, 912, 917, 922, 928, 933, 939, 945
    };
    Check(realClassChangeRewards.All(entry =>
            pvfQuestCatalog.TryGetDefinition(entry.QuestId, out var definition)
            && definition.JobChangeQuest == 1
            && definition.TryGetGrowthReward(out var chainType, out var growType)
            && chainType == 1
            && growType == entry.GrowType)
        && realAwakeningFinalQuestIds.All(questId =>
            pvfQuestCatalog.TryGetDefinition(questId, out var definition)
            && definition.JobChangeQuest == 2
            && definition.TryGetGrowthReward(out var chainType, out var growType)
            && chainType == 2
            && growType == 1),
        "all target-PVF class and awakening final quests resolve to their authoritative command 36 growth branch");
    var realBaseSwordman = new CharacterRecord(
        Guid.NewGuid(), "RealClassProbe", 0, 0, 18, 0, 0);
    var firstClassQuests = pvfQuestCatalog.GetAcceptableQuestIds(
        realBaseSwordman,
        [],
        []);
    var secondSoulBenderQuest = pvfQuestCatalog.GetAcceptableQuestIds(
        realBaseSwordman,
        [],
        [801]);
    Check(new ushort[] { 801, 804, 807, 832 }.All(firstClassQuests.Contains)
        && secondSoulBenderQuest.Contains((ushort)802)
        && !new ushort[] { 804, 807, 832 }.Any(secondSoulBenderQuest.Contains)
        && !pvfQuestCatalog.GetAcceptableQuestIds(
                realBaseSwordman with { GrowType = 2 },
                [],
                [])
            .Any(questId => questId is >= 801 and <= 862),
        "target-PVF swordman class chains expose all first choices, enforce collisions and disappear after advancement");
    var realUnawakenedRanger = new CharacterRecord(
        Guid.NewGuid(), "RealAwakeningProbe", 2, 1, 48, 0, 0);
    var firstRangerAwakeningQuests = pvfQuestCatalog.GetAcceptableQuestIds(
        realUnawakenedRanger,
        [],
        [],
        preferredNpcIndex: 17);
    var secondRangerAwakeningQuests = pvfQuestCatalog.GetAcceptableQuestIds(
        realUnawakenedRanger,
        [],
        [929],
        preferredNpcIndex: 17);
    Check(firstRangerAwakeningQuests.Contains((ushort)929)
        && !firstRangerAwakeningQuests.Contains((ushort)930)
        && secondRangerAwakeningQuests.Contains((ushort)930)
        && !pvfQuestCatalog.GetAcceptableQuestIds(
                realUnawakenedRanger with { AwakeningType = 1 },
                [],
                [],
                preferredNpcIndex: 17)
            .Any(questId => questId is >= 929 and <= 933),
        "target-PVF ranger awakening chain enforces numbered predecessors and closes after awakening");
    Check(pvfQuestCatalog.TryGetDefinition(933, out var rangerAwakeningQuest)
        && rangerAwakeningQuest.JobChangeQuest == 2
        && rangerAwakeningQuest.RewardType == "awakening type"
        && rangerAwakeningQuest.AwakeningType == 1
        && rangerAwakeningQuest.GrowthReward == 1
        && rangerAwakeningQuest.ScriptPath.EndsWith(
            "Epic Job/DP-5.qst",
            StringComparison.OrdinalIgnoreCase)
        && pvfQuestCatalog.TryGetDefinition(803, out var soulBenderClassQuest)
        && soulBenderClassQuest.JobChangeQuest == 1
        && soulBenderClassQuest.RewardType == "grow type"
        && soulBenderClassQuest.GrowthReward == 2
        && soulBenderClassQuest.PrerequisiteQuestIds.SequenceEqual(
            new ushort[] { 802 }),
        "real PVF class and awakening final quests cache their chain flags, paths, prerequisites and growth rewards");
    Check(pvfSkillCatalog.GetGrowTypeSkillChanges(0, 3).Any(change =>
            change.SkillId == 181 && change.Level == 0)
        && pvfSkillCatalog.GetGrowTypeSkillChanges(0, 3).Any(change =>
            change.SkillId == 182 && change.Level == 0)
        && pvfSkillCatalog.GetAwakeningSkillChanges(2, 1, 1).Any(change =>
            change.SkillId == 66
            && change.Level == 1
            && change.SkillClass < SkillCatalog.SkillClassCount),
        "real character .chr data exposes class removals and ranger awakening grants for the command 36 growth branch");
    Check(pvfQuestCatalog.TryGetDefinition(2101, out var farasEvolutionQuest)
        && farasEvolutionQuest.RewardType == "creature evolution"
        && farasEvolutionQuest.CreatureKind == 1
        && farasEvolutionQuest.CreatureLevel == 20
        && farasEvolutionQuest.EvolutionCreatureKind == 2
        && farasEvolutionQuest.InitialTrigger == 1
        && farasEvolutionQuest.TryApplyTriggerAction(1, 0x10, out var completedHuntTrigger)
        && completedHuntTrigger == 0
        && farasEvolutionQuest.TryApplyTriggerAction(
            completedHuntTrigger,
            0x10,
            out var cappedHuntTrigger)
        && cappedHuntTrigger == 0
        && !farasEvolutionQuest.IsTriggerComplete(1)
        && farasEvolutionQuest.IsTriggerComplete(completedHuntTrigger),
        "Faras evolution quest starts with one remaining target and action 0x10 decrements its first packed hunt counter");
    var farasEvolutionInventory = new Dictionary<ushort, CharacterItemRecord>
    {
        [CharacterCreatureInventoryLayout.EquippedCreatureSlot] = new(
            CharacterCreatureInventoryLayout.EquippedCreatureSlot,
            63_000,
            77,
            CreatureStomach: 100,
            CreatureExperience: 350,
            CreatureLevel: 21,
            CreatureName: "Faras",
            CreatureNoCharge: false)
    };
    Check(farasEvolutionQuest is not null
        && CharacterCreatureInventoryPlanner.TryEvolve(
            farasEvolutionInventory,
            farasEvolutionQuest,
            pvfItemCatalog,
            out var farasEvolutionPlan,
            out _)
        && farasEvolutionPlan.SourceItemId == 63_000
        && farasEvolutionPlan.EvolvedItemId == 63_001
        && farasEvolutionPlan.Inventory[
            CharacterCreatureInventoryLayout.EquippedCreatureSlot] is
            {
                ItemId: 63_001,
                CountOrValue: 77,
                CreatureStomach: 100,
                CreatureExperience: 350,
                CreatureLevel: 21,
                CreatureName: "Faras"
            },
        "Creature evolution changes only the species item while preserving uid, level, experience, hunger and name");

    var pvfWorldDrops = new WorldDropCatalog(
        pvfScripts,
        NullLogger<WorldDropCatalog>.Instance);
    var pvfMonsterDrops = new MonsterDropCatalog(
        pvfScripts,
        pvfItemCatalog,
        NullLogger<MonsterDropCatalog>.Instance);
    var pvfChampionDrops = new MonsterChampionDropCatalog(
        new ServerOptions(),
        pvfScripts,
        NullLogger<MonsterChampionDropCatalog>.Instance);
    Check(pvfChampionDrops.DefinitionCount > 0
            && pvfChampionDrops.ItemDropEntryCount > 0
            && pvfChampionDrops.CommonDropEntryCount > 0
            && pvfChampionDrops.SuperDropEntryCount > 0
            && pvfChampionDrops.TryGetDefinition(200, out var pvfHunterDrops)
            && pvfHunterDrops.CommonDrops.Contains(
                new MonsterChampionDropEntry(3_171, 7_000))
            && pvfHunterDrops.SuperDrops.Contains(
                new MonsterChampionDropEntry(3_166, 4_000))
            && pvfChampionDrops.TryGetDefinition(1_090, out var pvfSkullKaneDrops)
            && pvfSkullKaneDrops.ItemDrops.Contains(
                new MonsterChampionDropEntry(3_222, 10_000))
            && pvfChampionDrops.Roll(
                    1_090,
                    GameDungeonMonsterTypes.Normal,
                    new ZeroDropRandomSource())
                .Any(drop => drop.ItemId == 3_222 && drop.CountOrValue == 1),
        "DFLegacy Script.pvf exposes ordinary MOB item rows together with Hunter common and super champion-specific tables");
    var pvfHellDrops = new HellDropCatalog(
        pvfScripts,
        pvfMonsterDrops,
        NullLogger<HellDropCatalog>.Instance);
    Check(pvfHellDrops.ProbabilityBandCount == 4
            && pvfHellDrops.LevelRangeCount == 99
            && pvfHellDrops.GetDifficultyProbability(
                55,
                dungeonDifficulty: 0,
                hellPartyMode: 1) == 750
            && pvfHellDrops.GetDifficultyProbability(
                55,
                dungeonDifficulty: 0,
                hellPartyMode: 2) == 500
            && HellDropCatalog.ResolveDifficultyColumn(3, 1) == 3
            && HellDropCatalog.ResolveDifficultyColumn(3, 2) == 3
            && pvfHellDrops.TryGetLevelRange(55, out var hellLevel55Range)
            && hellLevel55Range.MinusLevel == 6
            && hellLevel55Range.PlusLevel == 1,
        "DFLegacy hell-drop probability, party-mode difficulty and level-window tables are parsed");
    Check(pvfHellDrops.RollRarity(
                0,
                new SequenceDropRandomSource(7_998)) == 0
            && pvfHellDrops.RollRarity(
                0,
                new SequenceDropRandomSource(7_999)) == 1
            && pvfHellDrops.RollRarity(
                0,
                new SequenceDropRandomSource(8_999)) == 2
            && pvfHellDrops.RollRarity(
                0,
                new SequenceDropRandomSource(9_499)) == 3
            && pvfHellDrops.RollRarity(
                0,
                new SequenceDropRandomSource(9_999)) == 4
            && pvfHellDrops.RollRarity(
                3,
                new SequenceDropRandomSource(9_698)) == 3
            && pvfHellDrops.RollRarity(
                3,
                new SequenceDropRandomSource(9_699)) == 3,
        "hell equipment rarity respects the PVF table at every dungeon difficulty");
    Check(pvfHellDrops.TryRollEquipment(
                dungeonLevel: 55,
                dungeonDifficulty: 0,
                hellPartyMode: 1,
                new ZeroDropRandomSource(),
                out var hellEquipmentId)
            && pvfItemCatalog.TryGetDefinition(hellEquipmentId, out var hellEquipment)
            && hellEquipment.InventoryCategory == ItemInventoryCategory.Equipment
            && hellEquipment.Grade is >= 49 and <= 55
            && hellEquipment.Rarity == 0,
        "a cosmofiend passing the probability gate rolls weighted equipment with common-rarity fallback support");
    Check(pvfHellDrops.TryRollEquipment(55, 0, 1,
                new SequenceDropRandomSource(751, 0), out var missedHellItem)
            && pvfItemCatalog.TryGetDefinition(missedHellItem, out var guaranteedHellItem)
            && guaranteedHellItem.Rarity == 0
            && pvfHellDrops.TryRollEquipment(55, 0, 2,
                new SequenceDropRandomSource(501, 0), out _)
            && !pvfHellDrops.TryRollEquipment(55, 0, 0,
                new SequenceDropRandomSource(), out _),
        "hell probability misses guarantee common equipment without a rarity roll; ordinary mode cannot roll hell equipment");
    Check(pvfHellDrops.TryRollEquipment(55, 0, 1,
                new SequenceDropRandomSource(750, 0, 0), out _)
            && pvfHellDrops.TryRollEquipment(55, 0, 2,
                new SequenceDropRandomSource(500, 0, 0), out _),
        "hell probability boundaries are inclusive and party mode affects the selected PVF column");
    var hasWorldDropLevel1 = pvfWorldDrops.TryGetLevel(1, out var worldDropLevel1);
    var hasWorldDropLevel55 = pvfWorldDrops.TryGetLevel(55, out var worldDropLevel55);
    Check(hasWorldDropLevel1
        && worldDropLevel1.Items.Count > 0
        && worldDropLevel1.TotalWeight
            == worldDropLevel1.Items.Sum(item => item.Weight)
        && hasWorldDropLevel55
        && worldDropLevel55.Items.Count > 0
        && worldDropLevel55.TotalWeight
            == worldDropLevel55.Items.Sum(item => item.Weight),
        "DFLegacy worlddrop.etc keeps every positive candidate and derives each level total from current weights");
    Check(pvfGoldDrops.TryGetLevel(55, out var goldDropLevel55)
        && goldDropLevel55.BaseAmount == 1_050
        && goldDropLevel55.SpreadPercent == 15,
        "DFLegacy gold table maps level 55 to 1050 with a 15 percent spread");
    Check(pvfMonsterDrops.GetGoldProbability(1) == 1_150
        && pvfMonsterDrops.GetGoldProbability(55) == 875
        && pvfMonsterDrops.GetGoldMultiplier(1, 0, false) == 1
        && pvfMonsterDrops.GetGoldMultiplier(1, 0, true) == 4,
        "DFLegacy monster gold probabilities and boss multiplier are parsed");
    Check(pvfMonsterDrops.TryGetProbabilityBand(1, out var monsterLevel1Band)
            && monsterLevel1Band is
            {
                GoldProbability: 1_150,
                ConsumableProbability: 60,
                EquipmentProbability: 200,
                RecipeProbability: 15,
                ArtifactProbability: 30
            }
            && pvfMonsterDrops.TryGetProbabilityBand(55, out var monsterLevel55Band)
            && monsterLevel55Band is
            {
                GoldProbability: 875,
                ConsumableProbability: 10,
                EquipmentProbability: 32,
                RecipeProbability: 3,
                ArtifactProbability: 5
            },
        "DFLegacy monster drop bands preserve all five configured branches");
    Check(pvfMonsterDrops.GetProbability(15, MonsterDropKind.Equipment) == 200
            && pvfMonsterDrops.GetProbability(16, MonsterDropKind.Equipment) == 100
            && pvfMonsterDrops.GetProbability(30, MonsterDropKind.Equipment) == 100
            && pvfMonsterDrops.GetProbability(31, MonsterDropKind.Equipment) == 65
            && pvfMonsterDrops.GetProbability(40, MonsterDropKind.Equipment) == 65
            && pvfMonsterDrops.GetProbability(41, MonsterDropKind.Equipment) == 32
            && pvfMonsterDrops.GetProbability(99, MonsterDropKind.Equipment) == 32,
        "DFLegacy monster equipment probability bands include every level boundary");
    Check(pvfMonsterDrops.GetRarityThresholds(MonsterDropKind.Consumable)
            .SequenceEqual([7_000, 9_499, 9_999, 10_000, 10_001])
            && pvfMonsterDrops.GetRarityThresholds(MonsterDropKind.Equipment)
                .SequenceEqual([7_000, 9_779, 9_999, 10_000, 10_001])
            && pvfMonsterDrops.GetRarityThresholds(MonsterDropKind.Recipe)
                .SequenceEqual([5_000, 9_499, 9_999, 10_000, 10_001])
            && pvfMonsterDrops.GetRarityThresholds(MonsterDropKind.Artifact)
                .SequenceEqual([7_000, 9_499, 9_999, 10_000, 10_001])
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(6_999)) == 0
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(7_000)) == 1
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(9_778)) == 1
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(9_779)) == 2
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(9_998)) == 2
            && pvfMonsterDrops.RollRarity(
                MonsterDropKind.Equipment,
                new SequenceDropRandomSource(9_999)) == 3,
        "all four rarity rows are parsed and equipment uses the original 1-to-10000 boundaries");
    Check(pvfMonsterDrops.LevelRangeCount == 99
            && pvfMonsterDrops.TryGetLevelRange(55, out var monsterLevel55Range)
            && monsterLevel55Range.MinusLevel == 6
            && monsterLevel55Range.PlusLevel == 1
            && pvfMonsterDrops.EquipmentCandidateCount > 0
            && pvfMonsterDrops.WeightedTableCount > 0
            && pvfMonsterDrops.TryGetWeightedTableStats(
                55,
                0,
                out var monsterLevel55CandidateCount,
                out var monsterLevel55TotalWeight)
            && monsterLevel55CandidateCount > 0
            && monsterLevel55TotalWeight > 0
            && pvfMonsterDrops.TryChooseEquipment(
                55,
                0,
                new ZeroDropRandomSource(),
                out var monsterLevel55EquipmentId)
            && pvfItemCatalog.TryGetDefinition(
                monsterLevel55EquipmentId,
                out var monsterLevel55Equipment)
            && monsterLevel55Equipment.InventoryCategory == ItemInventoryCategory.Equipment
            && monsterLevel55Equipment.Grade is >= 49 and <= 55
            && monsterLevel55Equipment.CreationRate > 0,
        "monster equipment tables are prebuilt from cached grade, rarity and creation-rate metadata");
    var weightedDefinitions = pvfItemCatalog.Definitions.Values
        .Where(HasPositiveDropWeight)
        .ToArray();
    var expectedMonsterCandidateCounts = new Dictionary<MonsterDropKind, int>
    {
        [MonsterDropKind.Consumable] = weightedDefinitions.Count(definition =>
            definition.ScriptKind == ItemScriptKind.Stackable
            && definition.TypeTag != "recipe"),
        [MonsterDropKind.Equipment] = weightedDefinitions.Count(definition =>
            definition.ScriptKind == ItemScriptKind.Equipment
            && definition.InventoryCategory == ItemInventoryCategory.Equipment),
        [MonsterDropKind.Recipe] = weightedDefinitions.Count(definition =>
            definition.ScriptKind == ItemScriptKind.Stackable
            && definition.TypeTag == "recipe"),
        [MonsterDropKind.Artifact] = weightedDefinitions.Count(definition =>
            definition.ScriptKind == ItemScriptKind.Equipment
            && definition.TypeTag is "artifact red" or "artifact blue" or "artifact green")
    };
    Check(expectedMonsterCandidateCounts[MonsterDropKind.Consumable] == 0
            && expectedMonsterCandidateCounts[MonsterDropKind.Equipment] > 0
            && expectedMonsterCandidateCounts[MonsterDropKind.Recipe] > 0
            && expectedMonsterCandidateCounts[MonsterDropKind.Artifact] == 0
            && expectedMonsterCandidateCounts.All(pair =>
                pvfMonsterDrops.HasItemPool(pair.Key)
                && pvfMonsterDrops.GetCandidateCount(pair.Key) == pair.Value)
            && pvfItemCatalog.Definitions.Values
                .Where(definition => definition.TypeTag == "creature")
                .All(definition => !pvfMonsterDrops.IsItemCandidate(
                    MonsterDropKind.Artifact,
                    definition.Id)),
        "four monster item pools use exact stackable, equipment, recipe and artifact filters");
    var weightedPoolsValid = true;
    foreach (var pair in expectedMonsterCandidateCounts)
    {
        if (pair.Value == 0)
        {
            weightedPoolsValid &= pvfMonsterDrops.GetWeightedTableCount(pair.Key) == 0;
            continue;
        }

        var foundTable = false;
        for (byte level = 1; level <= 99 && !foundTable; level++)
        {
            for (byte rarity = 0; rarity <= 4; rarity++)
            {
                if (!pvfMonsterDrops.TryGetWeightedTableStats(
                        pair.Key,
                        level,
                        rarity,
                        out var candidateCount,
                        out var totalWeight)
                    || candidateCount <= 0
                    || totalWeight <= 0)
                {
                    continue;
                }

                foundTable = pvfMonsterDrops.TryChooseItem(
                        pair.Key,
                        level,
                        rarity,
                        new ZeroDropRandomSource(),
                        out var itemId)
                    && pvfItemCatalog.TryGetDefinition(itemId, out var definition)
                    && pvfMonsterDrops.TryGetLevelRange(level, out var range)
                    && definition.Grade >= Math.Max(1, level - range.MinusLevel)
                    && definition.Grade < level + range.PlusLevel
                    && MatchesMonsterDropKind(definition, pair.Key);
                break;
            }
        }

        weightedPoolsValid &= foundTable;
    }

    Check(weightedPoolsValid,
        "each monster item kind has an independent cached table matching its positive creation-rate data");
    Check(Math.Abs(
                pvfMonsterDrops.GetMultiplier(
                    MonsterDropKind.Gold,
                    partyMemberCount: 4,
                    dungeonDifficulty: 3,
                    monsterType: GameDungeonMonsterTypes.Boss)
                - 4) < 0.000_001
            && new[]
                {
                    MonsterDropKind.Consumable,
                    MonsterDropKind.Equipment,
                    MonsterDropKind.Recipe,
                    MonsterDropKind.Artifact
                }
                .All(kind => Math.Abs(
                    pvfMonsterDrops.GetMultiplier(
                        kind,
                        partyMemberCount: 4,
                        dungeonDifficulty: 3,
                        monsterType: GameDungeonMonsterTypes.Boss)
                    - 10.24) < 0.000_001),
        "all five party, difficulty and monster-type multiplier rows are parsed");
    Check(pvfObjectDrops.TryGetProbabilityBand(1, out var objectLevel1)
            && objectLevel1.GoldProbability == 125
            && objectLevel1.EquipmentProbability == 25
            && pvfObjectDrops.TryGetLevelRange(55, out var objectLevel55Range)
            && objectLevel55Range.MinusLevel == 6
            && objectLevel55Range.PlusLevel == 1
            && pvfObjectDrops.EquipmentCandidateCount > 0,
        "DFLegacy object-drop probabilities, grade window and weighted equipment candidates are parsed");
    var forcedObjectDropGenerator = new ObjectDropGenerator(
        new ServerOptions
        {
            Drop = new DropOptions { ForceDrops = true }
        },
        pvfObjectDrops,
        pvfGoldDrops);
    var forcedObjectDrops = forcedObjectDropGenerator.Generate(
        1,
        dungeonDifficulty: 0,
        random: new ZeroDropRandomSource());
    Check(forcedObjectDrops.Any(drop => drop.IsGold)
            && forcedObjectDrops.Any(drop => !drop.IsGold && drop.ItemId != 0),
        "a forced DFLegacy object draw independently generates gold and a valid weighted equipment item");

    var normalDropGenerator = new DungeonDropGenerator(
        new ServerOptions(),
        pvfWorldDrops,
        pvfGoldDrops,
        pvfMonsterDrops);
    var level1WorldTotalWeight = hasWorldDropLevel1
        ? worldDropLevel1.TotalWeight
        : 0;
    var level1WorldHit = normalDropGenerator.Generate(
        1,
        isBoss: false,
        random: new SequenceDropRandomSource(
            9_999,
            9_999,
            9_999,
            9_999,
            9_999,
            0,
            0));
    var level1WorldLastCandidateHit = normalDropGenerator.Generate(
        1,
        isBoss: false,
        random: new SequenceDropRandomSource(
            9_999,
            9_999,
            9_999,
            9_999,
            9_999,
            0,
            Math.Max(0, level1WorldTotalWeight - 1)));
    var level1WorldCanMiss = level1WorldTotalWeight < 100_000;
    var level1WorldMiss = level1WorldCanMiss
        ? normalDropGenerator.Generate(
            1,
            isBoss: false,
            random: new SequenceDropRandomSource(
                9_999,
                9_999,
                9_999,
                9_999,
                9_999,
                Math.Max(0, level1WorldTotalWeight)))
        : [];
    Check(level1WorldHit.Count == 1
        && !level1WorldHit[0].IsGold
        && level1WorldLastCandidateHit.Count == 1
        && level1WorldLastCandidateHit[0].ItemId
            == worldDropLevel1.Items[^1].ItemId
        && (!level1WorldCanMiss || level1WorldMiss.Count == 0),
        "level 1 world-drop selection spans every current candidate by proportional weight");

    var level55WorldTotalWeight = hasWorldDropLevel55
        ? worldDropLevel55.TotalWeight
        : 0;
    var level55WorldHit = normalDropGenerator.Generate(
        55,
        isBoss: false,
        random: new SequenceDropRandomSource(
            9_999,
            9_999,
            9_999,
            9_999,
            9_999,
            0,
            0));
    var level55WorldCanMiss = level55WorldTotalWeight < 100_000;
    var level55WorldMiss = level55WorldCanMiss
        ? normalDropGenerator.Generate(
            55,
            isBoss: false,
            random: new SequenceDropRandomSource(
                9_999,
                9_999,
                9_999,
                9_999,
                9_999,
                Math.Max(0, level55WorldTotalWeight)))
        : [];
    Check(level55WorldHit.Count == 1
        && !level55WorldHit[0].IsGold
        && (!level55WorldCanMiss || level55WorldMiss.Count == 0),
        "level 55 world-drop chance follows the complete current candidate weight sum");

    var level55EquipmentHit = normalDropGenerator.Generate(
        55,
        isBoss: false,
        random: new SequenceDropRandomSource(
            9_999,
            9_999,
            31,
            0,
            0,
            9_999,
            9_999,
            99_999));
    var level55EquipmentMiss = normalDropGenerator.Generate(
        55,
        isBoss: false,
        random: new SequenceDropRandomSource(
            9_999,
            9_999,
            32,
            9_999,
            9_999,
            99_999));
    Check(level55EquipmentHit.Count == 1
            && !level55EquipmentHit[0].IsGold
            && pvfItemCatalog.TryGetDefinition(
                level55EquipmentHit[0].ItemId,
                out var generatedMonsterEquipment)
            && generatedMonsterEquipment.InventoryCategory == ItemInventoryCategory.Equipment
            && generatedMonsterEquipment.Grade is >= 49 and <= 55
            && level55EquipmentMiss.Count == 0,
        "level 55 normal-monster equipment boundary is exactly 32 out of 10000");

    var forcedDropGenerator = new DungeonDropGenerator(
        new ServerOptions
        {
            Drop = new DropOptions { ForceDrops = true }
        },
        pvfWorldDrops,
        pvfGoldDrops,
        pvfMonsterDrops);
    var minimumGoldDrop = forcedDropGenerator.Generate(
        55,
        isBoss: false,
        random: new SequenceDropRandomSource(
            0, 0, 0, 0, 9_499, 0, 0, 0));
    var maximumGoldDrop = forcedDropGenerator.Generate(
        55,
        isBoss: false,
        random: new SequenceDropRandomSource(
            30, 0, 0, 0, 9_499, 0, 0, 0));
    Check(minimumGoldDrop.First(drop => drop.IsGold).CountOrValue == 893
        && maximumGoldDrop.First(drop => drop.IsGold).CountOrValue == 1_207,
        "level 55 gold amount covers the complete integer minus/plus 15 percent range");
    var forcedLevel42Drops = forcedDropGenerator.Generate(
        42,
        isBoss: false,
        random: new SequenceDropRandomSource(
            0, 0, 0, 0, 9_499, 0, 0, 0));
    Check(forcedLevel42Drops.Count == 4
            && forcedLevel42Drops.Count(drop => drop.IsGold) == 1
            && forcedLevel42Drops.Count(drop => !drop.IsGold) == 3
            && pvfItemCatalog.TryGetDefinition(
                forcedLevel42Drops[1].ItemId,
                out var generatedEquipment)
            && MatchesMonsterDropKind(
                generatedEquipment,
                MonsterDropKind.Equipment)
            && pvfItemCatalog.TryGetDefinition(
                forcedLevel42Drops[2].ItemId,
                out var generatedRecipe)
            && MatchesMonsterDropKind(
                generatedRecipe,
                MonsterDropKind.Recipe),
        "gold, equipment, recipe and world-drop branches can coexist while zero-weight pools stay empty");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Smoke tests failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(" - " + failure);
    }
    return 1;
}

Console.WriteLine("All DFLegacy protocol smoke tests passed.");
return 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

void CheckThrows<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
        failures.Add(name);
    }
    catch (TException)
    {
    }
}

static bool HasPositiveDropWeight(ItemDefinition definition) =>
    definition.Grade is > 0 and <= byte.MaxValue
    && definition.Rarity is >= 0 and <= 4
    && definition.CreationRate > 0;

static bool MatchesMonsterDropKind(
    ItemDefinition definition,
    MonsterDropKind kind) =>
    kind switch
    {
        MonsterDropKind.Consumable =>
            definition.ScriptKind == ItemScriptKind.Stackable
            && definition.TypeTag != "recipe",
        MonsterDropKind.Equipment =>
            definition.ScriptKind == ItemScriptKind.Equipment
            && definition.InventoryCategory == ItemInventoryCategory.Equipment,
        MonsterDropKind.Recipe =>
            definition.ScriptKind == ItemScriptKind.Stackable
            && definition.TypeTag == "recipe",
        MonsterDropKind.Artifact =>
            definition.ScriptKind == ItemScriptKind.Equipment
            && definition.TypeTag is "artifact red" or "artifact blue" or "artifact green",
        _ => false
    };

static DungeonRoomDefinition CreateDungeonRoom(
    DungeonMapType mapType,
    params GameDungeonMonster[] monsters) => new(
    MapId: 1,
    mapType,
    monsters,
    Topology: 'C',
    HasScriptBossActor: monsters.Any(monster =>
        monster.Type == GameDungeonMonsterTypes.Boss),
    PassiveObjects: [],
    PassiveItemSpawns: []);

sealed class SequenceDropRandomSource(params int[] values) : IDropRandomSource
{
    private int _index;

    public int Next(int exclusiveMaximum)
    {
        if (_index >= values.Length)
        {
            throw new InvalidOperationException("The deterministic drop sequence was exhausted.");
        }

        var value = values[_index++];
        if (value < 0 || value >= exclusiveMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                $"Value {value} is outside [0, {exclusiveMaximum}).");
        }

        return value;
    }
}

sealed class ZeroDropRandomSource : IDropRandomSource
{
    public int Next(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        }

        return 0;
    }
}
