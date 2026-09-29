namespace DFLegacy.Server;

/// <summary>
/// Dungeon run state: selection countdown, monster session, clear-card and
/// result bookkeeping (07 §4.3). Populated when group 13 migrates in R5.
/// </summary>
public sealed class DungeonState
{
    public ushort CurrentDungeonId { get; set; }
}
