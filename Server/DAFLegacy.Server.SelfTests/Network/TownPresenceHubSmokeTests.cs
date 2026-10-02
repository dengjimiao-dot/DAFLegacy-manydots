using System.Net;
using DAFLegacy.Protocol;
using DAFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// 城镇广播中枢自测（docs/design/10 §4.1 G4 批次 3）：频道人口统计、区域交换与
/// 广播、查找服务、状态翻转钩子、解析端点反查与回收。
/// </summary>
internal static class TownPresenceHubSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var registry = new CharacterSessionRegistry(NullLogger<CharacterSessionRegistry>.Instance);
        var hub = new TownPresenceService(registry);
        var inbox = new Dictionary<Guid, List<GameServerPacket>>();
        void Capture(Guid sessionId, GameServerPacket packet)
        {
            if (!inbox.TryGetValue(sessionId, out var list))
            {
                list = [];
                inbox[sessionId] = list;
            }

            list.Add(packet);
        }

        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        registry.AttachPacketChannel(sessionA, packet => Capture(sessionA, packet));
        registry.AttachPacketChannel(sessionB, packet => Capture(sessionB, packet));

        // 连接层：人口按频道计数，断开回收。
        var runtimeA = new RuntimeSession { Id = sessionA, Service = "test", RemoteEndpoint = "127.0.0.1:1" };
        var runtimeB = new RuntimeSession { Id = sessionB, Service = "test", RemoteEndpoint = "127.0.0.1:2" };
        hub.AcquireConnection(sessionA, 1, runtimeA, IPAddress.Loopback);
        hub.AcquireConnection(sessionB, 1, runtimeB, IPAddress.Loopback);
        check(hub.GetChannelPopulation(1) == 2 && hub.GetChannelPopulation(2) == 0,
            "channel population counts acquired connections per channel");
        hub.AcquireConnection(Guid.NewGuid(), 2, new RuntimeSession { Id = Guid.NewGuid(), Service = "test", RemoteEndpoint = "x" }, IPAddress.Loopback);
        check(hub.GetChannelPopulation(2) == 1,
            "a second channel counts independently");
        hub.ReleaseConnection(sessionB);
        check(hub.GetChannelPopulation(1) == 1,
            "releasing a connection decrements its channel population");

        // 端点反查：解析端点写入即登记，回收即清除。
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40001);
        hub.SetResolvedEndpoint(sessionA, endpoint);
        check(hub.TryGetSessionByEndpoint("203.0.113.7:40001", out var attributed) && attributed == sessionA,
            "a resolved endpoint is attributable back to its session");
        hub.ReleaseConnection(sessionA);
        check(!hub.TryGetSessionByEndpoint("203.0.113.7:40001", out _),
            "releasing the connection clears its endpoint attribution");

        // 城镇层：区域交换与广播（重建 A/B 连接后发布在场）。
        hub.AcquireConnection(sessionA, 1, runtimeA, IPAddress.Loopback);
        hub.AcquireConnection(sessionB, 1, runtimeB, IPAddress.Loopback);
        var transitions = new List<TownPresenceTransition>();
        hub.RegisterTransitionHook(transitions.Add);
        hub.PublishTownPresence(
            new PartyPresenceUpdate(sessionA, Guid.NewGuid(), 11, 101, "A", 10, 1, 0, 3, 10, 20, 1, 1),
            TownPacketBuilders.CreateUserArea(0, 3, new GameTownUser(101, 10, 20, 1)));
        hub.PublishTownPresence(
            new PartyPresenceUpdate(sessionB, Guid.NewGuid(), 22, 102, "B", 11, 2, 0, 3, 30, 40, 2, 1),
            TownPacketBuilders.CreateUserArea(0, 3, new GameTownUser(102, 30, 40, 2)));
        check(inbox[sessionA].Any(packet => packet.ProtocolId == 23)
            && inbox[sessionB].Any(packet => packet.ProtocolId == 23),
            "both residents received each other's appearance and USER_AREA (NOTI 23)");
        check(hub.FindByUserId(102)?.Name == "B"
            && hub.FindBySession(sessionA)?.UserId == 101,
            "the hub lookup resolves by user id and by session");

        // 广播与翻转钩子。
        var reached = hub.SendToSameArea(sessionA, TownPacketBuilders.CreateUserLeave(101));
        check(reached == 1,
            "same-area delivery reaches exactly the other resident");
        hub.RemovePresence(sessionA);
        check(transitions.Count(t => t.Kind == TownPresenceTransitionKind.Entered) == 2
            && transitions.Count(t => t.Kind == TownPresenceTransitionKind.Left) == 1,
            "presence transitions fired entered/left hooks in order");
        check(hub.FindBySession(sessionA) is null,
            "the departed presence is gone from the hub table");
    }
}
