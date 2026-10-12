using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Backups;

/// <summary>
/// Makes the daily backup at the configured time (in the server's time zone setting) and deletes the oldest ones.
/// If the server was down at that time, it catches up shortly after starting.
/// </summary>
public sealed class BackupService(BackupStore backups, ServerSettings settings, EventLog events, ILogger<BackupService> logger) : BackgroundService
{
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);

    private CancellationTokenSource _wake = new();

    /// <summary>Why the last scheduled backup failed; null once one works.</summary>
    public string? LastError { get; private set; }

    /// <summary>Picks up a new time straight away.</summary>
    public void Reschedule() => _wake.Cancel();

    /// <summary>When the next daily backup runs, or null if it's off.</summary>
    public DateTimeOffset? NextRunAt() =>
        settings.BackupTime is { } at ? DirectorySyncService.NextRun(at, DateTimeOffset.UtcNow, settings.TimeZone) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var newest = backups.List().FirstOrDefault(b => b.Kind == BackupKind.Daily);
        var catchUp = settings.BackupTime is not null && (newest is null || DateTimeOffset.UtcNow - newest.CreatedAt > TimeSpan.FromHours(25));
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = catchUp ? DateTimeOffset.UtcNow + CatchUpDelay : NextRunAt();
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _wake.Token);
            try
            {
                await Task.Delay(next is { } at ? Clamp(at - DateTimeOffset.UtcNow) : Timeout.InfiniteTimeSpan, wake.Token);
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

            if (next is { } due && DateTimeOffset.UtcNow >= due - TimeSpan.FromSeconds(1))
            {
                catchUp = false;
                RunScheduled();
            }
        }
    }

    /// <summary>Makes the daily backup and deletes the ones past what's kept.</summary>
    public void RunScheduled()
    {
        if (settings.BackupTime is null)
            return;
        try
        {
            var backup = backups.Create(BackupKind.Daily);
            var deleted = backups.Prune(settings.BackupKeep);
            LastError = null;
            logger.LogInformation("Daily backup {Name} made ({Bytes} bytes); {Deleted} old ones deleted", backup.Name, backup.SizeBytes, deleted);
            if (backups.CopyError is { } copyError)
            {
                logger.LogError("The daily backup couldn't be copied to {CopyTo}: {Error}", backups.CopyTo, copyError);
                events.Record(EventCategory.Admin, EventLog.System, null, $"The daily backup was made but couldn't be copied to {backups.CopyTo}: {copyError}");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "The daily backup failed");
            events.Record(EventCategory.Admin, EventLog.System, null, $"The daily backup failed: {ex.Message}");
        }
    }

    /// <summary>Task.Delay can't wait longer than about 49 days, and a time just passed runs now.</summary>
    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1.5) ? TimeSpan.FromDays(1.5) : wait;
}
