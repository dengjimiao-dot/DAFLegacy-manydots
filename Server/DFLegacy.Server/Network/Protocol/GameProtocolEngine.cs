using System.Buffers.Binary;
using DFLegacy.Server;

namespace DFLegacy.Protocol;

public static class GameProtocolEngine
{
    public const int ClearRewardCardColumnCount = 4;
    public const byte NotificationPacketType = 0;
    public const sbyte NoChampionRewardOrdinal = -1;
    public const sbyte MaximumChampionRewardOrdinal = 11;
    public const byte CommandPacketType = 1;
    public const byte DungeonItemActionCommand = (byte)CmdPacketType.USE_STACKABLE;
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

}
