namespace DFLegacy.Server;

public sealed record CreatureHungerSettlementPlan(
    Dictionary<ushort, CharacterItemRecord> Inventory,
    uint CreatureUid,
    byte PreviousStomach,
    byte Stomach,
    byte PreviousRemainderSeconds,
    byte RemainderSeconds,
    uint ElapsedSeconds,
    uint ConsumedFoodCount,
    IReadOnlyList<ushort> ChangedFoodSlots)
{
    public bool StomachChanged => Stomach != PreviousStomach;

    // 13339's CCreature::IsDieCreature is true when the effective stomach is
    // below one.  Report the transition only once; a persisted zero value is
    // already represented by the client's dead Creature object.
    public bool BecameDead => PreviousStomach > 0 && Stomach == 0;

    public bool Changed => StomachChanged
        || RemainderSeconds != PreviousRemainderSeconds
        || ConsumedFoodCount != 0;
}

public static class CreatureHungerPlanner
{
    public const ushort FoodItemId = 24;
    public const byte MaximumStomach = 100;
    public const byte FoodRecovery = 30;
    public const uint SecondsPerStomachPoint = 60;

    public static bool TrySettle(
        IReadOnlyDictionary<ushort, CharacterItemRecord> inventory,
        uint elapsedSeconds,
        ItemCatalog catalog,
        out CreatureHungerSettlementPlan plan)
    {
        plan = null!;
        if (!inventory.TryGetValue(
                CharacterCreatureInventoryLayout.EquippedCreatureSlot,
                out var creature)
            || creature.CountOrValue == 0
            || !catalog.TryGetDefinition(creature.ItemId, out var definition)
            || !CharacterCreatureInventoryLayout.IsCreature(definition)
            || definition.CreatureSubType == 1)
        {
            return false;
        }

        var previousStomach = (byte)Math.Clamp(
            (int)(creature.CreatureStomach ?? MaximumStomach),
            0,
            MaximumStomach);
        var previousRemainder = (byte)Math.Clamp(
            (int)(creature.CreatureStomachRemainderSeconds ?? 0),
            0,
            SecondsPerStomachPoint - 1);
        var stomach = previousStomach;
        var totalSeconds = (ulong)previousRemainder + elapsedSeconds;
        var hungerPoints = totalSeconds / SecondsPerStomachPoint;
        var remainderSeconds = checked((byte)(
            totalSeconds % SecondsPerStomachPoint));
        var availableFood = inventory.Values
            .Where(item => item.ItemId == FoodItemId)
            .Aggregate(
                0UL,
                (total, item) => Math.Min(
                    ulong.MaxValue,
                    total + item.CountOrValue));
        ulong consumedFood = 0;

        if (stomach == 0)
        {
            // A Creature that was already starved does not consume food without
            // a new transition to zero. Manual feeding/revival remains separate.
            remainderSeconds = 0;
        }
        else if (hungerPoints < stomach)
        {
            stomach = checked((byte)(stomach - hungerPoints));
        }
        else
        {
            var pointsAfterFirstZero = hungerPoints - stomach;
            var foodNeeded = pointsAfterFirstZero / FoodRecovery + 1;
            consumedFood = Math.Min(foodNeeded, availableFood);
            if (consumedFood < foodNeeded)
            {
                stomach = 0;
                remainderSeconds = 0;
            }
            else
            {
                var pointsAfterLastFood = pointsAfterFirstZero % FoodRecovery;
                stomach = checked((byte)(FoodRecovery - pointsAfterLastFood));
            }
        }

        var planned = inventory.ToDictionary(pair => pair.Key, pair => pair.Value);
        var changedFoodSlots = new List<ushort>();
        var foodToConsume = consumedFood;
        foreach (var pair in planned
                     .Where(pair => pair.Value.ItemId == FoodItemId)
                     .OrderBy(pair => pair.Key)
                     .ToArray())
        {
            if (foodToConsume == 0)
            {
                break;
            }

            var consumedFromSlot = Math.Min(
                (ulong)pair.Value.CountOrValue,
                foodToConsume);
            var remaining = pair.Value.CountOrValue - checked((uint)consumedFromSlot);
            if (remaining == 0)
            {
                planned.Remove(pair.Key);
            }
            else
            {
                planned[pair.Key] = pair.Value with { CountOrValue = remaining };
            }

            changedFoodSlots.Add(pair.Key);
            foodToConsume -= consumedFromSlot;
        }

        planned[CharacterCreatureInventoryLayout.EquippedCreatureSlot] =
            creature with
            {
                CreatureStomach = stomach,
                CreatureStomachRemainderSeconds = remainderSeconds
            };
        plan = new CreatureHungerSettlementPlan(
            planned,
            creature.CountOrValue,
            previousStomach,
            stomach,
            previousRemainder,
            remainderSeconds,
            elapsedSeconds,
            checked((uint)consumedFood),
            changedFoodSlots);
        return true;
    }
}
