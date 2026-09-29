using DFLegacy.Protocol;

namespace DFLegacy.Server;

public sealed record CeraShopProduct(
    uint CommodityNo,
    ushort ItemId,
    uint Quantity,
    ushort AttributeValue,
    int CashPrice,
    string Section,
    PremiumContractDefinition? PremiumContract = null,
    ushort? WarehouseUpgradeCapacity = null,
    CeraShopDirectCurrencyGrant? DirectCurrencyGrant = null)
{
    public bool UsesMainInventory =>
        !string.Equals(Section, "avatar", StringComparison.OrdinalIgnoreCase);

    public int ClientCategory =>
        Section.ToLowerInvariant() switch
        {
            "avatar" => 0,
            "coin" => 1,
            "creature" => 3,
            _ => 2
        };
}

public sealed record PremiumContractDefinition(byte ServiceType, int Days);
public sealed record CeraShopDirectCurrencyGrant(int Gold, uint VictoryPoints);

public static class CeraShopDirectCurrencyPlanner
{
    public static bool TryApply(
        int currentGold,
        uint currentVictoryPoints,
        CeraShopDirectCurrencyGrant grant,
        uint quantity,
        out int nextGold,
        out uint nextVictoryPoints)
    {
        ArgumentNullException.ThrowIfNull(grant);
        nextGold = currentGold;
        nextVictoryPoints = currentVictoryPoints;
        if (currentGold < 0 || grant.Gold < 0 || quantity == 0)
        {
            return false;
        }

        var calculatedGold = (long)currentGold + (long)grant.Gold * quantity;
        var calculatedVictoryPoints = (ulong)currentVictoryPoints
            + (ulong)grant.VictoryPoints * quantity;
        if (calculatedGold > int.MaxValue
            || calculatedVictoryPoints > uint.MaxValue)
        {
            return false;
        }

        nextGold = checked((int)calculatedGold);
        nextVictoryPoints = checked((uint)calculatedVictoryPoints);
        return true;
    }
}

public sealed class CeraShopCatalog
{
    private readonly ScriptFileSystem _scripts;
    private readonly ItemCatalog _items;
    private readonly ILogger<CeraShopCatalog> _logger;
    private readonly Lazy<IReadOnlyDictionary<uint, CeraShopProduct>> _products;

    public CeraShopCatalog(
        ScriptFileSystem scripts,
        ItemCatalog items,
        ILogger<CeraShopCatalog> logger)
    {
        _scripts = scripts;
        _items = items;
        _logger = logger;
        _products = new Lazy<IReadOnlyDictionary<uint, CeraShopProduct>>(
            LoadProducts,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => _products.Value.Count;
    public IEnumerable<CeraShopProduct> Products => _products.Value.Values;

    public bool TryGetProduct(uint commodityNo, out CeraShopProduct product) =>
        _products.Value.TryGetValue(commodityNo, out product!);

    public bool TryGetPremiumContract(
        ushort itemId,
        out PremiumContractDefinition contract)
    {
        contract = null!;
        return _items.TryGetDefinition(itemId, out var definition)
            && TryResolvePremiumContract(definition, out contract);
    }

    public bool TryGetDirectCurrencyGrant(
        ushort itemId,
        out CeraShopDirectCurrencyGrant grant)
    {
        grant = null!;
        return _items.TryGetDefinition(itemId, out var definition)
            && TryResolveDirectCurrencyGrant(itemId, definition, out grant);
    }

    private IReadOnlyDictionary<uint, CeraShopProduct> LoadProducts()
    {
        var products = new Dictionary<uint, CeraShopProduct>();
        const string path = "etc/newcashshop.etc";
        if (!_scripts.FileExists(path))
        {
            _logger.LogWarning(
                "DFLegacy Cera-shop catalog was not found in {Source}.",
                _scripts.SourceDescription);
            return products;
        }

        var section = string.Empty;
        foreach (var rawLine in _scripts.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section.StartsWith('/'))
                {
                    section = string.Empty;
                }

                continue;
            }

            if (!TryParseProductLine(section, line, out var product))
            {
                continue;
            }

            products[product.CommodityNo] = product;
        }

        _logger.LogInformation(
            "Loaded {Count} DFLegacy Cera-shop products from {Path}.",
            products.Count,
            _scripts.SourceDescription);
        return products;
    }

    private bool TryParseProductLine(
        string section,
        string line,
        out CeraShopProduct product)
    {
        product = null!;
        var descriptionStart = line.IndexOf('`');
        var numericPrefix = descriptionStart >= 0 ? line[..descriptionStart] : line;
        var fields = numericPrefix.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries);

        if (string.Equals(section, "avatar", StringComparison.OrdinalIgnoreCase))
        {
            if (fields.Length < 3
                || !uint.TryParse(fields[0], out var avatarCommodityNo)
                || !ushort.TryParse(fields[1], out var avatarItemId)
                || !ushort.TryParse(fields[2], out var avatarAttributeValue)
                || avatarCommodityNo == 0
                || avatarItemId == 0
                || !_items.TryGetDefinition(avatarItemId, out var avatarDefinition)
                || avatarDefinition.InventoryCategory != ItemInventoryCategory.Avatar
                || avatarDefinition.Cash is null or < 0
                || !TryCalculateAvatarCashPrice(
                    avatarDefinition.Cash.Value,
                    avatarAttributeValue,
                    out var avatarCashPrice))
            {
                return false;
            }

            product = new CeraShopProduct(
                avatarCommodityNo,
                avatarItemId,
                1,
                avatarAttributeValue,
                avatarCashPrice,
                section);
            return true;
        }

        var isPackage = string.Equals(section, "package", StringComparison.OrdinalIgnoreCase);
        var isStandard = section is "coin"
            or "item"
            or "creature"
            or "premium"
            or "material"
            or "recoveryitem"
            or "visual";
        if (!isPackage && !isStandard)
        {
            return false;
        }

        var requiredFieldCount = isPackage ? 5 : 6;
        if (fields.Length < requiredFieldCount
            || !uint.TryParse(fields[0], out var commodityNo)
            || !ushort.TryParse(fields[1], out var itemId)
            || commodityNo == 0
            || itemId == 0)
        {
            return false;
        }

        var quantity = 1u;
        if (!isPackage
            && (!uint.TryParse(fields[2], out quantity) || quantity == 0))
        {
            quantity = 1;
        }

        var priceField = isPackage ? fields[4] : fields[5];
        if (!int.TryParse(priceField, out var cashPrice) || cashPrice < 0)
        {
            return false;
        }

        PremiumContractDefinition? premiumContract = null;
        ushort? warehouseUpgradeCapacity = null;
        CeraShopDirectCurrencyGrant? directCurrencyGrant = null;
        if (_items.TryGetDefinition(itemId, out var definition))
        {
            if (TryResolvePremiumContract(definition, out var resolvedContract))
            {
                premiumContract = resolvedContract;
            }

            if (TryResolveWarehouseUpgradeCapacity(
                    commodityNo,
                    itemId,
                    definition,
                    out var resolvedWarehouseCapacity))
            {
                warehouseUpgradeCapacity = resolvedWarehouseCapacity;
            }

            if (TryResolveDirectCurrencyGrant(
                    itemId,
                    definition,
                    out var resolvedCurrencyGrant))
            {
                directCurrencyGrant = resolvedCurrencyGrant;
            }
        }

        product = new CeraShopProduct(
            commodityNo,
            itemId,
            quantity,
            0,
            cashPrice,
            section,
            premiumContract,
            warehouseUpgradeCapacity,
            directCurrencyGrant);
        return true;
    }

    private static bool TryResolveDirectCurrencyGrant(
        ushort itemId,
        ItemDefinition definition,
        out CeraShopDirectCurrencyGrant grant)
    {
        grant = null!;
        if (definition.ScriptKind != ItemScriptKind.Stackable)
        {
            return false;
        }

        var path = definition.ScriptPath.Replace('\\', '/').ToLowerInvariant();
        // These target-client cash vouchers are settled by the Cera-shop
        // transaction itself. They never become usable inventory objects.
        grant = (itemId, path) switch
        {
            (190, "stackable/cash/gold_voucher.stk") => new(1_000_000, 0),
            (191, "stackable/cash/victorypoint_voucher.stk") => new(0, 100),
            _ => null!
        };
        return grant is not null;
    }

    private static bool TryResolveWarehouseUpgradeCapacity(
        uint commodityNo,
        ushort itemId,
        ItemDefinition definition,
        out ushort capacity)
    {
        capacity = 0;
        if (definition.ScriptKind != ItemScriptKind.Stackable)
        {
            return false;
        }

        var path = definition.ScriptPath.Replace('\\', '/').ToLowerInvariant();
        // DNF 1.0.1.9 binds each visible upgrade stage to an exact commodity,
        // item and script path; legacy or later-version cargo items are not aliases.
        capacity = (commodityNo, itemId, path) switch
        {
            (100_063, 50, "stackable/cash/safe_upgradekit.stk") => 24,
            (100_064, 57, "stackable/cash/safe_upgradekit2.stk") => 40,
            (100_065, 58, "stackable/cash/safe_upgradekit3.stk") => 56,
            (100_066, 59, "stackable/cash/safe_upgradekit4.stk") => 72,
            (100_067, 60, "stackable/cash/safe_upgradekit5.stk") => 88,
            (100_068, 61, "stackable/cash/safe_upgradekit6.stk") => 104,
            (100_069, 62, "stackable/cash/safe_upgradekit7.stk") => 120,
            _ => 0
        };
        return capacity != 0;
    }

    private static bool TryResolvePremiumContract(
        ItemDefinition definition,
        out PremiumContractDefinition contract)
    {
        contract = null!;
        if (definition.ScriptKind != ItemScriptKind.Stackable)
        {
            return false;
        }

        var path = definition.ScriptPath.Replace('\\', '/').ToLowerInvariant();
        contract = path switch
        {
            "stackable/cash/contract_monarch1.stk" =>
                new(GameProtocolEngine.OverlordContractServiceType, 1),
            "stackable/cash/contract_monarch3.stk" =>
                new(GameProtocolEngine.OverlordContractServiceType, 3),
            "stackable/cash/contract_monarch5.stk" =>
                new(GameProtocolEngine.OverlordContractServiceType, 5),
            "stackable/cash/contract_monarch7.stk" =>
                new(GameProtocolEngine.OverlordContractServiceType, 7),
            "stackable/cash/contract_monarch15.stk" =>
                new(GameProtocolEngine.OverlordContractServiceType, 15),
            "stackable/cash/contract_expert3.stk" =>
                new(GameProtocolEngine.MasterContractServiceType, 1),
            "stackable/cash/contract_expert7.stk" =>
                new(GameProtocolEngine.MasterContractServiceType, 3),
            "stackable/cash/contract_expert15.stk" =>
                new(GameProtocolEngine.MasterContractServiceType, 7),
            "stackable/cash/contract_expert30.stk" =>
                new(GameProtocolEngine.MasterContractServiceType, 15),
            _ => null!
        };
        return contract is not null;
    }

    private static bool TryCalculateAvatarCashPrice(
        int basePrice,
        ushort attributeValue,
        out int cashPrice)
    {
        if (attributeValue is < 1 or > 3)
        {
            cashPrice = 0;
            return false;
        }

        // TODO: Restore the client's 1.0x/2.2x/6.6x period pricing after
        // avatar expiration timestamps and expiry enforcement are implemented.
        // All avatar purchases are currently permanent, so charge 6.6x.
        var calculatedPrice = ((long)basePrice * 22 / 10) * 3;
        if (calculatedPrice is < 0 or > int.MaxValue)
        {
            cashPrice = 0;
            return false;
        }

        cashPrice = (int)calculatedPrice;
        return true;
    }

    private static string StripComment(string line)
    {
        var insideDescription = false;
        for (var index = 0; index + 1 < line.Length; index++)
        {
            if (line[index] == '`')
            {
                insideDescription = !insideDescription;
                continue;
            }

            if (!insideDescription && line[index] == '/' && line[index + 1] == '/')
            {
                return line[..index];
            }
        }

        return line;
    }
}
