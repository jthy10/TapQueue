using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>Sends a user's held jobs to a physical printer.</summary>
public sealed class ReleaseService(JobStore jobs, Spool spool, PrinterRegistry printers, AccessPolicy access, QuotaPolicy quotas, EventLog events, ILogger<ReleaseService> logger)
{
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(3);

    /// <param name="actor">Who asked, for the activity log: the user, "admin" or "station:&lt;id&gt;".</param>
    /// <param name="via">How, as it reads after the printer's name: "at station office", "from JAKE-PC"…</param>
    public async Task<ReleaseResponse> ReleaseAsync(UserRecord user, PrinterRecord printer, IReadOnlyList<long>? jobIds,
        string actor, string via, CancellationToken ct)
    {
        var response = await ReleaseJobsAsync(user, printer, jobIds, ct);
        var sent = response.Results.Where(r => r.Success).ToList();
        var failed = response.Results.Where(r => !r.Success).ToList();
        if (sent.Count > 0)
            events.Record(EventCategory.Job, actor, EventLog.User(user.Username),
                $"{user.Username}'s {Jobs(sent)} released to {printer.Name} {via}.");
        if (failed.Count > 0)
            events.Record(EventCategory.Job, actor, EventLog.User(user.Username),
                $"Couldn't release {user.Username}'s {Jobs(failed)} to {printer.Name} {via}: {failed[0].Error}");
        return response;
    }

    private static string Jobs(List<ReleaseResult> results) =>
        results.Count == 1 ? $"\"{results[0].JobName}\"" : $"{results.Count} jobs";

    private async Task<ReleaseResponse> ReleaseJobsAsync(UserRecord user, PrinterRecord printer, IReadOnlyList<long>? jobIds, CancellationToken ct)
    {
        var held = jobs.ListForUser(user.Id, heldOnly: true);
        var refusal = user.Disabled ? "This account is disabled."
            : !access.CanReleaseAt(user.Id, printer.Id) ? $"You aren't allowed to print at {printer.Name}."
            : null;
        if (refusal is not null)
            return new ReleaseResponse(held.Select(j => new ReleaseResult(j.Id, j.Name, false, refusal)).ToList());
        var toRelease = jobIds is null ? held : held.Where(j => jobIds.Contains(j.Id)).ToList();

        var results = new List<ReleaseResult>();
        if (jobIds is not null)
        {
            foreach (var missing in jobIds.Except(toRelease.Select(j => j.Id)))
                results.Add(new ReleaseResult(missing, "", false, "No held job with that id belongs to this user."));
        }

        // Oldest first, so pages come out in the order they were printed.
        foreach (var job in toRelease.OrderBy(j => j.Id))
            results.Add(await ReleaseOneAsync(user, job, printer, ct));
        return new ReleaseResponse(results);
    }

    private async Task<ReleaseResult> ReleaseOneAsync(UserRecord user, JobRecord job, PrinterRecord printer, CancellationToken ct)
    {
        // Checked job by job, since each one released uses up some of the limit.
        if (quotas.Refusal(user.Id, job.ChargedPages, DateTimeOffset.UtcNow) is { } overLimit)
            return new ReleaseResult(job.Id, job.Name, false, overLimit);
        if (!jobs.TryTransition(job.Id, JobStatus.Held, JobStatus.Releasing))
            return new ReleaseResult(job.Id, job.Name, false, "Job is no longer held.");

        try
        {
            var formats = printers.StatusOf(printer.Id)?.DocumentFormats ?? [];
            if (formats.Count > 0 && !formats.Contains(job.DocumentFormat, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{printer.Name} can't print {job.DocumentFormat} documents.");

            var request = IppMessage.CreateRequest(IppOperation.PrintJob, IppClient.NextRequestId(), printer.Uri);
            request.Group(IppTag.OperationAttributes)
                .Add("requesting-user-name", IppValue.Name(user.Username))
                .Add("job-name", IppValue.Name(job.Name))
                .Add("document-format", IppValue.MimeType(job.DocumentFormat));
            // Replay what the user picked in the print dialog (copies, page ranges, orientation…).
            // Without ipp-attribute-fidelity the printer ignores anything it can't do rather than failing.
            if (jobs.GetJobAttributes(job.Id) is { } template)
                request.Groups.Add(await JobTemplate.DecodeAsync(template, ct));

            using var client = new IppClient(printer.TlsSkipVerify, TimeSpan.FromMinutes(5));
            var busyUntil = DateTimeOffset.UtcNow + BusyTimeout;
            while (true)
            {
                IppMessage response;
                await using (var document = spool.OpenRead(job.Id))
                    response = await client.SendAsync(printer.Uri, request, document, ct);
                if (IppStatus.IsSuccess(response.Code))
                    break;

                // Some printers take one job at a time and answer "busy" until the last one is done.
                if (response.Code == IppStatus.ServerErrorBusy && DateTimeOffset.UtcNow < busyUntil)
                {
                    await Task.Delay(BusyRetryDelay, ct);
                    continue;
                }
                var message = response.OperationString("status-message");
                throw new InvalidOperationException($"Printer rejected the job (IPP status 0x{response.Code:X4}{(message is null ? "" : $": {message}")}).");
            }

            jobs.MarkReleased(job.Id, printer.Id);
            spool.Delete(job.Id);
            logger.LogInformation("Released job {JobId} \"{Name}\" for {User} to {Printer}", job.Id, job.Name, user.Username, printer.Id);
            return new ReleaseResult(job.Id, job.Name, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Put it back on hold so the user can try again at another printer.
            jobs.TryTransition(job.Id, JobStatus.Releasing, JobStatus.Held, ex.Message);
            logger.LogWarning(ex, "Failed to release job {JobId} for {User} to {Printer}", job.Id, user.Username, printer.Id);
            return new ReleaseResult(job.Id, job.Name, false, ex.Message);
        }
        catch
        {
            jobs.TryTransition(job.Id, JobStatus.Releasing, JobStatus.Held, "Release was interrupted.");
            throw;
        }
    }
}
