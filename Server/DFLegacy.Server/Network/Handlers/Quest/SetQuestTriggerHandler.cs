using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SET_QUEST_TRIGGER (CMD 35): applies the client's trigger action through the per-quest requirement policies (07 §5 group 7).
/// </summary>
public sealed class SetQuestTriggerHandler(
    ILogger<SetQuestTriggerHandler> logger,JsonGameStore store, QuestCatalog questCatalog, ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.SetQuestTriggerCommand;

    public string Name => "SetQuestTrigger";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // DF2008 sends sequence:u16, questId:u16 and a one-byte
        // trigger action. 0x10/0x20/0x40 decrement one of the
        // three packed nine-bit remaining counters; it does not send
        // the resulting u32 trigger value back to the server.
        if (session.Character.Active is null || request.Body.Length < 5)
        {
            var error = GameProtocolEngine.CreateCommandError(request, 22);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var questId = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, 2));
        var questIndex = session.Quest.Current.FindIndex(quest => quest.QuestId == questId);
        if (questIndex < 0)
        {
            var error = GameProtocolEngine.CreateCommandError(request, 22);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        var oldTrigger = session.Quest.Current[questIndex].Trigger;
        var triggerAction = request.Body[4];
        var trigger = oldTrigger;
        var validTrigger = false;
        if (questCatalog.TryGetDefinition(questId, out var definition))
        {
            if (QuestItemRequirementPlanner.IsSeeking(definition))
            {
                trigger = QuestItemRequirementPlanner.GetInitialTrigger(
                    definition,
                    session.Inventory.Main.Values,
                    session.Currency.Gold,
                    session.Currency.VictoryPoints);
                validTrigger = true;
            }
            else if (QuestPvpRankRequirement.IsPvpRank(definition))
            {
                // Native PvP quests send 0 when satisfied and 1
                // when rank falls below the requirement. Neither
                // action may override the authoritative rank.
                validTrigger = triggerAction is 0 or 1;
                trigger = senders.GetAuthoritativeQuestTrigger(context, 
                    definition, session.Inventory.Main.Values, session.Currency.Gold, session.Currency.VictoryPoints);
            }
            else if (QuestMeetNpcRequirement.IsMeetNpc(definition))
            {
                validTrigger = QuestMeetNpcRequirement.TryApplyClientCompletion(
                    definition,
                    oldTrigger,
                    triggerAction,
                    out trigger);
            }
            else if (QuestMapRequirement.IsClearMap(definition))
            {
                validTrigger = QuestMapRequirement.TryApplyClientCompletion(
                    definition, triggerAction, session.Dungeon.Run, out trigger);
            }
            else if (QuestDungeonClearRequirement.IsDungeonClearCondition(
                         definition))
            {
                // DF2008 evaluates all eight
                // [condition under clear] subtypes locally.
                // When the configured clear condition is met it
                // sends CMD 35 with action 0, meaning complete.
                validTrigger = session.Dungeon.CurrentDungeonId != 0
                    && session.Dungeon.ClearEnabled
                    && QuestDungeonClearRequirement.Matches(
                        definition,
                        session.Dungeon.CurrentDungeonId,
                        session.Dungeon.Difficulty)
                    && QuestDungeonClearRequirement.TryApplyClientCompletion(
                        definition,
                        triggerAction,
                        out trigger);
            }
            else
            {
                validTrigger = definition.TryApplyTriggerAction(
                    oldTrigger,
                    triggerAction,
                    out trigger);
            }
        }

        if (!validTrigger)
        {
            var error = GameProtocolEngine.CreateCommandError(request, 22);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected quest {QuestId} trigger action 0x{Action:X2}.",
                questId,
                triggerAction);
            return;
        }

        session.Quest.Current[questIndex] = session.Quest.Current[questIndex] with
        {
            Trigger = trigger
        };
        var savedQuestProgress = await store.SaveCharacterQuestsAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Quest.Current,
            session.Quest.CompletedIds,
            cancellationToken);
        if (savedQuestProgress is not null)
        {
            session.Character.Active = session.Character.Active with
            {
                Quests = savedQuestProgress.Quests,
                CompletedQuestIds =
                    savedQuestProgress.CompletedQuestIds
            };
        }

        var setQuestTriggerReply = GameProtocolEngine.CreateSetQuestTriggerReply(questId, trigger);
        await context.Outbound.SendAsync(setQuestTriggerReply, Name, cancellationToken);
        logger.LogInformation(
            "Updated quest {QuestId} trigger from {OldTrigger} to {Trigger} through action 0x{Action:X2}.",
            questId,
            oldTrigger,
            trigger,
            triggerAction);
    }
}
