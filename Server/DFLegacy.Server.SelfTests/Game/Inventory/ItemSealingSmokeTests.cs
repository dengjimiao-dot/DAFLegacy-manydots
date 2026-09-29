using DFLegacy.Server;

internal static class ItemSealingSmokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var sealing = CreateDefinition(ItemAttachType.Sealing);
        var sealedItem = new CharacterItemRecord(
            Slot: 9,
            ItemId: sealing.Id,
            CountOrValue: 1,
            SealState: CharacterItemSealing.Sealed);

        var unsealed = CharacterItemSealing.UnsealWhenEquipped(
            sealedItem,
            sealing);
        check(
            unsealed.SealState == CharacterItemSealing.Unsealed,
            "equipping [sealing] equipment permanently clears its sealed state");
        check(
            !CharacterItemSealing.CanTrade(unsealed, sealing),
            "unsealed [sealing] equipment follows [trade] restrictions");
        check(
            CharacterItemSealing.CanTrade(sealedItem, sealing),
            "sealed [sealing] equipment remains tradeable");
        check(
            CharacterItemSealing.GetInitialSealState(sealing)
                == CharacterItemSealing.Sealed,
            "new [sealing] equipment starts sealed");

        foreach (var attachType in new[] { ItemAttachType.Free, ItemAttachType.Trade })
        {
            var definition = CreateDefinition(attachType);
            var item = sealedItem with { ItemId = definition.Id };
            var unchanged = CharacterItemSealing.UnsealWhenEquipped(item, definition);
            check(
                unchanged.SealState == item.SealState,
                $"equipping [{attachType}] equipment does not alter its instance seal byte");
        }
    }

    private static ItemDefinition CreateDefinition(ItemAttachType attachType) => new(
        Id: attachType == ItemAttachType.Sealing ? (ushort)50001 : (ushort)(50010 + (int)attachType),
        ScriptPath: "equipment/test.equ",
        Name: "Seal smoke item",
        ScriptKind: ItemScriptKind.Equipment,
        InventoryCategory: ItemInventoryCategory.Equipment,
        TypeTag: "weapon",
        AttachType: attachType,
        Grade: 1,
        Rarity: 0,
        Weight: 1,
        MinimumLevel: 1,
        MaximumLevel: null,
        CreationRate: 100,
        StackLimit: null,
        MaximumHavingCount: null,
        Cash: null,
        Price: 1,
        Value: null,
        Durability: 100);
}
