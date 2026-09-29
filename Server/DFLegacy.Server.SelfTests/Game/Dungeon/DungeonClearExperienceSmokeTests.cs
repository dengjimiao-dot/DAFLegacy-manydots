using System.Text.Json;
using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DungeonClearExperienceSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dflegacy-clear-exp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "etc"));
        try
        {
            File.WriteAllText(Path.Combine(root, "etc/serverparameter.etc"), """
                [party user number exp bonusrate] 1 1.5 2 2.5
                [clear rank exp bonusrate] 0.05 0.1 0.15 0.2 0.3
                """);
            File.WriteAllText(Path.Combine(root, "etc/channel_info.etc"), """
                [dungeon]
                `[test_area]`
                <4::localized_name_999>
                1
                2
                [/dungeon]
                [server]
                1 // server id
                15 <4::channel_name_999> 0 `[test_area]` 10 0 0 1 0 0 0 0 0 0 0
                16 `Other channel` 0 `[test_area]` 30 0 0 0 0 0 0 0 0 0 0
                [/server]
                [server]
                2
                15 <4::channel_name_999> 0 `[test_area]` 50 0 0 0 0 0 0 0 0 0 0
                [/server]
                """);
            var starts = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var options = new ServerOptions
            {
                ScriptPvfPath = string.Empty,
                SkillScriptPath = root,
                Channel = new ChannelOptions { ServerNumber = 1, ChannelNumber = 15 },
                Experience = new ExperienceOptions
                {
                    BlackDiamondClearBonusRate = 0.05,
                    ClearEventBonusRate = 0.5,
                    ClearEventStartsAt = starts,
                    ClearEventEndsAt = starts.AddDays(1)
                }
            };
            var scripts = new ScriptFileSystem(options, NullLogger<ScriptFileSystem>.Instance);
            var catalog = new DungeonExperienceCatalog(options, scripts, NullLogger<DungeonExperienceCatalog>.Instance);
            var dungeon = new DungeonDefinition(1, 1, 1, 1, 1, 0, 0, 0, 0, "B");
            check(catalog.GetChannelBonusRate(1) == 0.1 && catalog.GetChannelBonusRate(2) == 0.1
                && catalog.GetChannelBonusRate(3) == 0 && catalog.GetChannelBonusRate(999) == 0,
                "channel EXP matches server, channel and dungeon group without parsing localized-name digits");
            check(catalog.GetEventBonusRate(starts.AddTicks(-1)) == 0
                && catalog.GetEventBonusRate(starts) == 0.5
                && catalog.GetEventBonusRate(starts.AddDays(1)) == 0,
                "clear EXP event uses an inclusive start and exclusive end");
            var bonuses = new DungeonClearBonusContext(true, true, true, 0.1,
                catalog.GetEventBonusRate(starts), catalog.GetChannelBonusRate(1));
            var result = catalog.CalculateClearExperience(1, dungeon, 0, 70, 20, bonuses: bonuses);
            check(result.BaseExperience == 300 && result.RankBonus == 30 && result.PartyBonus == 0
                && result.AvatarBonus == 6 && result.CreatureBonus == 6 && result.BlackDiamondBonus == 15
                && result.MentorBonus == 30 && result.EventBonus == 150 && result.ChannelBonus == 30
                && result.TotalExperience == 567, "all solo clear EXP bonuses are additive, not compounded");
            var plain = catalog.CalculateClearExperience(1, dungeon, 0, 70, 20);
            check(plain.TotalExperience == 330 && plain.BlackDiamondBonus == 0 && plain.AvatarBonus == 0
                && plain.CreatureBonus == 0 && plain.EventBonus == 0 && plain.MentorBonus == 0,
                "no equipment, premium, event or mentor eligibility means no corresponding clear EXP bonus");
            var party = catalog.CalculateClearExperience(1, dungeon, 0, 70, 20, 2,
                new DungeonClearBonusContext(HasAvatar: true, HasCreature: true));
            check(party.BaseExperience == 150 && party.PartyBonus == 75 && party.RankBonus == 22
                && party.AvatarBonus == 11 && party.CreatureBonus == 11 && party.TotalExperience == 269,
                "party EXP remains separately displayed and old-client avatar/creature bonuses use five percent");
            check(catalog.CalculateClearExperience(60, dungeon, 0, 105, 20, bonuses: bonuses).TotalExperience == 0
                && catalog.CalculateClearExperience(1, dungeon, 0, 105, 0, bonuses: bonuses).TotalExperience == 0,
                "max level and no-kill clears cannot acquire bonus-only EXP");
            var saturated = catalog.CalculateClearExperience(1, dungeon, 0, 70, 20,
                bonuses: bonuses with { MentorBonusRate = 1e20, ChannelBonusRate = 1e20 });
            check(saturated.TotalExperience == uint.MaxValue
                && (ulong)saturated.BaseExperience + saturated.RankBonus + saturated.PartyBonus
                + saturated.AvatarBonus + saturated.CreatureBonus + saturated.MentorBonus
                + saturated.EventBonus + saturated.BlackDiamondBonus + saturated.ChannelBonus == uint.MaxValue,
                "EXP saturation caps individual wire components, not only the persisted total");
            var mentor = Guid.NewGuid();
            var character = new CharacterRecord(Guid.NewGuid(), "pupil", 0, 0, 1, 0, 0,
                MentorCharacterId: mentor, MentorExperienceBonusPercent: 10);
            check(DungeonClearBonusContext.GetMentorBonusRate(character, id => id == mentor) == 0.1
                && DungeonClearBonusContext.GetMentorBonusRate(character, _ => false) == 0
                && DungeonClearBonusContext.GetMentorBonusRate(character with { MentorCharacterId = character.Id }, _ => true) == 0,
                "mentor EXP requires a distinct server-owned mentor relationship and online mentor");
            var restored = JsonSerializer.Deserialize<CharacterRecord>(JsonSerializer.Serialize(character));
            check(restored?.MentorCharacterId == mentor && restored.MentorExperienceBonusPercent == 10,
                "mentor EXP association survives state JSON round-trip");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
