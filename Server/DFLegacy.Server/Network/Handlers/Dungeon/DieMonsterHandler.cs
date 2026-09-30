using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// DIE_MONSTER (CMD 42): monster kill processing, drops and quest-item grants (07 §5 group 13).
/// </summary>
public sealed class DieMonsterHandler(
    DungeonExperienceCatalog dungeonExperienceCatalog,
    HellDropCatalog hellDropCatalog,
    MonsterChampionDropCatalog monsterChampionDrops,
    ServerOptions options,
    ItemCatalog itemCatalog, JsonGameStore store,
    DungeonCatalog dungeonCatalog,
    CharacterExperienceCatalog experienceCatalog,
    DungeonDropGenerator dropGenerator,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.DIE_MONSTER;

    public string Name => "DIE_MONSTER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (DungeonPacketBuilders.TryParseDieMonsterCommand(
                request.Body,
                out var dieCommand))
        {
            if (dieCommand.IsPassiveObject)
            {
                if (session.Dungeon.Room is not null
                    && session.Dungeon.Run.TryMarkPassiveObjectDestroyed(
                        dieCommand.EntityId,
                        out var passiveObjectIndex))
                {
                    session.Dungeon.DestroyedPassiveObjects.Add(passiveObjectIndex);
                    var activatedItemCount = session.Dungeon.GroundItems.ActivatePassiveObject(
                        passiveObjectIndex,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY);
                    session.Dungeon.Room = session.Dungeon.Run.CurrentRoom
                        ?? session.Dungeon.Room;
                    RuntimeLogger.Info(
                        "Passive object code {PassiveObjectCode}, association {PassiveObjectIndex} destroyed in room ({RoomX},{RoomY}); activatedItems={ActivatedItemCount}.",
                        dieCommand.EntityId,
                        passiveObjectIndex,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY,
                        activatedItemCount);
                }
                else
                {
                    RuntimeLogger.Warn(
                        "Ignored unknown passive object {PassiveObjectIndex} in room ({RoomX},{RoomY}).",
                        dieCommand.EntityId,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY);
                }

                return;
            }

            var monsterUniqueId = dieCommand.EntityId;
            var wasHellFiend = session.Dungeon.Run.IsHellFiend(monsterUniqueId);
            var hellPartyMode = session.Dungeon.Run.CurrentHellPartyMode;
            var isBossCascadeDeath =
                session.Dungeon.BossCascadeDeaths.Remove(monsterUniqueId);
            var wasAlive = session.Dungeon.AliveMonsters.Remove(monsterUniqueId);
            var defeatedMonster = wasAlive
                && session.Dungeon.Room is not null
                ? session.Dungeon.Room.Monsters.FirstOrDefault(monster =>
                    monster.UniqueId == monsterUniqueId)
                : null;
            if (!wasAlive || defeatedMonster is null)
            {
                RuntimeLogger.Warn(
                    "Ignored stale or unknown monster death {MonsterId} in room ({RoomX},{RoomY}).",
                    monsterUniqueId,
                    session.Dungeon.RoomX,
                    session.Dungeon.RoomY);
                return;
            }

            session.Dungeon.Run.MarkMonsterDefeated(monsterUniqueId);

            var championRewardOrdinal =
                GameProtocolConstants.NoChampionRewardOrdinal;

            switch (defeatedMonster.Type)
            {
                case GameDungeonMonsterTypes.Champion:
                case GameDungeonMonsterTypes.SuperChampion:
                    session.Dungeon.ChampionMonsterKills = Math.Min(
                        ushort.MaxValue,
                        session.Dungeon.ChampionMonsterKills + 1);
                    if (session.Dungeon.ChampionRewardOrdinal
                        < GameProtocolConstants.MaximumChampionRewardOrdinal)
                    {
                        championRewardOrdinal = checked((sbyte)(
                            ++session.Dungeon.ChampionRewardOrdinal));
                    }
                    break;
                case GameDungeonMonsterTypes.Boss:
                    session.Dungeon.BossMonsterKills = Math.Min(
                        ushort.MaxValue,
                        session.Dungeon.BossMonsterKills + 1);
                    break;
                default:
                    session.Dungeon.NormalMonsterKills = Math.Min(
                        ushort.MaxValue,
                        session.Dungeon.NormalMonsterKills + 1);
                    break;
            }

            var protocolDrops = new List<GameDungeonDrop>(6);
            if (!isBossCascadeDeath)
            {
                foreach (var generatedDrop in dropGenerator.Generate(
                             defeatedMonster.Level,
                             defeatedMonster.Type,
                             session.Dungeon.Difficulty))
                {
                    var groundItem = session.Dungeon.GroundItems.Add(
                        generatedDrop,
                        session.Character.LocalUserId,
                        monsterUniqueId,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY,
                        x: unchecked((short)defeatedMonster.X),
                        y: unchecked((short)defeatedMonster.Y),
                        catalog: itemCatalog);
                    protocolDrops.Add(new GameDungeonDrop(
                        groundItem.GroundId,
                        groundItem.ItemId,
                        groundItem.GetClientAddInfo(itemCatalog),
                        groundItem.GetClientDurability(itemCatalog),
                        groundItem.OwnerUserId));
                }

                foreach (var generatedDrop in monsterChampionDrops.Roll(
                             defeatedMonster.MonsterIndex,
                             defeatedMonster.Type))
                {
                    var groundItem = session.Dungeon.GroundItems.Add(
                        generatedDrop,
                        session.Character.LocalUserId,
                        monsterUniqueId,
                        session.Dungeon.RoomX,
                        session.Dungeon.RoomY,
                        x: unchecked((short)defeatedMonster.X),
                        y: unchecked((short)defeatedMonster.Y),
                        catalog: itemCatalog);
                    protocolDrops.Add(new GameDungeonDrop(
                        groundItem.GroundId,
                        groundItem.ItemId,
                        groundItem.GetClientAddInfo(itemCatalog),
                        groundItem.GetClientDurability(itemCatalog),
                        groundItem.OwnerUserId));
                }

                if (wasHellFiend && dropGenerator.Enabled)
                {
                    var hellLevel = dungeonCatalog.TryGetDefinition(session.Dungeon.CurrentDungeonId, out var hellDefinition)
                        ? hellDefinition.BasisLevel : defeatedMonster.Level;
                    var hellHit = hellDropCatalog.TryRollEquipment(
                        hellLevel,
                        session.Dungeon.Difficulty,
                        hellPartyMode,
                        GameRandomSource.Shared,
                        out var hellItemId,
                        options.Drop.RatePercent / 100.0 * options.Drop.EconomicRate,
                        options.Drop.ForceDrops);
                    RuntimeLogger.Info(
                        "Hell drop result for character {CharacterId}, dungeon {DungeonId}, monster {MonsterIndex}/{UniqueId}: hit={Hit}, item={ItemId}.",
                        session.Character.Active?.Id, session.Dungeon.CurrentDungeonId, defeatedMonster.MonsterIndex,
                        monsterUniqueId, hellHit, hellItemId);
                    if (hellHit)
                    {
                        var hellGroundItem = session.Dungeon.GroundItems.Add(
                            new DungeonGeneratedDrop(
                                IsGold: false,
                                hellItemId,
                                CountOrValue: 1),
                            session.Character.LocalUserId,
                            monsterUniqueId,
                            session.Dungeon.RoomX,
                            session.Dungeon.RoomY,
                            x: unchecked((short)defeatedMonster.X),
                            y: unchecked((short)defeatedMonster.Y),
                            catalog: itemCatalog);
                        protocolDrops.Add(new GameDungeonDrop(
                            hellGroundItem.GroundId,
                            hellGroundItem.ItemId,
                            hellGroundItem.GetClientAddInfo(itemCatalog),
                            hellGroundItem.GetClientDurability(itemCatalog),
                            hellGroundItem.OwnerUserId));
                    }
                }
            }

            var monsterDie =
                DungeonPacketBuilders.CreateMonsterDie(
                    monsterUniqueId,
                    protocolDrops,
                    championRewardOrdinal);
            await context.Queue.SendAsync(monsterDie, Name, cancellationToken);

            if (!isBossCascadeDeath && !session.Dungeon.TutorialActive)
            {
                await senders.GrantMonsterQuestItemsAsync(context,
                    defeatedMonster.MonsterIndex);
            }

            if (!isBossCascadeDeath && session.Character.Active is not null)
            {
                var gainedExperience =
                    dungeonCatalog.TryGetDefinition(
                        session.Dungeon.CurrentDungeonId,
                        out var experienceDungeon)
                        ? dungeonExperienceCatalog.CalculateMonsterExperience(
                            session.Character.Active.Level,
                            defeatedMonster.Level,
                            defeatedMonster.Type,
                            defeatedMonster.MonsterIndex,
                            experienceDungeon,
                            session.Dungeon.Difficulty,
                            partyMemberCount: 1)
                        : 0;
                var progression = experienceCatalog.Grant(
                    session.Character.Active,
                    gainedExperience);
                if (progression.GrantedExperience > 0)
                {
                    var grantedSkillPoints = senders.ApplyLevelUpSkillPoints(context, progression);
                    session.Character.Active = session.Character.Active with
                    {
                        Level = progression.Level,
                        Experience = progression.Experience,
                        Sp = session.Skill.CurrentSp
                    };
                    var savedProgression =
                        await store.SaveCharacterProgressionAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
                            session.Character.Active.Level,
                            session.Character.Active.Experience,
                            session.Skill.CurrentSp,
                            cancellationToken);
                    if (savedProgression is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not persist monster experience for character {session.Character.Active.Id}.");
                    }

                    session.Character.Active = savedProgression;
                    session.Dungeon.MonsterExperienceGained = (uint)Math.Min(
                        uint.MaxValue,
                        (ulong)session.Dungeon.MonsterExperienceGained
                        + progression.GrantedExperience);
                    session.Dungeon.LeveledUp |= progression.LeveledUp;

                    var experienceGained =
                        DungeonPacketBuilders.CreateExperienceGained(
                            checked((byte)Math.Clamp(
                                session.Character.Active.Level,
                                1,
                                byte.MaxValue)),
                            session.Character.Active.Experience,
                            extraExperienceA: 0,
                            extraExperienceB: 0,
                            skillPoints: checked((ushort)Math.Min(
                                session.Skill.CurrentSp,
                                ushort.MaxValue)));
                    await context.Queue.SendAsync(experienceGained, Name, cancellationToken);
                    RuntimeLogger.Info(
                        "Monster {MonsterId} (index {MonsterIndex}, level {MonsterLevel}, type {MonsterType}) granted character {CharacterId} {GrantedExperience} EXP; cumulative EXP is {Experience} at level {Level}.",
                        monsterUniqueId,
                        defeatedMonster.MonsterIndex,
                        defeatedMonster.Level,
                        defeatedMonster.Type,
                        session.Character.Active.Id,
                        progression.GrantedExperience,
                        session.Character.Active.Experience,
                        session.Character.Active.Level);
                    if (grantedSkillPoints > 0)
                    {
                        RuntimeLogger.Info(
                            "Monster {MonsterId} advanced character level {OldLevel}->{Level} and granted {SkillPoints} SP.",
                            monsterUniqueId,
                            progression.PreviousLevel,
                            progression.Level,
                            grantedSkillPoints);
                    }

                    if (progression.LeveledUp)
                    {
                        await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
                        await senders.ValidateAndCorrectCharacterStatsAsync(context,
                            "monster experience level-up");
                    }
                }
            }

            if (protocolDrops.Count > 0)
            {
                RuntimeLogger.Info(
                    "Monster {MonsterId} generated {DropCount} ground drops in room ({RoomX},{RoomY}).",
                    monsterUniqueId,
                    protocolDrops.Count,
                    session.Dungeon.RoomX,
                    session.Dungeon.RoomY);
            }

            var clearDecision = DungeonRoomClearRules.Evaluate(
                session.Dungeon.Room!,
                defeatedMonster,
                session.Dungeon.AliveMonsters);
            if (clearDecision.ForcedMonsterIds.Length > 0)
            {
                RuntimeLogger.Warn(
                    "Dungeon {DungeonId} received the final boss MONSTER_DIE with {MonsterCount} monsters still alive in room ({RoomX},{RoomY}); waiting for the native BOSS_DIE_CHECK/ALLDIE_MONSTER cascade instead of injecting a solo UDP peer packet.",
                    session.Dungeon.CurrentDungeonId,
                    clearDecision.ForcedMonsterIds.Length,
                    session.Dungeon.RoomX,
                    session.Dungeon.RoomY);
            }

            if (clearDecision.ShouldEnableClear && !session.Dungeon.ClearEnabled)
            {
                session.Dungeon.ClearEnabled = true;
                if (!session.Dungeon.TutorialActive)
                {
                    await senders.GrantClearQuestItemsAsync(context, cancellationToken);
                }
                var enableClear =
                    DungeonPacketBuilders.CreateEnableClearDungeon();
                await context.Queue.SendAsync(enableClear, Name, cancellationToken);
                RuntimeLogger.Info(
                    "Dungeon {DungeonId} boss map room ({RoomX},{RoomY}) cleared by {ClearReason}; settlement enabled",
                    session.Dungeon.CurrentDungeonId,
                    session.Dungeon.RoomX,
                    session.Dungeon.RoomY,
                    clearDecision.Reason);
            }

            if (!session.Dungeon.TutorialActive)
            {
                await senders.CompleteClearQuestTriggersAsync(context,
                    $"map {session.Dungeon.Room!.MapId} monster death");
            }
        }
    }
}
