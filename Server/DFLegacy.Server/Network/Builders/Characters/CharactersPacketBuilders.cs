using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Characters packet builders (07 §4.8.3 三层镜像: Builders/Characters).
/// Moved verbatim from GameProtocolEngine (R7); byte output unchanged.
/// </summary>
internal static class CharactersPacketBuilders
{
    public static GameServerPacket CreateExchangeServerCharacterInfoReply() =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.ExchangeServerCharacterInfoCommand,
            [1, 0]);           // result, character count

    public static GameServerPacket CreateDeleteCharacterReply(byte slot) =>
        new(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.DeleteCharacterCommand,
            [1, slot]);
    public static GameServerPacket CreateSelectCharacterReply(
        PacketFrame request,
        uint characterId = 1,
        ushort serverId = 1,
        ushort usedFatigue = 0,
        ushort maximumFatigue = GameProtocolEngine.DefaultMaximumFatigue,
        ushort premiumFatigue = 0,
        uint cash = 0,
        byte townId = 1,
        uint completedTutorialFlags = GameProtocolEngine.DefaultCompletedTutorialFlags,
        IReadOnlyList<GameQuestEntry>? activeQuests = null,
        IReadOnlyList<ushort>? completedQuestIds = null,
        IReadOnlyList<GamePremiumServiceEntry>? premiumServices = null,
        IReadOnlyDictionary<ushort, int>? questCompletionMapping = null)
    {
        if (request.Type != GameProtocolEngine.CommandPacketType
            || request.ProtocolId != GameProtocolEngine.SelectCharacterCommand
            || request.Body.Length < 3)
        {
            throw new ArgumentException(
                "SELECT_CHARACTER must contain a two-byte sequence and a character slot.",
                nameof(request));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);       // result
        writer.Write(characterId);
        writer.Write(serverId);
        writer.Write(usedFatigue);
        writer.Write(maximumFatigue);
        writer.Write(premiumFatigue);
        premiumServices ??= [];
        if (premiumServices.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(premiumServices));
        }

        writer.Write((byte)premiumServices.Count);
        foreach (var premiumService in premiumServices)
        {
            writer.Write(premiumService.ServiceType);
            writer.Write(premiumService.RemainSeconds);
        }

        writer.Write(cash);

        activeQuests ??= [];
        completedQuestIds ??= [];
        if (activeQuests.Count > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(activeQuests));
        }

        if (completedQuestIds.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(completedQuestIds));
        }

        if (completedQuestIds.Count != 0 && questCompletionMapping is null)
        {
            throw new NotSupportedException(
                "DFLegacy completed quests require the PVF quest completion mapping.");
        }

        // DFLegacy consumes exactly three (u16 quest id, u32 trigger) slots.
        // FFFF marks an unused slot; zero is a real quest id.
        for (var index = 0; index < 3; index++)
        {
            if (index < activeQuests.Count)
            {
                writer.Write(activeQuests[index].QuestId);
                writer.Write(activeQuests[index].Trigger);
            }
            else
            {
                writer.Write(ushort.MaxValue);
                writer.Write(0u);
            }
        }

        // DF2008 0x40FFF2: u8 group, u32 byte length, 32 bytes of bits.
        // 0x4EEB60 maps each bit through questmappingtable.tbl, not quest ID.
        var completedGroups = new SortedDictionary<byte, byte[]>();
        foreach (var questId in completedQuestIds.Distinct())
        {
            if (!questCompletionMapping!.TryGetValue(questId, out var index))
            {
                continue;
            }

            var group = index / 512;
            var bit = index % 512;
            if (index < 0 || group > 8 || bit >= 256)
            {
                throw new ArgumentOutOfRangeException(nameof(questCompletionMapping));
            }

            var groupId = (byte)group;
            if (!completedGroups.TryGetValue(groupId, out var bitmap))
            {
                bitmap = new byte[32];
                completedGroups.Add(groupId, bitmap);
            }
            bitmap[bit / 8] |= (byte)(1 << (bit % 8));
        }
        writer.Write((byte)completedGroups.Count);
        foreach (var (group, bitmap) in completedGroups)
        {
            writer.Write(group);
            writer.Write((uint)bitmap.Length);
            writer.Write(bitmap);
        }
        writer.Write(townId);
        // DFLegacy reads this dword directly into its lower 32 tutorial flags.
        // A newly created character receives zero until the mandatory tutorial
        // reports its final module; migrated and preset characters receive all bits.
        writer.Write(completedTutorialFlags);

        return new GameServerPacket(
            GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.SelectCharacterCommand,
            payload.ToArray());
    }
    public static GameServerPacket CreatePvpRecord(
        int wins,
        int losses,
        int pvpPoints,
        int currentRankPoint,
        int nextRankPoint,
        byte pvpGrade,
        byte pvpGradeExtension = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(Math.Max(0, wins));
        writer.Write(Math.Max(0, losses));
        writer.Write(Math.Max(0, pvpPoints));
        writer.Write(Math.Max(0, currentRankPoint));
        writer.Write(Math.Max(0, nextRankPoint));
        writer.Write(pvpGrade);
        writer.Write(pvpGradeExtension);
        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.PvpRecordNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateUserState(ushort userId, byte state) =>
        new(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.UserStateNotification,
            [(byte)userId, (byte)(userId >> 8), state]);
    public static GameServerPacket CreateEmptyUserInfo()
    {
        return CharactersPacketBuilders.CreateUserInfo([]);
    }
    public static GameServerPacket CreateUserInfo(IReadOnlyList<GameCharacterSummary> characters)
    {
        if (characters.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(characters));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // Mode 2 is the character-selection representation. Unlike mode 0,
        // it constructs the visible character cards directly in this build.
        writer.Write((byte)2);
        writer.Write((ushort)characters.Count);

        foreach (var character in characters)
        {
            if (character.NameBytes.Length is 0 or >= 31)
            {
                throw new ArgumentException("Character names must contain 1-30 encoded bytes.", nameof(characters));
            }

            writer.Write(character.Slot);
            PacketBodyWriter.WriteLengthPrefixedBytes(writer, character.NameBytes);
            writer.Write(character.Job);
            writer.Write(CharactersPacketBuilders.PackGrowthType(character));
            writer.Write(character.Level);
            // The legacy client indexes the rank-name table from this byte
            // (0 = 10级, 1 = 9级, ...).  AwakeningType is already packed in
            // the high growth bits by PackGrowthType and must not occupy this
            // field; doing so makes an awakened character display as 9级.
            writer.Write(Math.Clamp(character.PvpGrade, (byte)0, (byte)30));
            writer.Write((byte)0); // character state
            var appearanceItems = (character.AppearanceItems ?? [])
                .Where(item => item.Slot <= 9 && item.ItemId != ushort.MaxValue)
                .GroupBy(item => item.Slot)
                .Select(group => group.First())
                .OrderBy(item => item.Slot)
                .ToArray();
            if (appearanceItems.Length > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(characters));
            }

            writer.Write((byte)appearanceItems.Length);
            foreach (var item in appearanceItems)
            {
                writer.Write((byte)item.Slot);
                writer.Write(item.ItemId);
                writer.Write(item.State);
            }

            writer.Write((byte)0); // character-list extension state
            writer.Write(0u);      // guild id
            writer.Write((byte)0); // guild/member flag
            writer.Write((byte)0); // character-list state
            writer.Write((byte)0); // character-list option
            writer.Write((byte)1); // visible/selectable character card
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.UserInfoNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCurrentCharacterInfo(
        GameCharacterSummary character,
        ushort userId,
        IReadOnlyCollection<GameInventoryEntry>? equippedItems = null,
        GameCreatureAppearanceEntry? equippedCreature = null,
        bool hasBlackDiamond = false)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (character.NameBytes.Length is 0 or >= 31)
        {
            throw new ArgumentException(
                "Character names must contain 1-30 encoded bytes.",
                nameof(character));
        }

        if (equippedCreature is not null
            && (equippedCreature.ItemId == 0
                || equippedCreature.ItemId == ushort.MaxValue
                || equippedCreature.NameBytes.Length >= 31))
        {
            throw new ArgumentException(
                "Creature appearance requires a valid item id and at most 30 encoded name bytes.",
                nameof(equippedCreature));
        }

        var visibleItems = (equippedItems ?? [])
            // The mode-zero reader indexes a 12-entry compact appearance table
            // directly by slot. Slot 10 is the native [title name] endpoint.
            .Where(item => item.Slot <= 10 && item.ItemId != ushort.MaxValue)
            .GroupBy(item => item.Slot)
            .Select(group => group.First())
            .OrderBy(item => item.Slot)
            .ToArray();
        if (visibleItems.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(equippedItems));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // Mode 0 updates a player entity in the active town. The record's
        // leading id must match the server id returned by SELECT_CHARACTER so
        // the client recognizes it as the locally controlled character.
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write(userId);
        PacketBodyWriter.WriteLengthPrefixedBytes(writer, character.NameBytes);
        writer.Write(character.Job);
        writer.Write(CharactersPacketBuilders.PackGrowthType(character));
        writer.Write(character.Level);
        // Mode 0 uses the same legacy PVP-grade byte immediately after level.
        // Keep awakening in PackGrowthType; otherwise the town actor would
        // display the awakening stage as its PVP rank.
        writer.Write(Math.Clamp(character.PvpGrade, (byte)0, (byte)30));
        writer.Write((byte)0); // character state
        writer.Write((byte)0); // town-entity state
        writer.Write((byte)visibleItems.Length);
        foreach (var item in visibleItems)
        {
            // USERINFO mode 0 carries compact appearance entries. Unlike the
            // ten-byte mode-1 inventory object record, this is safe for the
            // locally controlled town actor.
            writer.Write((byte)item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.State);
        }

        writer.Write((byte)0); // town-entity extension state
        writer.Write(0u);      // guild id
        writer.Write((byte)0); // guild/member flag

        // DFLegacy mode zero keeps the legacy guild/title placeholders even
        // when they are empty. Omitting either length prefix shifts every
        // actor-state field and leaves the town scene black.
        // The old client converts this equipment id to [creature species]
        // through sub_50D8D0, then uses the adjacent name/state while its
        // mode-zero actor update creates the map Creature object.
        writer.Write(equippedCreature?.ItemId ?? (ushort)0);
        PacketBodyWriter.WriteLengthPrefixedBytes(writer, equippedCreature?.NameBytes ?? []);
        // The client stores !wireState on CNRDVirtualCreature+0x1A4 and only
        // registers the object in the scene when that internal value is zero.
        writer.Write(equippedCreature?.State ?? (byte)0);
        writer.Write((byte)0);   // PC-room name effect (record + 0xB0)
        // USERINFO mode zero stores this encrypted flag at record + 0xB8.
        // The name renderer uses value one for both the gold name color and
        // pcroompremiumreward.img index 3 (the black-diamond icon).
        writer.Write(hasBlackDiamond ? (byte)1 : (byte)0);
        // This is the town name-grade/reputation score, not an equipment id.
        // Values >= 100 select the client's generic GradeB/C/D title effects.
        // Equipped [title name] items belong in compact appearance slot 10.
        writer.Write(0u);
        // These following fields are independent guild-info state/name data.
        // Enabling them for a title makes the client construct a guild object.
        writer.Write((byte)0);
        PacketBodyWriter.WriteLengthPrefixedBytes(writer, []); // guild name
        writer.Write((byte)1);   // active entity
        writer.Write(0u);        // actor state A
        writer.Write(0u);        // actor state B
        writer.Write(0u);        // actor state C

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.UserInfoNotification,
            payload.ToArray());
    }
    public static GameServerPacket CreateCurrentCharacterDetails(
        ushort userId,
        uint experience = 0,
        IReadOnlyList<GameSkillEntry>? learnedSkills = null,
        GameCharacterCombatStats? combatStats = null,
        IReadOnlyCollection<GameInventoryEntry>? equippedItems = null,
        byte equippedCreatureLevel = 0,
        byte staminaRecoveryPercentage = 100,
        byte mode = 1)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (staminaRecoveryPercentage > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staminaRecoveryPercentage));
        }

        combatStats ??= new GameCharacterCombatStats(
            MaximumHp: 11_600,
            MaximumMp: 11_900,
            PhysicalAttack: 75,
            PhysicalDefense: 75,
            MagicalAttack: 45,
            MagicalDefense: 45,
            FireResistance: 0,
            WaterResistance: 0,
            DarkResistance: 0,
            LightResistance: 0,
            InventoryLimit: 480_000,
            HpRegeneration: 0,
            MpRegeneration: 500,
            MovementSpeed: 8_500,
            AttackSpeed: 8_500,
            CastSpeed: 7_000,
            HitRecovery: 6_000,
            JumpPower: 4_300,
            Weight: 500_000);

        using var statsPayload = new MemoryStream();
        using (var stats = new BinaryWriter(statsPayload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // DFLegacy reads a length-prefixed 81-byte combat-stat block.
            stats.Write(combatStats.MaximumHp);
            stats.Write(combatStats.MaximumMp);
            stats.Write(combatStats.PhysicalAttack);
            stats.Write(combatStats.PhysicalDefense);
            stats.Write(combatStats.MagicalAttack);
            stats.Write(combatStats.MagicalDefense);
            stats.Write(combatStats.FireResistance);
            stats.Write(combatStats.WaterResistance);
            stats.Write(combatStats.DarkResistance);
            stats.Write(combatStats.LightResistance);
            for (var index = 0; index < 17; index++)
            {
                stats.Write((ushort)0); // active-status resistances
            }

            stats.Write(combatStats.InventoryLimit);
            stats.Write(combatStats.HpRegeneration);
            stats.Write(combatStats.MpRegeneration);
            stats.Write(combatStats.MovementSpeed);
            stats.Write(combatStats.AttackSpeed);
            stats.Write(combatStats.CastSpeed);
            stats.Write(combatStats.HitRecovery);
            stats.Write(combatStats.JumpPower);
            stats.Write(combatStats.Weight);
            // The old client stores its current weakness/stamina recovery
            // percentage in the final byte of this exact 81-byte block.
            stats.Write(staminaRecoveryPercentage);
        }

        var statBytes = statsPayload.ToArray();
        if (statBytes.Length != 81)
        {
            throw new InvalidOperationException("The DFLegacy combat-stat block must contain exactly 81 bytes.");
        }

        learnedSkills ??= [];
        if (learnedSkills.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(learnedSkills));
        }

        var equipment = (equippedItems ?? [])
            .Where(item => item.Slot < 23)
            .OrderBy(item => item.Slot)
            .ToArray();
        if (equipment.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(equippedItems));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // Mode 1 is the plain current-character detail snapshot. Mode 3 shares
        // the entire body (mode switch 0x41A764 → shared reader 0x41A780,
        // which creates the per-user peer connection object at user+0x110 that
        // the dungeon-gate P2P handshake requires) and appends the tail read
        // at 0x41AEE0 whose 0x16/0x17 UI-slot calls start the member
        // connection state — party fan-out therefore uses mode 3 with the
        // tail present; omitting it leaves the party P2P dead and times the
        // client out.
        writer.Write(mode);          // full current-character subtype
        writer.Write((ushort)1);     // record count
        writer.Write(userId);
        writer.Write(experience);
        writer.Write((uint)statBytes.Length);
        writer.Write(statBytes);
        writer.Write((byte)equipment.Length);
        foreach (var item in equipment)
        {
            // USERINFO subtype 1 is the authoritative worn-equipment
            // projection in this client. Its equipment record is exactly:
            // slot:u8, item-id:u16, instance/value:u32, state:u8,
            // durability:u16. The handler also removes any of the 23 worn
            // slots omitted from this snapshot.
            writer.Write(checked((byte)item.Slot));
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
        }

        writer.Write((byte)learnedSkills.Count);
        foreach (var skill in learnedSkills)
        {
            // Subtype one uses compact skill-id/level pairs. The separate
            // SKILLINFO notification carries the third state byte.
            writer.Write(skill.SkillId);
            writer.Write(skill.Level);
        }
        writer.Write(equippedCreatureLevel);

        if (mode == 3)
        {
            // The mode-3 tail (0x41AEE0) always executes and drives the party
            // connection UI: the 0x16 slot call (sub_799EF0) registers a peer
            // snapshot, the 0x17 one the self snapshot — without it the
            // client never starts the member P2P handshake and the party
            // teardown times the client out. The reader consumes u32×3 +
            // u8×4 (a guild-context block, 0x41AEFB..0x41AF61) and u32×3
            // stored to record+0xCC/0xD0/0xD4 (title/name-tag ids,
            // 0x41AFC1..0x41B02D). No guild and no name tag means zeroes —
            // the reader never dereferences them here.
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
        }

        return new GameServerPacket(
            GameProtocolEngine.NotificationPacketType,
            GameProtocolEngine.UserInfoNotification,
            payload.ToArray());
    }
    private static byte PackGrowthType(GameCharacterSummary character)
    {
        // DF2008 stores the first growth branch in bits 0-3 and the awakening
        // stage in bits 4-6 of the same USERINFO byte.
        return (byte)((character.GrowType & 0x0F)
            | ((character.AwakeningType & 0x07) << 4));
    }
}
