using System.Text;
using DFLegacy.Protocol;

namespace DFLegacy.Server;

/// <summary>
/// Legacy premium-contract settlement builder (07 §4.7): redeems Cera-shop
/// contract stacks into service grants while selecting a character.
/// </summary>
public sealed partial class CharacterPacketBuilders
{
    internal bool TryPlanLegacyPremiumContractRedemption(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        out Dictionary<ushort, CharacterItemRecord> plannedInventory,
        out IReadOnlyList<PremiumServicePurchaseGrant> grants)
    {
        plannedInventory = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var daysByService = new Dictionary<byte, long>();
        foreach (var item in inventory.Values)
        {
            if (!ceraShopCatalog.TryGetPremiumContract(item.ItemId, out var contract)
                || item.CountOrValue == 0)
            {
                continue;
            }

            var itemDays = (long)contract.Days * item.CountOrValue;
            daysByService[contract.ServiceType] = Math.Min(
                int.MaxValue,
                daysByService.GetValueOrDefault(contract.ServiceType) + itemDays);
            plannedInventory.Remove(item.Slot);
        }

        grants = daysByService
            .Select(pair => new PremiumServicePurchaseGrant(
                pair.Key,
                checked((int)pair.Value)))
            .ToArray();
        return grants.Count > 0;
    }
}
