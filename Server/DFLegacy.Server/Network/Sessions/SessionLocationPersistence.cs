namespace DFLegacy.Server;

/// <summary>
/// Persists the active character's last stable town position (07 §4.3).
/// Shared by the return-to-selection flow, character re-selection and the
/// dungeon return paths; the dungeon-aware save in the connection teardown
/// stays separate because it anchors on the dungeon return position.
/// </summary>
internal static class SessionLocationPersistence
{
    public static async Task<CharacterRecord?> SaveCurrentLocationAsync(
        IGameStore store,
        ChannelSessionState session,
        CancellationToken cancellationToken)
    {
        if (session.Character.Active is null)
        {
            return null;
        }

        session.Character.Active = await store.SaveCharacterLocationAsync(
            session.Login.AccountName,
            session.Character.Active.Id,
            session.Character.TownId,
            session.Character.AreaId,
            session.Character.X,
            session.Character.Y,
            session.Character.Direction,
            cancellationToken) ?? session.Character.Active;
        return session.Character.Active;
    }
}
