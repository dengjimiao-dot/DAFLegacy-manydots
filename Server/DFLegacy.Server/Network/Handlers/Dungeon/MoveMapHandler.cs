using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// MOVE_MAP (CMD 48): in-dungeon room transition (07 §5 group 13).
/// </summary>
public sealed class MoveMapHandler(    ILogger<MoveMapHandler> logger,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.MoveMapCommand;

    public string Name => "MOVE_MAP";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // DFLegacy command 48 is sequence, target room X and
        // target room Y. Sending the next START_MAP packet
        // completes the door transition after room clear.
        if (request.Body.Length >= 4
            && session.Dungeon.CurrentDungeonId != 0
            && session.Dungeon.Layout is not null)
        {
            var targetX = request.Body[2];
            var targetY = request.Body[3];
            if (targetX == session.Dungeon.RoomX
                && targetY == session.Dungeon.RoomY)
            {
                var duplicateMoveError =
                    ConnectionPacketBuilders.CreateCommandError(
                        request,
                        errorCode: 4);
                await context.Outbound.SendAsync(duplicateMoveError, Name, cancellationToken);
                logger.LogDebug(
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
                await context.Outbound.SendAsync(moveReply, Name, cancellationToken);

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
                await context.Outbound.SendAsync(nextMap, Name, cancellationToken);
                if (transition.FirstVisit)
                {
                    await senders.ConsumeDungeonRoomFatigueAsync(context, 
                        targetX,
                        targetY);
                }
                await senders.EnableEmptyBossRoomSettlementAsync(context, 
                    session.Dungeon.Room);
                logger.LogInformation(
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
                await context.Outbound.SendAsync(moveError, Name, cancellationToken);
                logger.LogWarning(
                    "Dungeon {DungeonId} rejected disconnected or unavailable room ({RoomX},{RoomY})",
                    session.Dungeon.CurrentDungeonId,
                    targetX,
                    targetY);
            }
        }
    }
}
