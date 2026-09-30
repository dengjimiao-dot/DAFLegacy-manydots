using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Skill-panel senders (07 §4.7): skill info snapshot and the level-up SP
/// grant shared by the quest and dungeon flows.
/// </summary>
public sealed partial class ChannelSessionSenders
{
    public async Task SendCurrentSkillInfoAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        var skillInfo = SkillPacketBuilders.CreateSkillInfo(
            checked((ushort)Math.Min(context.State.Skill.CurrentSp, ushort.MaxValue)),
            GetCurrentGameSkills(context));
        await context.Queue.SendAsync(skillInfo, "SKILL_INFO", cancellationToken);
    }

    public int ApplyLevelUpSkillPoints(ConnectionContext context, CharacterExperienceGrant progression)
    {
        var granted = spCatalog.CalculateLevelUpReward(
            progression.PreviousLevel,
            progression.Level);
        if (granted == 0)
        {
            return 0;
        }

        context.State.Skill.CurrentSp = checked(context.State.Skill.CurrentSp + granted);
        return granted;
    }
}
