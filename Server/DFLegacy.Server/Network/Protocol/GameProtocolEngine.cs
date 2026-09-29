using System.Buffers.Binary;
using DFLegacy.Server;

namespace DFLegacy.Protocol;

public static class GameProtocolEngine
{

    public const int ClearRewardCardColumnCount = 4;
    public const byte NotificationPacketType = 0;
    // NOTI 7/8/9/10 are the party block: a 13-byte invite popup (case
    // 0x41B4C8), the 7-byte accept ack (case 0x41B5F1), the slot-diff party
    // roster (case 0x41B7A1) and the two-byte departure message (case
    // 0x41C41A) in DNF.exe 60A1.
    // NOTI 38's compact trailer is [reserved, champion ordinal, reserved].
    // The 13338 client uses ordinal 1..11 to play the Champion/SuperChampion
    // clear-reward animation before the card screen; 255 means no such reward.
    public const sbyte NoChampionRewardOrdinal = -1;
    public const sbyte MaximumChampionRewardOrdinal = 11;
    // 13338's notification dispatcher case 48 reads five little-endian
    // int32 values followed by two bytes: win, lose, PvP points, current
    // rank point, next rank point, grade and grade-extension.
    // DF2008 reads both Creature life notifications as one u16 area-user id.
    // The 13339 server names these DIED_CREATURE and REVIVAL_CREATURE.
    public const byte CommandPacketType = 1;
    // CMD 10-14 are the party block: type-0 peer requests are the party
    // invite/join path of this build (RESPONSE_PEER 11 answers them;
    // SET_PARTY_INFO 12, LEAVE_PARTY 13 and the slot-indexed kick 14
    // complete it, matching PacketNames.cs and the 60CN dispatcher).
    public const byte DungeonItemActionCommand = (byte)CmdPacketType.USE_STACKABLE;
    // 60CN ENUM_CMDPACKET_ANTIBOT: fire-and-forget client health report.
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
            (byte)CmdPacketType.CHECK_CONNECTION when challenge.Length == 16 => ConnectionPacketBuilders.CreateChannelInfo(
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
            (byte)CmdPacketType.LOGIN => new GameServerPacket(
                CommandPacketType,
                (byte)CmdPacketType.LOGIN,
                [1, 0, 0, 0, 0]),
            // The 1.0.1.9 client clears a possible old game session before it
            // begins the selected-channel login flow. A one-byte true result is
            // the complete successful response consumed by its EXIT handler.
            (byte)CmdPacketType.EXIT => ConnectionPacketBuilders.CreateCommandReply(request, success: true),
            // SELECT_CHARACTER uses a larger acknowledgement than the other
            // character-screen commands. It supplies the small account/town
            // state block consumed before the client leaves character select.
            (byte)CmdPacketType.SELECT_CHARACTER => CharactersPacketBuilders.CreateSelectCharacterReply(request),
            // CREATE_CHARACTER also consumes the command's generic one-byte
            // result. A successful result closes the creation panel and makes
            // this client refresh USERINFO.
            (byte)CmdPacketType.CREATE_CHARACTER => ConnectionPacketBuilders.CreateCommandReply(request, success: true),
            // DELETE_CHARACTER success carries the deleted character slot
            // after the common result byte. The client uses that slot to
            // remove the matching character card.
            (byte)CmdPacketType.DELETE_CHARACTER when request.Body.Length >= 3 =>
                CharactersPacketBuilders.CreateDeleteCharacterReply(request.Body[2]),
            // RETURN_SELECT_CHARACTER acknowledges the town-side transition.
            // The channel service follows this reply with a fresh USERINFO
            // roster after it has persisted and detached the active character.
            (byte)CmdPacketType.RETURN_SELECT_CHARACTER => ConnectionPacketBuilders.CreateCommandReply(request, success: true),
            // The dungeon USE_SKILL command only needs the common success
            // acknowledgement. Its cube-item transaction is completed by a
            // following UPDATE_ITEM_LIST notification from the channel.
            (byte)CmdPacketType.USE_SKILL => DungeonPacketBuilders.CreateUseSkillReply(),
            // GET_USERINFO is followed by an asynchronous USERINFO
            // notification rather than a command reply.
            (byte)CmdPacketType.GET_USERINFO => CharactersPacketBuilders.CreateEmptyUserInfo(),
            // DFLegacy starts both town-side polling systems as soon as its
            // local actor is created. Their replies use compact list layouts.
            (byte)CmdPacketType.CALL_MEMER_LIST => TownPacketBuilders.CreatePrivateStorePollReply(),
            (byte)CmdPacketType.REQUEST_BLACKLIST =>
                CharactersPacketBuilders.CreateExchangeServerCharacterInfoReply(),
            _ => null
        };
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

    /// <summary>Single-block PARTY_INFO type 3: clears this party id.</summary>


    /// <summary>
    /// NOTI 7 invite popup. The reader at 0x41B4C8 consumes u16 uid, u8 type,
    /// u32 peer id and, for type 0, two more u16 state words.
    /// </summary>

    /// <summary>
    /// NOTI 8 peer response ack (reader at 0x41B5F1): u16 uid, u8 type,
    /// u32 value. The type-0 zero-value form is the party accept the inviter
    /// needs to claim the party before the PARTY_INFO formation arrives.
    /// </summary>

    /// <summary>
    /// NOTI 10 departure notice (reader at 0x41C41A): u8 uid, u8 reason
    /// 0..9. Reason 0/1 render the left/kicked party chat lines.
    /// </summary>

    /// <summary>NOTI 6 removes the town user record for the uid (reader at
    /// 0x41B3C8 drops the map entry at 0x41B467 and cleans the scene).</summary>

    /// <summary>
    /// Parses SET_PARTY_INFO (CMD 12, writer at 0x7D5F86): after the two-byte
    /// sequence number the client writes u8 title kind, a u32-prefixed custom
    /// title when the kind is zero, and u8 user max (radio index + 2).
    /// </summary>

    /// <summary>Parses REQUEST_PEER (CMD 10): u16 target uid, u8 type, u32 id.</summary>

    /// <summary>
    /// Parses RESPONSE_PEER (CMD 11): u16 peer uid, u8 type, u32 value. The
    /// client accept path writes exactly seven payload bytes with a zero type
    /// and value (0x4FEBC0); refusals set a non-zero type or value.
    /// </summary>


    /// <summary>
    /// Party command failure replies are not the generic [0, code] form: the
    /// reply dispatcher reads a result byte first (zero = failure) and then
    /// one error byte at payload[1] for SET_PARTY_INFO (code 8 pops a party
    /// message, case 0x410799), LEAVE_PARTY (code 0x12, case 0x4107D8) and
    /// WALKOUT (code 4, case 0x410834). The trailing zero keeps the global
    /// read cursor inside the payload.
    /// </summary>

    /// <summary>The peer commands read their error byte at payload[2]
    /// instead: REQUEST_PEER pops a message on code 5 (case 0x4102F6) and
    /// RESPONSE_PEER on code 1 (case 0x4104FF).</summary>

    /// <summary>Parses WALKOUT_PARTY_MEMBER (CMD 14, writer at 0x8520F3):
    /// one byte holding the selected member's roster slot index.</summary>







































}
