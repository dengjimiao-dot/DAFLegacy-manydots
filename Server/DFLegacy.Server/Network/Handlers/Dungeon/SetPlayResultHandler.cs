using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SET_PLAY_RESULT (CMD 49): dungeon clear settlement — experience, fatigue and rewards (07 §5 group 13).
/// </summary>
public sealed class SetPlayResultHandler(
    DungeonExperienceCatalog dungeonExperienceCatalog, IGameStore store,
    ItemCatalog itemCatalog,
    DungeonCatalog dungeonCatalog,
    CharacterExperienceCatalog experienceCatalog,
    CharacterSpCatalog spCatalog,
    ClearRewardGenerator clearRewardGenerator,
    CharacterSessionRegistry characterSessions,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SET_PLAY_RESULT;

    public string Name => "SET_PLAY_RESULT";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.TutorialActive)
        {
            RuntimeLogger.Info(
                "Ignored normal settlement command for tutorial dungeon {DungeonId}; waiting for tutorial module {ModuleId}.",
                session.Dungeon.CurrentDungeonId,
                GameProtocolConstants.TutorialCompletionModule);
            return;
        }

        if (session.Dungeon.ClearEnabled && !session.Dungeon.ResultSent)
        {
            var hasClearScore = DungeonClearScore.TryParse(
                request.Body,
                out var clearScore);
            var resultCode = hasClearScore
                ? clearScore.CalculatedResultCode
                : (byte)0;
            var clientResultValue = request.Body.Length >= 6
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    request.Body.AsSpan(2, sizeof(uint)))
                : 0u;
            var playResult = DungeonPacketBuilders.CreatePlayResult(
                session.Character.LocalUserId,
                clientResultValue);
            await context.Queue.SendAsync(playResult, Name, cancellationToken);

            if (await senders.SettleDungeonCreatureHungerAsync(context,
                    notifyClient: true,
                    cancellationToken))
            {
                await senders.GrantDungeonCreatureExperienceAsync(context, cancellationToken);
            }
            else
            {
                session.Dungeon.ConsumedFatigue = 0;
            }
            session.Dungeon.CreatureStomachCheckpointSeconds = null;

            var hasDungeonDefinition =
                dungeonCatalog.TryGetDefinition(
                    session.Dungeon.CurrentDungeonId,
                    out var settledDungeon);
            var clearGrantedSkillPoints = 0;
            if (session.Dungeon.ClearExperience is null)
            {
                // Clear EXP is a separate reason from the
                // per-monster grants accumulated above.
                var calculatedClearExperience =
                    session.Character.Active is not null
                    && hasDungeonDefinition
                        ? dungeonExperienceCatalog.CalculateClearExperience(
                            session.Character.Active.Level,
                            settledDungeon,
                            session.Dungeon.Difficulty,
                            resultCode,
                            session.Dungeon.NormalMonsterKills
                            + session.Dungeon.ChampionMonsterKills
                            + session.Dungeon.BossMonsterKills,
                            partyMemberCount: 1,
                            bonuses: new DungeonClearBonusContext(
                                HasAvatar: session.Inventory.Avatar.Values.Any(item =>
                                    itemCatalog.TryGetDefinition(item.ItemId, out var avatar)
                                    && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(avatar, item.Slot)),
                                HasCreature: session.Inventory.Creature.TryGetValue(
                                    CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                                    out var clearCreature) && clearCreature.CountOrValue != 0,
                                HasBlackDiamond: senders.HasActivePremiumService(context, GameProtocolConstants.BlackDiamondServiceType),
                                MentorBonusRate: DungeonClearBonusContext.GetMentorBonusRate(
                                    session.Character.Active, characterSessions.IsOnline),
                                EventBonusRate: dungeonExperienceCatalog.GetEventBonusRate(DateTimeOffset.UtcNow),
                                ChannelBonusRate: dungeonExperienceCatalog.GetChannelBonusRate(session.Dungeon.CurrentDungeonId)))
                        : GameDungeonClearExperienceBreakdown.Empty;

                if (session.Character.Active is not null
                    && (session.Dungeon.MonsterExperienceGained > 0
                        || calculatedClearExperience.TotalExperience > 0))
                {
                    var clearProgression = experienceCatalog.Grant(
                        session.Character.Active,
                        calculatedClearExperience.TotalExperience);
                    clearGrantedSkillPoints =
                        spCatalog.CalculateLevelUpReward(
                            clearProgression.PreviousLevel,
                            clearProgression.Level);
                    var settledSp = checked(
                        session.Skill.CurrentSp + clearGrantedSkillPoints);
                    var savedProgression =
                        await store.SaveCharacterProgressionAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
                            clearProgression.Level,
                            clearProgression.Experience,
                            settledSp,
                            cancellationToken);
                    if (savedProgression is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not persist dungeon settlement for character {session.Character.Active.Id}.");
                    }

                    session.Skill.CurrentSp = settledSp;
                    session.Character.Active = savedProgression;
                    session.Dungeon.LeveledUp |= clearProgression.LeveledUp;
                    if (clearProgression.LeveledUp)
                    {
                        RuntimeLogger.Info(
                            "Dungeon clear advanced character level {OldLevel}->{Level} and granted {SkillPoints} SP.",
                            clearProgression.PreviousLevel,
                            clearProgression.Level,
                            clearGrantedSkillPoints);
                    }
                }

                // A non-null value is the per-run settlement marker.
                // Cache it only after progression persistence succeeds.
                session.Dungeon.ClearExperience = calculatedClearExperience;
            }

            var settledClearExperience =
                session.Dungeon.ClearExperience
                ?? GameDungeonClearExperienceBreakdown.Empty;
            var generationLevel = hasDungeonDefinition
                ? settledDungeon.BasisLevel
                : checked((byte)Math.Clamp(
                    session.Character.Active?.Level ?? 1,
                    1,
                    byte.MaxValue));
            session.Dungeon.ClearReward ??= clearRewardGenerator.Generate(
                generationLevel,
                session.Dungeon.Difficulty,
                senders.HasActivePremiumService(context,
                    GameProtocolConstants.BlackDiamondServiceType),
                !hasDungeonDefinition || settledDungeon.GoldCardUse,
                partyMemberCount: 1,
                normalMonsterKills: session.Dungeon.NormalMonsterKills,
                championMonsterKills: session.Dungeon.ChampionMonsterKills,
                bossMonsterKills: session.Dungeon.BossMonsterKills);
            var clearReward =
                DungeonPacketBuilders.CreateClearDungeonReward(
                    session.Character.LocalUserId,
                    session.Dungeon.ClearReward.GoldCardCost,
                    session.Dungeon.ClearReward.FreeGoldAmount,
                    session.Dungeon.ClearReward.FreeItemId,
                    goldCardEnabled: session.Dungeon.ClearReward.GoldCardEnabled,
                    goldItemId: session.Dungeon.ClearReward.GoldItemId,
                    premiumCardEnabled: senders.HasActivePremiumService(context,
                        GameProtocolConstants.BlackDiamondServiceType),
                    premiumItemId: session.Dungeon.ClearReward.PremiumItemId,
                    experience: settledClearExperience);
            await context.Queue.SendAsync(clearReward, Name, cancellationToken);
            RuntimeLogger.Info(
                "Dungeon {DungeonId} clear-card rewards prepared for user {UserId}: normalKills={NormalKills}, championKills={ChampionKills}, bossKills={BossKills}, championAnimationCount={ChampionAnimationCount}, freeGold={FreeGold}, freeItem={FreeItemId}, premiumItem={PremiumItemId}, goldItem={GoldItemId}, partyMemberCount={PartyMemberCount}.",
                session.Dungeon.CurrentDungeonId,
                session.Character.LocalUserId,
                session.Dungeon.NormalMonsterKills,
                session.Dungeon.ChampionMonsterKills,
                session.Dungeon.BossMonsterKills,
                session.Dungeon.ChampionRewardOrdinal,
                session.Dungeon.ClearReward.FreeGoldAmount,
                session.Dungeon.ClearReward.FreeItemId,
                session.Dungeon.ClearReward.PremiumItemId,
                session.Dungeon.ClearReward.GoldItemId,
                1);
            session.Dungeon.ResultSent = true;
            if (session.Dungeon.MonsterExperienceGained > 0
                || settledClearExperience.TotalExperience > 0)
            {
                await senders.SendCurrentEquipmentStateAsync(context, cancellationToken: cancellationToken);
                if (session.Dungeon.LeveledUp)
                {
                    await senders.SendCurrentSkillInfoAsync(context, cancellationToken);
                    await senders.ValidateAndCorrectCharacterStatsAsync(context,
                        "dungeon clear experience level-up");
                    await senders.SendAcceptableQuestListAsync(context, cancellationToken);
                }
            }

            if (hasClearScore
                && session.Character.Active is not null
                && DungeonDifficultyProgression.TryGetNextDifficulty(
                    session.Dungeon.Difficulty,
                    resultCode,
                    out var nextDifficulty))
            {
                var previousMaximum =
                    DungeonDifficultyProgression.GetMaximumDifficulty(
                        session.Character.Active.DungeonProgress,
                        session.Dungeon.CurrentDungeonId);
                if (previousMaximum < nextDifficulty)
                {
                    var savedDifficulty =
                        await store.UnlockNextDungeonDifficultyAsync(
                            session.Login.AccountName,
                            session.Character.Active.Id,
                            session.Dungeon.CurrentDungeonId,
                            session.Dungeon.Difficulty,
                            cancellationToken);
                    if (savedDifficulty is not null)
                    {
                        session.Character.Active = savedDifficulty;
                        // DF2008 uses an incremental NOTI 5 while the client is
                        // still in dungeon state. The client compares this one
                        // record with its cached permission, opens the difficulty
                        // unlock window, and then updates the cache.
                        var difficultyUnlocked =
                            DungeonPacketBuilders.CreateDungeonPermissions(
                                [new GameDungeonPermissionEntry(
                                    session.Dungeon.CurrentDungeonId,
                                    nextDifficulty)]);
                        await context.Queue.SendAsync(difficultyUnlocked, Name, cancellationToken);
                        RuntimeLogger.Info(
                            "Unlocked dungeon {DungeonId} difficulty {Difficulty} after clearing difficulty {ClearedDifficulty} with result code {ResultCode}.",
                            session.Dungeon.CurrentDungeonId,
                            nextDifficulty,
                            session.Dungeon.Difficulty,
                            resultCode);
                    }
                    else
                    {
                        RuntimeLogger.Warn(
                            "Could not persist dungeon {DungeonId} difficulty {Difficulty} for character {CharacterId}.",
                            session.Dungeon.CurrentDungeonId,
                            nextDifficulty,
                            session.Character.Active.Id);
                    }
                }
            }

            if (!hasClearScore)
            {
                RuntimeLogger.Warn(
                    "Dungeon {DungeonId} settlement did not contain the 24-byte score layout; higher difficulty rank gates used result code zero.",
                    session.Dungeon.CurrentDungeonId);
            }
            else if (!clearScore.IsWireResultCodeConsistent)
            {
                RuntimeLogger.Warn(
                    "Dungeon {DungeonId} ignored inconsistent client result code {WireResultCode}; calculated {CalculatedResultCode} from score {CalculatedScore}.",
                    session.Dungeon.CurrentDungeonId,
                    clearScore.WireResultCode,
                    clearScore.CalculatedResultCode,
                    clearScore.CalculatedScore);
            }

            RuntimeLogger.Info(
                "Dungeon {DungeonId} settlement sent for user {UserId}, result {ResultValue}, resultCode={ResultCode}, goldCost={GoldCost}, monsterExp={MonsterExperience}, clearExp={ClearExperience}, base={BaseExperience}, rank={RankBonus}, party={PartyBonus}, mentor={MentorBonus}, avatar={AvatarBonus}, creature={CreatureBonus}, event={EventBonus}, blackDiamond={BlackDiamondBonus}, channel={ChannelBonus}",
                session.Dungeon.CurrentDungeonId,
                session.Character.LocalUserId,
                clientResultValue,
                resultCode,
                session.Dungeon.ClearReward.GoldCardCost,
                session.Dungeon.MonsterExperienceGained,
                settledClearExperience.TotalExperience,
                settledClearExperience.BaseExperience,
                settledClearExperience.RankBonus,
                settledClearExperience.PartyBonus,
                settledClearExperience.MentorBonus,
                settledClearExperience.AvatarBonus,
                settledClearExperience.CreatureBonus,
                settledClearExperience.EventBonus,
                settledClearExperience.BlackDiamondBonus,
                settledClearExperience.ChannelBonus);
        }
    }
}
