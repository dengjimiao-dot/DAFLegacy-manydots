using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Skill-layout builders (07 §4.7): the job/grow-type initial skill tables
/// and the merged initial skill list (including the debug swordman set).
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal static readonly GameSkillEntry[] DebugSwordmanSkills =
    [
        new(5, 20), new(8, 20), new(11, 20), new(6, 20),
        new(13, 15), new(18, 8), new(25, 10), new(36, 20),
        new(41, 10), new(44, 20), new(46, 8), new(16, 20),
        new(17, 5), new(28, 1), new(29, 1), new(30, 10)
    ];

    internal static readonly IReadOnlyDictionary<int, GameSkillEntry[]> InitialSkillsByJob =
        new Dictionary<int, GameSkillEntry[]>
        {
            // Script/character/*/*.chr -> [initial value] / [skill].
            [0] =
            [
                new(181, 2, 102), new(182, 2, 103), new(184, 1, 104),
                new(173, 1, 105), new(179, 5, 106),
                new(5, 1, 0), new(46, 1, 1)
            ],
            [1] =
            [
                new(181, 3, 102), new(182, 3, 103), new(184, 1, 104),
                new(172, 1, 105), new(179, 5, 106),
                new(5, 1, 0), new(46, 1, 1)
            ],
            [2] =
            [
                new(181, 2, 102), new(184, 1, 103), new(185, 1, 104),
                new(179, 5, 105), new(4, 1, 0), new(47, 1, 1)
            ],
            [3] =
            [
                new(181, 3, 102), new(182, 3, 103), new(184, 2, 104),
                new(187, 1, 105), new(179, 5, 106),
                new(11, 1, 0), new(12, 1, 1)
            ],
            [4] =
            [
                new(181, 1, 102), new(182, 1, 103), new(184, 1, 104),
                new(179, 5, 105), new(173, 1, 106),
                new(1, 1, 0), new(51, 1, 1)
            ]
        };

    internal static IReadOnlyList<GameSkillEntry> GetInitialSkills(CharacterRecord character)
    {
        var skills = InitialSkillsByJob.TryGetValue(character.Job, out var initial)
            ? initial.AsEnumerable()
            : [];

        // DebugConfig.txt defines its larger skill set for the level-60,
        // grow-type-2 swordman only. Merge it with the mandatory base skills
        // and keep the highest level when an id appears in both lists.
        if (character.Job == 0 && character.GrowType == 2 && character.Level >= 50)
        {
            skills = skills.Concat(DebugSwordmanSkills);
        }

        return skills
            .GroupBy(skill => skill.SkillId)
            .Select(group =>
            {
                var highest = group.OrderByDescending(skill => skill.Level).First();
                var stableSlot = group.FirstOrDefault(skill => skill.Slot.HasValue)?.Slot;
                return highest with { Slot = stableSlot ?? highest.Slot };
            })
            .OrderBy(skill => skill.Slot ?? byte.MaxValue)
            .ThenBy(skill => skill.SkillId)
            .ToArray();
    }
}
