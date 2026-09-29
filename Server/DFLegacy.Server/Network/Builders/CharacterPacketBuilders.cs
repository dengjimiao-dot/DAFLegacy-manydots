using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Character-roster packet builders for the login/character flow (07 §4.7,
/// organized along the S4A21 InitPacketBuilderRegistry idea): the USERINFO
/// roster the select screen consumes, and starter inventories for newly
/// created characters. Pure orchestration over the store and item catalogs;
/// holds no per-connection state.
/// </summary>
public sealed class CharacterPacketBuilders(
    JsonGameStore store,
    ItemCatalog itemCatalog,
    PvpExperienceCatalog pvpExperienceCatalog,
    CeraShopCatalog ceraShopCatalog)
{
    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    private const ushort EquippedTitleSlot = 10;

    private static readonly (ushort ItemId, uint Count)[] TestStarterInventoryItems =
    [
        (8, 100), (27093, 1), (14873, 1), (12874, 1),
        (10874, 1), (16873, 1), (18874, 1), (22021, 1),
        (20093, 1), (24032, 1), (27546, 1), (28223, 1),
        (3037, 1000)
    ];
    private static readonly (ushort ItemId, uint Count)[] NormalStarterInventoryItems =
    [
        (26043, 1),
        (8, 100)
    ];

    public async Task<GameServerPacket> CreateUserInfoAsync(uint accountUid, CancellationToken cancellationToken)
    {
        var characters = await store.GetCharactersAsync(accountUid, cancellationToken);
        var summaries = characters
            .Take(MaximumTransferRoster.MaximumVisibleCharacters)
            .Select(character => new GameCharacterSummary(
                character.Slot,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                (character.Equipment ?? [])
                    .Where(item => item.ItemId != 0
                        && (item.Slot == 9
                            || itemCatalog.TryGetDefinition(
                                item.ItemId,
                                out var definition)
                            && CharacterAvatarInventoryLayout.IsCompatibleWornSlot(
                                definition,
                                item.Slot)))
                    .Select(ToGameInventoryEntry)
                    .ToArray(),
                character.CharacterNo,
                (byte)Math.Clamp(character.AwakeningType, byte.MinValue, byte.MaxValue),
                pvpExperienceCatalog.GetGradeForPoints(character.PvpPoints)))
            .ToArray();
        return GameProtocolEngine.CreateUserInfo(summaries);
    }

    public List<CharacterItemRecord> CreateStarterInventory(uint accountUid) =>
        GetStarterInventoryItems(accountUid)
            .Select((item, index) => CharacterItemIdentity.Ensure(
                new CharacterItemRecord(
                    Slot: checked((ushort)(index + 9)),
                    ItemId: item.ItemId,
                    CountOrValue: item.Count,
                    Durability: itemCatalog.GetInitialDurability(item.ItemId)),
                itemCatalog))
            .ToList();

    internal static IReadOnlyList<(ushort ItemId, uint Count)>
        GetStarterInventoryItems(uint accountUid) =>
        AccountUidRules.IsTest(accountUid)
            ? TestStarterInventoryItems
            : AccountUidRules.IsNormal(accountUid)
                ? NormalStarterInventoryItems
                : [];

    internal static byte[] EncodeCharacterName(string name)
    {
        var bytes = ClientEncoding.GetBytes(name);
        if (bytes.Length is > 0 and < 31)
        {
            return bytes;
        }

        return Encoding.ASCII.GetBytes(name.Length == 0 ? "DFLegacy" : name[..Math.Min(name.Length, 20)]);
    }

    internal GameInventoryEntry ToGameInventoryEntry(CharacterItemRecord item)
    {
        var wire = CharacterItemWireProjection.Project(item, itemCatalog);
        return new GameInventoryEntry(
            item.Slot,
            item.ItemId,
            wire.AddInfo,
            wire.ItemAttr,
            wire.Durability,
            wire.SealState);
    }

    // ---- Item/roster/mail builder family migrated verbatim from
    // EntranceService (R1): the SELECT initialization burst and the
    // inventory refresh packets share these builders across groups.

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
    // Assembly of the dispatch table (07 §4.6): handler construction is
    // manual because every dependency is already a constructor parameter
    // here; a duplicate registration throws before any listener starts.

    internal static CreateCharacterMailRequest[] CreateOverflowMailRequests(
        IEnumerable<CharacterMailAttachmentRecord> items,
        string source) =>
        items.Select(item => new CreateCharacterMailRequest(
                "DFLegacy",
                $"{source}: delivered because the inventory was full or overweight.",
                Attachment: item))
            .ToArray();

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

    internal IEnumerable<GameServerPacket> CreateTownInitializationPackets(
        int skillPoints,
        IReadOnlyList<GameSkillEntry> panelSkills,
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> avatarInventory,
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity,
        IEnumerable<CharacterItemRecord> creatureInventory,
        IReadOnlyList<ushort> acceptableQuestIds,
        IReadOnlyList<GameDungeonPermissionEntry> dungeonPermissions)
    {
        // Dungeon permissions are u16-counted (u16 id, u8 state) records, and
        // the acceptable-quest list is u8-counted. The stamina notification is
        // sent later, after AREA_USERS has created the local town actor.
        yield return GameProtocolEngine.CreateDungeonPermissions(dungeonPermissions);

        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
        yield return CreateAvatarInventoryPacket(avatarInventory);
        yield return CreateWarehousePacket(warehouse, warehouseCapacity);
        foreach (var creaturePacket in CreateCreatureInventoryPackets(creatureInventory))
        {
            yield return creaturePacket;
        }
        yield return GameProtocolEngine.CreateSkillInfo(
            (ushort)Math.Clamp(skillPoints, ushort.MinValue, ushort.MaxValue),
            panelSkills);
        yield return GameProtocolEngine.CreateAcceptableQuestList(acceptableQuestIds);
    }

    internal bool TryPlanLegacyPremiumContractRedemption(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out IReadOnlyList<PremiumServicePurchaseGrant> grants)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var daysByService = new Dictionary<byte, long>();
        foreach (var item in inventory.Values)
        {
            if (!ceraShopCatalog.TryGetPremiumContract(item.ItemId, out var contract)
                || item.CountOrValue == 0)
            {
                continue;
            }

            var itemDays = (long)contract.Days * item.CountOrValue;
            daysByService[contract.ServiceType] = Math.Min(
                int.MaxValue,
                daysByService.GetValueOrDefault(contract.ServiceType) + itemDays);
            plannedInventory.Remove(item.Slot);
        }

        grants = daysByService
            .Select(pair => new PremiumServicePurchaseGrant(
                pair.Key,
                checked((int)pair.Value)))
            .ToArray();
        return grants.Count > 0;
    }

    internal GameServerPacket CreateMainInventoryPacket(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        ushort? preferredLastSlot = null)
    {
        var entries = inventory
            .Where(item => item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _)
                && (item.Slot >= CharacterInventoryLayout.QuickSlotStart
                    || (item.Slot == CharacterInventoryLayout.RevivalCoinSlot
                        && item.ItemId
                            == CharacterInventoryLayout.RevivalCoinItemId)))
            .OrderBy(item => preferredLastSlot.HasValue
                && item.Slot == preferredLastSlot.Value
                    ? 1
                    : 0)
            .ThenBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateMainItemList(
            checked((uint)Math.Max(0, gold)),
            victoryPoints,
            entries);
    }

    internal GameServerPacket CreateWarehousePacket(
        IEnumerable<CharacterItemRecord> warehouse,
        ushort warehouseCapacity)
    {
        var entries = warehouse
            .Where(item => item.Slot < warehouseCapacity
                && item.ItemId != 0
                && !CharacterWarehouseProgression.TryGetItemTargetCapacity(
                    item.ItemId,
                    out _))
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(
            2,
            entries,
            warehouseCapacity);
    }

    internal GameServerPacket CreateAvatarInventoryPacket(
        IEnumerable<CharacterItemRecord> avatarInventory)
    {
        var entries = avatarInventory
            .Where(item => CharacterAvatarInventoryLayout.IsBagSlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(1, entries);
    }

    internal IEnumerable<GameServerPacket> CreateCreatureInventoryPackets(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var items = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .ToArray();
        var creatures = items
            .Where(item => itemCatalog.TryGetDefinition(item.ItemId, out var definition)
                && CharacterCreatureInventoryLayout.IsCreature(definition))
            .GroupBy(item => item.CountOrValue)
            .Select(group => group.First())
            .Where(item => item.CountOrValue != 0)
            .Select(item => new GameCreatureInfoEntry(
                item.CountOrValue,
                item.CreatureStomach ?? 100,
                item.CreatureExperience ?? 0,
                item.CreatureLevel ?? 1,
                EncodeCreatureName(item.CreatureName, item.ItemId),
                item.CreatureNoCharge ?? false))
            .ToArray();
        yield return GameProtocolEngine.CreateCreatureInfo(creatures);
        yield return CreateCreatureInventoryItemListPacket(items);
    }

    internal GameServerPacket CreateCreatureInventoryItemListPacket(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var entries = creatureInventory
            .Where(item => CharacterCreatureInventoryLayout.IsInventorySlot(item.Slot)
                && item.ItemId != 0)
            .OrderBy(item => item.Slot)
            .Select(ToGameInventoryEntry)
            .ToArray();
        return GameProtocolEngine.CreateItemList(7, entries);
    }

    internal byte[] EncodeCreatureName(string? name, ushort itemId)
    {
        if (string.IsNullOrWhiteSpace(name)
            && itemCatalog.TryGetDefinition(itemId, out var definition))
        {
            name = definition.Name;
        }

        name = string.IsNullOrWhiteSpace(name) ? "Creature" : name;
        var output = new byte[29];
        ClientEncoding.GetEncoder().Convert(
            name.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return output.AsSpan(0, bytesUsed).ToArray();
    }

    internal static byte GetEquippedCreatureLevel(
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory) =>
        creatureInventory.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var creature)
            ? creature.CreatureLevel ?? 1
            : (byte)0;

    internal GameInventoryEntry[] CreateWornEquipmentSnapshot(
        IEnumerable<CharacterItemRecord> equipment,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory)
    {
        var snapshot = equipment
            .Where(item => item.Slot is < CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot
                or > CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
            .Select(ToGameInventoryEntry)
            .ToList();
        var creatureSlots = new (ushort StorageSlot, ushort ClientSlot)[]
        {
            (CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                CharacterCreatureInventoryLayout.ClientEquippedCreatureSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactRedSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactRedSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactBlueSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactBlueSlot),
            (CharacterCreatureInventoryLayout.EquippedArtifactGreenSlot,
                CharacterCreatureInventoryLayout.ClientEquippedArtifactGreenSlot)
        };
        foreach (var (storageSlot, clientSlot) in creatureSlots)
        {
            if (creatureInventory.TryGetValue(storageSlot, out var item))
            {
                snapshot.Add(ToGameInventoryEntry(item with { Slot = clientSlot }));
            }
        }

        return snapshot.OrderBy(item => item.Slot).ToArray();
    }

    internal IEnumerable<GameServerPacket> CreateInventoryRefreshPackets(
        int gold,
        uint victoryPoints,
        IEnumerable<CharacterItemRecord> inventory,
        IEnumerable<CharacterItemRecord> equipment)
    {
        yield return CreateMainInventoryPacket(gold, victoryPoints, inventory);
    }


    internal GameMailEntry ToGameMailEntry(CharacterMailRecord mail)
    {
        GameMailAttachment? attachment = null;
        if (mail.Attachment is not null)
        {
            var wire = CharacterItemWireProjection.Project(
                mail.Attachment,
                itemCatalog);
            attachment = new GameMailAttachment(
                mail.Attachment.ItemId,
                wire.AddInfo,
                wire.ItemAttr,
                wire.Durability,
                wire.SealState);
        }

        return new GameMailEntry(
            mail.Id,
            EncodeMailField(mail.Sender, 29, "DFLegacy"),
            EncodeMailField(mail.Text, 511, string.Empty),
            checked((uint)Math.Max(0, mail.Gold)),
            attachment,
            mail.SentAt,
            mail.ExpiresAt,
            mail.State is 2 or 3 ? mail.State : (ushort)1);
    }

    internal static CharacterItemRecord NormalizeAvatarInstance(
        CharacterItemRecord item) =>
        item with
        {
            CountOrValue = 1,
            Durability = 0,
            AvatarRemainingSeconds = item.AvatarRemainingSeconds ?? 0,
            AvatarAbilityIndex = item.AvatarAbilityIndex ?? 0
        };

    internal static byte[] EncodeMailField(
        string value,
        int maximumBytes,
        string fallback)
    {
        value ??= fallback;
        var output = new byte[maximumBytes];
        ClientEncoding.GetEncoder().Convert(
            value.AsSpan(),
            output.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        if (bytesUsed == 0 && fallback.Length != 0)
        {
            return ClientEncoding.GetBytes(fallback);
        }

        return output.AsSpan(0, bytesUsed).ToArray();
    }

    // ---- Town presence builders (migrated verbatim from EntranceService).

    internal GameServerPacket CreateTownAppearancePacket(
        CharacterRecord character,
        ushort userId,
        IEnumerable<CharacterItemRecord> equipment,
        IEnumerable<CharacterItemRecord> creatureInventory,
        bool hasBlackDiamond)
    {
        var equipmentSnapshot = equipment.ToArray();

        return GameProtocolEngine.CreateCurrentCharacterInfo(
            new GameCharacterSummary(
                0,
                EncodeCharacterName(character.Name),
                (byte)Math.Clamp(character.Job, byte.MinValue, byte.MaxValue),
                (byte)Math.Clamp(character.GrowType, 0, 15),
                (byte)Math.Clamp(character.Level, 1, byte.MaxValue),
                AwakeningType: (byte)Math.Clamp(
                    character.AwakeningType,
                    byte.MinValue,
                    byte.MaxValue),
                PvpGrade: pvpExperienceCatalog.GetGradeForPoints(
                    character.PvpPoints)),
            userId,
            equipmentSnapshot
                .Where(item => item.Slot <= EquippedTitleSlot)
                .Select(ToGameInventoryEntry)
                .ToArray(),
            CreateEquippedCreatureAppearance(creatureInventory),
            hasBlackDiamond);
    }

    internal GameCreatureAppearanceEntry? CreateEquippedCreatureAppearance(
        IEnumerable<CharacterItemRecord> creatureInventory)
    {
        var creature = creatureInventory.FirstOrDefault(item =>
            item.Slot == CharacterCreatureInventoryLayout.EquippedCreatureSlot
            && item.ItemId != 0);
        return creature is null
            ? null
            : new GameCreatureAppearanceEntry(
                creature.ItemId,
                EncodeCreatureName(creature.CreatureName, creature.ItemId),
                State: 1);
    }

    // ---- Mail attachment planning (migrated verbatim).

    internal static bool TryPlanMailAttachment(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> avatarInventory,
        IReadOnlyDictionary<ushort, CharacterItemRecord> creatureInventory,
        CharacterMailAttachmentRecord? attachment,
        ItemCatalog catalog,
        uint weightLimit,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedAvatarInventory,
        out Dictionary<ushort, CharacterItemRecord> plannedCreatureInventory,
        out byte? destinationListType,
        out ushort? destinationSlot)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        plannedAvatarInventory = avatarInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        plannedCreatureInventory = creatureInventory.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        destinationListType = null;
        destinationSlot = null;
        if (attachment is null)
        {
            return true;
        }

        if (CharacterWarehouseProgression.TryGetItemTargetCapacity(
                attachment.ItemId,
                out _))
        {
            destinationListType = 0;
            return true;
        }

        if (!catalog.TryGetDefinition(attachment.ItemId, out var definition))
        {
            return false;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Avatar)
        {
            if (!CharacterAvatarInventoryPlanner.TryPlace(
                    avatarInventory,
                    attachment,
                    catalog,
                    out var avatarPlacement,
                    out _))
            {
                return false;
            }

            plannedAvatarInventory = avatarPlacement.Inventory;
            destinationListType = 1;
            destinationSlot = avatarPlacement.DestinationSlot;
            return true;
        }

        if (definition.InventoryCategory == ItemInventoryCategory.Creature)
        {
            var creatureItem = new CharacterItemRecord(
                ushort.MaxValue,
                attachment.ItemId,
                attachment.CountOrValue,
                attachment.State,
                attachment.Durability,
                attachment.SealState,
                InstanceId: attachment.InstanceId);
            if (!CharacterCreatureInventoryPlanner.TryPlace(
                    creatureInventory,
                    creatureItem,
                    catalog,
                    preferredEmptySlot: null,
                    out var creaturePlacement,
                    out _))
            {
                return false;
            }

            plannedCreatureInventory = creaturePlacement.Inventory;
            destinationListType = 7;
            destinationSlot = creaturePlacement.DestinationSlot;
            return true;
        }

        if (!CharacterInventoryPlanner.TryPlace(
                inventory,
                attachment,
                catalog,
                weightLimit,
                out var placement,
                out _))
        {
            return false;
        }

        plannedInventory = placement.Inventory;
        destinationListType = 0;
        destinationSlot = placement.DestinationSlot;
        return true;
    }
}
