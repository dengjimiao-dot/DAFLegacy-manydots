namespace DFLegacy.Server;

public sealed record DungeonClearBonusContext(
    bool HasAvatar = false,
    bool HasCreature = false,
    bool HasBlackDiamond = false,
    double MentorBonusRate = 0,
    double EventBonusRate = 0,
    double ChannelBonusRate = 0)
{
    public static double GetMentorBonusRate(CharacterRecord character, Func<Guid, bool> isOnline) =>
        character.MentorCharacterId is { } mentorId
        && mentorId != Guid.Empty && mentorId != character.Id
        && character.MentorExperienceBonusPercent > 0 && isOnline(mentorId)
            ? character.MentorExperienceBonusPercent / 100.0 : 0;
}
