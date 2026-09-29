using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ProtocolGoldenSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var samples = PacketByteSamples.LoadAll();
        check(samples.ContainsKey("cmd:0") && samples.ContainsKey("cmd:3"),
            "golden sample library carries the R0 pilot protocols (CHECK_CONNECTION 0, EXIT 3)");

        // R0 试点协议的线上样本必须解出声明的帧头(07 §7:样本即黄金标准)。
        var checkConnectionFrame = PacketByteSamples.DecodeFrame(samples["cmd:0"]);
        check(checkConnectionFrame.Type == GameProtocolEngine.CommandPacketType
            && checkConnectionFrame.ProtocolId == GameProtocolEngine.CheckConnectionCommand
            && checkConnectionFrame.DeclaredCrc32 == 0x58C223BEu,
            "cmd:0 sample decodes to a CHECK_CONNECTION command frame with its original CRC");
        var exitFrame = PacketByteSamples.DecodeFrame(samples["cmd:3"]);
        check(exitFrame.Type == GameProtocolEngine.CommandPacketType
            && exitFrame.ProtocolId == GameProtocolEngine.ExitCommand,
            "cmd:3 sample decodes to an EXIT command frame");

        // 注册表语义(07 §4.6):一号一 handler,重复注册启动期失败。
        var registry = new GameCommandRegistry();
        var exitHandler = new ExitHandler(NullLogger<ExitHandler>.Instance);
        registry.Register("session.connection", exitHandler);
        check(registry.TryGet(GameProtocolEngine.ExitCommand, out var mounted)
            && ReferenceEquals(mounted, exitHandler),
            "registry mounts EXIT by protocol id");
        check(!registry.TryGet(200, out _),
            "registry lookup misses unmounted protocol ids");
        try
        {
            registry.Register("session.connection",
                new ExitHandler(NullLogger<ExitHandler>.Instance));
            check(false, "duplicate registration must fail startup");
        }
        catch (InvalidOperationException)
        {
            check(true, "duplicate registration fails startup with the source group named");
        }

        // 阶段阶梯(07 §4.3/§4.8.1):CurrentPhase 随状态域单调抬升。
        var state = new ChannelSessionState();
        check(state.CurrentPhase == CommandPhase.Any, "fresh session sits at phase Any");
        state.Login.AccountUid = 77;
        check(state.CurrentPhase == CommandPhase.Authenticated,
            "account uid lifts the session to Authenticated");
        state.Dungeon.CurrentDungeonId = 9;
        check(state.CurrentPhase == CommandPhase.InDungeon,
            "dungeon id lifts the session to InDungeon");
        state.Dungeon.CurrentDungeonId = 0;
        check(state.CurrentPhase == CommandPhase.Authenticated,
            "clearing the dungeon returns the phase to the pre-dungeon value");

        // 端口门槛(07 §4.8.1 U3):频道口专属 handler 拒绝入口口帧。
        var dispatcher = new GameCommandDispatcher(NullLogger.Instance);
        var (entranceContext, entranceStream) = CreateTestContext(isChannelPort: false);
        check(!await dispatcher.TryDispatchAsync(
                entranceContext, exitFrame, exitHandler, CancellationToken.None),
            "channel-port handler refuses frames from the entrance port");
        check(entranceStream.Length == 0,
            "refused frames never reach the outbound stream");

        // 阶段门槛:声明 CharacterSelected 的 handler 在 Any 阶段被拒。
        var (gatedContext, gatedStream) = CreateTestContext(isChannelPort: true);
        check(!await dispatcher.TryDispatchAsync(
                gatedContext,
                exitFrame,
                new StubHandler(200, "STUB", CommandPhase.CharacterSelected),
                CancellationToken.None),
            "dispatcher refuses a handler whose required phase exceeds the session");
        check(gatedStream.Length == 0,
            "gate-refused handlers never write to the outbound stream");

        // EXIT 端到端 golden:注册表 → 门槛 → handler → 出站泵 → 线上字节。
        var (exitContext, exitStream) = CreateTestContext(isChannelPort: true);
        check(await dispatcher.TryDispatchAsync(
                exitContext, exitFrame, exitHandler, CancellationToken.None),
            "EXIT dispatches through the new pipeline");
        var expectedExit = ConnectionPacketBuilders.CreateCommandReply(exitFrame, success: true).Encode();
        check(exitStream.ToArray().SequenceEqual(expectedExit),
            "EXIT(CMD 3) reply bytes match the legacy chain output through the new pipeline");

        // CHECK_CONNECTION 端到端 golden:seed 逐次现取,先读出实际 seed,
        // 再以同 seed 重建期望载荷做全字节对比。
        var (channelContext, channelStream) = CreateTestContext(isChannelPort: true);
        check(await dispatcher.TryDispatchAsync(
                channelContext,
                checkConnectionFrame,
                new CheckConnectionHandler(new ServerOptions()),
                CancellationToken.None),
            "CHECK_CONNECTION dispatches through the new pipeline");
        var channelInfo = PacketByteSamples.DecodeServerPacket(channelStream.ToArray());
        check(channelInfo.Type == GameProtocolEngine.NotificationPacketType
            && channelInfo.ProtocolId == GameProtocolEngine.ChannelInfoNotification,
            "CHECK_CONNECTION replies with a CHANNELINFO notification");
        var actualSeed = ReadChannelInfoSeed(channelInfo.Payload);
        var expectedChannelInfo = ConnectionPacketBuilders.CreateChannelInfo(
            Encoding.ASCII.GetBytes("Local Channel 1"),
            channelType: 1,
            result: 1,
            serverId: 1,
            channelNumber: 1,
            seed: actualSeed,
            [Encoding.ASCII.GetBytes("127.0.0.1")],
            port1: 7002,
            port2: 7003);
        check(channelInfo.Payload.SequenceEqual(expectedChannelInfo.Payload),
            "CHANNELINFO payload matches the legacy inline branch bytes for the same seed");
        var unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        check(Math.Abs(actualSeed - unixNow) <= 5,
            "CHANNELINFO seed is the current unix time, as the legacy branch produced");

        // 出站泵 FIFO(07 §3 I1):批量入队按序落流,单写者不乱序。
        var batch = new List<GameServerPacket>
        {
            ConnectionPacketBuilders.CreateCommandReply(exitFrame, success: true),
            ConnectionPacketBuilders.CreateCommandReply(checkConnectionFrame, success: true),
            ConnectionPacketBuilders.CreateCommandReply(exitFrame, success: false),
        };
        var (batchContext, batchStream) = CreateTestContext(isChannelPort: true);
        await batchContext.Outbound.SendBatchAsync(batch);
        var expectedBatch = batch.SelectMany(packet => packet.Encode()).ToArray();
        check(batchStream.ToArray().SequenceEqual(expectedBatch),
            "outbound pump preserves strict FIFO byte order for batched sends");
        check(batchContext.Runtime.SentPackets == batch.Count,
            "outbound pump counts every delivered packet at the single accounting point");

        await entranceContext.Outbound.DisposeAsync();
        await gatedContext.Outbound.DisposeAsync();
        await exitContext.Outbound.DisposeAsync();
        await channelContext.Outbound.DisposeAsync();
        await batchContext.Outbound.DisposeAsync();
    }

    private static (ConnectionContext Context, MemoryStream Stream) CreateTestContext(bool isChannelPort)
    {
        var session = new RuntimeSession
        {
            Id = Guid.NewGuid(),
            Service = isChannelPort ? "channel:test" : "entrance:test",
            RemoteEndpoint = "test",
        };
        var stream = new MemoryStream();
        var outbound = new OutboundQueue(
            stream,
            session,
            _ => { },
            NullLogger.Instance);
        var context = new ConnectionContext(
            session,
            null,
            isChannelPort,
            new ChannelSessionState(),
            outbound,
            NullLogger.Instance,
            CancellationToken.None);
        return (context, stream);
    }

    /// <summary>
    /// CHANNELINFO payload layout: display-length prefixed name, four uint
    /// fields plus two bytes, then the seed the legacy branch sourced from
    /// the wall clock.
    /// </summary>
    private static uint ReadChannelInfoSeed(byte[] payload)
    {
        var displayLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var seedOffset = 4 + displayLength + sizeof(uint) + sizeof(uint) + 1 + 1;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(seedOffset, sizeof(uint)));
    }

    private sealed class StubHandler(
        ushort protocolId,
        string name,
        CommandPhase phase,
        bool requiresChannelPort = false) : IGameCommandHandler
    {
        public ushort ProtocolId => protocolId;
        public string Name => name;
        public CommandPhase RequiredPhase => phase;
        public bool RequiresChannelPort => requiresChannelPort;

        public ValueTask HandleAsync(ConnectionContext context, PacketFrame frame, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
