using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// START_GAME (CMD 15): opens dungeon selection — fatigue gate, return anchor, state/UDP peer packets and eligibility (07 §5 group 13).
/// </summary>
public sealed class StartGameHandler(
    IGameStore store, WorldMapCatalog worldMapCatalog,
    GameplayDatagramService gameplayDatagram,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.START_GAME;

    public string Name => "START_GAME";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        // Walking into a [dungeon gate] emits command 15.
        // DF2008 handles exhausted party members in this command,
        // before entering the dungeon-selection screen.
        var maximumFatigue = senders.HasActivePremiumService(context,
                GameProtocolConstants.BlackDiamondServiceType)
            ? GameProtocolConstants.BlackDiamondMaximumFatigue
            : GameProtocolConstants.DefaultMaximumFatigue;
        var requiresTutorial = session.Character.Active is { TutorialStarted: false };
        if (session.Character.Active is null
            || (!requiresTutorial
                && !DungeonFatigueRules.CanEnterNewDungeon(
                    session.Character.Active.UsedFatigue,
                    maximumFatigue)))
        {
            var fatigueError = StaminaPacketBuilders.CreateEnterDungeonSelectionInsufficientFatigueReply();
            await context.Queue.SendAsync(fatigueError, Name, cancellationToken);
            RuntimeLogger.Info(
                "Rejected dungeon-selection entry for user {UserId}: used fatigue {UsedFatigue}/{MaximumFatigue}.",
                session.Character.LocalUserId,
                session.Character.Active?.UsedFatigue ?? 0,
                maximumFatigue);
            return;
        }

        // DAFLegacy transitions to dungeon selection after state=1,
        // the UDP peer table, local party-host index, and
        // notification 27's compact solo-session body.
        session.Dungeon.ReturnTownId = session.Character.TownId;
        session.Dungeon.ReturnAreaId = session.Character.AreaId;
        session.Dungeon.ReturnX = session.Character.X;
        session.Dungeon.ReturnY = session.Character.Y;
        session.Dungeon.ReturnDirection = session.Character.Direction;
        session.Dungeon.InSelection = true;
        session.Dungeon.SelectionWorldMap = worldMapCatalog.TryGetForGate(
            session.Character.TownId, session.Character.AreaId, out var gateWorldMap)
            ? gateWorldMap : null;
        // Return before DF2008 auto-selects at 30.3 seconds.
        session.Dungeon.SelectionDeadline = requiresTutorial
            ? null : DateTimeOffset.UtcNow.AddSeconds(30);
        await SessionLocationPersistence.SaveCurrentLocationAsync(store, session, cancellationToken);
        var dungeonState = CharactersPacketBuilders.CreateUserState(
            session.Character.LocalUserId,
            state: 1);
        await context.Queue.SendAsync(dungeonState, Name, cancellationToken);

        var peerInfo = gameplayDatagram.TryGetPeerInfo(
            context.Runtime.Id,
            session.Character.LocalUserId,
            accountId: 1,
            out var localPeer)
                ? new[] { localPeer }
                : [];
        var udpPeers = PartyPacketBuilders.CreateUdpPeerInfo(peerInfo);
        await context.Queue.SendAsync(udpPeers, Name, cancellationToken);

        var udpHost = PartyPacketBuilders.CreateUdpHost();
        await context.Queue.SendAsync(udpHost, Name, cancellationToken);

        await senders.SendDungeonPermissionsAsync(context, cancellationToken);

        await senders.SendDungeonSelectionEligibilityAsync(context, cancellationToken);
    }
}
