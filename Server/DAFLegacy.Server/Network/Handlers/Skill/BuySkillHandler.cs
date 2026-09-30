using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// BUY_SKILL (CMD 31): learns or levels one skill through the prerequisite, cost and Premium-effective level checks (07 §5 group 8).
/// </summary>
public sealed class BuySkillHandler(IGameStore store,
    SkillCatalog skillCatalog,
    PremiumBenefitCatalog premiumBenefitCatalog,
    ChannelSessionSenders senders) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.BUY_SKILL;

    public string Name => "BuySkill";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null || request.Body.Length < 3)
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        // DNF 1.0.1.9 command 31 carries one u8 skill id
        // after the two-byte command sequence.
        var wireSkillIndex = request.Body[2];
        if (!skillCatalog.TryGetDefinition(
                session.Character.Active.Job,
                wireSkillIndex,
                out var definition))
        {
            var error = ConnectionPacketBuilders.CreateCommandError(request, 4);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            return;
        }

        var existingIndex = session.Skill.Current.FindIndex(skill =>
            skill.SkillId == definition.SkillId);
        var currentLevel = existingIndex >= 0
            ? session.Skill.Current[existingIndex].Level
            : 0;
        var targetLevel = currentLevel + 1;
        var maximumLevel = definition.MaximumLevelFor(
            session.Character.Active.GrowType,
            session.Character.Active.AwakeningType);
        var hasPrerequisites = true;
        for (var index = 0; index + 1 < definition.Prerequisites.Length; index += 2)
        {
            var prerequisiteId = definition.Prerequisites[index];
            var prerequisiteLevel = definition.Prerequisites[index + 1];
            if (prerequisiteId is < byte.MinValue or > byte.MaxValue
                || !session.Skill.Current.Any(skill =>
                    skill.SkillId == (byte)prerequisiteId
                    && skill.Level >= prerequisiteLevel))
            {
                hasPrerequisites = false;
                break;
            }
        }

        var cost = definition.CostFor(currentLevel, targetLevel);
        var requiredCharacterLevel =
            definition.RequiredCharacterLevelFor(targetLevel);
        var effectiveCharacterLevel =
            premiumBenefitCatalog.GetEffectiveSkillLevel(
                session.Character.Active.Level,
                senders.GetActivePremiumServiceTypes(context));
        if (maximumLevel <= 0
            || targetLevel > maximumLevel
            || requiredCharacterLevel > effectiveCharacterLevel
            || !hasPrerequisites
            || cost > session.Skill.CurrentSp)
        {
            var errorCode = cost > session.Skill.CurrentSp ? (byte)2 : (byte)18;
            var error = ConnectionPacketBuilders.CreateCommandError(request, errorCode);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Info(
                "Rejected skill {SkillId} level {TargetLevel}: character level {CharacterLevel}, Premium-effective level {EffectiveLevel}, required level {RequiredLevel}, SP {CurrentSp}/{Cost}.",
                definition.SkillId,
                targetLevel,
                session.Character.Active.Level,
                effectiveCharacterLevel,
                requiredCharacterLevel,
                session.Skill.CurrentSp,
                cost);
            return;
        }

        byte learnedSkillSlot;
        if (existingIndex >= 0)
        {
            session.Skill.Current[existingIndex] = session.Skill.Current[existingIndex] with
            {
                Level = checked((byte)targetLevel)
            };
            learnedSkillSlot = session.Skill.Current[existingIndex].Slot;
        }
        else
        {
            var occupied = session.Skill.Current
                .Select(skill => skill.Slot)
                .ToHashSet();
            var slot = skillCatalog.AllocateSlot(definition, occupied);
            if (!slot.HasValue)
            {
                var error = ConnectionPacketBuilders.CreateCommandError(request, 1);
                await context.Queue.SendAsync(error, Name, cancellationToken);
                return;
            }

            session.Skill.Current.Add(new CharacterSkillRecord(
                slot.Value,
                definition.SkillId,
                checked((byte)targetLevel)));
            learnedSkillSlot = slot.Value;
        }

        session.Skill.CurrentSp -= cost;
        session.Skill.Current = session.Skill.Current.OrderBy(skill => skill.Slot).ToList();
        session.Character.Active = await store.SaveCharacterSkillsAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Skill.CurrentSp,
            session.Skill.Current,
            cancellationToken) ?? session.Character.Active;

        var buySkillReply = SkillPacketBuilders.CreateBuySkillReply(
            checked((ushort)Math.Min(session.Skill.CurrentSp, ushort.MaxValue)),
            learnedSkillSlot,
            definition.SkillId,
            checked((byte)targetLevel));
        await context.Queue.SendAsync(buySkillReply, Name, cancellationToken);
        RuntimeLogger.Info(
            "Learned skill {SkillId} level {Level}; cost {Cost} SP, {RemainingSp} SP remains.",
            definition.SkillId,
            targetLevel,
            cost,
            session.Skill.CurrentSp);
    }
}
