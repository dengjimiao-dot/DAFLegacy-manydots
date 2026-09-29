namespace DFLegacy.Server;

/// <summary>
/// Five inventory spaces plus warehouse capacity (07 §4.3). The compound
/// replay cache is parked here until group 11 (Craft) migration decides its
/// final domain; it holds request-dedup state for the craft commands.
/// </summary>
public sealed class InventoryState
{
    public Dictionary<ushort, CharacterItemRecord> Main = new();

    public Dictionary<ushort, CharacterItemRecord> Avatar = new();

    public Dictionary<ushort, CharacterItemRecord> Equipped = new();

    public Dictionary<ushort, CharacterItemRecord> Warehouse = new();

    public Dictionary<ushort, CharacterItemRecord> Creature = new();

    public ushort WarehouseCapacity = CharacterWarehouseProgression.InitialCapacity;

    public Dictionary<ushort, CompoundItemReplayEntry> CompoundReplay = new();

    public Queue<ushort> CompoundReplayOrder = new();
}
