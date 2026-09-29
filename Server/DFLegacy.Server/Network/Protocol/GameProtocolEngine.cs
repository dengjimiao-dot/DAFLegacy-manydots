using System.Buffers.Binary;

namespace DFLegacy.Protocol;

public sealed record GameCharacterSummary(
    ushort Slot,
    byte[] NameBytes,
    byte Job,
    byte GrowType,
    byte Level,
    IReadOnlyList<GameInventoryEntry>? AppearanceItems = null,
    uint CharacterNo = 0,
    byte AwakeningType = 0,
    byte PvpGrade = 0);

public sealed record GameInventoryEntry(
    ushort Slot,
    ushort ItemId,
    uint CountOrValue,
    byte State = 0,
    ushort Durability = 0,
    byte SealState = 0);
public sealed record GameCreatureInfoEntry(
    uint Uid,
    byte Stomach,
    uint Experience,
    byte Level,
    byte[] NameBytes,
    bool NoCharge = false);
public sealed record GameCreatureAppearanceEntry(
    ushort ItemId,
    byte[] NameBytes,
    byte State = 1);
public sealed record GameItemConsumptionEntry(
    ushort Slot,
    uint ConsumedCount);
public sealed record GameDisjointRewardEntry(
    ushort DestinationSlot,
    ushort ItemId,
    uint Count);
public readonly record struct GameCeraShopReward(ushort ItemId, uint Count);

public sealed record GameSkillEntry(byte SkillId, byte Level, byte? Slot = null);
public sealed record GameQuestEntry(ushort QuestId, uint Trigger = 0);
public sealed record GameQuestConsumedItem(ushort Slot, uint RemainingCount);
public sealed record GameQuestInsertedItem(ushort Slot, ushort ItemId, uint CountOrSeed);
public sealed record GameQuestGrowthSkillChange(byte SkillId, byte SkillClass, byte Level);
public sealed record GamePremiumServiceEntry(byte ServiceType, uint RemainSeconds);
public sealed record GameCharacterCombatStats(
    uint MaximumHp,
    uint MaximumMp,
    short PhysicalAttack,
    short PhysicalDefense,
    short MagicalAttack,
    short MagicalDefense,
    short FireResistance,
    short WaterResistance,
    short DarkResistance,
    short LightResistance,
    uint InventoryLimit,
    ushort HpRegeneration,
    ushort MpRegeneration,
    ushort MovementSpeed,
    ushort AttackSpeed,
    ushort CastSpeed,
    ushort HitRecovery,
    ushort JumpPower,
    uint Weight);
public sealed record GameDungeonPermissionEntry(ushort DungeonId, byte ClearState);
public sealed record GameTownUser(
    ushort UserId,
    short X,
    short Y,
    byte Direction);
public sealed record GameDungeonMonster(
    byte MapListIndex,
    ushort UniqueId,
    ushort MonsterIndex,
    byte Level,
    byte Type = 0,
    byte IsBoxMonster = 0,
    byte BoxIndex = 0,
    bool IsHellHidden = false,
    ushort X = 0,
    ushort Y = 0);

public static class GameDungeonMonsterTypes
{
    public const byte Normal = 0;
    public const byte Champion = 1;
    public const byte SuperChampion = 2;
    public const byte Boss = 3;
}

public sealed record GameDungeonClearExperienceBreakdown
{
    public static GameDungeonClearExperienceBreakdown Empty { get; } = new();

    public uint BaseExperience { get; init; }

    public uint RankBonus { get; init; }

    public uint PartyBonus { get; init; }

    public uint AvatarBonus { get; init; }

    public uint EventBonus { get; init; }

    public uint BlackDiamondBonus { get; init; }

    public uint ChannelBonus { get; init; }

    public uint MentorBonus { get; init; }

    public uint CreatureBonus { get; init; }

    public uint TotalExperience => (uint)Math.Min(
        uint.MaxValue,
        (ulong)BaseExperience
        + RankBonus
        + PartyBonus
        + AvatarBonus
        + EventBonus
        + BlackDiamondBonus
        + ChannelBonus
        + MentorBonus
        + CreatureBonus);
}

public sealed record GameDungeonDrop(
    ushort GroundId,
    ushort ItemId,
    uint AddInfo,
    ushort Durability = 0,
    ushort OwnerUserId = 0,
    byte ItemAttr = 0);
public sealed record GamePassiveObjectDrop(
    byte AssociationSlot,
    ushort GroundId,
    ushort ItemId,
    uint AddInfo,
    ushort OwnerUserId = 0);
public readonly record struct GameDieMonsterCommand(
    ushort EntityId,
    bool IsPassiveObject);

public readonly record struct GameUseCoinCommand(ushort TargetUserId);
public readonly record struct GameDropItemCommand(
    short X,
    short Y,
    byte ItemSpace,
    ushort Slot,
    uint Count);
public readonly record struct GameBossDieCheckCommand(
    ushort ParticipantId,
    ushort BossId,
    uint SkillChecksum);
public readonly record struct GameUpgradeItemCommand(
    ushort TargetSlot,
    ushort TargetItemId,
    ushort MaterialSlot);
public readonly record struct GameResetItemAttributeCommand(
    ushort TargetSlot,
    ushort TargetItemId,
    ushort MaterialSlot);
public readonly record struct GameCompoundItemCommand(
    ushort Sequence,
    ushort SourceValue,
    bool SourceIsItemId,
    byte CraftCount);
public readonly record struct GameCompoundItemConsumption(
    byte ListType,
    ushort Slot,
    uint RemainingCount);
public readonly record struct GameCompoundItemResult(
    ushort Slot,
    ushort ItemId,
    uint CountOrValue,
    byte State,
    ushort Durability);
public readonly record struct GameCompoundAvatarCommand(
    ushort MaterialSlot,
    ushort FirstAvatarItemId,
    ushort SecondAvatarItemId,
    byte SelectedOption,
    byte FirstAvatarSlot,
    byte SecondAvatarSlot);
public readonly record struct GameAvatarCompoundConsumption(
    byte ListType,
    ushort Slot,
    uint RemainingCount);

public sealed record GameMailAttachment(
    ushort ItemId,
    uint CountOrValue,
    byte State = 0,
    ushort Durability = 0,
    byte SealState = 0);

public sealed record GameMailEntry(
    uint MailId,
    byte[] SenderBytes,
    byte[] TextBytes,
    uint Gold,
    GameMailAttachment? Attachment,
    uint SentAt,
    uint ExpiresAt,
    ushort State = 1);

public enum GameMessageType : byte
{
    Type00DualRouteBrown = 0,
    Type01LightGreen = 1,
    Type02LightCyan = 2,
    Type03White = 3,
    Type04Brown = 4,
    Type05White = 5,
    Type06Magenta = 6,
    Type07LightGreen = 7,
    Type08Orange = 8,
    Type09White = 9,
    Type10White = 10,
    Type11LightCyan = 11,
    Type12Yellow = 12,
    Type13SpeakerYellow = 13,
    Type14SpeakerYellow = 14,
    Type15SpeakerYellow = 15,
    Type16Brown = 16,
    Type17ClientFormattedNotice = 17,
    Type18DirectOutput = 18
}

/// <summary>
/// Handles the initial command exchange used after the client selects a channel.
/// Game/channel packets share the ten-byte transport header with entrance packets,
/// but the body begins with a two-byte command sequence number. Command payload
/// serialization starts after that sequence number.
/// </summary>
public static class GameProtocolEngine
{
    private const byte GrowthSkillClassCount = 5;
    private const ushort PartyEmptySlotUserId = 0xFFFF;

    public const int ClearRewardCardColumnCount = 4;
    public const byte NotificationPacketType = 0;
    public const byte CheckConnectionNotification = 0;
    public const byte ChannelInfoNotification = 1;
    public const byte UserInfoNotification = 2;
    public const byte UserStateNotification = 3;
    public const byte StaminaNotification = 4;
    public const byte DungeonPermissionNotification = 5;
    public const byte UserLeaveNotification = 6;
    // NOTI 7/8/9/10 are the party block: a 13-byte invite popup (case
    // 0x41B4C8), the 7-byte accept ack (case 0x41B5F1), the slot-diff party
    // roster (case 0x41B7A1) and the two-byte departure message (case
    // 0x41C41A) in DNF.exe 60A1.
    public const byte PeerRequestNotification = 7;
    public const byte PeerResponseNotification = 8;
    public const byte PartyInfoNotification = 9;
    public const byte WalkoutNotification = 10;
    public const byte UdpPeerInfoNotification = 11;
    public const byte MessageNotification = 12;
    public const byte ItemListNotification = 13;
    public const byte UpdateItemListNotification = 14;
    public const byte SkillInfoNotification = 19;
    public const byte AcceptableQuestListNotification = 21;
    public const byte UserPositionNotification = 22;
    public const byte UserAreaNotification = 23;
    public const byte AreaUsersNotification = 24;
    public const byte UdpHostNotification = 26;
    public const byte EnterSelectDungeonNotification = 27;
    public const byte DungeonInfoNotification = 28;
    public const byte StartMapNotification = 29;
    public const byte FinishLoadingNotification = 30;
    public const byte EnableClearDungeonNotification = 31;
    public const byte DieStateNotification = 32;
    public const byte FailClearDungeonNotification = 33;
    public const byte PlayResultNotification = 34;
    public const byte ClearDungeonRewardNotification = 35;
    public const byte FatigueNotification = 36;
    public const byte ExperienceGainedNotification = 37;
    public const byte MonsterDieNotification = 38;
    // NOTI 38's compact trailer is [reserved, champion ordinal, reserved].
    // The 13338 client uses ordinal 1..11 to play the Champion/SuperChampion
    // clear-reward animation before the card screen; 255 means no such reward.
    public const sbyte NoChampionRewardOrdinal = -1;
    public const sbyte MaximumChampionRewardOrdinal = 11;
    public const byte GetItemNotification = 39;
    public const byte DropItemNotification = 40;
    // 13338's notification dispatcher case 48 reads five little-endian
    // int32 values followed by two bytes: win, lose, PvP points, current
    // rank point, next rank point, grade and grade-extension.
    public const byte PvpRecordNotification = 48;
    public const byte CeraUpdateNotification = 53;
    public const byte PremiumServiceNotification = 66;
    public const byte MailboxMailListNotification = 97;
    public const byte MailboxRemoveMailNotification = 98;
    public const byte MailboxAlarmNotification = 99;
    // DF2008 reads both Creature life notifications as one u16 area-user id.
    // The 13339 server names these DIED_CREATURE and REVIVAL_CREATURE.
    public const byte DiedCreatureNotification = 100;
    public const byte CreatureRenameNotification = 101;
    public const byte CreatureGainExperienceNotification = 102;
    public const byte CreatureStateNotification = 103;
    public const byte CreatureResponseNotification = 104;
    public const byte CreatureInfoNotification = 105;
    public const byte CreatureEvolutionNotification = 106;
    public const byte CreatureEvoluteNotification = 106;
    public const byte RevivalCreatureNotification = 107;
    public const byte RenameCreatureCommand = 103;
    public const byte HatchCreatureCommand = 105;
    public const byte ResponseCreatureCommand = 104;
    public const byte EventInfoNotification = 120;
    public const byte CreatureMessageNotification = 126;
    public const byte BossDieCheckNotification = 127;
    public const byte CreatureScriptMessageNotification = 131;
    public const byte MegaphoneMessageNotification = 130;
    public const byte EnterGameWorldCompleteNotification = 136;
    public const byte CommandPacketType = 1;
    public const byte CheckConnectionCommand = 0;
    public const byte LoginCommand = 1;
    public const byte SetUdpEndpointCommand = 2;
    public const byte ExitCommand = 3;
    public const byte SelectCharacterCommand = 4;
    public const byte CreateCharacterCommand = 5;
    public const byte DeleteCharacterCommand = 6;
    public const byte ReturnSelectCharacterCommand = 7;
    public const byte GetUserInfoCommand = 8;
    public const byte RecoverStaminaCommand = 9;
    // CMD 10-14 are the party block: type-0 peer requests are the party
    // invite/join path of this build (RESPONSE_PEER 11 answers them;
    // SET_PARTY_INFO 12, LEAVE_PARTY 13 and the slot-indexed kick 14
    // complete it, matching PacketNames.cs and the 60CN dispatcher).
    public const byte RequestPeerCommand = 10;
    public const byte ResponsePeerCommand = 11;
    public const byte SetPartyInfoCommand = 12;
    public const byte LeavePartyCommand = 13;
    public const byte WalkoutPartyMemberCommand = 14;
    public const byte StartGameCommand = 15;
    public const byte SelectDungeonCommand = 16;
    public const byte SendMessageCommand = 17;
    public const byte CreatureSendMessageCommand = 124;
    public const byte CreatureScriptMessageCommand = 132;
    public const byte DeleteItemCommand = 18;
    public const byte MoveItemSpaceCommand = 19;
    public const byte SortItemCommand = 20;
    public const byte BuyItemCommand = 21;
    public const byte SellItemCommand = 24;
    public const byte RepairEquipmentCommand = 25;
    public const byte SetItemTradeStateCommand = 26;
    public const byte CompoundItemCommand = 27;
    public const byte DisjointItemCommand = 28;
    public const byte UseLotteryItemCommand = 29;
    public const byte ChangeSkillSlotCommand = 30;
    public const byte BuySkillCommand = 31;
    public const byte IncreaseStatusCommand = 32;
    public const byte AcceptQuestCommand = 33;
    public const byte GiveupQuestCommand = 34;
    public const byte SetQuestTriggerCommand = 35;
    public const byte FinishQuestCommand = 36;
    public const byte SetUserPositionCommand = 37;
    public const byte SetUserAreaCommand = 38;
    public const byte FinishLoadingCommand = 40;
    public const byte UseSkillCommand = 41;
    public const byte DieMonsterCommand = 42;
    public const byte DieCharacterCommand = 43;
    public const byte UseCoinCommand = 44;
    public const byte GiveupGameCommand = 45;
    public const byte GetItemCommand = 46;
    public const byte UseStackableItemCommand = 47;
    public const byte DungeonItemActionCommand = UseStackableItemCommand;
    public const byte MoveMapCommand = 48;
    public const byte SetPlayResultCommand = 49;
    public const byte DropItemCommand = 50;
    public const byte DecreaseDurabilityCommand = 51;
    public const byte CeraShopBuyCommand = 67;
    public const byte GenerateCeraTicketCommand = 68;
    public const byte ScoreScrollStateCommand = 72;
    public const byte CardSelectRightStateCommand = 73;
    public const byte SelectCardCommand = 74;
    public const byte EplpCommand = 75;
    public const byte PrivateStorePollCommand = 82;
    public const byte UpgradeItemCommand = 83;
    public const byte ResetItemAttributeCommand = 84;
    // 60CN ENUM_CMDPACKET_ANTIBOT: fire-and-forget client health report.
    public const byte AntibotCommand = 161;
    public const byte MailboxSendCommand = 97;
    public const byte MailboxExtractItemCommand = 98;
    public const byte MailboxOpenCommand = 99;
    public const byte CompoundAvatarCommand = 102;
    public const byte BossDieCheckCommand = 127;
    public const byte ExchangeServerCharacterInfoCommand = 130;
    public const byte BackToVillageCommand = 142;
    public const byte MailboxStateCommand = 144;
    public const byte AllDieMonsterCommand = 148;
    public const byte TutorialCompletionCommand = 153;
    public const ushort TutorialDungeonId = 10_000;
    public const ushort TutorialMapId = 61_000;
    public const uint TutorialCompletionModule = 31;
    public const ushort DefaultMaximumFatigue = 156;
    public const ushort BlackDiamondBonusFatigue = 32;
    public const ushort BlackDiamondMaximumFatigue =
        DefaultMaximumFatigue + BlackDiamondBonusFatigue;

    // DF2008 CMD 15 reads error 0x16 followed by the exhausted party-member index.
    public const byte EnterDungeonSelectionInsufficientFatigueError = 0x16;
    // DF2008 starts ground-item pickup optimistically. CMD 46 error 0x15 stays
    // silent but enters the common rollback at 0x00414198, which clears the
    // pending pickup flag at item-object + 0x7A5 so the same item falls back.
    public const byte GetItemSilentRejectionError = 0x15;
    public const uint DefaultCompletedTutorialFlags = uint.MaxValue;
    public const byte BlackDiamondServiceType = 16;
    public const byte OverlordContractServiceType = 22;
    public const byte MasterContractServiceType = 27;
    public const ushort PremiumServiceUpdateAction = 2;

    public static GameServerPacket? Handle(PacketFrame request)
    {
        return Handle(request, []);
    }

    public static GameServerPacket? Handle(PacketFrame request, ReadOnlySpan<byte> challenge)
    {
        if (request.Type != CommandPacketType || request.Body.Length < sizeof(ushort))
        {
            return null;
        }

        return request.ProtocolId switch
        {
            CheckConnectionCommand when challenge.Length == 16 => CreateChannelInfo(
                "Local Channel 1"u8,
                channelType: 1,
                result: 1,
                serverId: 1,
                channelNumber: 1,
                seed: 0,
                ["127.0.0.1"u8.ToArray()],
                port1: 2311,
                port2: 2311),
            // A successful login response contains the general success byte,
            // three account flags and one trailing client-state flag. Zeroes
            // select the normal character-list path in this client build.
            LoginCommand => new GameServerPacket(
                CommandPacketType,
                LoginCommand,
                [1, 0, 0, 0, 0]),
            // The 1.0.1.9 client clears a possible old game session before it
            // begins the selected-channel login flow. A one-byte true result is
            // the complete successful response consumed by its EXIT handler.
            ExitCommand => CreateCommandReply(request, success: true),
            // SELECT_CHARACTER uses a larger acknowledgement than the other
            // character-screen commands. It supplies the small account/town
            // state block consumed before the client leaves character select.
            SelectCharacterCommand => CreateSelectCharacterReply(request),
            // CREATE_CHARACTER also consumes the command's generic one-byte
            // result. A successful result closes the creation panel and makes
            // this client refresh USERINFO.
            CreateCharacterCommand => CreateCommandReply(request, success: true),
            // DELETE_CHARACTER success carries the deleted character slot
            // after the common result byte. The client uses that slot to
            // remove the matching character card.
            DeleteCharacterCommand when request.Body.Length >= 3 =>
                CreateDeleteCharacterReply(request.Body[2]),
            // RETURN_SELECT_CHARACTER acknowledges the town-side transition.
            // The channel service follows this reply with a fresh USERINFO
            // roster after it has persisted and detached the active character.
            ReturnSelectCharacterCommand => CreateCommandReply(request, success: true),
            // The dungeon USE_SKILL command only needs the common success
            // acknowledgement. Its cube-item transaction is completed by a
            // following UPDATE_ITEM_LIST notification from the channel.
            UseSkillCommand => CreateUseSkillReply(),
            // GET_USERINFO is followed by an asynchronous USERINFO
            // notification rather than a command reply.
            GetUserInfoCommand => CreateEmptyUserInfo(),
            // DFLegacy starts both town-side polling systems as soon as its
            // local actor is created. Their replies use compact list layouts.
            PrivateStorePollCommand => CreatePrivateStorePollReply(),
            ExchangeServerCharacterInfoCommand =>
                CreateExchangeServerCharacterInfoReply(),
            _ => null
        };
    }

    public static GameServerPacket CreateCommandReply(PacketFrame request, bool success)
    {
        if (request.Body.Length < sizeof(ushort))
        {
            throw new ArgumentException("A game command must contain a two-byte sequence number.", nameof(request));
        }

        return new GameServerPacket(
            CommandPacketType,
            request.ProtocolId,
            [success ? (byte)1 : (byte)0]);
    }

    public static GameServerPacket CreateUseSkillReply() =>
        new(
            CommandPacketType,
            UseSkillCommand,
            [1]);

    public static GameServerPacket CreatePrivateStorePollReply() =>
        new(
            CommandPacketType,
            PrivateStorePollCommand,
            [
                1,             // result
                0, 0,          // local store state
                0, 0, 0, 0,   // empty store name
                0, 0,          // store flags
                0, 0, 0, 0,   // first store value
                0, 0, 0, 0,   // second store value
                0              // additional store count
            ]);

    public static GameServerPacket CreateExchangeServerCharacterInfoReply() =>
        new(
            CommandPacketType,
            ExchangeServerCharacterInfoCommand,
            [1, 0]);           // result, character count

    public static GameServerPacket CreateDeleteCharacterReply(byte slot) =>
        new(
            CommandPacketType,
            DeleteCharacterCommand,
            [1, slot]);

    public static GameServerPacket CreateDeleteItemReply(
        byte listType,
        IReadOnlyList<GameItemConsumptionEntry> entries)
    {
        if (entries.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(entries));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(listType);
        writer.Write((byte)entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Slot);
            writer.Write(entry.ConsumedCount);
        }

        return new GameServerPacket(
            CommandPacketType,
            DeleteItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateDeleteItemError(
        byte listType,
        byte errorCode) =>
        new(
            CommandPacketType,
            DeleteItemCommand,
            [0, errorCode, listType]);

    public static GameServerPacket CreateUseStackableItemReply(
        ushort slot,
        byte listType) =>
        new(
            CommandPacketType,
            UseStackableItemCommand,
            [
                1,
                (byte)slot,
                (byte)(slot >> 8),
                listType
            ]);

    public static GameServerPacket CreateUseStackableItemError(
        byte errorCode,
        byte listType) =>
        new(
            CommandPacketType,
            UseStackableItemCommand,
            [0, errorCode, listType]);

    public static GameServerPacket CreateDungeonItemActionReply(
        ushort slot,
        byte listType) =>
        CreateUseStackableItemReply(slot, listType);

    public static GameServerPacket CreateCommandError(PacketFrame request, byte errorCode)
    {
        if (request.Body.Length < sizeof(ushort))
        {
            throw new ArgumentException("A game command must contain a two-byte sequence number.", nameof(request));
        }

        return new GameServerPacket(
            CommandPacketType,
            request.ProtocolId,
            [0, errorCode]);
    }

    public static GameServerPacket CreateGetItemSilentRejection(PacketFrame request)
    {
        if (request.ProtocolId != GetItemCommand)
        {
            throw new ArgumentException(
                "A silent item-pickup rejection requires a GET_ITEM request.",
                nameof(request));
        }

        return CreateCommandError(request, GetItemSilentRejectionError);
    }

    public static GameServerPacket CreateEnterDungeonSelectionInsufficientFatigueReply(
        byte partyMemberIndex = 0) =>
        new(
            CommandPacketType,
            StartGameCommand,
            [0, EnterDungeonSelectionInsufficientFatigueError, partyMemberIndex]);

    public static GameServerPacket CreateDisjointItemReply(
        ushort sourceSlot,
        byte itemSpace,
        IReadOnlyList<GameDisjointRewardEntry> rewards)
    {
        if (rewards.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rewards));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(itemSpace);
        writer.Write((byte)rewards.Count);
        foreach (var reward in rewards)
        {
            writer.Write(reward.DestinationSlot);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }

        return new GameServerPacket(
            CommandPacketType,
            DisjointItemCommand,
            payload.ToArray());
    }

    public static bool TryParseCompoundItemCommand(
        ReadOnlySpan<byte> body,
        out GameCompoundItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // recipe item id or inventory slot
            + sizeof(byte)                   // source is item id
            + sizeof(byte)                   // craft count
            + sizeof(byte)                   // reserved zero
            + sizeof(byte);                  // fixed one
        if (body.Length != bodyLength
            || body[4] > 1
            || body[5] == 0
            || body[6] != 0
            || body[7] != 1)
        {
            return false;
        }

        command = new GameCompoundItemCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            body[4] != 0,
            body[5]);
        return true;
    }

    public static GameServerPacket CreateCompoundItemReply(
        IReadOnlyList<GameCompoundItemConsumption> consumedItems,
        IReadOnlyList<GameCompoundItemResult> resultItems)
    {
        ArgumentNullException.ThrowIfNull(consumedItems);
        ArgumentNullException.ThrowIfNull(resultItems);
        if (consumedItems.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(consumedItems));
        }

        if (resultItems.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(resultItems));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(checked((byte)consumedItems.Count));
        foreach (var item in consumedItems)
        {
            writer.Write(item.ListType);
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(checked((byte)resultItems.Count));
        foreach (var item in resultItems)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
        }

        return new GameServerPacket(
            CommandPacketType,
            CompoundItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCompoundItemFailure(byte errorCode) =>
        new(
            CommandPacketType,
            CompoundItemCommand,
            [0, errorCode]);

    public static bool TryParseUpgradeItemCommand(
        ReadOnlySpan<byte> body,
        out GameUpgradeItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // target slot
            + sizeof(ushort)                 // target item id
            + sizeof(ushort);                // material slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameUpgradeItemCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))));
        return command.TargetItemId != 0
            && command.TargetSlot != command.MaterialSlot;
    }

    public static GameServerPacket CreateUpgradeItemReply(
        ushort materialSlot,
        uint materialRemaining,
        byte resultCode,
        byte newLevel,
        ushort targetSlot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(materialSlot);
        writer.Write(materialRemaining);
        writer.Write(resultCode);
        writer.Write(newLevel);
        writer.Write(targetSlot);
        if (resultCode > 2)
        {
            writer.Write((byte)0);
        }

        return new GameServerPacket(
            CommandPacketType,
            UpgradeItemCommand,
            payload.ToArray());
    }

    public static bool TryParseResetItemAttributeCommand(
        ReadOnlySpan<byte> body,
        out GameResetItemAttributeCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // target slot
            + sizeof(ushort)                 // target item id
            + sizeof(ushort);                // material slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameResetItemAttributeCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))));
        return command.TargetItemId != 0
            && command.TargetSlot != command.MaterialSlot;
    }

    public static GameServerPacket CreateResetItemAttributeReply(
        ushort materialSlot,
        uint materialRemaining,
        ushort operationType)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(materialSlot);
        writer.Write(materialRemaining);
        writer.Write(operationType);
        return new GameServerPacket(
            CommandPacketType,
            ResetItemAttributeCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateResetItemAttributeFailure(byte errorCode) =>
        new(
            CommandPacketType,
            ResetItemAttributeCommand,
            [0, errorCode]);

    public static bool TryParseCompoundAvatarCommand(
        ReadOnlySpan<byte> body,
        out GameCompoundAvatarCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(ushort)                 // material slot
            + sizeof(ushort)                 // first item-list marker
            + sizeof(ushort)                 // first avatar item id
            + sizeof(ushort)                 // second item-list marker
            + sizeof(ushort)                 // second avatar item id
            + sizeof(ushort)                 // option-list marker
            + sizeof(byte)                   // selected option row
            + sizeof(byte)                   // first avatar slot
            + sizeof(byte);                  // second avatar slot
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameCompoundAvatarCommand(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(10, sizeof(ushort))),
            body[14],
            body[15],
            body[16]);
        return true;
    }

    public static GameServerPacket CreateCompoundAvatarReply(
        IEnumerable<GameAvatarCompoundConsumption> consumedItems,
        ushort resultSlot,
        ushort resultItemId,
        uint remainingSeconds,
        ushort selectedOption)
    {
        var consumed = (consumedItems ?? [])
            .Take(byte.MaxValue)
            .ToArray();
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(checked((byte)consumed.Length));
        foreach (var item in consumed)
        {
            writer.Write(item.ListType);
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(resultSlot);
        writer.Write(resultItemId);
        writer.Write(remainingSeconds);
        writer.Write(selectedOption);
        return new GameServerPacket(
            CommandPacketType,
            CompoundAvatarCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCompoundAvatarFailure(byte errorCode = 4) =>
        new(
            CommandPacketType,
            CompoundAvatarCommand,
            [0, errorCode]);

    public static GameServerPacket CreateUseLotteryItemReply(
        ushort sourceSlot,
        ushort destinationSlot,
        ushort itemId,
        uint countOrValue,
        ushort instanceValue = 0,
        byte itemAttribute = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(destinationSlot);
        writer.Write(itemId);
        writer.Write(countOrValue);
        writer.Write(instanceValue);
        writer.Write(itemAttribute);
        return new GameServerPacket(
            CommandPacketType,
            UseLotteryItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateIncreaseStatusReply(
        ushort sourceSlot,
        byte statusType,
        int amount,
        ushort firstAuxiliaryValue = 0,
        ushort secondAuxiliaryValue = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(statusType);
        writer.Write(amount);
        writer.Write(firstAuxiliaryValue);
        writer.Write(secondAuxiliaryValue);
        return new GameServerPacket(
            CommandPacketType,
            IncreaseStatusCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateMailboxOpenReply(ushort notLoadedCount = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(notLoadedCount);
        return new GameServerPacket(
            CommandPacketType,
            MailboxOpenCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateMailboxExtractReply(uint mailId)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(mailId);
        return new GameServerPacket(
            CommandPacketType,
            MailboxExtractItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateMailboxStateReply(uint mailId, ushort state)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(mailId);
        writer.Write(state);
        return new GameServerPacket(
            CommandPacketType,
            MailboxStateCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateMailboxList(
        IReadOnlyList<GameMailEntry> mails,
        short notLoadedCount = 0,
        bool reset = true,
        bool includeEmptyPackageCarriers = false)
    {
        ArgumentNullException.ThrowIfNull(mails);
        if (mails.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(mails));
        }

        foreach (var mail in mails)
        {
            ValidateMailEntry(mail);
        }

        var packageMails = includeEmptyPackageCarriers
            ? mails.ToArray()
            : mails
                .Where(mail => mail.Gold != 0 || mail.Attachment is not null)
                .ToArray();

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // The first section is a reward-package list, not a mail-summary list.
        // Text-only and archived mail belongs only to the body section below.
        writer.Write((byte)packageMails.Length);
        writer.Write(reset ? (byte)0 : (byte)1);
        foreach (var mail in packageMails)
        {
            var attachment = mail.Attachment;
            writer.Write(mail.MailId);
            WriteLengthPrefixedBytes(writer, mail.SenderBytes);
            writer.Write(mail.Gold);
            writer.Write(attachment?.ItemId ?? (ushort)0);

            // DFLegacy's mailbox item tuple predates ITEM_LIST and uses a
            // different byte order: seal, instance/value, durability, state.
            writer.Write(attachment?.SealState ?? (byte)0);
            writer.Write(attachment?.CountOrValue ?? 0u);
            writer.Write(attachment?.Durability ?? (ushort)0);
            writer.Write(attachment?.State ?? (byte)0);
            writer.Write(mail.SentAt);
            writer.Write(mail.MailId); // joins the package and text records
        }

        writer.Write(notLoadedCount);

        // Every visible mail has a body record. The state selects the client's
        // inbox (1/2) or archive (3); MailId correlates it with a package above.
        writer.Write((ushort)mails.Count);
        foreach (var mail in mails)
        {
            writer.Write(mail.MailId);
            writer.Write(mail.ExpiresAt);
            WriteLengthPrefixedBytes(writer, mail.SenderBytes);
            WriteLengthPrefixedBytes(writer, mail.TextBytes);
            writer.Write(mail.SentAt);
            writer.Write(mail.State);
        }

        return new GameServerPacket(
            NotificationPacketType,
            MailboxMailListNotification,
            payload.ToArray());
    }

    public static IReadOnlyList<GameServerPacket> CreateMailboxSnapshot(
        IReadOnlyList<GameMailEntry> mails,
        short notLoadedCount = 0)
    {
        ArgumentNullException.ThrowIfNull(mails);

        var packets = new List<GameServerPacket>();
        var reset = true;

        // The old client carries one package-header id into every body in the
        // same NOTI 97. Restore archived bodies one at a time while the inbox
        // is empty so their empty carriers set that id without adding rows.
        foreach (var archivedMail in mails.Where(mail => mail.State == 3))
        {
            packets.Add(CreateMailboxList(
                [archivedMail],
                notLoadedCount,
                reset,
                includeEmptyPackageCarriers: true));
            reset = false;
        }

        var inboxMails = mails
            .Where(mail => mail.State != 3)
            .ToArray();
        if (inboxMails.Length != 0 || packets.Count == 0)
        {
            packets.Add(CreateMailboxList(
                inboxMails,
                notLoadedCount,
                reset));
        }

        return packets;
    }

    public static GameServerPacket CreateMailboxRemoveMail(
        IReadOnlyCollection<uint> mailIds)
    {
        ArgumentNullException.ThrowIfNull(mailIds);
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(checked((uint)mailIds.Count));
        foreach (var mailId in mailIds)
        {
            writer.Write(mailId);
        }

        return new GameServerPacket(
            NotificationPacketType,
            MailboxRemoveMailNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateMailboxAlarm(short newMailCount) =>
        new(
            NotificationPacketType,
            MailboxAlarmNotification,
            [(byte)newMailCount, (byte)(newMailCount >> 8)]);

    private static void ValidateMailEntry(GameMailEntry mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (mail.MailId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mail), "Mail ids must be non-zero.");
        }

        if (mail.SenderBytes.Length is 0 or >= 30)
        {
            throw new ArgumentException(
                "DFLegacy mail senders must contain 1-29 encoded bytes.",
                nameof(mail));
        }

        if (mail.TextBytes.Length >= 512)
        {
            throw new ArgumentException(
                "DFLegacy mail text must contain at most 511 encoded bytes.",
                nameof(mail));
        }

        if (mail.Attachment is { ItemId: 0 })
        {
            throw new ArgumentException(
                "Mail attachments must have a non-zero item id.",
                nameof(mail));
        }

        if (mail.State is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mail),
                "DFLegacy mail states must be received, read or archived.");
        }
    }

    public static GameServerPacket CreateSelectCharacterReply(
        PacketFrame request,
        uint characterId = 1,
        ushort serverId = 1,
        ushort usedFatigue = 0,
        ushort maximumFatigue = DefaultMaximumFatigue,
        ushort premiumFatigue = 0,
        uint cash = 0,
        byte townId = 1,
        uint completedTutorialFlags = DefaultCompletedTutorialFlags,
        IReadOnlyList<GameQuestEntry>? activeQuests = null,
        IReadOnlyList<ushort>? completedQuestIds = null,
        IReadOnlyList<GamePremiumServiceEntry>? premiumServices = null,
        IReadOnlyDictionary<ushort, int>? questCompletionMapping = null)
    {
        if (request.Type != CommandPacketType
            || request.ProtocolId != SelectCharacterCommand
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
            CommandPacketType,
            SelectCharacterCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreatePremiumServiceNotification(
        ushort action,
        byte serviceType,
        uint remainSeconds)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(action);
        writer.Write(serviceType);
        writer.Write(remainSeconds);
        return new GameServerPacket(
            NotificationPacketType,
            PremiumServiceNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        CreateChatMessageNotification(
            MessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);

    public static GameServerPacket CreateCreatureScriptMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        CreateChatMessageNotification(
            CreatureScriptMessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);

    public static GameServerPacket CreateCreatureMessageNotification(
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes) =>
        CreateChatMessageNotification(
            CreatureMessageNotification,
            messageType,
            targetAreaUserId,
            messageBytes);

    public static GameServerPacket CreateMegaphoneNotification(
        GameMessageType messageType,
        byte ownerFlag,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> senderName,
        ReadOnlySpan<byte> messageBytes)
    {
        // NOTI 130 is read by DNF.exe case 130 (sub_419420 @0x427414) with
        // the global-cursor dstr reader sub_8B3250: a length of zero or at
        // least the destination capacity leaves the cursor unadvanced and
        // shifts every later field, so the caps below are hard requirements.
        // Flag byte matching the local player routes the message to the chat
        // window; any other value renders the speaker banner (0xFF00F2FF).
        if (senderName.Length is < 1 or >= 0x1E)
        {
            throw new ArgumentOutOfRangeException(nameof(senderName));
        }

        if (messageBytes.Length is < 1 or >= 0x200)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)messageType);
        writer.Write(ownerFlag);
        writer.Write(targetAreaUserId);
        writer.Write((uint)senderName.Length);
        writer.Write(senderName);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            NotificationPacketType,
            MegaphoneMessageNotification,
            payload.ToArray());
    }

    private static GameServerPacket CreateChatMessageNotification(
        byte notificationId,
        GameMessageType messageType,
        ushort targetAreaUserId,
        ReadOnlySpan<byte> messageBytes)
    {
        // The 2008 client uses a fixed 0x100-byte destination buffer and only
        // accepts non-empty strings whose encoded length is below that size.
        // DNF.exe 0x41CA99 parses NOTI 126 and 131 through this same payload
        // layout; the notification id only selects the final display mode.
        if (messageBytes.Length is < 1 or >= 0x100)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)messageType);
        writer.Write(targetAreaUserId);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            NotificationPacketType,
            notificationId,
            payload.ToArray());
    }

    public static GameServerPacket CreatePopupNotification(
        ReadOnlySpan<byte> messageBytes)
    {
        // NOTI 126 is the legacy CREATURE_MESSAGE packet. Message type zero
        // formats the received text with client string 640 and opens popup 44.
        if (messageBytes.Length is < 1 or >= 0x100)
        {
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write((uint)messageBytes.Length);
        writer.Write(messageBytes);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureMessageNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateItemList(
        byte listType,
        IReadOnlyList<GameInventoryEntry> items,
        ushort listParameter = 0)
    {
        if (items.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(listType);
        if (listType == 2)
        {
            writer.Write(listParameter);
        }

        writer.Write((ushort)items.Count);
        foreach (var item in items)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
            writer.Write(item.SealState);
        }

        return new GameServerPacket(
            NotificationPacketType,
            ItemListNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateMainItemList(
        uint gold,
        IReadOnlyList<GameInventoryEntry> items)
    {
        return CreateMainItemList(gold, victoryPoints: 0, items);
    }

    public static GameServerPacket CreateMainItemList(
        uint gold,
        uint victoryPoints,
        IReadOnlyList<GameInventoryEntry> items)
    {
        if (items.Count > ushort.MaxValue - 2)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        // DFLegacy keeps three virtual currency objects in main-list slots 0..2:
        // gold (slot 0/item 0), revival coins (slot 1/item 1), and victory
        // points (slot 2/item 2). ITEM_LIST has no separate wallet header.
        // Revival coins remain inventory-backed, while the two account/character
        // numeric balances are synthesized here on every full main-list update.
        var entries = new GameInventoryEntry[items.Count + 2];
        entries[0] = new GameInventoryEntry(
            Slot: 0,
            ItemId: 0,
            CountOrValue: gold);
        entries[1] = new GameInventoryEntry(
            Slot: 2,
            ItemId: 2,
            CountOrValue: victoryPoints);
        for (var index = 0; index < items.Count; index++)
        {
            entries[index + 2] = items[index];
        }

        return CreateItemList(0, entries);
    }

    public static GameServerPacket CreateUpdateItemList(
        byte listType,
        IReadOnlyList<GameInventoryEntry> items)
    {
        if (items.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(items));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(listType);
        writer.Write((ushort)items.Count);
        foreach (var item in items)
        {
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrValue);
            writer.Write(item.State);
            writer.Write(item.Durability);
            writer.Write(item.SealState);
        }

        return new GameServerPacket(
            NotificationPacketType,
            UpdateItemListNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureInfo(
        IReadOnlyList<GameCreatureInfoEntry> creatures)
    {
        if (creatures.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(creatures));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(checked((byte)creatures.Count));
        foreach (var creature in creatures)
        {
            if (creature.Uid == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(creatures));
            }

            if (creature.NameBytes.Length >= 30)
            {
                throw new ArgumentOutOfRangeException(nameof(creatures));
            }

            writer.Write(creature.Uid);
            writer.Write(creature.Stomach);
            writer.Write(creature.Experience);
            writer.Write(creature.Level);
            WriteLengthPrefixedBytes(writer, creature.NameBytes);
            writer.Write((byte)(creature.NoCharge ? 1 : 0));
        }

        return new GameServerPacket(
            NotificationPacketType,
            CreatureInfoNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureState(
        uint creatureUid,
        uint state)
    {
        if (creatureUid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(creatureUid));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(creatureUid);
        writer.Write(state);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureStateNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateDiedCreature(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // NOTI 100 is not keyed by the Creature uid.  The old client first
        // resolves this area-user id and then marks that user's Creature map
        // object as dead.
        writer.Write(userId);
        return new GameServerPacket(
            NotificationPacketType,
            DiedCreatureNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateRevivalCreature(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // NOTI 107 has the same one-field layout as NOTI 100.  It clears the
        // death flag on the resolved Creature object.
        writer.Write(userId);
        return new GameServerPacket(
            NotificationPacketType,
            RevivalCreatureNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureGainExperience(
        byte level,
        uint cumulativeExperience)
    {
        if (level == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        // DF2008 NOTI 102 has no Creature uid or normal/grow discriminator.
        // The client applies this fixed record to the currently equipped pet.
        writer.Write(level);
        writer.Write(cumulativeExperience);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureGainExperienceNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureEvolution(
        byte evolutionCreatureId,
        ushort userId)
    {
        if (evolutionCreatureId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evolutionCreatureId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(evolutionCreatureId);
        writer.Write(userId);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureEvolutionNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureRename(
        ushort userId,
        byte[] nameBytes)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (nameBytes.Length is 0 or >= 20)
        {
            throw new ArgumentOutOfRangeException(nameof(nameBytes));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        WriteLengthPrefixedBytes(writer, nameBytes);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureRenameNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateCreatureResponse(ushort userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        return new GameServerPacket(
            NotificationPacketType,
            CreatureResponseNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateHatchCreatureReply(
        bool success,
        byte errorCode = 1) =>
        new(
            CommandPacketType,
            HatchCreatureCommand,
            success ? [1] : [0, errorCode]);

    public static GameServerPacket CreateRenameCreatureReply(
        bool success,
        ushort cardSlot = 0,
        byte itemSpace = 7,
        byte errorCode = 1)
    {
        if (!success)
        {
            return new(
                CommandPacketType,
                RenameCreatureCommand,
                [0, errorCode]);
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(cardSlot);
        writer.Write(itemSpace);
        return new GameServerPacket(
            CommandPacketType,
            RenameCreatureCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateMoveItemSpaceReply(
        byte sourceListType,
        ushort sourceSlot,
        uint movedCount,
        byte destinationListType,
        ushort destinationSlot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(sourceListType);
        writer.Write(sourceSlot);
        writer.Write(movedCount);
        writer.Write(destinationListType);
        writer.Write(destinationSlot);

        return new GameServerPacket(
            CommandPacketType,
            MoveItemSpaceCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateSortItemReply(byte itemSpace) =>
        new(CommandPacketType, SortItemCommand, [1, itemSpace]);

    public static GameServerPacket CreateSortItemError(
        byte itemSpace,
        byte errorCode = 1) =>
        new(CommandPacketType, SortItemCommand, [0, errorCode, itemSpace]);

    public static GameServerPacket CreateBuyItemReply(
        uint updatedGold,
        uint updatedVictoryPoints,
        uint auxiliaryItemSpaceValue,
        uint updatedCera,
        ushort slot,
        ushort itemId,
        uint countOrValue)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        // DF2008's command-21 success handler reads four complete wallet values
        // before the purchased item tuple. In particular, the fourth u32 is
        // copied into the Cera balance. A shorter reply makes the client read
        // its ASCII-'0' receive-buffer padding as 0x30303030 (808464432 Cera).
        writer.Write(updatedGold);
        writer.Write(updatedVictoryPoints);
        writer.Write(auxiliaryItemSpaceValue);
        writer.Write(updatedCera);
        writer.Write(slot);
        writer.Write(itemId);
        writer.Write(countOrValue);
        return new GameServerPacket(
            CommandPacketType,
            BuyItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCeraShopBuyReply(
        int shopCategory,
        uint commodityNo,
        IReadOnlyList<GameCeraShopReward>? rewards = null)
    {
        rewards ??= [];
        if (rewards.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rewards),
                "DF2008 command 67 supports at most 65535 result entries.");
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write(Math.Clamp(shopCategory, 0, 3));
        writer.Write(commodityNo);
        // The DF2008 client keys its Cera result-item map by these values.
        // Keeping them equal also selects the normal purchase-completion path.
        writer.Write(commodityNo);
        writer.Write(commodityNo);
        writer.Write(ushort.MaxValue);
        writer.Write((ushort)rewards.Count);
        foreach (var reward in rewards)
        {
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }

        writer.Write(ushort.MaxValue);
        writer.Write(0u);
        return new GameServerPacket(
            CommandPacketType,
            CeraShopBuyCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCeraShopBuyError(
        byte errorCode,
        int shopCategory = 0,
        uint commodityNo = uint.MaxValue)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)0);
        writer.Write(errorCode);
        writer.Write(Math.Clamp(shopCategory, 0, 3));
        writer.Write(commodityNo);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        return new GameServerPacket(
            CommandPacketType,
            CeraShopBuyCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCeraTicketReply(
        string billingKey,
        uint ticketNumber = 0)
    {
        var keyBytes = System.Text.Encoding.ASCII.GetBytes(billingKey);
        if (keyBytes.Length is 0 or >= 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(billingKey),
                "The DFLegacy Cera ticket must contain 1 to 63 ASCII bytes.");
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)keyBytes.Length);
        writer.Write(keyBytes);
        writer.Write(ticketNumber);
        return new GameServerPacket(
            CommandPacketType,
            GenerateCeraTicketCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateCeraUpdate(bool success, uint value)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(success ? (byte)1 : (byte)0);
        writer.Write(value);
        return new GameServerPacket(
            NotificationPacketType,
            CeraUpdateNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateSellItemReply(
        uint updatedGold,
        byte itemSpace,
        ushort slot,
        ushort soldCount)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(updatedGold);
        writer.Write(itemSpace);
        writer.Write(slot);
        writer.Write(soldCount);
        return new GameServerPacket(
            CommandPacketType,
            SellItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateRepairEquipmentReply(
        uint updatedGold,
        byte itemSpace,
        ushort slot)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(updatedGold);
        writer.Write(itemSpace);
        writer.Write(slot);
        return new GameServerPacket(
            CommandPacketType,
            RepairEquipmentCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateDecreaseDurabilityReply(byte slot) =>
        new(
            CommandPacketType,
            DecreaseDurabilityCommand,
            [1, slot]);

    public static bool TryParseDropItemCommand(
        ReadOnlySpan<byte> body,
        out GameDropItemCommand command)
    {
        command = default;
        const int bodyLength = sizeof(ushort) // command sequence
            + sizeof(short)                  // map x
            + sizeof(short)                  // map y
            + sizeof(byte)                   // item space
            + sizeof(ushort)                 // source slot
            + sizeof(uint);                  // count
        if (body.Length != bodyLength)
        {
            return false;
        }

        command = new GameDropItemCommand(
            BinaryPrimitives.ReadInt16LittleEndian(body.Slice(2, sizeof(short))),
            BinaryPrimitives.ReadInt16LittleEndian(body.Slice(4, sizeof(short))),
            body[6],
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(7, sizeof(ushort))),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(9, sizeof(uint))));
        return command.Count > 0;
    }

    public static GameServerPacket CreateDropItemReply(
        byte itemSpace,
        ushort slot,
        uint count)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(itemSpace);
        writer.Write(slot);
        writer.Write(count);
        return new GameServerPacket(
            CommandPacketType,
            DropItemCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateDropItemErrorReply(
        byte errorCode,
        byte itemSpace) =>
        new(
            CommandPacketType,
            DropItemCommand,
            [0, errorCode, itemSpace]);

    public static GameServerPacket CreateSetItemTradeStateReply(
        ushort itemId,
        byte listType)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(itemId);
        writer.Write(listType);
        writer.Write((byte)0);
        return new GameServerPacket(
            CommandPacketType,
            SetItemTradeStateCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateChangeSkillSlotReply(
        byte sourceSlot,
        byte destinationSlot) =>
        new(
            CommandPacketType,
            ChangeSkillSlotCommand,
            [1, sourceSlot, destinationSlot]);

    public static GameServerPacket CreateBuySkillReply(
        ushort remainingSkillPoints,
        byte slot,
        byte skillId,
        byte level)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(remainingSkillPoints);
        writer.Write(slot);
        writer.Write(skillId);
        writer.Write(level);
        return new GameServerPacket(CommandPacketType, BuySkillCommand, payload.ToArray());
    }

    public static GameServerPacket CreateSkillInfo(
        ushort skillPoints,
        IReadOnlyList<GameSkillEntry> skills)
    {
        if (skills.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(skills));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(skillPoints);
        writer.Write((byte)skills.Count);
        for (var index = 0; index < skills.Count; index++)
        {
            var skill = skills[index];
            // DNF 1.0.1.9 protocol 19 records are slot/id/level triples.
            // When no stable UI slot is persisted yet, assign compact slots in
            // packet order. The learned-skill list in USERINFO remains id/level.
            writer.Write(skill.Slot ?? checked((byte)index));
            writer.Write(skill.SkillId);
            writer.Write(skill.Level);
        }

        writer.Write((byte)0); // no per-skill auxiliary values
        return new GameServerPacket(
            NotificationPacketType,
            SkillInfoNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateStamina(byte value) =>
        new(NotificationPacketType, StaminaNotification, [value]);

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
            NotificationPacketType,
            PvpRecordNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateFailClearDungeon(byte stamina) =>
        new(NotificationPacketType, FailClearDungeonNotification, [stamina]);

    public static GameServerPacket CreateRecoverStaminaReply(int remainingGold)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write((uint)Math.Max(0, remainingGold));
        return new GameServerPacket(
            CommandPacketType,
            RecoverStaminaCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateRecoverStaminaError(byte errorCode) =>
        new(
            CommandPacketType,
            RecoverStaminaCommand,
            [0, errorCode]);

    public static GameServerPacket CreateDieState(ushort userId, bool isAlive) =>
        new(
            NotificationPacketType,
            DieStateNotification,
            [(byte)userId, (byte)(userId >> 8), isAlive ? (byte)1 : (byte)0]);

    public static GameServerPacket CreateFatigue(
        ushort usedFatigue = 0,
        ushort maximumFatigue = DefaultMaximumFatigue,
        ushort premiumFatigue = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(usedFatigue);
        writer.Write(maximumFatigue);
        writer.Write(premiumFatigue);
        return new GameServerPacket(
            NotificationPacketType,
            FatigueNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateEventInfo(uint featureFlags = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(featureFlags);
        return new GameServerPacket(
            NotificationPacketType,
            EventInfoNotification,
            payload.ToArray());
    }

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
            NotificationPacketType,
            DungeonPermissionNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateUserState(ushort userId, byte state) =>
        new(
            NotificationPacketType,
            UserStateNotification,
            [(byte)userId, (byte)(userId >> 8), state]);

    public static GameServerPacket CreateAcceptableQuestList(
        IReadOnlyList<ushort> questIds)
    {
        if (questIds.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(questIds));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)questIds.Count);
        foreach (var questId in questIds)
        {
            writer.Write(questId);
        }

        return new GameServerPacket(
            NotificationPacketType,
            AcceptableQuestListNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateEmptyAcceptableQuestList() =>
        CreateAcceptableQuestList([]);

    public static GameServerPacket CreateAcceptQuestReply(
        ushort questId,
        uint trigger = 0,
        IReadOnlyList<GameQuestInsertedItem>? insertedItems = null)
    {
        // DF2008 0x4133D1 reads quest:u16, trigger:u32, grants:u8.
        // 0x4EC960 hides the script's [delete npc index] when trigger != 0;
        // CMD 35 trigger 0 restores it through 0x4EBB60. No extra NPC packet.
        var inserted = insertedItems ?? [];
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(trigger);
        writer.Write(checked((byte)Math.Min(byte.MaxValue, inserted.Count)));
        foreach (var item in inserted.Take(byte.MaxValue))
        {
            // DF2008 CMD 33 reads slot, item ID, then the inserted count/add_info.
            writer.Write(item.Slot);
            writer.Write(item.ItemId);
            writer.Write(item.CountOrSeed);
        }

        return new GameServerPacket(CommandPacketType, AcceptQuestCommand, payload.ToArray());
    }

    public static GameServerPacket CreateGiveupQuestReply(ushort questId)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        return new GameServerPacket(CommandPacketType, GiveupQuestCommand, payload.ToArray());
    }

    public static GameServerPacket CreateSetQuestTriggerReply(
        ushort questId,
        uint trigger)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write(trigger);
        return new GameServerPacket(CommandPacketType, SetQuestTriggerCommand, payload.ToArray());
    }

    public static GameServerPacket CreateFinishQuestReply(
        ushort questId,
        IReadOnlyList<GameQuestConsumedItem>? consumedItems = null,
        IReadOnlyList<GameQuestInsertedItem>? insertedItems = null,
        byte chainType = 0,
        byte growNumber = 0,
        IReadOnlyList<GameQuestGrowthSkillChange>? growthSkillChanges = null)
    {
        if (chainType is not 0 and not 1 and not 2)
        {
            throw new ArgumentOutOfRangeException(nameof(chainType));
        }

        var consumed = consumedItems ?? [];
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(questId);
        writer.Write((byte)0); // completion type
        writer.Write(checked((byte)Math.Min(byte.MaxValue, consumed.Count)));
        foreach (var item in consumed.Take(byte.MaxValue))
        {
            writer.Write((byte)0); // inventory update type
            writer.Write(item.Slot);
            writer.Write(item.RemainingCount);
        }

        writer.Write(chainType);
        if (chainType == 0)
        {
            var inserted = insertedItems ?? [];
            writer.Write(checked((byte)Math.Min(byte.MaxValue, inserted.Count)));
            foreach (var item in inserted.Take(byte.MaxValue))
            {
                writer.Write(item.Slot);
                writer.Write(item.ItemId);
                writer.Write(item.CountOrSeed);
            }
        }
        else
        {
            writer.Write(growNumber);
            var changes = (growthSkillChanges ?? [])
                .Where(change => change.SkillClass < GrowthSkillClassCount)
                .Take(byte.MaxValue)
                .ToArray();
            writer.Write(checked((byte)changes.Length));
            foreach (var change in changes)
            {
                // DF2008 CMD 36 reads each change as class, skill ID, then level.
                writer.Write(change.SkillClass);
                writer.Write(change.SkillId);
                writer.Write(change.Level);
            }
        }

        return new GameServerPacket(CommandPacketType, FinishQuestCommand, payload.ToArray());
    }

    public static GameServerPacket CreateUserPosition(
        GameTownUser user,
        ushort movementValue = 100)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(user.UserId);
        writer.Write(user.X);
        writer.Write(user.Y);
        writer.Write(user.Direction);
        writer.Write(movementValue);
        return new GameServerPacket(
            NotificationPacketType,
            UserPositionNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateUserArea(
        byte townId,
        byte areaId,
        GameTownUser user)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(user.UserId);
        writer.Write(townId);
        writer.Write(areaId);
        writer.Write(user.X);
        writer.Write(user.Y);
        writer.Write(user.Direction);
        writer.Write((byte)1); // active town actor
        return new GameServerPacket(
            NotificationPacketType,
            UserAreaNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateAreaUsers(
        byte townId,
        byte areaId,
        IReadOnlyList<GameTownUser> users)
    {
        if (users.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(users));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(townId);
        writer.Write(areaId);
        writer.Write((ushort)users.Count);
        foreach (var user in users)
        {
            writer.Write(user.UserId);
            writer.Write(user.X);
            writer.Write(user.Y);
            writer.Write(user.Direction);
            writer.Write((byte)1); // active town actor
        }

        return new GameServerPacket(
            NotificationPacketType,
            AreaUsersNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateUdpPeerInfo(
        IReadOnlyList<GamePeerInfo> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (peers.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(peers));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)peers.Count);
        foreach (var peer in peers)
        {
            writer.Write(peer.UserId);
            WriteIpv4Address(writer, peer.LocalAddress);
            WriteIpv4Address(writer, peer.PublicAddress);
            writer.Write(peer.PublicPort);
            writer.Write(peer.AccountId);
            writer.Write(peer.NatType);
            writer.Write(peer.Mtu);
        }

        return new GameServerPacket(
            NotificationPacketType,
            UdpPeerInfoNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateUdpHost(byte partyIndex = 0)
    {
        if (partyIndex >= 8)
        {
            throw new ArgumentOutOfRangeException(nameof(partyIndex));
        }

        return new GameServerPacket(
            NotificationPacketType,
            UdpHostNotification,
            [partyIndex]);
    }

    /// <summary>
    /// PARTY_INFO (NOTI 9) uses a block list in this build: u16 block count,
    /// then per block u16 party id and u8 type. Type 0/1 carry the settings
    /// pair the client reads at 0x41B8F9 (u8 title kind, u8 user max — kind-0
    /// titles render from the client's local string 0x272, never from the
    /// wire); type 0/2 carry four roster slots of u16 uid + u8 state plus
    /// the leader slot tail byte (reader at 0x41B9CD, leader store at
    /// 0x41BAD7); type 3 is the five-byte clear header only. There is no
    /// trailing has-extra byte in this build.
    /// </summary>
    public static GameServerPacket CreatePartyInfo(
        IReadOnlyList<PartyInfoWireBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(blocks));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((ushort)blocks.Count);
        foreach (var block in blocks)
        {
            writer.Write((ushort)block.PartyId);
            writer.Write(block.BlockType);
            if (block.BlockType is 0 or 1)
            {
                WritePartySettings(writer, block.TitleKind, block.UserMax);
            }

            if (block.BlockType is 0 or 2)
            {
                if (block.Members.Count > 4)
                {
                    throw new ArgumentOutOfRangeException(nameof(blocks));
                }

                for (var slot = 0; slot < 4; slot++)
                {
                    if (slot < block.Members.Count)
                    {
                        writer.Write(block.Members[slot].UserId);
                        writer.Write(block.Members[slot].State);
                    }
                    else
                    {
                        // The client renders 0xFFFF slots as empty (cleared
                        // slots are written as 0xFFFF at 0x41BB1C).
                        writer.Write(PartyEmptySlotUserId);
                        writer.Write((byte)0);
                    }
                }

                writer.Write(block.LeaderSlot);
            }
        }

        return new GameServerPacket(
            NotificationPacketType,
            PartyInfoNotification,
            payload.ToArray());
    }

    /// <summary>Single-block PARTY_INFO type 3: clears this party id.</summary>
    public static GameServerPacket CreatePartyClear(int partyId) =>
        CreatePartyInfo(
        [
            new PartyInfoWireBlock(
                partyId,
                BlockType: 3,
                TitleKind: 0,
                UserMax: 0,
                Members: [],
                LeaderSlot: 0),
        ]);

    private static void WritePartySettings(BinaryWriter writer, byte titleKind, byte userMax)
    {
        // Downstream settings are the bare two-byte pair (reader 0x41B8F9):
        // the client loads its local default-title string 0x272 for kind 0
        // and leaves the title empty otherwise — the u32-prefixed dstr only
        // exists on the uplink SET_PARTY_INFO writer (0x7D5F86). Emitting it
        // here desynchronises the reader: userMax and every roster slot
        // shift by the four length bytes.
        writer.Write(titleKind);
        writer.Write(userMax);
    }

    /// <summary>
    /// NOTI 7 invite popup. The reader at 0x41B4C8 consumes u16 uid, u8 type,
    /// u32 peer id and, for type 0, two more u16 state words.
    /// </summary>
    public static GameServerPacket CreatePeerRequest(
        ushort fromUserId,
        byte requestType,
        uint peerId,
        ushort fromUserState = 0,
        ushort fromUserSecondaryState = 0)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(fromUserId);
        writer.Write(requestType);
        writer.Write(peerId);
        if (requestType == 0)
        {
            writer.Write(fromUserState);
            writer.Write(fromUserSecondaryState);
        }

        return new GameServerPacket(
            NotificationPacketType,
            PeerRequestNotification,
            payload.ToArray());
    }

    /// <summary>
    /// NOTI 8 peer response ack (reader at 0x41B5F1): u16 uid, u8 type,
    /// u32 value. The type-0 zero-value form is the party accept the inviter
    /// needs to claim the party before the PARTY_INFO formation arrives.
    /// </summary>
    public static GameServerPacket CreatePeerResponse(
        ushort fromUserId,
        byte responseType,
        uint value)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(fromUserId);
        writer.Write(responseType);
        writer.Write(value);
        return new GameServerPacket(
            NotificationPacketType,
            PeerResponseNotification,
            payload.ToArray());
    }

    /// <summary>
    /// NOTI 10 departure notice (reader at 0x41C41A): u8 uid, u8 reason
    /// 0..9. Reason 0/1 render the left/kicked party chat lines.
    /// </summary>
    public static GameServerPacket CreateWalkout(ushort userId, byte reason)
    {
        // The wire uid is u8 here (reader 0x41C42F); larger ids truncate for
        // the matching lookup, which only affects the departure chat line.
        return new GameServerPacket(
            NotificationPacketType,
            WalkoutNotification,
            [(byte)userId, reason]);
    }

    /// <summary>NOTI 6 removes the town user record for the uid (reader at
    /// 0x41B3C8 drops the map entry at 0x41B467 and cleans the scene).</summary>
    public static GameServerPacket CreateUserLeave(ushort userId)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, userId);
        return new GameServerPacket(
            NotificationPacketType,
            UserLeaveNotification,
            payload);
    }

    /// <summary>
    /// Parses SET_PARTY_INFO (CMD 12, writer at 0x7D5F86): after the two-byte
    /// sequence number the client writes u8 title kind, a u32-prefixed custom
    /// title when the kind is zero, and u8 user max (radio index + 2).
    /// </summary>
    public static bool TryParseSetPartyInfo(
        ReadOnlySpan<byte> body,
        out PartySettingsRequest request)
    {
        request = new PartySettingsRequest(0, [], 0);
        // The block runs for every party command (10-14); short bodies such
        // as WALKOUT's three bytes must fall through to the dispatch switch
        // instead of throwing here.
        if (body.Length < 3)
        {
            return false;
        }

        var titleKind = body[2];
        if (titleKind != 0)
        {
            if (body.Length < 4)
            {
                return false;
            }

            request = new PartySettingsRequest(
                titleKind,
                [],
                body[3]);
            return true;
        }

        if (body.Length < 8)
        {
            return false;
        }

        var titleLength = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(3, 4));
        if (titleLength >= 0x40 || body.Length < 8 + titleLength)
        {
            return false;
        }

        var titleBytes = body.Slice(7, (int)titleLength).ToArray();
        request = new PartySettingsRequest(
            titleKind,
            titleBytes,
            body[7 + (int)titleLength]);
        return true;
    }

    /// <summary>Parses REQUEST_PEER (CMD 10): u16 target uid, u8 type, u32 id.</summary>
    public static bool TryParseRequestPeer(
        ReadOnlySpan<byte> body,
        out PeerRequestFrame request)
    {
        request = new PeerRequestFrame(0, 0, 0);
        if (body.Length < 9)
        {
            return false;
        }

        request = new PeerRequestFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
            body[4],
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(5, 4)));
        return true;
    }

    /// <summary>
    /// Parses RESPONSE_PEER (CMD 11): u16 peer uid, u8 type, u32 value. The
    /// client accept path writes exactly seven payload bytes with a zero type
    /// and value (0x4FEBC0); refusals set a non-zero type or value.
    /// </summary>
    public static bool TryParseResponsePeer(
        ReadOnlySpan<byte> body,
        out PeerResponseFrame response)
    {
        response = new PeerResponseFrame(0, 0, 0);
        if (body.Length < 9)
        {
            return false;
        }

        response = new PeerResponseFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
            body[4],
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(5, 4)));
        return true;
    }

    public static bool IsAcceptedPeerResponse(PeerResponseFrame response) =>
        response.ResponseType == 0 && response.Value == 0;

    /// <summary>
    /// Party command failure replies are not the generic [0, code] form: the
    /// reply dispatcher reads a result byte first (zero = failure) and then
    /// one error byte at payload[1] for SET_PARTY_INFO (code 8 pops a party
    /// message, case 0x410799), LEAVE_PARTY (code 0x12, case 0x4107D8) and
    /// WALKOUT (code 4, case 0x410834). The trailing zero keeps the global
    /// read cursor inside the payload.
    /// </summary>
    public static GameServerPacket CreatePartyCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            CommandPacketType,
            request.ProtocolId,
            [0, errorCode, 0, 0]);

    /// <summary>The peer commands read their error byte at payload[2]
    /// instead: REQUEST_PEER pops a message on code 5 (case 0x4102F6) and
    /// RESPONSE_PEER on code 1 (case 0x4104FF).</summary>
    public static GameServerPacket CreatePeerCommandError(
        PacketFrame request,
        byte errorCode) =>
        new(
            CommandPacketType,
            request.ProtocolId,
            [0, 0, errorCode, 0]);

    /// <summary>Parses WALKOUT_PARTY_MEMBER (CMD 14, writer at 0x8520F3):
    /// one byte holding the selected member's roster slot index.</summary>
    public static bool TryParseWalkoutPartyMember(
        ReadOnlySpan<byte> body,
        out byte targetSlot)
    {
        targetSlot = 0;
        if (body.Length < 3)
        {
            return false;
        }

        targetSlot = body[2];
        return targetSlot < 4;
    }

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

        return new(NotificationPacketType, EnterSelectDungeonNotification, payload.ToArray());
    }

    public static GameServerPacket CreateTutorialDungeonInfo(byte difficulty) =>
        CreateDungeonInfo(
            TutorialDungeonId,
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
            NotificationPacketType,
            DungeonInfoNotification,
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
            NotificationPacketType,
            StartMapNotification,
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
            NotificationPacketType,
            StartMapNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateEmptyStartMap(
        byte roomX,
        byte roomY,
        ushort mapId,
        uint randomSeed = 0) =>
        CreateStartMap(roomX, roomY, mapId, [], randomSeed: randomSeed);

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
        CreateStartMap(
            roomX: 0,
            roomY: 0,
            mapId: TutorialMapId,
            monsters: CreateTutorialMonsters());

    public static bool IsTutorialCompletionModule(ReadOnlySpan<byte> body) =>
        body.Length >= 6
        && BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(2, sizeof(uint)))
            == TutorialCompletionModule;

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

        // Captured DFLegacy command-42 bodies are 23 bytes. The final byte is
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
            CommandPacketType,
            UseCoinCommand,
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
                NotificationPacketType,
                BossDieCheckNotification,
                [0, errorCode]);
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        writer.Write(allBossesReported ? (byte)1 : (byte)0);
        writer.Write(bossId);
        return new GameServerPacket(
            NotificationPacketType,
            BossDieCheckNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateFinishLoading() =>
        new(NotificationPacketType, FinishLoadingNotification, []);

    public static GameServerPacket CreateEnterGameWorldComplete() =>
        new(NotificationPacketType, EnterGameWorldCompleteNotification, []);

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
            NotificationPacketType,
            ExperienceGainedNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateMonsterDie(
        ushort uniqueId,
        IReadOnlyList<GameDungeonDrop>? drops = null,
        sbyte championRewardOrdinal = NoChampionRewardOrdinal)
    {
        drops ??= [];
        if (drops.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(drops));
        }

        if (championRewardOrdinal != NoChampionRewardOrdinal
            && (championRewardOrdinal < 1
                || championRewardOrdinal > MaximumChampionRewardOrdinal))
        {
            throw new ArgumentOutOfRangeException(nameof(championRewardOrdinal));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(uniqueId);
        writer.Write((byte)drops.Count);
        foreach (var drop in drops)
        {
            // DFLegacy's compact death record is ground id, item id, add-info,
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
            NotificationPacketType,
            MonsterDieNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateDropItem(
        ushort userId,
        short x,
        short y,
        GameDungeonDrop drop)
    {
        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write(userId);
        writer.Write(x);
        writer.Write(y);
        writer.Write(drop.GroundId);
        writer.Write(drop.ItemId);
        // DFLegacy NOTI 40 does not reuse the five-field record embedded in
        // NOTI 38. The client reads item attr as a byte before add-info and
        // does not read an owner id at the end of this notification.
        writer.Write(drop.ItemAttr);
        writer.Write(drop.AddInfo);
        writer.Write(drop.Durability);
        return new GameServerPacket(
            NotificationPacketType,
            DropItemNotification,
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
            NotificationPacketType,
            GetItemNotification,
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
            NotificationPacketType,
            GetItemNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateEnableClearDungeon() =>
        new(NotificationPacketType, EnableClearDungeonNotification, []);

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
            NotificationPacketType,
            PlayResultNotification,
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
        if (localPartyIndex >= ClearRewardCardColumnCount)
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

        // DFLegacy increments its visible-card count for every non-empty entry
        // in this first group. Keep all four columns present even in solo play.
        for (var cardIndex = 0;
             cardIndex < ClearRewardCardColumnCount;
             cardIndex++)
        {
            WriteFreeClearRewardPreview(
                writer,
                cardExists: true,
                freeItemId,
                freeGoldAmount);
        }

        writer.Write(goldCardEnabled ? goldCardCost : 0u);

        // The paid-card result remains hidden in NOTI 35. It is revealed by
        // SELECT_CARD's group-3 result list only after the player buys a card.
        for (var partyIndex = 0;
             partyIndex < ClearRewardCardColumnCount;
             partyIndex++)
        {
            writer.Write((byte)0);
        }

        for (var partyIndex = 0;
             partyIndex < ClearRewardCardColumnCount;
             partyIndex++)
        {
            WriteEquipmentRewardPreview(
                writer,
                premiumCardEnabled && partyIndex == localPartyIndex,
                premiumItemId);
        }

        return new GameServerPacket(
            NotificationPacketType,
            ClearDungeonRewardNotification,
            payload.ToArray());
    }

    public static GameServerPacket CreateScoreScrollStateReply() =>
        new(CommandPacketType, ScoreScrollStateCommand, [1]);

    public static GameServerPacket CreateCardSelectRightState(
        bool localUserCanSelect = true,
        byte localPartyIndex = 0)
    {
        if (localPartyIndex >= ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        for (var partyIndex = 0;
             partyIndex < ClearRewardCardColumnCount;
             partyIndex++)
        {
            writer.Write(partyIndex == localPartyIndex
                ? (ushort)(localUserCanSelect ? 1 : 0)
                : ushort.MaxValue);
        }

        return new GameServerPacket(
            CommandPacketType,
            CardSelectRightStateCommand,
            payload.ToArray());
    }

    public static GameServerPacket CreateSelectCardReply(
        byte? freeCardIndex,
        byte? goldCardIndex,
        bool goldRewardRevealed = false,
        ushort? goldRewardItemId = null,
        byte localPartyIndex = 0)
    {
        if (freeCardIndex >= ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(freeCardIndex));
        }

        if (goldCardIndex >= ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(goldCardIndex));
        }

        if (localPartyIndex >= ClearRewardCardColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPartyIndex));
        }

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        writer.Write((byte)1);
        for (byte cardIndex = 0;
             cardIndex < ClearRewardCardColumnCount;
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
            CommandPacketType,
            SelectCardCommand,
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
        new(CommandPacketType, EplpCommand, [1, state, option]);

    public static GameServerPacket CreateCheckConnection(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("The check-connection challenge must be exactly 16 bytes.", nameof(challenge));
        }

        return new GameServerPacket(
            NotificationPacketType,
            CheckConnectionNotification,
            challenge.ToArray());
    }

    public static GameServerPacket CreateEmptyUserInfo()
    {
        return CreateUserInfo([]);
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
            WriteLengthPrefixedBytes(writer, character.NameBytes);
            writer.Write(character.Job);
            writer.Write(PackGrowthType(character));
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
            NotificationPacketType,
            UserInfoNotification,
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
        WriteLengthPrefixedBytes(writer, character.NameBytes);
        writer.Write(character.Job);
        writer.Write(PackGrowthType(character));
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
        WriteLengthPrefixedBytes(writer, equippedCreature?.NameBytes ?? []);
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
        WriteLengthPrefixedBytes(writer, []); // guild name
        writer.Write((byte)1);   // active entity
        writer.Write(0u);        // actor state A
        writer.Write(0u);        // actor state B
        writer.Write(0u);        // actor state C

        return new GameServerPacket(
            NotificationPacketType,
            UserInfoNotification,
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
            NotificationPacketType,
            UserInfoNotification,
            payload.ToArray());
    }

    private static void WriteLengthPrefixedBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write((uint)value.Length);
        writer.Write(value);
    }

    private static byte PackGrowthType(GameCharacterSummary character)
    {
        // DF2008 stores the first growth branch in bits 0-3 and the awakening
        // stage in bits 4-6 of the same USERINFO byte.
        return (byte)((character.GrowType & 0x0F)
            | ((character.AwakeningType & 0x07) << 4));
    }

    private static void WriteIpv4Address(BinaryWriter writer, System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.MapToIPv4().GetAddressBytes();
        writer.Write(bytes);
    }

    public static GameServerPacket CreateChannelInfo(
        ReadOnlySpan<byte> displayName,
        uint channelType,
        uint result,
        byte serverId,
        byte channelNumber,
        uint seed,
        IReadOnlyList<byte[]> hosts,
        uint port1,
        uint port2)
    {
        if (displayName.Length >= 0x40)
        {
            throw new ArgumentException("The DFLegacy channel display name must be shorter than 64 bytes.", nameof(displayName));
        }

        ArgumentNullException.ThrowIfNull(hosts);

        using var payload = new MemoryStream();
        using var writer = new BinaryWriter(payload);
        WriteLengthPrefixedBytes(writer, displayName.ToArray());
        writer.Write(channelType);
        writer.Write(result);
        writer.Write(serverId);
        writer.Write(channelNumber);
        writer.Write(seed);
        writer.Write((uint)hosts.Count);
        foreach (var host in hosts)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (host.Length >= 0x104)
            {
                throw new ArgumentException("A DFLegacy channel host must be shorter than 260 bytes.", nameof(hosts));
            }

            WriteLengthPrefixedBytes(writer, host);
        }

        writer.Write(port1);
        writer.Write(port2);
        return new GameServerPacket(
            NotificationPacketType,
            ChannelInfoNotification,
            payload.ToArray());
    }
}
