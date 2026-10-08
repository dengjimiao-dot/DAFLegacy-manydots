using System.Text;
using DAFLegacy.Server;

namespace DAFLegacy.Protocol;

/// <summary>
/// 兜底应答表：为未注册 handler 的命令给出固定回包，由频道泵层在注册表未命中时调用
/// （07 §11.5）；<c>LoginHandler</c> 也委托此处产出 LOGIN 的成功字节，避免同一字节
/// 序列两处实现。71 个协议已全部走注册表，本表当前无线上路径，保留为防御性回退。
/// 各条回包形态冻结；CHECK_CONNECTION 的通告端点取配置（与
/// <c>CheckConnectionHandler</c> 同源，避免同一包两处各写一份端口）。
/// </summary>
public static class FallbackCommandReplies
{
    public static GameServerPacket? TryCreateReply(PacketFrame request, ServerOptions options) =>
        TryCreateReply(request, [], options);

    public static GameServerPacket? TryCreateReply(
        PacketFrame request,
        ReadOnlySpan<byte> challenge,
        ServerOptions options)
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
                [Encoding.ASCII.GetBytes(options.GameplayDatagram.Host)],
                port1: checked((uint)options.GameplayDatagram.PrimaryPort),
                port2: checked((uint)options.GameplayDatagram.SecondaryPort)),
            // Login success: the four leading flags keep the verified zeroes
            // (zeroes select the normal character-list path in this client
            // build), and the fields the client reads after them — account,
            // relay host, two relay ports, two trailing bytes — are supplied
            // by the shared builder (2026-10-08 IDA 复核: the reply case reads
            // all of them). This defensive path has no account in scope.
            (byte)CmdPacketType.LOGIN => ConnectionPacketBuilders.CreateLoginOk(accountUid: 0),
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
