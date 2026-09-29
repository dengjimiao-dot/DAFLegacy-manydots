namespace DFLegacy.Server;

public sealed class WeaknessRecoveryService(
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    ILogger<WeaknessRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(GetDelayUntilNextMinute(DateTimeOffset.Now), stoppingToken);

            var updates = await store.AdvanceCharacterWeaknessRecoveryAsync(
                DateTimeOffset.UtcNow,
                stoppingToken);
            foreach (var update in updates)
            {
                characterSessions.NotifyWeaknessRecovery(
                    update.CharacterId,
                    update.Recovery);
            }

            if (updates.Length > 0)
            {
                logger.LogInformation(
                    "Advanced persisted weakness recovery for {CharacterCount} character(s) on the server minute tick; {RecoveredCount} reached full recovery.",
                    updates.Length,
                    updates.Count(update =>
                        update.Recovery == DungeonWeaknessPolicy.FullStamina));
            }
        }
    }

    internal static TimeSpan GetDelayUntilNextMinute(DateTimeOffset now)
    {
        var elapsedTicks = now.Ticks % TimeSpan.TicksPerMinute;
        return TimeSpan.FromTicks(
            elapsedTicks == 0
                ? TimeSpan.TicksPerMinute
                : TimeSpan.TicksPerMinute - elapsedTicks);
    }
}
