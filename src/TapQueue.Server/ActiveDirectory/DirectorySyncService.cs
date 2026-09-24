namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// Runs the daily Active Directory sync at the configured time in the server's time zone. If the
/// server was down at that time, it catches up shortly after starting.
///
/// The schedule only takes over after an admin has run a sync by hand, so the first one (which can
/// add hundreds of users and link local ones) is always looked at in a preview first.
/// </summary>
public sealed class DirectorySyncService(DirectoryStore store, DirectorySync sync, ILogger<DirectorySyncService> logger) : BackgroundService
{
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunRetention = TimeSpan.FromDays(90);

    private CancellationTokenSource _wake = new();

    /// <summary>Picks up a new time or enabled setting straight away.</summary>
    public void Reschedule() => _wake.Cancel();

    /// <summary>When the next daily sync runs, or null if the daily sync is off.</summary>
    public DateTimeOffset? NextRunAt()
    {
        var config = store.Config();
        return config.Enabled && DirectoryConfig.IsValidTime(config.SyncTime) ? NextRun(config.DailyAt, DateTimeOffset.Now, TimeZoneInfo.Local) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.AbandonUnfinishedRuns();
        var catchUp = store.LastCompletedSync() is { } last && DateTimeOffset.UtcNow - last.StartedAt > TimeSpan.FromHours(25);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = catchUp ? DateTimeOffset.Now + CatchUpDelay : NextRunAt();
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _wake.Token);
            try
            {
                await Task.Delay(next is { } at ? Clamp(at - DateTimeOffset.Now) : Timeout.InfiniteTimeSpan, wake.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _wake = new CancellationTokenSource();
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (next is { } due && DateTimeOffset.Now >= due - TimeSpan.FromSeconds(1))
            {
                catchUp = false;
                RunScheduled();
            }
        }
    }

    private void RunScheduled()
    {
        if (!store.Config().Enabled)
            return;
        if (store.LastCompletedSync() is null)
        {
            logger.LogInformation("Skipping the daily directory sync: run the first one from the console or CLI");
            return;
        }
        try
        {
            sync.Run(DirectorySync.ScheduleTrigger, dryRun: false, force: false);
            store.DeleteRunsOlderThan(DateTimeOffset.UtcNow - RunRetention);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("Skipping the daily directory sync: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Directory sync failed");
        }
    }

    /// <summary>Task.Delay can't wait longer than about 49 days, and a time just passed runs now.</summary>
    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1.5) ? TimeSpan.FromDays(1.5) : wait;

    /// <summary>The next time the clock in <paramref name="zone"/> reads <paramref name="at"/>, after <paramref name="now"/>.</summary>
    internal static DateTimeOffset NextRun(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        for (var day = DateOnly.FromDateTime(local.DateTime); ; day = day.AddDays(1))
        {
            var candidate = day.ToDateTime(at);
            // A time skipped by the clocks going forward runs an hour later that day.
            if (zone.IsInvalidTime(candidate))
                candidate = candidate.AddHours(1);
            var result = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
            if (result > now)
                return result;
        }
    }
}
