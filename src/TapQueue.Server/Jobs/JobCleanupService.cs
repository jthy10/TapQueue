using TapQueue.Server.Config;
using TapQueue.Server.Data;

namespace TapQueue.Server.Jobs;

/// <summary>Deletes held jobs nobody released in time, half-received jobs, dead client sessions and old activity.</summary>
public sealed class JobCleanupService(JobStore jobs, SessionStore sessions, Spool spool, EventLog events, ServerConfig config, ILogger<JobCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReceivingTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan EventRetention = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                RunOnce(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job cleanup failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public void RunOnce(DateTimeOffset now)
    {
        foreach (var job in jobs.ListStale(now, ReceivingTimeout))
        {
            var newStatus = job.Status == JobStatus.Held ? JobStatus.Expired : JobStatus.Canceled;
            if (!jobs.TryTransition(job.Id, job.Status, newStatus))
                continue;
            spool.Delete(job.Id);
            logger.LogInformation("Job {JobId} \"{Name}\" {Status}", job.Id, job.Name, newStatus);
            if (newStatus == JobStatus.Expired)
                events.Record(EventCategory.Job, "system", job.Username is null ? EventLog.Job(job.Id) : EventLog.User(job.Username),
                    $"\"{job.Name}\" (job #{job.Id}) expired after {config.Jobs.HoldHours} hours without being released.");
        }
        sessions.DeleteExpired(TimeSpan.FromMinutes(config.Auth.SessionTimeoutMinutes));
        events.DeleteOlderThan(now - EventRetention);
    }
}
