using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// GIVEUP_QUEST (CMD 34): removes a giveup-able quest and refreshes the acceptable list (07 §5 group 7).
/// </summary>
public sealed class GiveupQuestHandler(JsonGameStore store, QuestCatalog questCatalog, ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.GIVEUP_QUEST;

    public string Name => "GiveupQuest";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 19);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var questId = BinaryPrimitives.ReadUInt16LittleEndian(
            request.Body.AsSpan(2, 2));
        var questIndex = session.Quest.Current.FindIndex(quest => quest.QuestId == questId);
        if (questIndex < 0)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 19);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        if (questCatalog.TryGetDefinition(questId, out var definition)
            && !definition.CanGiveup)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 20);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        session.Quest.Current.RemoveAt(questIndex);
        session.Character.Active = await store.SaveCharacterQuestsAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Quest.Current,
            session.Quest.CompletedIds,
            cancellationToken) ?? session.Character.Active;

        var giveupQuestReply = QuestPacketBuilders.CreateGiveupQuestReply(questId);
        await context.Queue.SendAsync(giveupQuestReply, Name, cancellationToken);
        await senders.SendAcceptableQuestListAsync(context, cancellationToken);
        RuntimeLogger.Info("Gave up quest {QuestId}.", questId);
    }
}
