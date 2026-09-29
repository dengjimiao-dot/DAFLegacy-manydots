namespace DFLegacy.Server;

/// <summary>
/// Cumulative session phases used by the dispatch gate (07 §4.8.1 U3).
/// A session at a higher phase satisfies the requirements of every lower
/// phase; <see cref="Any"/> is reachable before authentication.
/// </summary>
public enum CommandPhase
{
    Any = 0,
    Authenticated = 1,
    CharacterSelected = 2,
    InDungeon = 3,
}
