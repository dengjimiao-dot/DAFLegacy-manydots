namespace DFLegacy.Server;

public sealed record TransferArchetype(string Name, int Job, int GrowType);

public static class MaximumTransferRoster
{
    public const int CharacterLevel = CharacterExperienceCatalog.MaximumLevel;
    public const int CharacterSkillPoints = 10_000;
    public const int CharacterCash = 1_000_000;
    public const int CharacterGold = 1_000_000;
    public const ushort ColorlessCubeFragmentItemId = 3037;
    public const uint ColorlessCubeFragmentCount = 1_000;
    public const int MaximumVisibleCharacters = 24;

    public static readonly IReadOnlyList<TransferArchetype> Archetypes =
    [
        new("BladeMaster", 0, 1),
        new("SoulBender", 0, 2),
        new("Berserker", 0, 3),
        new("Asura", 0, 4),

        new("NenMaster", 1, 1),
        new("Striker", 1, 2),
        new("Brawler", 1, 3),
        new("Grappler", 1, 4),

        new("Ranger", 2, 1),
        new("Launcher", 2, 2),
        new("Mechanic", 2, 3),
        new("Spitfire", 2, 4),

        new("Elementalist", 3, 1),
        new("Summoner", 3, 2),
        new("BattleMage", 3, 3),
        new("Witch", 3, 4),

        new("Crusader", 4, 1),
        new("Infighter", 4, 2),
        new("Exorcist", 4, 3),
        new("Avenger", 4, 4)
    ];

    public static CharacterRecord[] Create() =>
        Archetypes
            .Select(archetype => new CharacterRecord(
                Id: Guid.NewGuid(),
                Name: archetype.Name,
                Job: archetype.Job,
                GrowType: archetype.GrowType,
                Level: CharacterLevel,
                Sp: CharacterSkillPoints,
                Cash: CharacterCash,
                Gold: CharacterGold,
                Inventory:
                [
                    new CharacterItemRecord(
                        Slot: 9,
                        ItemId: ColorlessCubeFragmentItemId,
                        CountOrValue: ColorlessCubeFragmentCount)
                ],
                Equipment: []))
            .ToArray();
}
