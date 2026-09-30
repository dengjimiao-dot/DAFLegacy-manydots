using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// RECOVER_STAMINA (CMD 9): gold-cost weakness recovery with the stamina notification (07 §5 group 4).
/// </summary>
public sealed class RecoverStaminaHandler(    JsonGameStore store,
    StaminaRecoveryCatalog staminaRecoveryCatalog
    ) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.RECOVER_STAMINA;

    public string Name => "STAMINA_RECOVER";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Character.Active is null
            || session.Dungeon.CurrentDungeonId != 0
            || session.Dungeon.InSelection)
        {
            var invalidState =
                StaminaPacketBuilders.CreateRecoverStaminaError(4);
            await context.Queue.SendAsync(invalidState, Name, cancellationToken);
            return;
        }

        var recoveryCost = staminaRecoveryCatalog.GetCost(
            session.Character.Active.Level);
        var recovery = await store.RecoverCharacterWeaknessAsync(
            session.Character.Active.Id,
            recoveryCost,
            cancellationToken);
        if (!recovery.Success || recovery.Character is null)
        {
            var errorCode = recovery.Failure switch
            {
                WeaknessRecoveryFailure.NotWeakened => (byte)18,
                WeaknessRecoveryFailure.InsufficientGold => (byte)22,
                _ => (byte)4
            };
            var error =
                StaminaPacketBuilders.CreateRecoverStaminaError(errorCode);
            await context.Queue.SendAsync(error, Name, cancellationToken);
            RuntimeLogger.Info(
                "Rejected stamina recovery for character {CharacterId}: failure={Failure}, cost={Cost}, gold={Gold}.",
                session.Character.Active.Id,
                recovery.Failure,
                recoveryCost,
                session.Currency.Gold);
            return;
        }

        session.Character.Active = recovery.Character;
        session.Currency.Gold = session.Character.Active.Gold;
        var recoveryReply =
            StaminaPacketBuilders.CreateRecoverStaminaReply(session.Currency.Gold);
        await context.Queue.SendAsync(recoveryReply, Name, cancellationToken);

        var fullStamina = StaminaPacketBuilders.CreateStamina(
            DungeonWeaknessPolicy.FullStamina);
        await context.Queue.SendAsync(fullStamina, Name, cancellationToken);
        RuntimeLogger.Info(
            "Recovered character {CharacterId} from weakness for {Cost} gold; remaining gold={Gold}.",
            session.Character.Active.Id,
            recoveryCost,
            session.Currency.Gold);
    }
}
