using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// USE_SKILL (CMD 41): dungeon skill use acknowledgement (07 §5 group 13).
/// </summary>
public sealed class UseSkillHandler(
    RuntimeState runtime,
    ServerOptions options, SkillCatalog skillCatalog
    ) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.USE_SKILL;

    public string Name => "USE_SKILL";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (!options.EnablePacketTracing)
        {
            runtime.TracePacketAlways(
                context.Runtime,
                "RX-USE-SKILL",
                request,
                options.HexDumpLimit);
        }

        var skillId = request.Body.Length >= 3
            ? request.Body[2]
            : byte.MaxValue;
        if (session.Character.Active is not null
            && session.Dungeon.CurrentDungeonId != 0
            && skillCatalog.TryGetDefinition(
                session.Character.Active.Job,
                skillId,
                out var usedSkill)
            && usedSkill.ConsumedItemId != 0
            && usedSkill.ConsumedItemCount > 0)
        {
            // Command 18 is emitted before USE_SKILL and
            // owns both the authoritative material
            // deduction and the client's pending item
            // transaction. Do not consume the stack twice.
            RuntimeLogger.Info(
                "USE_SKILL job={Job}, skill={SkillId}, configuredItem={ItemId} x{ConfiguredCount}; material handled by command 18.",
                session.Character.Active.Job,
                skillId,
                usedSkill.ConsumedItemId,
                usedSkill.ConsumedItemCount);
        }
        else
        {
            RuntimeLogger.Info(
                "USE_SKILL job={Job}, skill={SkillId}, dungeon={DungeonId}; no configured item transaction.",
                session.Character.Active?.Job,
                skillId,
                session.Dungeon.CurrentDungeonId);
        }

        var useSkillReply =
            DungeonPacketBuilders.CreateUseSkillReply();
        await context.Queue.SendAsync(useSkillReply, Name, cancellationToken);

    }
}
