namespace DAFLegacy.Server;

/// <summary>
/// Protocol-id keyed dispatch table (07 §4.6). One handler per protocol id;
/// duplicate registration fails at startup with both source groups named
/// (07 §3 I5) so conflicts can never surface as runtime misrouting.
/// </summary>
public sealed class GameCommandRegistry
{
    private readonly Dictionary<ushort, IGameCommandHandler> _handlers = [];
    private readonly Dictionary<ushort, string> _sourceGroups = [];

    public void Register(string group, IGameCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(handler);
        if (_handlers.TryGetValue(handler.ProtocolId, out var existing))
        {
            throw new InvalidOperationException(
                $"Duplicate game command registration for protocol {handler.ProtocolId}: "
                + $"'{existing.Name}' from group '{_sourceGroups[handler.ProtocolId]}' conflicts with "
                + $"'{handler.Name}' from group '{group}'.");
        }

        _handlers.Add(handler.ProtocolId, handler);
        _sourceGroups.Add(handler.ProtocolId, group);
    }

    public bool TryGet(ushort protocolId, out IGameCommandHandler handler) =>
        _handlers.TryGetValue(protocolId, out handler!);

    public IReadOnlyCollection<ushort> Registered => _handlers.Keys;

    /// <summary>Registration entries ordered by group then protocol id, for the startup listing.</summary>
    public IEnumerable<(string Group, IGameCommandHandler Handler)> Entries =>
        _handlers
            .Select(pair => (Group: _sourceGroups[pair.Key], Handler: pair.Value))
            .OrderBy(entry => entry.Group, StringComparer.Ordinal)
            .ThenBy(entry => entry.Handler.ProtocolId);
}
