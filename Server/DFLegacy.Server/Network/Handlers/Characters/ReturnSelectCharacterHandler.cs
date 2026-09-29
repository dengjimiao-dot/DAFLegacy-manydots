using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// RETURN_SELECT_CHARACTER (CMD 7): the town menu's "Select Character"
/// action. Persists progression and the last stable town position, detaches
/// town presence and party membership, releases the character lease and
/// clears the whole session state. Characters that have not finished the
/// tutorial are refused with error 4, exactly as the legacy branch did.
/// </summary>
public sealed class ReturnSelectCharacterHandler(
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    PartyCoordinator partyCoordinator,
    ILogger<ReturnSelectCharacterHandler> logger) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.ReturnSelectCharacterCommand;

    public string Name => "RETURN_SELECT_CHARACTER";

    // Works both with and without an active character: the branch only
    // persists and detaches when one is attached (07 §4.8.1 registration).
    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame frame,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is { TutorialStarted: false })
        {
            var tutorialError = GameProtocolEngine.CreateCommandError(
                frame,
                errorCode: 4);
            await context.Outbound.SendAsync(tutorialError, Name, cancellationToken);
            logger.LogInformation(
                "Rejected return to character selection for {CharacterId} until the tutorial is complete.",
                session.Character.Active.Id);
            return;
        }

        var returningCharacterId = session.Character.Active?.Id;
        if (session.Character.Active is not null)
        {
            if (session.Dungeon.CurrentDungeonId != 0 && session.Dungeon.MonsterExperienceGained > 0)
            {
                session.Character.Active = await store.SaveCharacterProgressionAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Character.Active.Level,
                    session.Character.Active.Experience,
                    session.Skill.CurrentSp,
                    cancellationToken) ?? session.Character.Active;
            }

            // Command 7 is the town menu's "Select
            // Character" action. Persist the last stable
            // town position before detaching the active
            // character from this channel session.
            await SessionLocationPersistence.SaveCurrentLocationAsync(
                store,
                session,
                cancellationToken);
        }

        // Leaving the game world detaches town presence
        // and party membership before the lease goes.
        partyCoordinator.OnSessionClosed(context.Runtime.Id);
        characterSessions.Release(session.Character.Lease);
        session.Character.Lease = null;
        session.Character.Active = null;
        session.Character.LastProjectedCombatStats = null;
        session.Premium.ExpiryUnix.Clear();
        session.Inventory.Main.Clear();
        session.Inventory.Avatar.Clear();
        session.Inventory.Equipped.Clear();
        session.Inventory.Warehouse.Clear();
        session.Inventory.WarehouseCapacity = CharacterWarehouseProgression.InitialCapacity;
        session.Skill.Current.Clear();
        session.Quest.Current.Clear();
        session.Quest.CompletedIds.Clear();
        session.Skill.CurrentSp = 0;
        session.Currency.Gold = 0;
        session.Currency.Cash = 0;
        session.Currency.VictoryPoints = 0;
        session.Dungeon.CurrentDungeonId = 0;
        session.Dungeon.Layout = null;
        session.Dungeon.Room = null;
        session.Dungeon.InSelection = false;
        session.Dungeon.RoomX = 0;
        session.Dungeon.RoomY = 0;
        session.Dungeon.Difficulty = 0;
        session.Dungeon.AliveMonsters.Clear();
        session.Dungeon.ReportedBossDeaths.Clear();
        session.Dungeon.BossCascadeDeaths.Clear();
        session.Dungeon.DestroyedPassiveObjects.Clear();
        session.Dungeon.GroundItems.ResetDungeon();
        session.Dungeon.Run.Stop();
        session.Dungeon.TutorialActive = false;
        session.Dungeon.CharacterAlive = true;
        session.Dungeon.DeathGeneration++;
        session.Dungeon.DeathTimeout = null;
        session.Dungeon.ClearEnabled = false;
        session.Dungeon.ResultSent = false;
        session.Dungeon.CardLayoutSent = false;
        session.Dungeon.CardRightsSent = false;
        session.Dungeon.CardSelectionDeadline = null;
        session.Dungeon.ClearReward = null;
        session.Dungeon.FreeCardIndex = null;
        session.Dungeon.GoldCardIndex = null;
        session.Dungeon.MonsterExperienceGained = 0;
        session.Dungeon.ClearExperience = null;
        session.Dungeon.ConsumedFatigue = 0;
        session.Dungeon.CreatureStomachCheckpointSeconds = null;
        session.Dungeon.LeveledUp = false;
        session.Dungeon.NormalMonsterKills = 0;
        session.Dungeon.ChampionMonsterKills = 0;
        session.Dungeon.BossMonsterKills = 0;
        session.Dungeon.ChampionRewardOrdinal = 0;
        session.Character.MovementValue = 100;
        session.Character.AreaRosterInitialized = false;
        session.Weakness.StaminaStateInitialized = false;

        var returnReply = GameProtocolEngine.CreateCommandReply(
            frame,
            success: true);
        await context.Outbound.SendAsync(returnReply, Name, cancellationToken);

        // The 60CN client refreshes the roster itself with a
        // GET_USERINFO (CMD 8) right after this acknowledgement;
        // pushing the roster here too arrives as a second list
        // and makes the select-screen event notice pop up twice.
        logger.LogInformation(
            "Returned character {CharacterId} to character selection for account {Account}.",
            returningCharacterId,
            session.Login.AccountName);
    }
}
