using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// SET_USER_POSITION (CMD 37): records the town position, seeds AREA_USERS once and mirrors movement to party presence (07 §5 group 14).
/// </summary>
public sealed class SetUserPositionHandler(PartyCoordinator partyCoordinator,
    ChannelSessionSenders senders) : IGameCommandHandler
{

    public ushort ProtocolId => (byte)CmdPacketType.SET_USER_POSITION;

    public string Name => "SetUserPosition";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // DNF 1.0.1.9 SET_USER_POSITION is:
        // sequence, int16 x, int16 y, byte direction,
        // uint16 movement value. Establishing AREA_USERS
        // here is what enables the local map's town-exit
        // trigger to emit SET_USER_AREA.
        if (request.Body.Length >= 9)
        {
            session.Character.X = BinaryPrimitives.ReadInt16LittleEndian(
                request.Body.AsSpan(2, sizeof(short)));
            session.Character.Y = BinaryPrimitives.ReadInt16LittleEndian(
                request.Body.AsSpan(4, sizeof(short)));
            session.Character.Direction = request.Body[6];
            session.Character.MovementValue = BinaryPrimitives.ReadUInt16LittleEndian(
                request.Body.AsSpan(7, sizeof(ushort)));
        }

        // Movement is only mirrored while the actor is a
        // town entity; dungeon-run positions stay private.
        if (session.Dungeon.CurrentDungeonId == 0 && !session.Dungeon.InSelection)
        {
            partyCoordinator.UpdatePresencePosition(
                context.Runtime.Id,
                session.Character.X,
                session.Character.Y,
                session.Character.Direction);
        }

        // The local client already owns its movement.
        // Re-sending USER_POSITION or AREA_USERS on every
        // one-second update makes it rebuild the area and
        // snaps the character back. Seed the roster once
        // when entering the town session, then only retain
        // subsequent coordinates server-side.
        if (!session.Character.AreaRosterInitialized)
        {
            var townUser = new GameTownUser(
                session.Character.LocalUserId,
                session.Character.X,
                session.Character.Y,
                session.Character.Direction);

            var areaUsers = TownPacketBuilders.CreateAreaUsers(
                session.Character.TownId,
                session.Character.AreaId,
                [townUser]);
            await context.Queue.SendAsync(areaUsers, Name, cancellationToken);
            session.Character.AreaRosterInitialized = true;
            await senders.SendTownFatigueStateAsync(context, cancellationToken);
            await senders.SendTownAppearanceAsync(context, waitForTownActor: true, cancellationToken);
            await senders.SendEnterGameWorldCompleteAsync(context, cancellationToken);
            senders.PublishTownPresence(context);
        }
    }
}
