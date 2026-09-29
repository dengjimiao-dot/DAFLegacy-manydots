using System.Buffers.Binary;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// SELECT_CARD (CMD 74): clear-card pick with the free/gold reward grant (07 §5 group 13).
/// </summary>
public sealed class SelectCardHandler(    ILogger<SelectCardHandler> logger,
    ChannelSessionSenders senders) : IGameCommandHandler
{
    public ushort ProtocolId => (byte)CmdPacketType.SELECT_CARD;

    public string Name => "SELECT_CARD";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (session.Dungeon.CardRightsSent && session.Dungeon.ClearReward is not null)
        {
            var cardType = request.Body.Length >= 3
                ? request.Body[2]
                : (byte)0;
            var selectedCardIndex = request.Body.Length >= 4
                ? request.Body[3]
                : (byte)0;
            var inventoryChanged = false;
            var selectionAccepted = false;
            ushort? selectedRewardItemId = null;
            if (selectedCardIndex >= 4)
            {
                logger.LogWarning(
                    "Ignored invalid dungeon card index {CardIndex} from user {UserId}",
                    selectedCardIndex,
                    session.Character.LocalUserId);
            }
            else if (cardType == 0 && !session.Dungeon.FreeCardIndex.HasValue)
            {
                var freeGrant = await senders.GrantFreeClearRewardAsync(context, 
                    selectedCardIndex);
                if (freeGrant.Saved)
                {
                    selectionAccepted = true;
                    selectedRewardItemId = freeGrant.RewardItemId;
                    inventoryChanged = freeGrant.InventoryChanged;
                }
            }
            else if (cardType == 1
                && !session.Dungeon.GoldCardIndex.HasValue
                && session.Dungeon.ClearReward.GoldCardEnabled
                && session.Dungeon.ClearReward.GoldCardCost <= int.MaxValue
                && session.Currency.Gold >= (int)session.Dungeon.ClearReward.GoldCardCost)
            {
                var goldGrant = await senders.SaveClearRewardEquipmentAsync(context, 
                    session.Currency.Gold - (int)session.Dungeon.ClearReward.GoldCardCost,
                    [session.Dungeon.ClearReward.GoldItemId]);
                if (goldGrant.Saved)
                {
                    session.Dungeon.GoldCardIndex = selectedCardIndex;
                    selectionAccepted = true;
                    selectedRewardItemId = goldGrant.Granted[0]
                        ? session.Dungeon.ClearReward.GoldItemId
                        : null;
                    inventoryChanged = true;
                }
            }
            else if (cardType == 0
                && session.Dungeon.FreeCardIndex == selectedCardIndex)
            {
                selectionAccepted = true;
                selectedRewardItemId = session.Dungeon.ClearReward.FreeItemId
                    ?? session.Dungeon.ClearReward.PremiumItemId;
            }
            else if (cardType == 1
                && session.Dungeon.GoldCardIndex == selectedCardIndex)
            {
                selectionAccepted = true;
                selectedRewardItemId = session.Dungeon.ClearReward.GoldItemId;
            }
            else if (cardType is not 0 and not 1)
            {
                logger.LogWarning(
                    "Ignored invalid dungeon card type {CardType} from user {UserId}",
                    cardType,
                    session.Character.LocalUserId);
            }

            // CMD 74 supports only type 0 (free) and type 1
            // (paid). A black-diamond character's second
            // free reward is NOTI 35 group 3 at the same
            // selected column and is settled with type 0.
            await senders.SendCardSelectionSnapshotAsync(context, inventoryChanged);
            logger.LogInformation(
                "Dungeon {DungeonId} card type {CardType}, index {CardIndex} processed for user {UserId}; accepted={Accepted}, reward={RewardItemId}",
                session.Dungeon.CurrentDungeonId,
                cardType,
                selectedCardIndex,
                session.Character.LocalUserId,
                selectionAccepted,
                selectedRewardItemId);
        }
        else
        {
            logger.LogWarning(
                "Ignored dungeon card selection before card rights for user {UserId}",
                session.Character.LocalUserId);
        }
    }
}
