using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>个人商店 handler 族共享（11 号 P4）：名字 dstr 解析（与社交族同口径）。</summary>
internal static class PrivateStoreParsing
{
    public static string? ParseTitle(PacketFrame request)
    {
        if (request.Body.Length < 6)
        {
            return null;
        }

        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            request.Body.AsSpan(2, 4));
        if (length is < 1 or > 40 || request.Body.Length < 6 + (int)length)
        {
            return null;
        }

        return SocialCommandParsing.ClientEncoding.GetString(request.Body.AsSpan(6, (int)length));
    }
}

/// <summary>CMD 88 CREATE_PRIVATE_STORE：建占位摊（幂等），应答 {u8}。</summary>
public sealed class CreatePrivateStoreHandler(
    PrivateStoreRegistry stores,
    TownPresenceService town) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.CREATE_PRIVATE_STORE;

    public string Name => "CreatePrivateStore";

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
                PrivateStorePacketBuilders.CreateStoreByteReply(request, success: false),
                Name,
                cancellationToken);
            return;
        }

        var store = stores.Create(active.Id, context.State.Character.LocalUserId);
        var presence = town.FindBySession(context.Runtime.Id);
        if (presence is not null)
        {
            // 建摊区域广播（NOTI 78；候选布局）。
            await context.Queue.SendAsync(
                PrivateStorePacketBuilders.CreatePrivateStoreCreated(
                    new PrivateStoreBroadcastEntry(
                        presence.UserId,
                        presence.TownId,
                        presence.AreaId,
                        0,
                        0)),
                Name,
                cancellationToken);
        }

        await context.Queue.SendAsync(
            PrivateStorePacketBuilders.CreateStoreByteReply(request, success: true),
            Name,
            cancellationToken);
    }
}

/// <summary>CMD 90：挂名并开张，应答 {u8}。</summary>
public sealed class CompletePrivateStoreHandler(PrivateStoreRegistry stores) : IGameCommandHandler
{
    public ushort ProtocolId => 90;

    public string Name => "CompletePrivateStore";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        var title = PrivateStoreParsing.ParseTitle(request);
        var active = context.State.Character.Active;
        var ok = title is not null
            && active is not null
            && stores.Complete(active.Id, title);
        await context.Queue.SendAsync(
            PrivateStorePacketBuilders.CreateStoreByteReply(request, success: ok),
            Name,
            cancellationToken);
    }
}

/// <summary>CMD 86 ENTER_PRIVATE_STORE：进入他人摊位，应答摊头+在售条目。</summary>
public sealed class EnterPrivateStoreHandler(
    PrivateStoreRegistry stores,
    TownPresenceService town) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.ENTER_PRIVATE_STORE;

    public string Name => "EnterPrivateStore";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        if (request.Body.Length < 6)
        {
            await context.Queue.SendAsync(
                PrivateStorePacketBuilders.CreateEnterStoreReply(request, false, 0, []),
                Name,
                cancellationToken);
            return;
        }

        var ownerUserId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            request.Body.AsSpan(2, 4));
        var ownerPresence = town.FindByUserId((ushort)ownerUserId);
        var store = ownerPresence is null
            ? null
            : stores.FindByCharacterId(ownerPresence.CharacterId);
        if (store is null)
        {
            await context.Queue.SendAsync(
                PrivateStorePacketBuilders.CreateEnterStoreReply(request, false, 0, []),
                Name,
                cancellationToken);
            return;
        }

        var entries = store.Listings
            .Select(listing => new PrivateStorePacketBuilders.PrivateStoreListEntry(
                listing.Slot,
                listing.ItemUid,
                0,
                listing.PriceGold,
                listing.Count,
                0))
            .ToList();
        await context.Queue.SendAsync(
            PrivateStorePacketBuilders.CreateEnterStoreReply(
                request,
                success: true,
                ownerUserId: (ushort)ownerUserId,
                entries),
            Name,
            cancellationToken);
    }

}

/// <summary>CMD 87 EXIT_PRIVATE_STORE：无应答 case（P-35 应答映射），客户端本地退出。</summary>
public sealed class ExitPrivateStoreHandler : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.EXIT_PRIVATE_STORE;

    public string Name => "ExitPrivateStore";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>CMD 89 REMOVE_PRIVATE_STORE：收摊清目录，应答 {u8}。</summary>
public sealed class RemovePrivateStoreHandler(PrivateStoreRegistry stores) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.REMOVE_PRIVATE_STORE;

    public string Name => "RemovePrivateStore";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(
        ConnectionContext context,
        PacketFrame request,
        CancellationToken cancellationToken)
    {
        if (context.State.Character.Active is not null)
        {
            stores.Remove(context.State.Character.Active.Id);
        }

        await context.Queue.SendAsync(
            PrivateStorePacketBuilders.CreateStoreByteReply(request, success: true),
            Name,
            cancellationToken);
    }
}
