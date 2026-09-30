using DAFLegacy.Server;

namespace DAFLegacy.Protocol;

/// <summary>
/// 兜底应答表：为未注册 handler 的命令给出固定回包，由频道泵层在注册表未命中时调用
/// （07 §11.5）；<c>LoginHandler</c> 也委托此处产出 LOGIN 的成功字节，避免同一字节
/// 序列两处实现。71 个协议已全部走注册表，本表当前无线上路径，保留为防御性回退。
/// 回包字节冻结，改动即破坏客户端兼容。
/// </summary>
public static class FallbackCommandReplies
{
    public static GameServerPacket? TryCreateReply(PacketFrame request) =>
        TryCreateReply(request, []);

    public static GameServerPacket? TryCreateReply(PacketFrame request, ReadOnlySpan<byte> challenge)
    {
        if (request.Type != GameProtocolConstants.CommandPacketType
            || request.Body.Length < sizeof(ushort))
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
                GameProtocolConstants.CommandPacketType,
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
            // DAFLegacy starts both town-side polling systems as soon as its
            // local actor is created. Their replies use compact list layouts.
            (byte)CmdPacketType.CALL_MEMER_LIST => TownPacketBuilders.CreatePrivateStorePollReply(),
            (byte)CmdPacketType.REQUEST_BLACKLIST =>
                CharactersPacketBuilders.CreateExchangeServerCharacterInfoReply(),
            _ => null
        };
    }
}
