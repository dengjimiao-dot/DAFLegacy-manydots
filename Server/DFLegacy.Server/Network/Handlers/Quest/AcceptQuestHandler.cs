using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// ACCEPT_QUEST (CMD 33): validates the three-quest slot limit, dependency-item grants and persists the acceptance transaction (07 §5 group 7).
/// </summary>
public sealed class AcceptQuestHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    QuestCatalog questCatalog,
    DungeonCatalog dungeonCatalog,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.ACCEPT_QUEST;

    public string Name => "AcceptQuest";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 23);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var questId = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, 2));
        if (session.Quest.Current.Count >= 3)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var acceptableQuestIds = senders.GetAcceptableQuestIds(context);
        if (!acceptableQuestIds.Contains(questId)
            || !questCatalog.TryGetDefinition(questId, out var definition))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 18);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var acceptancePlan = QuestAcceptancePlanner.Create(
            session.Inventory.Main,
            definition,
            itemCatalog,
            senders.GetInventoryWeightLimit(context, session.Character.Active));
        if (!acceptancePlan.Success)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 17);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected quest {QuestId} because dependency item grants failed: {Failure} ({Error})",
                questId,
                acceptancePlan.Failure,
                acceptancePlan.Error);
            return;
        }

        var plannedCompletedQuestIds = session.Quest.CompletedIds.ToHashSet();
        if (definition.Repeatable)
        {
            plannedCompletedQuestIds.Remove(questId);
        }

        var acceptedQuest = new CharacterQuestRecord(
            questId,
            senders.GetAuthoritativeQuestTrigger(context, 
                definition,
                acceptancePlan.Inventory.Values,
                session.Currency.Gold,
                session.Currency.VictoryPoints));
        var plannedQuests = session.Quest.Current
            .Append(acceptedQuest)
            .ToList();
        var questDungeonUnlocks = HiddenDungeonUnlockCatalog.ResolveQuestDungeonUnlockIds(
            questId, definition, dungeonCatalog);
        var unlockedHiddenDungeon = questDungeonUnlocks.Any(dungeonId =>
            !(session.Character.Active.UnlockedDungeonIds ?? []).Contains(dungeonId));
        var savedQuestProgress = await store.SaveCharacterQuestProgressAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            acceptancePlan.Inventory.Values,
            plannedQuests,
            plannedCompletedQuestIds,
            cancellationToken,
            unlockedDungeonIds: questDungeonUnlocks);
        if (savedQuestProgress is null)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 18);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Warn(
                "Rejected quest {QuestId} because the quest and dependency-item transaction could not be persisted.",
                questId);
            return;
        }

        session.Character.Active = savedQuestProgress;
        session.Inventory.Main = (savedQuestProgress.Inventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Quest.Current.Clear();
        session.Quest.Current.AddRange(savedQuestProgress.Quests ?? []);
        session.Quest.CompletedIds.Clear();
        session.Quest.CompletedIds.UnionWith(
            savedQuestProgress.CompletedQuestIds ?? []);

        var acceptQuestReply = QuestPacketBuilders.CreateAcceptQuestReply(
            questId,
            acceptedQuest.Trigger,
            acceptancePlan.InsertedItems
                .Select(item => new GameQuestInsertedItem(
                    item.Slot,
                    item.ItemId,
                    item.CountOrSeed))
                .ToArray());
        await context.Queue.SendAsync(acceptQuestReply, Name, cancellationToken);
        if (definition.DeleteNpcIndex >= 0)
        {
            RuntimeLogger.Info(
                "Quest {QuestId} accepted with trigger {Trigger}; client delete-NPC index is {NpcIndex}.",
                questId, acceptedQuest.Trigger, definition.DeleteNpcIndex);
        }
        if (unlockedHiddenDungeon)
        {
            await senders.SendDungeonPermissionsAsync(context, cancellationToken);
        }
        await senders.SendAcceptableQuestListAsync(context, cancellationToken);
        RuntimeLogger.Info(
            "Accepted quest {QuestId} and granted {Count} dependency-item slot updates: {Items}.",
            questId,
            acceptancePlan.InsertedItems.Count,
            string.Join(
                ", ",
                acceptancePlan.InsertedItems.Select(item =>
                    $"{item.ItemId}x{item.CountOrSeed}@{item.Slot}")));
    }
}
