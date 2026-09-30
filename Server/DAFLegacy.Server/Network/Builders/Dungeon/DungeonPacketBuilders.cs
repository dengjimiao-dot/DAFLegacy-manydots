using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Dungeon packet builders (07 §4.8.3 三层镜像: Builders/Dungeon).
/// Moved verbatim from the R7 engine split; byte output unchanged.
/// </summary>
internal static class DungeonPacketBuilders
{
    public static GameServerPacket CreateUseSkillReply() =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.USE_SKILL,
            [1]);
    public static GameServerPacket CreateDungeonItemActionReply(
        ushort slot,
        byte listType) =>
        InventoryPacketBuilders.CreateUseStackableItemReply(slot, listType);
    public static GameServerPacket CreateFailClearDungeon(byte stamina) =>
        new(GameProtocolConstants.NotificationPacketType, (byte)NotiPacketType.FAIL_CLEAR_DUNGEON, [stamina]);
    public static GameServerPacket CreateDieState(ushort userId, bool isAlive) =>
        new(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.DIE_STATE,
            [(byte)userId, (byte)(userId >> 8), isAlive ? (byte)1 : (byte)0]);
    public static GameServerPacket CreateDungeonPermissions(
        IReadOnlyList<GameDungeonPermissionEntry> permissions)
    {
        if (permissions.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((ushort)permissions.Count);
        foreach (var permission in permissions)
        {
            writer.Write(permission.DungeonId);
            writer.Write(Math.Min((byte)3, permission.ClearState));
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.DUNGEON_PERMISSION,
            payload.ToArray());
    }
    /// <summary>Single-block PARTY_INFO type 3: clears this party id.</summary>
    public static GameServerPacket CreatePartyClear(int partyId) =>
        PartyPacketBuilders.CreatePartyInfo(
        [
            new PartyInfoWireBlock(
                partyId,
                BlockType: 3,
                TitleKind: 0,
                UserMax: 0,
                Members: [],
                LeaderSlot: 0),
        ]);
    public static GameServerPacket CreateEnterSelectDungeon(
        bool hellQuestsCompleted,
        IReadOnlyList<ushort> missingHellItemPartyIndices)
    {
        // DF2008 0x41F1CF reads quest eligibility (0 triggers string 5431).
        // 0x41F27A reads the count; 0x41F2BE reads u16 PARTY SLOTS for string
        // 5430, not user IDs. Do not use the newer server's NOTI 27 layout.
        if (missingHellItemPartyIndices.Count > 4
            || missingHellItemPartyIndices.Any(index => index >= 4))
        {
            throw new ArgumentOutOfRangeException(nameof(missingHellItemPartyIndices));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(hellQuestsCompleted);
        writer.Write((byte)missingHellItemPartyIndices.Count);
        foreach (var index in missingHellItemPartyIndices)
        {
            writer.Write(index);
        }

        return new(GameProtocolConstants.NotificationPacketType, (byte)NotiPacketType.START_GAME, payload.ToArray());
    }
    public static GameServerPacket CreateTutorialDungeonInfo(byte difficulty) =>
        DungeonPacketBuilders.CreateDungeonInfo(
            GameProtocolConstants.TutorialDungeonId,
            difficulty,
            mazeIndex: 0,
            bossX: byte.MaxValue,
            bossY: byte.MaxValue);
    public static GameServerPacket CreateDungeonInfo(
        ushort dungeonId,
        byte difficulty,
        byte mazeIndex,
        byte bossX,
        byte bossY,
        byte flagA = 0,
        byte flagB = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(dungeonId);
        writer.Write(difficulty);
        writer.Write(mazeIndex);
        writer.Write(bossX);
        writer.Write(bossY);
        writer.Write(flagA);
        writer.Write(flagB);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.DUNGEON_INFO,
            payload.ToArray());
    }
    public static GameServerPacket CreateStartMap(
        byte roomX,
        byte roomY,
        ushort mapId,
        IReadOnlyList<GameDungeonMonster> monsters,
        IReadOnlyList<GamePassiveObjectDrop>? passiveObjectDrops = null,
        uint randomSeed = 0,
        byte roomStateFlag = 1,
        byte hellPartyMode = 0)
    {
        passiveObjectDrops ??= [];
        if (monsters.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(monsters));
        }

        if (passiveObjectDrops.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(passiveObjectDrops));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(roomX);
        writer.Write(roomY);
        writer.Write(randomSeed);
        writer.Write(roomStateFlag is 1 or 2 ? roomStateFlag : (byte)1);
        writer.Write(mapId);
        writer.Write((byte)monsters.Count);
        foreach (var monster in monsters)
        {
            // DNF 1.0.1.9 START_MAP compresses a runtime monster to nine
            // bytes. The client indexes the selected .map file's [monster]
            // rows with the first byte, then uses the following fields as its
            // actor id, monster.lst index, level, type and box metadata.
            writer.Write(monster.MapListIndex);
            writer.Write(monster.UniqueId);
            writer.Write(monster.MonsterIndex);
            writer.Write(monster.Level);
            writer.Write(monster.Type);
            writer.Write(monster.IsBoxMonster);
            writer.Write(monster.BoxIndex);
        }
        writer.Write((byte)passiveObjectDrops.Count);
        foreach (var drop in passiveObjectDrops)
        {
            // Unlike a notification-38 death drop, a START_MAP passive drop
            // begins with the map parser's item-association slot and has no
            // item-state field.
            writer.Write(drop.AssociationSlot);
            writer.Write(drop.GroundId);
            writer.Write(drop.ItemId);
            writer.Write(drop.AddInfo);
            writer.Write(drop.OwnerUserId);
        }
        writer.Write(hellPartyMode);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.START_MAP,
            payload.ToArray());
    }
    public static GameServerPacket CreateCachedStartMap(
        byte roomX,
        byte roomY,
        uint randomSeed)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(roomX);
        writer.Write(roomY);
        writer.Write(randomSeed);
        // DNF 1.0.1.9 skips the map, monster and item sections when this
        // state byte is zero and reactivates its coordinate-scoped snapshot.
        writer.Write((byte)0);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.START_MAP,
            payload.ToArray());
    }
    public static GameServerPacket CreateEmptyStartMap(
        byte roomX,
        byte roomY,
        ushort mapId,
        uint randomSeed = 0) =>
        DungeonPacketBuilders.CreateStartMap(roomX, roomY, mapId, [], randomSeed: randomSeed);
    public static GameDungeonMonster[] CreateTutorialMonsters() =>
    [
        new(0, 0, 1, 1, X: 1078, Y: 177),
        new(1, 1, 1, 1, X: 557, Y: 208),
        new(2, 2, 1, 1, X: 487, Y: 332),
        new(3, 3, 1, 1, X: 1026, Y: 334)
    ];
    public static GameServerPacket CreateTutorialStartMap() =>
        // The tutorial dungeon is id 10000, but START_MAP resolves its scene
        // through map.lst where Tutorial/tutorial.map is registered as 61000.
        DungeonPacketBuilders.CreateStartMap(
            roomX: 0,
            roomY: 0,
            mapId: GameProtocolConstants.TutorialMapId,
            monsters: DungeonPacketBuilders.CreateTutorialMonsters());
    public static bool IsTutorialCompletionModule(ReadOnlySpan<byte> body) =>
        body.Length >= 6
        && BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(2, sizeof(uint)))
            == GameProtocolConstants.TutorialCompletionModule;
    public static bool TryParseDieMonsterCommand(
        ReadOnlySpan<byte> body,
        out GameDieMonsterCommand command)
    {
        command = default;
        if (body.Length < 4)
        {
            return false;
        }

        var entityId = BinaryPrimitives.ReadUInt16LittleEndian(
            body.Slice(2, sizeof(ushort)));

        // Captured DAFLegacy command-42 bodies are 23 bytes. The final byte is
        // the passive-object discriminator; ordinary monster deaths send 0.
        const int passiveObjectFlagOffset = 22;
        var isPassiveObject = body.Length > passiveObjectFlagOffset
            && body[passiveObjectFlagOffset] == 1;

        command = new GameDieMonsterCommand(entityId, isPassiveObject);
        return true;
    }
    public static bool TryParseUseCoinCommand(
        ReadOnlySpan<byte> body,
        out GameUseCoinCommand command)
    {
        command = default;
        if (body.Length < 4)
        {
            return false;
        }

        command = new GameUseCoinCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))));
        return true;
    }
    public static GameServerPacket CreateUseCoinReply(ushort targetUserId) =>
        new(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.USE_COIN,
            [1, (byte)targetUserId, (byte)(targetUserId >> 8)]);
    public static bool TryParseBossDieCheckCommand(
        ReadOnlySpan<byte> body,
        out GameBossDieCheckCommand command)
    {
        command = default;
        if (body.Length < 10)
        {
            return false;
        }

        command = new GameBossDieCheckCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(6, 4)));
        return true;
    }
    public static GameServerPacket CreateBossDieCheck(
        bool success,
        bool allBossesReported,
        ushort bossId = 0,
        byte errorCode = 0)
    {
        if (!success)
        {
            return new GameServerPacket(
                GameProtocolConstants.NotificationPacketType,
                (byte)NotiPacketType.BOSS_DIE_CHECK,
                [0, errorCode]);
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(allBossesReported ? (byte)1 : (byte)0);
        writer.Write(bossId);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.BOSS_DIE_CHECK,
            payload.ToArray());
    }
    public static GameServerPacket CreateFinishLoading() =>
        new(GameProtocolConstants.NotificationPacketType, (byte)NotiPacketType.FINISH_LOADING, []);
    public static GameServerPacket CreateExperienceGained(
        byte level,
        uint cumulativeExperience,
        uint extraExperienceA,
        uint extraExperienceB,
        ushort skillPoints)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(level);
        writer.Write(cumulativeExperience);
        writer.Write(extraExperienceA);
        writer.Write(extraExperienceB);
        writer.Write(skillPoints);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.EXP,
            payload.ToArray());
    }
    public static GameServerPacket CreateMonsterDie(
        ushort uniqueId,
        IReadOnlyList<GameDungeonDrop>? drops = null,
        sbyte championRewardOrdinal = GameProtocolConstants.NoChampionRewardOrdinal)
    {
        drops ??= [];
        if (drops.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(drops));
        }

        if (championRewardOrdinal != GameProtocolConstants.NoChampionRewardOrdinal
            && (championRewardOrdinal < 1
                || championRewardOrdinal > GameProtocolConstants.MaximumChampionRewardOrdinal))
        {
            throw new ArgumentOutOfRangeException(nameof(championRewardOrdinal));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(uniqueId);
        writer.Write((byte)drops.Count);
        foreach (var drop in drops)
        {
            // DAFLegacy's compact death record is ground id, item id, add-info,
            // durability and the owning user id. Add-info is polymorphic: it
            // is the stack count for stackables and reinforcement for normal
            // equipment.
            writer.Write(drop.GroundId);
            writer.Write(drop.ItemId);
            writer.Write(drop.AddInfo);
            writer.Write(drop.Durability);
            writer.Write(drop.OwnerUserId);
        }

        // 13338's NOTI 38 is a compact six-byte no-drop record. The middle
        // trailer byte is not an owner index: Champion/SuperChampion kills
        // carry their 1..11 clear-reward animation ordinal here. Boss and
        // ordinary deaths use the no-reward sentinel (255).
        writer.Write((byte)0);
        writer.Write((byte)championRewardOrdinal);
        writer.Write((byte)0);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.DIE_MONSTER,
            payload.ToArray());
    }
    public static GameServerPacket CreateGoldItemPickup(
        ushort groundId,
        ushort pickerUserId,
        uint goldAmount,
        byte pickerPartyIndex = 0)
    {
        if (pickerPartyIndex >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(pickerPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(groundId);
        writer.Write(pickerUserId);
        for (var partyIndex = 0; partyIndex < 4; partyIndex++)
        {
            writer.Write(partyIndex == pickerPartyIndex ? goldAmount : 0u);
            writer.Write((byte)0); // no economic-control adjustment entries
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.GET_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateInventoryItemPickup(
        ushort groundId,
        ushort pickerUserId,
        ushort destinationSlot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(groundId);
        writer.Write(pickerUserId);
        // DNF 1.0.1.9 interprets every non-zero byte here as a party-routing
        // dice result. Direct pickup does not participate in routing, so all
        // four party result bytes must stay zero.
        writer.Write(new byte[4]);

        writer.Write(pickerUserId);
        writer.Write(destinationSlot);
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.GET_ITEM,
            payload.ToArray());
    }
    public static GameServerPacket CreateEnableClearDungeon() =>
        new(GameProtocolConstants.NotificationPacketType, (byte)NotiPacketType.ENABLE_CLEAR_DUNGEON, []);
    public static GameServerPacket CreatePlayResult(
        ushort userId,
        uint resultValue,
        ushort resultDetailA = 0,
        ushort resultDetailB = 0)
    {
        _ = resultValue;
        _ = resultDetailA;
        _ = resultDetailB;

        // DNF 1.0.1.9 reads five bytes: the party user id followed by
        // three score flags and one reserved result byte.
        var payload = new byte[]
        {
            checked((byte)userId),
            0,
            0,
            0,
            0,
        };
        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.PLAY_RESULT,
            payload);
    }
    public static GameServerPacket CreateClearDungeonReward(
        ushort userId,
        uint goldCardCost,
        uint freeGoldAmount = 0,
        ushort? freeItemId = null,
        bool goldCardEnabled = true,
        ushort? goldItemId = null,
        bool premiumCardEnabled = false,
        ushort? premiumItemId = null,
        byte localPartyIndex = 0,
        GameDungeonClearExperienceBreakdown? experience = null)
    {
        if (localPartyIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(checked((byte)userId));

        experience ??= GameDungeonClearExperienceBreakdown.Empty;

        // DNF 1.0.1.9 reads nine uint32 values. Its NOTI 35 receiver derives
        // the displayed base value as wire[0] - wire[2], so wire[0] must carry
        // base plus the party bonus rather than base alone.
        writer.Write((uint)Math.Min(
            uint.MaxValue,
            (ulong)experience.BaseExperience + experience.PartyBonus));
        writer.Write(experience.RankBonus);
        writer.Write(experience.PartyBonus);
        writer.Write(experience.AvatarBonus);
        writer.Write(experience.EventBonus);
        // DF2008 setters: +0x130 black diamond, +0x134 matching channel,
        // +0x120 mentor. These are not the newer server's PC-room/material fields.
        writer.Write(experience.BlackDiamondBonus);
        writer.Write(experience.ChannelBonus);
        writer.Write(experience.MentorBonus);
        writer.Write(experience.CreatureBonus);

        // No immediate inventory rewards are attached to this notification.
        writer.Write((byte)0);

        // DAFLegacy increments its visible-card count for every non-empty entry
        // in this first group. Keep all four columns present even in solo play.
        for (var cardIndex = 0;
             cardIndex < GameProtocolConstants.ClearRewardCardColumnCount;
             cardIndex++)
        {
            DungeonPacketBuilders.WriteFreeClearRewardPreview(
                writer,
                cardExists: true,
                freeItemId,
                freeGoldAmount);
        }

        writer.Write(goldCardEnabled ? goldCardCost : 0u);

        // The paid-card result remains hidden in NOTI 35. It is revealed by
        // SELECT_CARD's group-3 result list only after the player buys a card.
        for (var partyIndex = 0;
             partyIndex < GameProtocolConstants.ClearRewardCardColumnCount;
             partyIndex++)
        {
            writer.Write((byte)0);
        }

        for (var partyIndex = 0;
             partyIndex < GameProtocolConstants.ClearRewardCardColumnCount;
             partyIndex++)
        {
            DungeonPacketBuilders.WriteEquipmentRewardPreview(
                writer,
                premiumCardEnabled && partyIndex == localPartyIndex,
                premiumItemId);
        }

        return new GameServerPacket(
            GameProtocolConstants.NotificationPacketType,
            (byte)NotiPacketType.CLEAR_DUNGEON_REWARD,
            payload.ToArray());
    }
    public static GameServerPacket CreateScoreScrollStateReply() =>
        new(GameProtocolConstants.CommandPacketType, (byte)CmdPacketType.SCORE_SCROLL_STATE, [1]);
    public static GameServerPacket CreateCardSelectRightState(
        bool localUserCanSelect = true,
        byte localPartyIndex = 0)
    {
        if (localPartyIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        for (var partyIndex = 0;
             partyIndex < GameProtocolConstants.ClearRewardCardColumnCount;
             partyIndex++)
        {
            writer.Write(partyIndex == localPartyIndex
                ? (ushort)(localUserCanSelect ? 1 : 0)
                : ushort.MaxValue);
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.CARD_SELECT_RIGHT_STATE,
            payload.ToArray());
    }
    public static GameServerPacket CreateSelectCardReply(
        byte? freeCardIndex,
        byte? goldCardIndex,
        bool goldRewardRevealed = false,
        ushort? goldRewardItemId = null,
        byte localPartyIndex = 0)
    {
        if (freeCardIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(freeCardIndex));
        }

        if (goldCardIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(goldCardIndex));
        }

        if (localPartyIndex >= GameProtocolConstants.ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        for (byte cardIndex = 0;
             cardIndex < GameProtocolConstants.ClearRewardCardColumnCount;
             cardIndex++)
        {
            writer.Write(freeCardIndex == cardIndex
                ? localPartyIndex
                : byte.MaxValue);
            writer.Write(goldCardIndex == cardIndex
                ? localPartyIndex
                : byte.MaxValue);

            var writeGoldReward = goldRewardRevealed
                && goldCardIndex == cardIndex;
            writer.Write((byte)(writeGoldReward
                ? goldRewardItemId.HasValue ? 2 : 1
                : 0));
            if (writeGoldReward)
            {
                // DNF 1.0.1.9 treats the first record as the gold line and
                // renders equipment only from the second record. Paid cards
                // award no gold, but still require this zero-gold record.
                writer.Write((ushort)0);
                writer.Write(0u);

                if (goldRewardItemId.HasValue)
                {
                    writer.Write(goldRewardItemId.Value);
                    writer.Write(0u);
                }
            }
        }

        return new GameServerPacket(
            GameProtocolConstants.CommandPacketType,
            (byte)CmdPacketType.SELECT_CARD,
            payload.ToArray());
    }
    private static void WriteFreeClearRewardPreview(
        BinaryWriter writer,
        bool cardExists,
        ushort? itemId,
        uint goldAmount)
    {
        writer.Write((byte)(cardExists
            ? itemId.HasValue ? 2 : 1
            : 0));
        if (!cardExists)
        {
            return;
        }

        // A free card always contains its clear-gold record. Equipment is a
        // second, independently rolled record in the same card.
        writer.Write((ushort)0);
        writer.Write(goldAmount);

        if (itemId.HasValue)
        {
            writer.Write(itemId.Value);
            writer.Write(0u);
        }
    }
    private static void WriteEquipmentRewardPreview(
        BinaryWriter writer,
        bool rewardExists,
        ushort? itemId)
    {
        if (!rewardExists)
        {
            writer.Write((byte)0);
            return;
        }

        // The client always interprets the first record as the gold line and
        // renders an equipment icon only when a second record exists.
        writer.Write((byte)(itemId.HasValue ? 2 : 1));
        writer.Write((ushort)0);
        writer.Write(0u);

        if (itemId.HasValue)
        {
            writer.Write(itemId.Value);
            writer.Write(0u);
        }
    }
    public static GameServerPacket CreateEplpReply(byte state, byte option) =>
        new(GameProtocolConstants.CommandPacketType, (byte)CmdPacketType.EPLP_COMMAND, [1, state, option]);
}
