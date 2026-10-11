using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// BOSS_DIE_CHECK (CMD 127): checks boss-death progress and unlocks the clear (07 §5 group 13).
/// </summary>
public sealed class BossDieCheckHandler() : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.BOSS_DIE_CHECK;

    public string Name => "BOSS_DIE_CHECK";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        var parsed = DungeonPacketBuilders.TryParseBossDieCheckCommand(
            request.Body,
            out var bossDieCheck);
        var bossIds = session.Dungeon.Room is not null
            ? session.Dungeon.Room.Monsters
                .Where(monster => monster.Type
                    == GameDungeonMonsterTypes.Boss)
                .Select(monster => monster.UniqueId)
                .ToHashSet()
            : [];
        var validBoss = parsed
            && session.Dungeon.Room is not null
            && session.Dungeon.Room.MapType == DungeonMapType.Boss
            && bossDieCheck.ParticipantId == session.Character.LocalUserId
            && bossIds.Contains(bossDieCheck.BossId);

        GameServerPacket bossDieReply;
        if (validBoss)
        {
            session.Dungeon.ReportedBossDeaths.Add(bossDieCheck.BossId);
            var allBossesReported = bossIds.All(
                session.Dungeon.ReportedBossDeaths.Contains);
            // 多场次副本（[dummy appear count] > 0，当前仅 51 号）：紧邻死亡上报的
            // BOSS_DIE_CHECK 是真死标记；无该校验的 Boss 死亡为假死（客户端屏幕上
            // Boss 仍存活，continue fighting）。真机抓包记录见 docs/design/15 §2.4。
            session.Dungeon.PendingBossDeathCheckId = bossDieCheck.BossId;
            if (allBossesReported)
            {
                session.Dungeon.BossCascadeDeaths.UnionWith(
                    session.Dungeon.AliveMonsters.Where(monsterId =>
                        !bossIds.Contains(monsterId)));
            }

            bossDieReply = DungeonPacketBuilders.CreateBossDieCheck(
                success: true,
                allBossesReported,
                bossDieCheck.BossId);
            RuntimeLogger.Info(
                "Dungeon {DungeonId} accepted BOSS_DIE_CHECK from participant {ParticipantId} for boss {BossId}; reported={ReportedCount}/{BossCount}, native-all-die={AllBossesReported}, checksum={SkillChecksum:X8}.",
                session.Dungeon.CurrentDungeonId,
                bossDieCheck.ParticipantId,
                bossDieCheck.BossId,
                session.Dungeon.ReportedBossDeaths.Count,
                bossIds.Count,
                allBossesReported,
                bossDieCheck.SkillChecksum);
        }
        else
        {
            bossDieReply = DungeonPacketBuilders.CreateBossDieCheck(
                success: false,
                allBossesReported: false,
                errorCode: 2);
            RuntimeLogger.Warn(
                "Rejected BOSS_DIE_CHECK in dungeon {DungeonId}, room ({RoomX},{RoomY}); parsed={Parsed}, participant={ParticipantId}, expectedParticipant={ExpectedParticipantId}, boss={BossId}, mapType={MapType}.",
                session.Dungeon.CurrentDungeonId,
                session.Dungeon.RoomX,
                session.Dungeon.RoomY,
                parsed,
                parsed ? bossDieCheck.ParticipantId : 0,
                session.Character.LocalUserId,
                parsed ? bossDieCheck.BossId : 0,
                session.Dungeon.Room?.MapType);
        }

        await context.Queue.SendAsync(bossDieReply, Name, cancellationToken);
    }
}
