using System.Buffers.Binary;
using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// CERA_SHOP_BUY (CMD 67): Cera-shop purchases with attribute selection and cash accounting (07 §5 group 10).
/// </summary>
public sealed class CeraShopBuyHandler(JsonGameStore store,
    ItemCatalog itemCatalog,
    CeraShopCatalog ceraShopCatalog,
    ServerOptions options,
    RuntimeState runtime,
    CharacterSessionRegistry characterSessions,
    ILogger<CeraShopBuyHandler> logger,
    CharacterPacketBuilders builders,
    ChannelSessionSenders senders) : IGameCommandHandler
{

    private static readonly Encoding ClientEncoding = PvfEncodings.Cp936Lossy();

    public ushort ProtocolId => (byte)CmdPacketType.BUY_CERASHOP_ITEM;

    public string Name => "CeraShopBuy";

    public CommandPhase RequiredPhase => CommandPhase.Any;

    public bool RequiresChannelPort => true;

    public async ValueTask HandleAsync(ConnectionContext context, PacketFrame request, CancellationToken cancellationToken)
    {
        var session = context.State;
        if (!options.EnablePacketTracing)
        {
            // Cera-shop protocol work is still being
            // completed. Keep only this small decoded
            // request in the admin trace so a rejected
            // product can be diagnosed without enabling
            // high-volume tracing for movement packets.
            runtime.TracePacketAlways(
                context.Runtime,
                "RX-CERA",
                request,
                options.HexDumpLimit);
        }

        if (session.Character.Active is null
            || !ShopParsers.TryReadCeraShopPurchase(
                request.Body,
                out var requestedProducts,
                out var isGift)
            || isGift)
        {
            var error = ShopPacketBuilders.CreateCeraShopBuyError(
                errorCode: 4);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected malformed Cera-shop request session.Character.Active={HasCharacter}, body={Body}.",
                session.Character.Active is not null,
                Convert.ToHexString(request.Body));
            return;
        }

        var errorProduct = requestedProducts[0];
        var errorClientCategory = -1;
        var plannedInventory = session.Inventory.Main.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedAvatarInventory = session.Inventory.Avatar.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedCreatureInventory = session.Inventory.Creature.ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        var plannedWarehouseCapacity = session.Inventory.WarehouseCapacity;
        var warehouseUpgraded = false;
        var overflowMails = new List<CreateCharacterMailRequest>();
        var immediateContainerOverflow =
            new List<CharacterMailAttachmentRecord>();
        var immediateContainerRewards =
            new List<CeraShopContentReward>();
        var purchaseReplyRewards =
            new List<IReadOnlyList<GameCeraShopReward>>();
        var immediateContainerMailCount = 0;
        var openedContainerCount = 0UL;
        var premiumServiceGrants =
            new List<PremiumServicePurchaseGrant>();
        var plannedGold = session.Currency.Gold;
        var plannedVictoryPoints = session.Currency.VictoryPoints;
        var totalCashPrice = 0L;
        var purchaseValid = true;
        foreach (var requestedProduct in requestedProducts)
        {
            var purchaseReplyIndex = purchaseReplyRewards.Count;
            purchaseReplyRewards.Add([]);
            errorClientCategory = -1;
            if (!ceraShopCatalog.TryGetProduct(
                    requestedProduct.CommodityNo,
                    out var product))
            {
                purchaseValid = false;
                errorProduct = requestedProduct;
                break;
            }

            if (!product.UsesMainInventory
                && requestedProduct.AttributeValue
                    != product.AttributeValue)
            {
                purchaseValid = false;
                errorProduct = requestedProduct;
                break;
            }

            errorClientCategory = product.ClientCategory;
            totalCashPrice += product.CashPrice;
            if (totalCashPrice > int.MaxValue)
            {
                purchaseValid = false;
                errorProduct = requestedProduct;
                break;
            }

            if (!itemCatalog.TryGetDefinition(
                    product.ItemId,
                    out var itemDefinition)
                || itemDefinition.InventoryCategory
                        is not (ItemInventoryCategory.Avatar
                            or ItemInventoryCategory.Creature)
                    && !product.UsesMainInventory
                || itemDefinition.IsEquipment
                    && product.Quantity != 1)
            {
                purchaseValid = false;
                errorProduct = requestedProduct;
                break;
            }

            var isWarehouseUpgradeProduct =
                product.WarehouseUpgradeCapacity.HasValue
                || CharacterWarehouseProgression
                    .TryGetItemTargetCapacity(
                        product.ItemId,
                        out _);
            if (isWarehouseUpgradeProduct)
            {
                var targetCapacity = product.WarehouseUpgradeCapacity
                    ?? CharacterWarehouseProgression.ApplyItemTarget(
                        plannedWarehouseCapacity,
                        product.ItemId);
                // Each target-client STK carries an absolute capacity.
                // It is settled as an effect and never enters the bag.
                if (session.Dungeon.CurrentDungeonId != 0
                    || product.Quantity != 1)
                {
                    purchaseValid = false;
                    errorProduct = requestedProduct;
                    break;
                }

                plannedWarehouseCapacity = Math.Max(
                    plannedWarehouseCapacity,
                    targetCapacity);
                warehouseUpgraded = true;
                return;
            }

            if (product.PremiumContract is { } contract)
            {
                var grantedDays = (long)contract.Days
                    * product.Quantity;
                if (grantedDays is <= 0 or > int.MaxValue)
                {
                    purchaseValid = false;
                    errorProduct = requestedProduct;
                    break;
                }

                premiumServiceGrants.Add(new(
                    contract.ServiceType,
                    (int)grantedDays));
                return;
            }

            if (product.DirectCurrencyGrant is { } currencyGrant)
            {
                if (!CeraShopDirectCurrencyPlanner.TryApply(
                        plannedGold,
                        plannedVictoryPoints,
                        currencyGrant,
                        product.Quantity,
                        out plannedGold,
                        out plannedVictoryPoints))
                {
                    purchaseValid = false;
                    errorProduct = requestedProduct;
                    break;
                }

                return;
            }

            if (itemDefinition.CeraBooster is not null
                || itemDefinition.CeraPackage is not null)
            {
                if (!CeraShopContentPlanner.TryCreatePurchase(
                        plannedInventory,
                        plannedAvatarInventory,
                        plannedCreatureInventory,
                        itemDefinition,
                        product.Quantity,
                        itemCatalog,
                        senders.GetInventoryWeightLimit(context, session.Character.Active),
                        GameRandomSource.Shared,
                        rewardItemId =>
                            CharacterWarehouseProgression
                                .TryGetItemTargetCapacity(
                                    rewardItemId,
                                    out _)
                            || ceraShopCatalog.TryGetPremiumContract(
                                rewardItemId,
                                out _)
                            || ceraShopCatalog.TryGetDirectCurrencyGrant(
                                rewardItemId,
                                out _),
                        out var contentsPlan,
                        out var contentsFailure))
                {
                    purchaseValid = false;
                    errorProduct = requestedProduct;
                    logger.LogWarning(
                        "Could not plan immediate Cera-shop container {ItemId}: {Failure}.",
                        product.ItemId,
                        contentsFailure);
                    break;
                }

                var contentEffectsValid = true;
                foreach (var reward in contentsPlan.Rewards)
                {
                    if (ceraShopCatalog.TryGetPremiumContract(
                            reward.ItemId,
                            out var rewardContract))
                    {
                        var grantedDays = (long)rewardContract.Days
                            * reward.Count;
                        if (grantedDays is <= 0 or > int.MaxValue)
                        {
                            contentEffectsValid = false;
                            break;
                        }

                        premiumServiceGrants.Add(new(
                            rewardContract.ServiceType,
                            checked((int)grantedDays)));
                    }
                    else if (ceraShopCatalog.TryGetDirectCurrencyGrant(
                                 reward.ItemId,
                                 out var rewardCurrencyGrant)
                        && !CeraShopDirectCurrencyPlanner.TryApply(
                            plannedGold,
                            plannedVictoryPoints,
                            rewardCurrencyGrant,
                            reward.Count,
                            out plannedGold,
                            out plannedVictoryPoints))
                    {
                        contentEffectsValid = false;
                        break;
                    }
                }

                if (!contentEffectsValid)
                {
                    purchaseValid = false;
                    errorProduct = requestedProduct;
                    logger.LogWarning(
                        "Could not settle immediate effects from Cera-shop container {ItemId}.",
                        product.ItemId);
                    break;
                }

                // Original Cera-shop processing expands both
                // [cera booster] and [cera package] during the
                // purchase transaction. The container itself is
                // therefore never inserted into the inventory.
                plannedInventory = contentsPlan.MainInventory;
                plannedAvatarInventory = contentsPlan.AvatarInventory;
                plannedCreatureInventory = contentsPlan.CreatureInventory;
                immediateContainerOverflow.AddRange(
                    contentsPlan.OverflowItems);
                immediateContainerRewards.AddRange(contentsPlan.Rewards);
                purchaseReplyRewards[purchaseReplyIndex] = contentsPlan
                    .Rewards
                    .GroupBy(reward => reward.ItemId)
                    .Select(group => new GameCeraShopReward(
                        group.Key,
                        checked((uint)group.Aggregate(
                            0UL,
                            (total, reward) => checked(
                                total + reward.Count)))))
                    .ToArray();
                openedContainerCount = checked(
                    openedContainerCount + product.Quantity);
                return;
            }

            var purchasedItem = CharacterItemIdentity.Ensure(
                new CharacterMailAttachmentRecord(
                product.ItemId,
                product.Quantity,
                Durability: itemDefinition.InventoryCategory
                    == ItemInventoryCategory.Equipment
                        ? itemCatalog.GetInitialDurability(product.ItemId)
                        : itemDefinition.IsEquipment
                            ? (ushort)1_000
                            : (ushort)0,
                SealState: CharacterItemSealing.GetInitialSealState(
                    itemDefinition)),
                itemDefinition);
            var placementFailure = InventoryPlacementFailure.None;
            if (itemDefinition.InventoryCategory
                    == ItemInventoryCategory.Avatar
                && CharacterAvatarInventoryPlanner.TryPlace(
                    plannedAvatarInventory,
                    purchasedItem,
                    itemCatalog,
                    out var avatarPurchasePlan,
                    out placementFailure))
            {
                plannedAvatarInventory = avatarPurchasePlan.Inventory;
                return;
            }

            if (itemDefinition.InventoryCategory
                    == ItemInventoryCategory.Creature
                && CharacterCreatureInventoryPlanner.TryPlace(
                    plannedCreatureInventory,
                    new CharacterItemRecord(
                        ushort.MaxValue,
                        purchasedItem.ItemId,
                        purchasedItem.CountOrValue,
                        purchasedItem.State,
                        purchasedItem.Durability,
                        purchasedItem.SealState,
                        InstanceId: purchasedItem.InstanceId),
                    itemCatalog,
                    preferredEmptySlot: null,
                    out var creaturePurchasePlan,
                    out placementFailure))
            {
                plannedCreatureInventory =
                    creaturePurchasePlan.Inventory;
                return;
            }

            if (itemDefinition.InventoryCategory
                    is not (ItemInventoryCategory.Avatar
                        or ItemInventoryCategory.Creature)
                && CharacterInventoryPlanner.TryPlace(
                    plannedInventory,
                    purchasedItem,
                    itemCatalog,
                    senders.GetInventoryWeightLimit(context, session.Character.Active),
                    out var purchasePlan,
                    out placementFailure))
            {
                plannedInventory = purchasePlan.Inventory;
                return;
            }

            if (placementFailure == InventoryPlacementFailure.InvalidItem)
            {
                purchaseValid = false;
                errorProduct = requestedProduct;
                break;
            }

            overflowMails.AddRange(CharacterPacketBuilders.CreateOverflowMailRequests(
                CharacterInventoryPlanner.CreateMailAttachments(
                    purchasedItem,
                    itemCatalog),
                "Cera shop purchase"));
        }

        if (purchaseValid && immediateContainerRewards.Count > 0)
        {
            var capacityBeforeContents = plannedWarehouseCapacity;
            var upgradeSettlement =
                CharacterWarehouseProgression.SettleUpgradeItems(
                    plannedInventory.Values,
                    session.Inventory.Warehouse.Values,
                    plannedWarehouseCapacity);
            plannedInventory = upgradeSettlement.Inventory
                .ToDictionary(item => item.Slot);
            plannedWarehouseCapacity = upgradeSettlement.Capacity;
            foreach (var reward in immediateContainerRewards)
            {
                plannedWarehouseCapacity =
                    CharacterWarehouseProgression.ApplyItemTarget(
                        plannedWarehouseCapacity,
                        reward.ItemId);
            }

            warehouseUpgraded |= plannedWarehouseCapacity
                != capacityBeforeContents;
            var containerOverflowMails = CharacterPacketBuilders.CreateOverflowMailRequests(
                immediateContainerOverflow.Where(item =>
                    !CharacterWarehouseProgression
                        .TryGetItemTargetCapacity(item.ItemId, out _)),
                "Cera shop container reward");
            immediateContainerMailCount = containerOverflowMails.Length;
            overflowMails.AddRange(containerOverflowMails);
        }

        if (!purchaseValid || session.Currency.Cash < totalCashPrice)
        {
            var error = ShopPacketBuilders.CreateCeraShopBuyError(
                errorCode: 4,
                errorClientCategory,
                errorProduct.CommodityNo);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            logger.LogWarning(
                "Rejected Cera-shop purchase commodity={CommodityNo}, products={Count}, cash={Cash}, price={Price}.",
                errorProduct.CommodityNo,
                requestedProducts.Length,
                session.Currency.Cash,
                totalCashPrice);
            return;
        }

        var nextCash = session.Currency.Cash - checked((int)totalCashPrice);
        var savedCharacter =
            await store.SaveCharacterCeraShopPurchaseAsync(
                session.Login.AccountName,
                session.Character.Active.Id,
                nextCash,
                plannedGold,
                plannedInventory.Values,
                session.Inventory.Equipped.Values,
                cancellationToken,
                overflowMails,
                plannedAvatarInventory.Values,
                plannedCreatureInventory.Values,
                premiumServiceGrants,
                victoryPoints: plannedVictoryPoints,
                expectedCash: session.Currency.Cash,
                warehouseCapacity: plannedWarehouseCapacity,
                expectedWarehouseCapacity: session.Inventory.WarehouseCapacity);
        if (savedCharacter is null)
        {
            var error = ShopPacketBuilders.CreateCeraShopBuyError(
                errorCode: 4,
                errorClientCategory,
                errorProduct.CommodityNo);
            await context.Outbound.SendAsync(error, Name, cancellationToken);
            return;
        }

        _ = senders.AdoptSavedWarehouseUpgradeState(context, savedCharacter);
        session.Currency.Cash = nextCash;
        session.Currency.Gold = savedCharacter.Gold;
        session.Currency.VictoryPoints = savedCharacter.VictoryPoints;
        session.Inventory.Avatar = (savedCharacter.AvatarInventory ?? [])
            .ToDictionary(item => item.Slot);
        session.Inventory.Creature = (savedCharacter.CreatureInventory ?? [])
            .ToDictionary(item => item.Slot);
        var accountCharacters = await store.GetCharactersAsync(
            session.Login.AccountName,
            cancellationToken);
        foreach (var accountCharacter in accountCharacters.Where(
                     character => character.Id != session.Character.Active.Id))
        {
            characterSessions.NotifyCera(
                accountCharacter.Id,
                checked((uint)session.Currency.Cash));
        }
        if (overflowMails.Count > 0)
        {
            characterSessions.NotifyMailReceived(session.Character.Active.CharacterNo);
        }

        if (premiumServiceGrants.Count > 0)
        {
            var premiumStates = await store.GetPremiumServicesAsync(
                session.Login.AccountName,
                cancellationToken);
            foreach (var serviceType in premiumServiceGrants
                         .Select(grant => grant.ServiceType)
                         .Distinct())
            {
                var premiumState = premiumStates.FirstOrDefault(state =>
                    state.ServiceType == serviceType);
                if (premiumState?.Active == true)
                {
                    await senders.SendPremiumServiceInfoAsync(context, 
                        serviceType,
                        premiumState.RemainingSeconds);
                    foreach (var accountCharacter in accountCharacters.Where(
                                 character => character.Id
                                     != session.Character.Active.Id))
                    {
                        characterSessions.NotifyPremiumInfo(
                            accountCharacter.Id,
                            serviceType,
                            premiumState.RemainingSeconds);
                    }
                }
            }
        }
        for (var index = 0; index < requestedProducts.Length; index++)
        {
            var requestedProduct = requestedProducts[index];
            // The request option byte is commonly 0xFF
            // and is not the category consumed by the
            // command-67 response handler. Return the
            // category of the resolved catalog product so
            // the client closes and refreshes the correct
            // Cera-shop panel.
            _ = ceraShopCatalog.TryGetProduct(
                requestedProduct.CommodityNo,
                out var purchasedProduct);
            var buyReply =
                ShopPacketBuilders.CreateCeraShopBuyReply(
                    purchasedProduct.ClientCategory,
                    requestedProduct.CommodityNo,
                    purchaseReplyRewards[index]);
            await context.Outbound.SendAsync(buyReply, Name, cancellationToken);
        }

        if (warehouseUpgraded)
        {
            var warehouseRefresh = builders.CreateWarehousePacket(
                session.Inventory.Warehouse.Values,
                session.Inventory.WarehouseCapacity);
            await context.Outbound.SendAsync(warehouseRefresh, Name, cancellationToken);
        }

        var mainRefresh = builders.CreateMainInventoryPacket(
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            session.Inventory.Main.Values);
        await context.Outbound.SendAsync(mainRefresh, Name, cancellationToken);

        var avatarRefresh = builders.CreateAvatarInventoryPacket(
            session.Inventory.Avatar.Values);
        await context.Outbound.SendAsync(avatarRefresh, Name, cancellationToken);

        foreach (var creatureRefresh in builders.CreateCreatureInventoryPackets(
                     session.Inventory.Creature.Values))
        {
            await context.Outbound.SendAsync(creatureRefresh, Name, cancellationToken);
        }

        var cashUpdate = ShopPacketBuilders.CreateCeraUpdate(
            success: true,
            value: (uint)session.Currency.Cash);
        await context.Outbound.SendAsync(cashUpdate, Name, cancellationToken);

        // The command-67 result window is limited and does not
        // replace the authoritative inventory refresh. Keep the
        // chat entries so every reward and its full count remain
        // visible even when a package contains many item kinds.
        foreach (var rewardGroup in immediateContainerRewards
                     .GroupBy(reward => reward.ItemId))
        {
            var rewardCount = rewardGroup.Aggregate(
                0UL,
                (total, reward) => checked(total + reward.Count));
            var rewardName = itemCatalog.TryGetDefinition(
                    rewardGroup.Key,
                    out var rewardDefinition)
                && !string.IsNullOrWhiteSpace(rewardDefinition.Name)
                    ? rewardDefinition.Name
                    : $"item {rewardGroup.Key}";
            await senders.SendTypedMessageNotificationSafeAsync(context, GameMessageType.Type16Brown, 
                0,
                ClientEncoding.GetBytes(
                    $"获得[{rewardName}]{rewardCount}个"));
        }

        if (immediateContainerMailCount > 0)
        {
            await senders.SendTypedMessageNotificationSafeAsync(context, GameMessageType.Type16Brown, 
                0,
                ClientEncoding.GetBytes(
                    "背包空间不足，部分奖励已发送至邮箱。"));
        }

        var purchasedProductSummary = string.Join(
            ", ",
            requestedProducts.Select(requestedProduct =>
                ceraShopCatalog.TryGetProduct(
                    requestedProduct.CommodityNo,
                    out var purchasedProduct)
                    ? $"{purchasedProduct.CommodityNo}:item={purchasedProduct.ItemId}x{purchasedProduct.Quantity}@{purchasedProduct.CashPrice}"
                    : $"{requestedProduct.CommodityNo}:unresolved"));
        logger.LogInformation(
            "Completed Cera-shop purchase [{Products}] ({Count} product(s)) for {Price}; cash={Cash}, gold={Gold}, victoryPoints={VictoryPoints}, openedContainers={OpenedContainerCount}, containerRewards={ContainerRewardCount}, PremiumGrants={PremiumGrantCount}, WarehouseCapacity={WarehouseCapacity}.",
            purchasedProductSummary,
            requestedProducts.Length,
            totalCashPrice,
            session.Currency.Cash,
            session.Currency.Gold,
            session.Currency.VictoryPoints,
            openedContainerCount,
            immediateContainerRewards.Count,
            premiumServiceGrants.Count,
            session.Inventory.WarehouseCapacity);
    }
}
