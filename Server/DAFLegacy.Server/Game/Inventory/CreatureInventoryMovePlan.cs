namespace DAFLegacy.Server;

/// <summary>
/// Planned outcome of a creature-space inventory move (07 §5 group 9/12):
/// the resulting warehouse and creature dictionaries plus the reply values
/// the client needs. Moved out of EntranceService so the move planner can
/// live with the session senders during the migration.
/// </summary>
internal sealed record CreatureInventoryMovePlan(
    Dictionary<ushort, CharacterItemRecord> Warehouse,
    Dictionary<ushort, CharacterItemRecord> CreatureInventory,
    uint MovedCount,
    ushort ReplyDestinationSlot,
    ushort ItemId,
    bool EquippedSlotsChanged,
    bool EquippedCreatureChanged);
