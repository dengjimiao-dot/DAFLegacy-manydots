using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// SET_USER_AREA (CMD 38): commits the area transition — persists the position, rebuilds USER_AREA/AREA_USERS projections and restores stamina silently (07 §5 group 14).
/// </summary>
public sealed class SetUserAreaHandler(IGameStore store,
    ChannelSessionSenders senders) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.SET_USER_AREA;

    public string Name => "SetUserArea";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // sequence, byte town, byte area, int16 x, int16 y
        if (request.Body.Length >= 8)
        {
            session.Character.TownId = request.Body[2];
            session.Character.AreaId = request.Body[3];
            session.Character.X = BinaryPrimitives.ReadInt16LittleEndian(
                request.Body.AsSpan(4, sizeof(short)));
            session.Character.Y = BinaryPrimitives.ReadInt16LittleEndian(
                request.Body.AsSpan(6, sizeof(short)));
        }

        await SessionLocationPersistence.SaveCurrentLocationAsync(store, session, cancellationToken);

        var townUser = new GameTownUser(
            session.Character.LocalUserId,
            session.Character.X,
            session.Character.Y,
            session.Character.Direction);
        var userArea = TownPacketBuilders.CreateUserArea(
            session.Character.TownId,
            session.Character.AreaId,
            townUser);
        await context.Queue.SendAsync(userArea, Name, cancellationToken);

        // USER_AREA deliberately ignores the local user;
        // AREA_USERS commits the local scene transition but
        // recreates the town actor with stamina zero. Rebuild
        // its projections, then restore stamina through the
        // client's silent NOTI 33 path.
        var areaUsers = TownPacketBuilders.CreateAreaUsers(
            session.Character.TownId,
            session.Character.AreaId,
            [townUser]);
        await context.Queue.SendAsync(areaUsers, Name, cancellationToken);
        session.Character.AreaRosterInitialized = true;
        await senders.SendTownFatigueStateAsync(context, cancellationToken);
        await senders.SendTownAppearanceAsync(context, waitForTownActor: true, cancellationToken);
        await senders.SendSilentTownStaminaStateAsync(context, cancellationToken);
        await senders.SendEnterGameWorldCompleteAsync(context, cancellationToken);
        senders.PublishTownPresence(context);
    }
}
