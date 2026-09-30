namespace DAFLegacy.Server;

/// <summary>Skill list and free skill points (07 §4.3).</summary>
public sealed class SkillState
{
    public List<CharacterSkillRecord> Current = [];

    public int CurrentSp;
}
