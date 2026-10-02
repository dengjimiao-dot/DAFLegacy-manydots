using DAFLegacy.Protocol;
using DAFLegacy.Server;

/// <summary>
/// PVP 房间状态机自测（11 号 P6 骨架；docs/design/12 §3.6）：建房/进房/座位/准备/
/// 开战校验/击杀计数/断线清理与房主继任；简单构建器形状（44/46/45）。
/// </summary>
internal static class PvpRoomSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var registry = new PvpRoomRegistry();
        var host = Guid.NewGuid();
        var guest = Guid.NewGuid();

        // 建房与进房。
        var room = registry.Create(host, 1);
        check(room is not null && room.Seats[0].Occupied && room.Seats[0].UserId == 1,
            "creating a room seats the host in slot 0");
        var joined = registry.Join(guest, 2);
        check(joined is not null && joined.Seats[0].Occupied && joined.Seats[1].Occupied,
            "joining seats the guest in the first free slot");
        var third = Guid.NewGuid();
        check(registry.Join(third, 3) is not null, "a third player joins an open room");

        // 座位与准备。
        check(registry.SetSeat(guest, 4, 2), "the guest moves to a team-2 seat");
        check(registry.SetReady(guest, true) == true, "the guest readies up");
        check(registry.SetReady(host, true) == true, "the host readies up");

        // 开战校验：第三人未就绪 → 拒绝。
        check(registry.Start(host) is null,
            "start is rejected while any occupant is not ready");
        check(registry.SetSeat(guest, 1, 1), "the guest moves back to a team-1 seat");
        check(registry.Start(host) is null,
            "start is rejected with only one team present");
        check(registry.SetReady(third, true) == true, "the third player readies up");
        check(registry.SetSeat(guest, 4, 2) && registry.SetReady(guest, true) == true,
            "the guest returns to team 2 and readies");

        // 击杀与清理。
        check(registry.Start(host) is not null,
            "start passes with everyone ready across two teams");
        check(registry.ReportKill(guest, 0, 4), "a kill is reported against started rooms");
        var roomAfterKill = registry.FindBySession(host)!;
        check(roomAfterKill.Seats[0].Deaths == 1 && roomAfterKill.Seats[4].Kills == 1,
            "the kill counter lands on victim deaths and killer kills");
        check(registry.Leave(host) is not null && registry.FindBySession(host) is null,
            "the host leaving clears their seat");
        check(registry.FindBySession(guest)!.HostSessionId == third,
            "host designation passes to the lowest occupied seat (the third player)");
        check(registry.Leave(guest) is not null, "disconnect cleanup reports the room");
        check(registry.FindBySession(guest) is null, "the disconnected seat is released");

        // 构建器形状。
        var ready = PvpPacketBuilders.CreatePvpReady(3, true);
        check(ready.ProtocolId == (byte)NotiPacketType.PVP_READY_STATE
            && ready.Payload.SequenceEqual(new byte[] { 3, 1 }),
            "NOTI 44 carries seat and ready bytes");
        var kill = PvpPacketBuilders.CreatePvpKill(5, 2);
        check(kill.ProtocolId == (byte)NotiPacketType.DIE_PVP_CHARACTER
            && kill.Payload.SequenceEqual(new byte[] { 5, 2 }),
            "NOTI 46 carries victim and killer seat bytes");
        var start = PvpPacketBuilders.CreatePvpStartHead(2, 1);
        check(start.ProtocolId == (byte)NotiPacketType.START_PVP
            && start.Payload.SequenceEqual(new byte[] { 2, 1 }),
            "NOTI 45 head carries map and mode bytes");
    }
}
