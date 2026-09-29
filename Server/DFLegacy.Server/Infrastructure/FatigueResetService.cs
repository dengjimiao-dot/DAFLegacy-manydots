namespace DFLegacy.Server;

public sealed class FatigueResetService(
    JsonGameStore store,
    CharacterSessionRegistry characterSessions,
    ILogger<FatigueResetService> logger) : BackgroundService
{
    private static readonly TimeSpan ResetTime = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaximumClockCheckDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var result = await store.ResetFatigueForCurrentDayAsync(
                now,
                stoppingToken);
            if (result.NewFatigueDay)
            {
                foreach (var characterId in result.CharacterIds)
                {
                    characterSessions.NotifyFatigueReset(characterId);
                }

                logger.LogInformation(
                    "Daily 06:00 fatigue reset completed for day {FatigueDayKey}; reset {CharacterCount} character(s).",
                    result.FatigueDayKey,
                    result.ResetCharacterCount);
            }

            await Task.Delay(GetNextCheckDelay(DateTimeOffset.Now), stoppingToken);
        }
    }

    private static TimeSpan GetNextCheckDelay(DateTimeOffset now)
    {
        var nextResetDate = now.TimeOfDay < ResetTime
            ? now.Date
            : now.Date.AddDays(1);
        var nextLocalTime = nextResetDate + ResetTime;
        var nextOffset = TimeZoneInfo.Local.GetUtcOffset(nextLocalTime);
        var nextReset = new DateTimeOffset(
            DateTime.SpecifyKind(nextLocalTime, DateTimeKind.Unspecified),
            nextOffset);
        var delay = nextReset - now;
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return delay < MaximumClockCheckDelay
            ? delay
            : MaximumClockCheckDelay;
    }
}
