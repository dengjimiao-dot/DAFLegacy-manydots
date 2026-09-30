using DAFLegacy.Protocol;

namespace DAFLegacy.Server;

/// <summary>
/// Replayed compound-request entry: the request body plus the reply the
/// server already produced, kept connection-local so a repeated COMPOUND
/// request gets the identical reply instead of re-rolling (07 §4.3).
/// Moved out of EntranceService so the session state domains can reference it.
/// </summary>
public sealed record CompoundItemReplayEntry(
    byte[] RequestBody,
    GameServerPacket Reply,
    bool RefreshWarehouse);
