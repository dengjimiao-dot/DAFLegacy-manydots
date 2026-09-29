using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SELECT_DUNGEON (CMD 16): enters the chosen dungeon map and streams the room bootstrap (07 §5 group 13).
/// </summary>
public sealed class SelectDungeonHandler(    JsonGameStore store,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    ILogger<SelectDungeonHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SELECT_DUNGEON;

    public string Name => "SELECT_DUNGEON";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // DFLegacy command 16 is sequence, u16 dungeon id,
        // byte difficulty and byte option flag.
        var dungeonId = request.Body.Length >= 4
            ? BinaryPrimitives.ReadUInt16LittleEndian(
                request.Body.AsSpan(2, sizeof(ushort)))
            : (ushort)1;
        var difficulty = request.Body.Length >= 5
            ? request.Body[4]
            : (byte)0;
        var dungeonOption = request.Body.Length >= 6
            ? request.Body[5]
            : (byte)0;

        var requiresTutorial = session.Character.Active is { TutorialStarted: false };
        if (!requiresTutorial && session.Dungeon.InSelection
            && session.Dungeon.SelectionDeadline is { } entryDeadline
            && DateTimeOffset.UtcNow >= entryDeadline)
        {
            // A ready packet may win Task.WhenAny at the deadline.
            // Check again before layout generation or ticket/fatigue consumption.
            await senders.ReturnToTownFromDungeonAsync(context, "selection-countdown-expired");
            return;
        }

        if (!requiresTutorial && !session.Dungeon.InSelection)
        {
            logger.LogWarning("Ignored stale dungeon selection for {CharacterId}.", session.Character.Active?.Id);
            return;
        }

        if (!requiresTutorial && dungeonOption != 0
            && (session.Dungeon.SelectionWorldMap is null
                || !session.Dungeon.SelectionWorldMap.DungeonIds.Contains(dungeonId)
                || !session.Dungeon.SelectionWorldMap.MeetsHellQuests(session.Quest.CompletedIds)
                || !session.Dungeon.SelectionWorldMap.HasHellItems(session.Inventory.Main.Values)
                || !dungeonCatalog.IsHellDungeon(dungeonId)))
        {
            var rejected = ConnectionPacketBuilders.CreateCommandError(request, errorCode: 14);
            await context.Outbound.SendAsync(rejected, Name, cancellationToken);
            await senders.SendDungeonSelectionEligibilityAsync(context, cancellationToken);
            return;
        }
        if (requiresTutorial
            && dungeonId != GameProtocolEngine.TutorialDungeonId)
        {
            var tutorialRequiredError = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 4);
            await context.Outbound.SendAsync(tutorialRequiredError, Name, cancellationToken);
            logger.LogInformation(
                "Rejected dungeon {DungeonId} for character {CharacterId} until tutorial dungeon {TutorialDungeonId} is complete.",
                dungeonId,
                session.Character.Active!.Id,
                GameProtocolEngine.TutorialDungeonId);
            return;
        }

        if (dungeonId == GameProtocolEngine.TutorialDungeonId)
        {
            if (!requiresTutorial)
            {
                var closedTutorialError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 4);
                await context.Outbound.SendAsync(closedTutorialError, Name, cancellationToken);
                logger.LogInformation(
                    "Rejected tutorial dungeon replay for character {CharacterId}.",
                    session.Character.Active?.Id);
                return;
            }

            var tutorialMonsters = DungeonPacketBuilders.CreateTutorialMonsters();
            session.Dungeon.InSelection = false;
            session.Dungeon.CurrentDungeonId = GameProtocolEngine.TutorialDungeonId;
            session.Dungeon.Layout = null;
            session.Dungeon.RoomX = 0;
            session.Dungeon.RoomY = 0;
            session.Dungeon.Difficulty = difficulty;
            session.Dungeon.Room = new DungeonRoomDefinition(
                GameProtocolEngine.TutorialMapId,
                DungeonMapType.Boss,
                tutorialMonsters,
                Topology: 'B',
                HasScriptBossActor: false,
                PassiveObjects: [],
                PassiveItemSpawns: []);
            session.Dungeon.AliveMonsters.Clear();
            session.Dungeon.AliveMonsters.UnionWith(
                tutorialMonsters.Select(monster => monster.UniqueId));
            session.Dungeon.ReportedBossDeaths.Clear();
            session.Dungeon.BossCascadeDeaths.Clear();
            session.Dungeon.DestroyedPassiveObjects.Clear();
            session.Dungeon.GroundItems.ResetDungeon();
            session.Dungeon.Run.Stop();
            session.Dungeon.TutorialActive = true;
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

            var tutorialInfo =
                DungeonPacketBuilders.CreateTutorialDungeonInfo(difficulty);
            await context.Outbound.SendAsync(tutorialInfo, Name, cancellationToken);

            var tutorialStartMap =
                DungeonPacketBuilders.CreateTutorialStartMap();
            await context.Outbound.SendAsync(tutorialStartMap, Name, cancellationToken);
            logger.LogInformation(
                "Started mandatory tutorial dungeon/scene {TutorialId} for character {CharacterId}; monsters={MonsterCount}, difficulty={Difficulty}, option={Option}; no PVF dungeon or map definition was queried.",
                GameProtocolEngine.TutorialDungeonId,
                session.Character.Active!.Id,
                tutorialMonsters.Length,
                difficulty,
                dungeonOption);
            return;
        }

        if (session.Character.Active is null
            || !dungeonCatalog.TryGetDefinition(dungeonId, out var dungeon))
        {
            logger.LogWarning(
                "Client selected unknown dungeon {DungeonId}; request ignored.",
                dungeonId);
            return;
        }

        if (!senders.GetVisibleDungeonIds(context).Contains(dungeonId))
        {
            var lockedDungeonError = ConnectionPacketBuilders.CreateCommandError(
                request,
                errorCode: 18);
            await context.Outbound.SendAsync(lockedDungeonError, Name, cancellationToken);
            logger.LogWarning(
                "Rejected locked hidden dungeon {DungeonId} for character {CharacterId}.",
                dungeonId,
                session.Character.Active.Id);
            return;
        }

        var maximumDifficulty =
            DungeonDifficultyProgression.GetMaximumDifficulty(
                session.Character.Active.DungeonProgress,
                dungeonId);
        if (difficulty > maximumDifficulty)
        {
            var lockedDifficultyError =
                ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 18);
            await context.Outbound.SendAsync(lockedDifficultyError, Name, cancellationToken);
            logger.LogWarning(
                "Rejected locked dungeon difficulty {Difficulty} for dungeon {DungeonId}; character {CharacterId} has maximum {MaximumDifficulty}.",
                difficulty,
                dungeonId,
                session.Character.Active.Id,
                maximumDifficulty);
            return;
        }

        if (!dungeonCatalog.TryCreateLayout(
                dungeonId,
                out var dungeonLayout,
                partyMemberCount: 1))
        {
            logger.LogWarning(
                "Dungeon {DungeonId} has no usable maze layout; request ignored.",
                dungeonId);
            return;
        }

        var unfinishedQuestDefinitions = session.Quest.Current
            .Where(quest => quest.Trigger != 0)
            .Select(quest => questCatalog.TryGetDefinition(quest.QuestId, out var definition)
                ? definition : null)
            .OfType<QuestDefinition>();
        if (dungeonCatalog.TryApplyQuestMap(
                dungeonLayout, unfinishedQuestDefinitions, out var questMapId))
        {
            logger.LogInformation(
                "Dungeon {DungeonId} selected rescue quest map {MapId} for character {CharacterId}.",
                dungeonId, questMapId, session.Character.Active.Id);
        }

        if (!session.Dungeon.Run.TryStart(
                dungeonLayout,
                difficulty,
                dungeonOption,
                out var initialTransition))
        {
            logger.LogWarning(
                "Dungeon {DungeonId} maze {MazeIndex} start room ({RoomX},{RoomY}) has no map.",
                dungeonId,
                dungeonLayout.MazeIndex,
                dungeonLayout.StartX,
                dungeonLayout.StartY);
            return;
        }

        CharacterItemRecord? consumedHellTicket = null;
        GameServerPacket? pendingHellTicketUpdate = null;
        if (session.Dungeon.Run.HellMode)
        {
            var plannedInventory = session.Inventory.Main.ToDictionary();
            if (session.Dungeon.SelectionWorldMap is null
                || !session.Dungeon.SelectionWorldMap.TryConsumeHellItems(session.Inventory.Main, out plannedInventory))
            {
                session.Dungeon.Run.Stop();
                var ticketError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 14);
                await context.Outbound.SendAsync(ticketError, Name, cancellationToken);
                await senders.SendDungeonSelectionEligibilityAsync(context, cancellationToken);
                logger.LogInformation(
                    "Rejected hell entry for dungeon {DungeonId}: no matching ticket ({TicketIds}).",
                    dungeonId,
                    string.Join(',', dungeon.HellTicketItemIds));
                return;
            }

            var changedTickets = session.Inventory.Main.Values
                .Where(item => !plannedInventory.TryGetValue(item.Slot, out var remaining)
                    || remaining.CountOrValue != item.CountOrValue)
                .OrderBy(item => item.Slot).ToArray();
            consumedHellTicket = changedTickets.FirstOrDefault();

            var savedTicketConsumption =
                await store.SaveCharacterInventoryAsync(
                    session.Login.AccountName,
                    session.Character.Active.Id,
                    session.Currency.Gold,
                    plannedInventory.Values,
                    session.Inventory.Equipped.Values,
                    cancellationToken);
            if (savedTicketConsumption is null)
            {
                session.Dungeon.Run.Stop();
                var ticketSaveError = ConnectionPacketBuilders.CreateCommandError(
                    request,
                    errorCode: 19);
                await context.Outbound.SendAsync(ticketSaveError, Name, cancellationToken);
                return;
            }

            session.Character.Active = savedTicketConsumption;
            session.Inventory.Main = plannedInventory;
            pendingHellTicketUpdate = InventoryPacketBuilders.CreateUpdateItemList(
                0,
                changedTickets.Select(item => plannedInventory.TryGetValue(item.Slot, out var remaining)
                    ? builders.ToGameInventoryEntry(remaining)
                    : new GameInventoryEntry(item.Slot, ushort.MaxValue, 0)).ToArray());
        }

        session.Dungeon.InSelection = false;
        session.Dungeon.TutorialActive = false;
        session.Dungeon.CurrentDungeonId = dungeonId;
        session.Dungeon.Layout = dungeonLayout;
        session.Dungeon.RoomX = session.Dungeon.Run.RoomX;
        session.Dungeon.RoomY = session.Dungeon.Run.RoomY;
        session.Dungeon.Difficulty = difficulty;
        session.Dungeon.Room = initialTransition.Room;
        session.Dungeon.AliveMonsters.Clear();
        session.Dungeon.AliveMonsters.UnionWith(
            session.Dungeon.Room.Monsters.Select(monster => monster.UniqueId));
        session.Dungeon.ReportedBossDeaths.Clear();
        session.Dungeon.BossCascadeDeaths.Clear();
        session.Dungeon.DestroyedPassiveObjects.Clear();
        session.Dungeon.GroundItems.ResetDungeon();
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

        byte hellRoomX = 0;
        byte hellRoomY = 0;
        if (session.Dungeon.Run.HellMode)
        {
            _ = dungeonCatalog.TryGetHellRoom(
                dungeonId,
                out _,
                out hellRoomX,
                out hellRoomY);
        }

        var dungeonInfo = DungeonPacketBuilders.CreateDungeonInfo(
            dungeonId,
            difficulty,
            mazeIndex: dungeonLayout.MazeIndex,
            bossX: dungeonLayout.BossX,
            bossY: dungeonLayout.BossY,
            flagA: hellRoomX,
            flagB: hellRoomY);
        await context.Outbound.SendAsync(dungeonInfo, Name, cancellationToken);
        if (pendingHellTicketUpdate is not null)
        {
            await context.Outbound.SendAsync(pendingHellTicketUpdate, Name, cancellationToken);
        }

        var passiveObjectDrops = senders.RegisterPassiveObjectDrops(
            session.Dungeon.Room,
            session.Dungeon.GroundItems,
            session.Character.LocalUserId,
            session.Dungeon.RoomX,
            session.Dungeon.RoomY);
        var startMap = DungeonPacketBuilders.CreateStartMap(
            roomX: session.Dungeon.RoomX,
            roomY: session.Dungeon.RoomY,
            mapId: session.Dungeon.Room.MapId,
            monsters: session.Dungeon.Room.Monsters,
            passiveObjectDrops: passiveObjectDrops,
            randomSeed: session.Dungeon.Room.ClientRandomSeed,
            roomStateFlag: session.Dungeon.Run.RoomStateFlag,
            hellPartyMode: session.Dungeon.Run.CurrentHellPartyMode);
        await context.Outbound.SendAsync(startMap, Name, cancellationToken);
        session.Dungeon.CreatureStomachCheckpointSeconds =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await senders.ConsumeDungeonRoomFatigueAsync(context, 
            session.Dungeon.RoomX,
            session.Dungeon.RoomY);
        await senders.EnableEmptyBossRoomSettlementAsync(context, session.Dungeon.Room);
        logger.LogInformation(
            "Dungeon {DungeonId} selected maze {MazeIndex}, start ({StartX},{StartY}), boss ({BossX},{BossY}), start map {MapId}, monsters={MonsterCount}, champions={ChampionCount}, mapChampionBase={MapChampionBase}, clientSeed={ClientSeed}, hellMode={HellMode}, partyMode={HellPartyMode}, ticket={TicketItemId}.",
            dungeonId,
            dungeonLayout.MazeIndex,
            dungeonLayout.StartX,
            dungeonLayout.StartY,
            dungeonLayout.BossX,
            dungeonLayout.BossY,
            session.Dungeon.Room.MapId,
            session.Dungeon.Room.Monsters.Length,
            session.Dungeon.Room.Monsters.Count(monster =>
                monster.Type == GameDungeonMonsterTypes.Champion),
            session.Dungeon.Room.BaseChampionCount,
            session.Dungeon.Room.ClientRandomSeed,
            session.Dungeon.Run.HellMode,
            session.Dungeon.Run.CurrentHellPartyMode,
            consumedHellTicket?.ItemId ?? 0);
    }
}
