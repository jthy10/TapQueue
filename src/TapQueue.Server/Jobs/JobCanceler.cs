using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Deletes a job for the person it belongs to. A held job is simply dropped; it never counted
/// against a page limit. One that was already sent is stopped at the printer if the printer still
/// can, and the pages that didn't print are given back: all of them if it hadn't started (the job
/// then counts as canceled), the rest if the printer says how many it printed. A printer that had
/// started and doesn't say gives nothing back, since a job stopped on its last page would
/// otherwise print for free.
/// </summary>
public sealed class JobCanceler(JobStore jobs, Spool spool, PrinterRegistry printers, QuotaPolicy quotas, EventLog events, PrintTracker tracker, ILogger<JobCanceler> logger)
{
    private static readonly TimeSpan PrinterTimeout = TimeSpan.FromSeconds(15);
    /// <summary>How long a printer gets to say the job it was told to stop has stopped.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    /// <param name="Done">False when the job is still what it was; <paramref name="Message"/> then says why.</param>
    public sealed record Result(bool Done, int RefundedPages, string Message);

    public async Task<Result> CancelAsync(UserRecord user, JobRecord job, CancellationToken ct)
    {
        if (jobs.TryTransition(job.Id, JobStatus.Held, JobStatus.Canceled))
        {
            spool.Delete(job.Id);
            events.Record(EventCategory.Job, user.Username, EventLog.User(user.Username), $"{user.Username} canceled \"{job.Name}\" (job #{job.Id}).");
            return new Result(true, 0, $"Deleted \"{job.Name}\". It hadn't printed, so it never counted against a page limit.");
        }
        job = jobs.Get(job.Id) ?? job;
        if (job.Status != JobStatus.Released)
            return new Result(false, 0, job.Status == JobStatus.Releasing
                ? $"\"{job.Name}\" is on its way to the printer. Try again in a moment."
                : $"\"{job.Name}\" is no longer waiting to print.");
        if (job.RefundedPages > 0)
            return new Result(false, 0, $"\"{job.Name}\" was already stopped at the printer.");
        if (job.PrinterJobId is not { } printerJobId || job.ReleasedPrinterId is null || printers.Find(job.ReleasedPrinterId) is not { } printer)
            return new Result(false, 0, $"\"{job.Name}\" has already been sent to the printer and can't be stopped from here.");

        await tracker.Turn.WaitAsync(ct);
        try
        {
            return await StopAtPrinterAsync(user, job, printer, printerJobId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't ask {Printer} to stop job {JobId} for {User}: {Error}", printer.Id, job.Id, user.Username, ex.Message);
            return new Result(false, 0, $"Couldn't reach {printer.Name} to stop \"{job.Name}\".");
        }
        finally
        {
            tracker.Turn.Release();
        }
    }

    private async Task<Result> StopAtPrinterAsync(UserRecord user, JobRecord job, PrinterRecord printer, int printerJobId, CancellationToken ct)
    {
        using var client = new IppClient(printer.TlsSkipVerify, PrinterTimeout);
        var alreadyPrinted = new Result(false, 0, $"\"{job.Name}\" has already printed at {printer.Name}.");

        var before = await JobStateAsync(client, printer, user.Username, printerJobId, ct);
        if (before is null || before.Value.State >= IppJobState.Canceled)
            return alreadyPrinted; // finished, or the printer has forgotten it (which it does after finishing)

        var cancel = await client.SendAsync(printer.Uri, JobRequest(IppOperation.CancelJob, printer, user.Username, printerJobId), null, ct);
        if (!IppStatus.IsSuccess(cancel.Code))
            return cancel.Code is IppStatus.ClientErrorNotPossible or IppStatus.ClientErrorNotFound
                ? alreadyPrinted
                : new Result(false, 0, $"{printer.Name} wouldn't stop \"{job.Name}\" (IPP status 0x{cancel.Code:X4}).");

        // Printers stop a job they're in the middle of a moment after saying they will.
        var after = before;
        var until = DateTimeOffset.UtcNow + SettleTimeout;
        while (true)
        {
            after = await JobStateAsync(client, printer, user.Username, printerJobId, ct) ?? after;
            if (after.Value.State >= IppJobState.Canceled || DateTimeOffset.UtcNow >= until)
                break;
            await Task.Delay(SettleDelay, ct);
        }
        if (after.Value.State == IppJobState.Completed)
            return alreadyPrinted;

        var charged = job.ChargedPages;
        var notStarted = before.Value.State is IppJobState.Pending or IppJobState.PendingHeld;
        var printed = after.Value.Printed ?? (notStarted ? 0 : (int?)null);
        var refund = printed is null ? 0 : Math.Clamp(charged - printed.Value, 0, charged);
        if (refund == charged)
        {
            if (!jobs.TryTransition(job.Id, JobStatus.Released, JobStatus.Canceled))
                refund = 0;
        }
        else if (refund > 0 && !jobs.Refund(job.Id, refund))
        {
            refund = 0;
        }
        // It's over at the printer, so there's nothing left to follow or to keep the document for.
        if (after.Value.State >= IppJobState.Canceled)
        {
            jobs.FinishPrinting(job.Id, PrintState.Stopped, printed);
            spool.Delete(job.Id);
        }

        var limited = quotas.UsageFor(user.Id, DateTimeOffset.UtcNow).Count > 0;
        var message = $"Stopped \"{job.Name}\" at {printer.Name}" + (printed, refund) switch
        {
            (null, _) => ". It had started printing and the printer doesn't say how far it got, so its pages still count.",
            (0, _) => " before it printed." + (limited ? $" Its {Pages(refund)} no longer count against your page limit." : ""),
            (_, 0) => $" after {Pages(printed.Value)}.",
            _ => $" after {Pages(printed.Value)}." + (limited ? $" The other {Pages(refund)} no longer count against your page limit." : ""),
        };
        logger.LogInformation("{User} stopped job {JobId} \"{Name}\" at {Printer}: {Printed} printed, {Refund} of {Charged} pages given back",
            user.Username, job.Id, job.Name, printer.Id, printed?.ToString() ?? "unknown", refund, charged);
        events.Record(EventCategory.Job, user.Username, EventLog.User(user.Username),
            $"{user.Username} stopped \"{job.Name}\" (job #{job.Id}) at {printer.Name}" + (printed is null
                ? "; the printer doesn't say how much had printed, so nothing was given back."
                : $" after {Pages(printed.Value)}; {Pages(refund)} of {charged} given back."));
        return new Result(true, refund, message);
    }

    /// <summary>
    /// The job's state at the printer, how many sides it says it has printed and what it says about
    /// the job; null if the printer doesn't know the job (any more) or won't say.
    /// </summary>
    internal static async Task<(int State, int? Printed, string? Reason)?> JobStateAsync(IppClient client, PrinterRecord printer, string username, int printerJobId, CancellationToken ct)
    {
        var request = JobRequest(IppOperation.GetJobAttributes, printer, username, printerJobId);
        request.Group(IppTag.OperationAttributes).Add("requested-attributes", IppValue.Keyword("job-state"),
            IppValue.Keyword("job-impressions-completed"), IppValue.Keyword("job-state-reasons"), IppValue.Keyword("job-state-message"));
        var response = await client.SendAsync(printer.Uri, request, null, ct);
        if (!IppStatus.IsSuccess(response.Code) || response.Find(IppTag.JobAttributes, "job-state")?.First?.AsInt() is not { } state)
            return null;
        var reason = response.Find(IppTag.JobAttributes, "job-state-message")?.First?.AsString();
        if (string.IsNullOrWhiteSpace(reason))
            reason = string.Join(", ", response.Find(IppTag.JobAttributes, "job-state-reasons")?.Values
                .Select(v => v.AsString()).Where(r => r is not (null or "none" or "job-canceled-at-device" or "aborted-by-system"))
                .Select(r => r!.Replace('-', ' ')) ?? []);
        return (state, response.Find(IppTag.JobAttributes, "job-impressions-completed")?.First?.AsInt(), reason.Length == 0 ? null : reason);
    }

    /// <summary>Sent as the person the job was printed as, since printers only let a job's owner stop it.</summary>
    private static IppMessage JobRequest(short operation, PrinterRecord printer, string username, int printerJobId)
    {
        var request = IppMessage.CreateRequest(operation, IppClient.NextRequestId(), printer.Uri);
        request.Group(IppTag.OperationAttributes)
            .Add("job-id", IppValue.Integer(printerJobId))
            .Add("requesting-user-name", IppValue.Name(username));
        return request;
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";
}
