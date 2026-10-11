using System.Buffers.Binary;
using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// SELECT_CARD (CMD 74): clear-card pick with the free/gold reward grant (07 §5 group 13).
/// </summary>
public sealed class SelectCardHandler(
    ChannelSessionSenders senders,
    DungeonCatalog dungeonCatalog) : IGameCommandHandler
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
                RuntimeLogger.Warn(
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
                RuntimeLogger.Warn(
                    "Ignored invalid dungeon card type {CardType} from user {UserId}",
                    cardType,
                    session.Character.LocalUserId);
            }

            // CMD 74 supports only type 0 (free) and type 1
            // (paid). A black-diamond character's second
            // free reward is NOTI 35 group 3 at the same
            // selected column and is settled with type 0.
            await senders.SendCardSelectionSnapshotAsync(context, inventoryChanged);
            RuntimeLogger.Info(
                "Dungeon {DungeonId} card type {CardType}, index {CardIndex} processed for user {UserId}; accepted={Accepted}, reward={RewardItemId}",
                session.Dungeon.CurrentDungeonId,
                cardType,
                selectedCardIndex,
                session.Character.LocalUserId,
                selectionAccepted,
                selectedRewardItemId);
            await TryAdvanceDummyPhaseAsync(context, cancellationToken);
        }
        else
        {
            RuntimeLogger.Warn(
                "Ignored dungeon card selection before card rights for user {UserId}",
                session.Character.LocalUserId);
        }
    }

    /// <summary>多场次副本（[dummy appear count] &gt; 0，当前仅 51 号）：每场皮特击杀都
    /// 走完整结算流程（真机行为：打完皮特弹结算窗、门不开）；翻牌收尾后若击杀数未达
    /// 「1 + 假身出现次数」，把同一房间重建为 [type] [dummy] 假人场（假身皮特登场）
    /// 继续下一场。第 3 场击杀后不再重建，流程按终局收尾（docs/design/15 §4.1）。</summary>
    private async Task TryAdvanceDummyPhaseAsync(
        ConnectionContext context,
        CancellationToken cancellationToken)
    {
        var session = context.State;
        if (!dungeonCatalog.TryGetDefinition(
                session.Dungeon.CurrentDungeonId,
                out var definition)
            || definition.DummyAppearCount <= 0
            || session.Dungeon.BossPhaseKills >= 1 + definition.DummyAppearCount
            || session.Dungeon.Layout is null
            || !dungeonCatalog.TryCreateDummyRoom(
                session.Dungeon.Layout,
                session.Dungeon.RoomX,
                session.Dungeon.RoomY,
                session.Dungeon.Difficulty,
                out var dummyRoom))
        {
            return;
        }

        session.Dungeon.ClearEnabled = false;
        session.Dungeon.ResultSent = false;
        session.Dungeon.CardLayoutSent = false;
        session.Dungeon.CardRightsSent = false;
        session.Dungeon.CardSelectionDeadline = null;
        session.Dungeon.ClearReward = null;
        session.Dungeon.ClearExperience = null;
        session.Dungeon.FreeCardIndex = null;
        session.Dungeon.GoldCardIndex = null;
        session.Dungeon.ReportedBossDeaths.Clear();
        session.Dungeon.BossCascadeDeaths.Clear();
        session.Dungeon.Room = dummyRoom;
        session.Dungeon.AliveMonsters.Clear();
        session.Dungeon.AliveMonsters.UnionWith(
            dummyRoom.Monsters.Select(monster => monster.UniqueId));
        var dummyStartMap = DungeonPacketBuilders.CreateStartMap(
            session.Dungeon.RoomX,
            session.Dungeon.RoomY,
            dummyRoom.MapId,
            dummyRoom.Monsters,
            passiveObjectDrops: [],
            randomSeed: dummyRoom.ClientRandomSeed,
            roomStateFlag: session.Dungeon.Run.RoomStateFlag,
            hellPartyMode: session.Dungeon.Run.CurrentHellPartyMode);
        await context.Queue.SendAsync(dummyStartMap, Name, cancellationToken);
        RuntimeLogger.Info(
            "Dungeon {DungeonId} dummy phase advanced after {BossPhaseKills} piper kills: rebuilt room ({RoomX},{RoomY}) with dummy map {MapId}, monsters={MonsterCount}.",
            session.Dungeon.CurrentDungeonId,
            session.Dungeon.BossPhaseKills,
            session.Dungeon.RoomX,
            session.Dungeon.RoomY,
            dummyRoom.MapId,
            dummyRoom.Monsters.Length);
    }
}
