using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CHANGE_SKILLSLOT (CMD 30): swaps quick slots, including the adjacent-command compound move the client sends as two back-to-back frames (07 §5 group 8).
/// </summary>
public sealed class ChangeSkillSlotHandler(JsonGameStore store,
    SkillCatalog skillCatalog,
    ILogger<ChangeSkillSlotHandler> logger) : IGameCommandHandler
{

    public ushort ProtocolId => GameProtocolEngine.ChangeSkillSlotCommand;

    public string Name => "ChangeSkillSlot";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 4)
        {
            var invalidQuickSlotReply =
                GameProtocolEngine.CreateCommandError(request, errorCode: 1);
            await context.Outbound.SendAsync(invalidQuickSlotReply, Name, cancellationToken);
            return;
        }

        var sourceQuickSlot = request.Body[2];
        var destinationQuickSlot = request.Body[3];

        // Dropping a skill onto an occupied quick slot sends
        // spare->target and target->source as two adjacent
        // commands. Wait for both before acknowledging them
        // in wire order, so the target is not left visibly
        // empty between the two client-side swaps.
        var adjacentRead = context.ReadAdjacentFrame is null
            ? default
            : await context.ReadAdjacentFrame(
                TimeSpan.FromMilliseconds(5),
                static raw => raw.Type == GameProtocolEngine.CommandPacketType
                    && raw.ProtocolId == GameProtocolEngine.ChangeSkillSlotCommand,
                cancellationToken);
        if (adjacentRead.DecodeFailed)
        {
            return;
        }

        var adjacentRequest = adjacentRead.Frame;
        if (adjacentRequest is not null)
        {
            if (adjacentRequest.Body.Length >= 4)
            {
                var adjacentSourceSlot = adjacentRequest.Body[2];
                var adjacentDestinationSlot = adjacentRequest.Body[3];
                var isCompoundMove = adjacentSourceSlot == destinationQuickSlot;
                var replies = new[]
                {
                    (Request: request,
                        Source: sourceQuickSlot,
                        Destination: destinationQuickSlot),
                    (Request: adjacentRequest,
                        Source: adjacentSourceSlot,
                        Destination: adjacentDestinationSlot)
                };

                IReadOnlyList<CharacterSkillRecord> candidateSkills = session.Skill.Current;
                var rejected = false;
                var rejectionReason = string.Empty;
                foreach (var (_, replySourceSlot, replyDestinationSlot) in replies)
                {
                    if (!skillCatalog.TrySwapSlots(
                        session.Character.Active.Job,
                        candidateSkills,
                        replySourceSlot,
                        replyDestinationSlot,
                        out var swappedSkills,
                        out rejectionReason))
                    {
                        rejected = true;
                        break;
                    }

                    candidateSkills = swappedSkills;
                }

                if (!rejected && !session.Skill.Current.SequenceEqual(candidateSkills))
                {
                    session.Skill.Current = candidateSkills.ToList();
                    session.Character.Active = await store.SaveCharacterSkillsAsync(
                        session.Login.AccountName,
                        session.Character.Active.Id,
                        session.Skill.CurrentSp,
                        session.Skill.Current,
                        cancellationToken) ?? session.Character.Active;
                }

                foreach (var (replyRequest, replySourceSlot, replyDestinationSlot) in replies)
                {
                    var quickSlotBatchReply = rejected
                        ? GameProtocolEngine.CreateCommandError(
                            replyRequest,
                            errorCode: 1)
                        : GameProtocolEngine.CreateChangeSkillSlotReply(
                            replySourceSlot,
                            replyDestinationSlot);
                    await context.Outbound.SendAsync(quickSlotBatchReply, Name, cancellationToken);
                }

                if (rejected)
                {
                    logger.LogWarning(
                        "Rejected adjacent skill-slot changes {FirstSource}->{FirstDestination}, {SecondSource}->{SecondDestination}: {Reason}.",
                        sourceQuickSlot,
                        destinationQuickSlot,
                        adjacentSourceSlot,
                        adjacentDestinationSlot,
                        rejectionReason);
                }
                else
                {
                    logger.LogInformation(
                        "Changed adjacent skill slots {FirstSource}->{FirstDestination}, {SecondSource}->{SecondDestination}; compound={Compound}.",
                        sourceQuickSlot,
                        destinationQuickSlot,
                        adjacentSourceSlot,
                        adjacentDestinationSlot,
                        isCompoundMove);
                }

                return;
            }
        }

        var skillSlotMoveAccepted = skillCatalog.TrySwapSlots(
            session.Character.Active.Job,
            session.Skill.Current,
            sourceQuickSlot,
            destinationQuickSlot,
            out var movedSkills,
            out var simpleRejectionReason);
        if (skillSlotMoveAccepted && !session.Skill.Current.SequenceEqual(movedSkills))
        {
            session.Skill.Current = movedSkills.ToList();
            session.Character.Active = await store.SaveCharacterSkillsAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Skill.CurrentSp,
                session.Skill.Current,
                cancellationToken) ?? session.Character.Active;
        }

        var quickSlotReply = skillSlotMoveAccepted
            ? GameProtocolEngine.CreateChangeSkillSlotReply(
                sourceQuickSlot,
                destinationQuickSlot)
            : GameProtocolEngine.CreateCommandError(request, errorCode: 1);
        await context.Outbound.SendAsync(quickSlotReply, Name, cancellationToken);
        if (skillSlotMoveAccepted)
        {
            logger.LogInformation(
                "Changed skill slot from {SourceSlot} to {DestinationSlot}.",
                sourceQuickSlot,
                destinationQuickSlot);
        }
        else
        {
            logger.LogWarning(
                "Rejected skill-slot change {SourceSlot}->{DestinationSlot}: {Reason}.",
                sourceQuickSlot,
                destinationQuickSlot,
                simpleRejectionReason);
        }

    }
}
