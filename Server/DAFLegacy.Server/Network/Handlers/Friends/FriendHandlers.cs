using System.Buffers.Binary;
using System.Text;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>社交命令的共享解析与刷新（11 号 P2）：请求体 = 序号 u16 + dstr 名字
/// （u32 长度 + GBK 字节），名字长度受 dstr 容量约束（1..30）。</summary>
internal static class SocialCommandParsing
{
    internal static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public static string? ParseName(PacketFrame request)
    {
        if (request.Body.Length < 6)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(request.Body.AsSpan(2, 4));
        if (length is < 1 or > 30 || request.Body.Length < 6 + (int)length)
        {
            return null;
        }

        return ClientEncoding.GetString(request.Body.AsSpan(6, (int)length));
    }

}

/// <summary>CMD 63 ADD_FRIEND：按名添加单向好友，应答携带新好友完整条目并重发列表。</summary>
public sealed class AddFriendHandler(
    IGameStore store,
    TownPresenceService town,
    CharacterSessionRegistry characterSessions) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.ADD_FRIEND;

    public string Name => "AddFriend";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var name = SocialCommandParsing.ParseName(request);
        var active = context.State.Character.Active;
        if (name is null || active is null)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        var (ok, error) = await store.AddFriendAsync(active.Id, name, cancellationToken);
        if (!ok)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        var target = await store.FindCharacterByNameAsync(name, cancellationToken);
        var accountUid = target is null ? 0 : await store.GetCharacterAccountUidAsync(target.Id, cancellationToken);
        var presence = target is null ? null : town.FindByCharacter(target.Id);
        await context.Queue.SendAsync(
            SocialPacketBuilders.CreateAddFriendReply(
                request,
                name,
                (byte)(target?.Level ?? 1),
                (byte)(target?.Job ?? 0),
                (byte)(target?.GrowType ?? 0),
                accountUid,
                online: presence is not null),
            Name,
            cancellationToken);
        SocialPresenceNotifier.SendFriendList(store, town, characterSessions, context.Runtime.Id, active.Id);
    }
}

/// <summary>CMD 64 REMOVE_FRIEND：按名删除单向边（幂等），应答回显并重发列表。</summary>
public sealed class RemoveFriendHandler(
    IGameStore store,
    TownPresenceService town,
    CharacterSessionRegistry characterSessions) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REMOVE_FRIEND;

    public string Name => "RemoveFriend";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var name = SocialCommandParsing.ParseName(request);
        var active = context.State.Character.Active;
        if (name is null || active is null)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        await store.RemoveFriendAsync(active.Id, name, cancellationToken);
        await context.Queue.SendAsync(
            SocialPacketBuilders.CreateRemoveFriendReply(request, name),
            Name,
            cancellationToken);
        SocialPresenceNotifier.SendFriendList(store, town, characterSessions, context.Runtime.Id, active.Id);
    }
}

/// <summary>CMD 128 REGISTER_TO_BLACKLIST：按名加入角色级黑名单（J1 裁决：角色级锚定）。</summary>
public sealed class RegisterToBlacklistHandler(IGameStore store) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REGISITER_TO_BLACKLIST;

    public string Name => "RegisterToBlacklist";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var name = SocialCommandParsing.ParseName(request);
        var active = context.State.Character.Active;
        if (name is null || active is null)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        var (ok, _) = await store.AddBlacklistAsync(active.Id, name, cancellationToken);
        await context.Queue.SendAsync(
            ok
                ? SocialPacketBuilders.CreateBlacklistAddReply(request)
                : ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
            Name,
            cancellationToken);
    }
}

/// <summary>CMD 129 DELETE_TO_BLACKLIST：按名移出黑名单（幂等），应答回显名字。</summary>
public sealed class DeleteToBlacklistHandler(IGameStore store) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.DELETE_TO_BLACKLIST;

    public string Name => "DeleteToBlacklist";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var name = SocialCommandParsing.ParseName(request);
        var active = context.State.Character.Active;
        if (name is null || active is null)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        await store.RemoveBlacklistAsync(active.Id, name, cancellationToken);
        await context.Queue.SendAsync(
            SocialPacketBuilders.CreateBlacklistRemoveReply(request, name),
            Name,
            cancellationToken);
    }
}

/// <summary>CMD 130 REQUEST_BLACKLIST：黑名单全量（tm 风格加入日期），替换原 canned 应答。</summary>
public sealed class RequestBlacklistHandler(IGameStore store) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REQUEST_BLACKLIST;

    public string Name => "RequestBlacklist";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var active = context.State.Character.Active;
        if (active is null)
        {
            await context.Queue.SendAsync(
                ConnectionPacketBuilders.CreateCommandError(request, errorCode: 3),
                Name,
                cancellationToken);
            return;
        }

        var rows = await store.GetBlacklistAsync(active.Id, cancellationToken);
        var entries = new List<SocialPacketBuilders.BlacklistEntry>(rows.Length);
        foreach (var row in rows)
        {
            // created_at 为 unix 秒；客户端按 struct tm 消费（日/月−1/年−1900），本地时区。
            var local = DateTimeOffset.FromUnixTimeSeconds(row.CreatedAtUnix).LocalDateTime;
            entries.Add(new SocialPacketBuilders.BlacklistEntry(
                row.TargetName,
                (byte)local.Day,
                (byte)(local.Month - 1),
                (byte)(local.Year - 1900)));
        }

        await context.Queue.SendAsync(
            SocialPacketBuilders.CreateBlacklistListReply(request, entries),
            Name,
            cancellationToken);
    }
}
