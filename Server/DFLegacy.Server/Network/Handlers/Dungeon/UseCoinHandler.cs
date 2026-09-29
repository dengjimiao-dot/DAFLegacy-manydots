using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// USE_COIN (CMD 44): revival coin consumption inside the dungeon (07 §5 group 13).
/// </summary>
public sealed class UseCoinHandler(    JsonGameStore store,
    ILogger<UseCoinHandler> logger) : IGameCommandHandler
{
    public ushort ProtocolId => GameProtocolEngine.UseCoinCommand;

    public string Name => "USE_COIN";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        byte? errorCode = null;
        GameUseCoinCommand useCoinCommand = default;
        if (!GameProtocolEngine.TryParseUseCoinCommand(
                request.Body,
                out useCoinCommand)
            || session.Dungeon.CurrentDungeonId == 0
            || session.Character.Active is null)
        {
            errorCode = 19;
        }
        else if (useCoinCommand.TargetUserId != session.Character.LocalUserId)
        {
            errorCode = 21;
        }
        else if (session.Dungeon.CharacterAlive)
        {
            errorCode = 18;
        }
        else if (!CharacterInventoryPlanner.TryConsumeRevivalCoin(
                     session.Inventory.Main,
                     out var plannedInventory))
        {
            errorCode = 17;
        }
        else
        {
            var saved = await store.SaveCharacterInventoryAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                session.Currency.Gold,
                plannedInventory.Values,
                session.Inventory.Equipped.Values,
                cancellationToken);
            if (saved is null)
            {
                errorCode = 19;
            }
            else
            {
                session.Character.Active = saved;
                session.Inventory.Main = plannedInventory;
                session.Dungeon.CharacterAlive = true;
                session.Dungeon.DeathGeneration++;
                session.Dungeon.DeathTimeout = null;

                // DFLegacy restores the actor from DIE_STATE
                // before its USE_COIN reply closes the revive UI.
                // The reply also decrements the client's local
                // virtual-coin object, so no NOTI 14 is sent.
                var reviveState = GameProtocolEngine.CreateDieState(
                    useCoinCommand.TargetUserId,
                    isAlive: true);
                await context.Outbound.SendAsync(reviveState, Name, cancellationToken);

                var useCoinReply =
                    GameProtocolEngine.CreateUseCoinReply(
                        useCoinCommand.TargetUserId);
                await context.Outbound.SendAsync(useCoinReply, Name, cancellationToken);
                logger.LogInformation(
                    "User {UserId} revived in dungeon {DungeonId}; revival coins remaining={RemainingCoins}.",
                    session.Character.LocalUserId,
                    session.Dungeon.CurrentDungeonId,
                    plannedInventory.GetValueOrDefault(
                        CharacterInventoryLayout.RevivalCoinSlot)
                        ?.CountOrValue ?? 0);
                return;
            }
        }

        var error = GameProtocolEngine.CreateCommandError(
            request,
            errorCode!.Value);
        await context.Outbound.SendAsync(error, Name, cancellationToken);
        logger.LogWarning(
            "Rejected USE_COIN for user {UserId}, target={TargetUserId}, error={ErrorCode}.",
            session.Character.LocalUserId,
            useCoinCommand.TargetUserId,
            errorCode.Value);
    }
}
