using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// MOVE_MAP (CMD 48): in-dungeon room transition (07 §5 group 13).
/// </summary>
public sealed class MoveMapHandler(
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.MOVE_MAP;

    public string Name => "MOVE_MAP";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // DAFLegacy command 48 is sequence, target room X and
        // target room Y. Sending the next START_MAP packet
        // completes the door transition after room clear.
        if (request.Body.Length >= 4
            && session.Dungeon.CurrentDungeonId != 0
            && session.Dungeon.Layout is not null)
        {
            var targetX = request.Body[2];
            var targetY = request.Body[3];
            if (session.Dungeon.Run.IsBossRoomEntranceBlocked(targetX, targetY))
            {
                var conditionError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    GameProtocolConstants.MoveMapBossEntranceConditionError);
                await context.Queue.SendAsync(conditionError, Name, cancellationToken);
                RuntimeLogger.Info(
                    "Rejected boss entrance for dungeon {DungeonId}: configured entrance condition is not satisfied.",
                    session.Dungeon.CurrentDungeonId);
            }
            else if (session.Dungeon.ClearEnabled
                || session.Dungeon.Run.IsCleared
                || session.Dungeon.Run.IsBossClearPending)
            {
                // 通关判定已成立（含 Boss 死亡窗口待确认）时，DF2008 的
                // MOVE_MAP 以错误 0x13 被拒并清除 actor +0x244 移动标记。
                var completedMoveError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    GameProtocolConstants.MoveMapInvalidStateError);
                await context.Queue.SendAsync(completedMoveError, Name, cancellationToken);
                RuntimeLogger.Info(
                    "Rejected MOVE_MAP after boss completion for dungeon {DungeonId}; cleared={Cleared}, bossClearPending={BossClearPending}, resultSent={ResultSent}.",
                    session.Dungeon.CurrentDungeonId,
                    session.Dungeon.ClearEnabled || session.Dungeon.Run.IsCleared,
                    session.Dungeon.Run.IsBossClearPending,
                    session.Dungeon.ResultSent);
            }
            else if (targetX == session.Dungeon.RoomX
                && targetY == session.Dungeon.RoomY)
            {
                var duplicateMoveError =
                    ConnectionPacketBuilders.CreateCommandError(
                        request,
                        errorCode: 4);
                await context.Queue.SendAsync(duplicateMoveError, Name, cancellationToken);
                RuntimeLogger.Debug(
                    "Rejected duplicate MOVE_MAP for current dungeon {DungeonId} room ({RoomX},{RoomY}).",
                    session.Dungeon.CurrentDungeonId,
                    targetX,
                    targetY);
            }
            else if (session.Dungeon.Run.TryMoveRoom(
                    targetX,
                    targetY,
                    out var transition))
            {
                var previousRoomX = session.Dungeon.RoomX;
                var previousRoomY = session.Dungeon.RoomY;
                await senders.SettleDungeonCreatureHungerAsync(context,
                    notifyClient: true,
                    cancellationToken);
                var moveReply = ConnectionPacketBuilders.CreateCommandReply(
                    request,
                    success: true);
                await context.Queue.SendAsync(moveReply, Name, cancellationToken);

                // Ground items belong to their originating room. Moving
                // rooms must not synthesize NOTI 39: DNF 1.0.1.9 treats
                // its non-zero routing bytes as dice results, while the
                // authoritative item remains unclaimed on the server.

                if (transition.CompletedHellRoomOnExit)
                {
                    session.Dungeon.GroundItems.DetachAllPassiveObjectItems(
                        previousRoomX,
                        previousRoomY);
                }

                session.Dungeon.RoomX = targetX;
                session.Dungeon.RoomY = targetY;
                session.Dungeon.ClearEnabled = false;
                session.Dungeon.ResultSent = false;
                session.Dungeon.CardLayoutSent = false;
                session.Dungeon.CardRightsSent = false;
                session.Dungeon.CardSelectionDeadline = null;
                session.Dungeon.ClearReward = null;
                session.Dungeon.FreeCardIndex = null;
                session.Dungeon.GoldCardIndex = null;
                session.Dungeon.AliveMonsters.Clear();
                session.Dungeon.ReportedBossDeaths.Clear();
                session.Dungeon.BossCascadeDeaths.Clear();
                session.Dungeon.DestroyedPassiveObjects.Clear();
                session.Dungeon.Room = transition.Room;
                session.Dungeon.AliveMonsters.UnionWith(
                    session.Dungeon.Room.Monsters.Select(monster =>
                        monster.UniqueId));
                if (session.Dungeon.Run.IsCurrentHellRoomCleared)
                {
                    session.Dungeon.GroundItems.DetachAllPassiveObjectItems(
                        targetX,
                        targetY);
                }
                GameServerPacket nextMap;
                if (transition.ReuseClientRoomState)
                {
                    nextMap = DungeonPacketBuilders.CreateCachedStartMap(
                        targetX,
                        targetY,
                        session.Dungeon.Room.ClientRandomSeed);
                }
                else
                {
                    var passiveObjectDrops = transition.FirstVisit
                        ? senders.RegisterPassiveObjectDrops(
                            session.Dungeon.Room,
                            session.Dungeon.GroundItems,
                            session.Character.LocalUserId,
                            targetX,
                            targetY)
                        : senders.GetRegisteredPassiveObjectDrops(
                            session.Dungeon.GroundItems,
                            targetX,
                            targetY);
                    nextMap = DungeonPacketBuilders.CreateStartMap(
                        targetX,
                        targetY,
                        session.Dungeon.Room.MapId,
                        session.Dungeon.Room.Monsters,
                        passiveObjectDrops,
                        randomSeed: session.Dungeon.Room.ClientRandomSeed,
                        roomStateFlag: session.Dungeon.Run.RoomStateFlag,
                        hellPartyMode: session.Dungeon.Run.CurrentHellPartyMode);
                }
                await context.Queue.SendAsync(nextMap, Name, cancellationToken);
                if (transition.FirstVisit)
                {
                    await senders.ConsumeDungeonRoomFatigueAsync(context,
                        targetX,
                        targetY);
                }
                await senders.EnableEmptyBossRoomSettlementAsync(context,
                    session.Dungeon.Room);
                RuntimeLogger.Info(
                    "Dungeon {DungeonId} moved to room ({RoomX},{RoomY}), map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, firstVisit={FirstVisit}, reuseClientRoomState={ReuseClientRoomState}, hellPartyMode={HellPartyMode}, hellRoomClearedOnExit={HellRoomClearedOnExit}.",
                    session.Dungeon.CurrentDungeonId,
                    targetX,
                    targetY,
                    session.Dungeon.Room.MapId,
                    session.Dungeon.Room.Monsters.Length,
                    session.Dungeon.Room.Monsters.Count(monster =>
                        monster.Type == GameDungeonMonsterTypes.Champion),
                    session.Dungeon.Room.BaseChampionCount,
                    session.Dungeon.Room.ClientRandomSeed,
                    transition.FirstVisit,
                    transition.ReuseClientRoomState,
                    session.Dungeon.Run.CurrentHellPartyMode,
                    transition.CompletedHellRoomOnExit);
            }
            else
            {
                var moveError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 4);
                await context.Queue.SendAsync(moveError, Name, cancellationToken);
                RuntimeLogger.Warn(
                    "Dungeon {DungeonId} rejected disconnected or unavailable room ({RoomX},{RoomY})",
                    session.Dungeon.CurrentDungeonId,
                    targetX,
                    targetY);
            }
        }
    }
}
