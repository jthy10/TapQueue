using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Follows released jobs at their printer until the printer says how they ended. A printer that
/// accepts a job has only queued it: it can still abort it (a document it can't render, a fault),
/// or someone can stop it at the printer's own panel. Until the printer says "completed" the server
/// keeps the document, so that:
/// <list type="bullet">
/// <item>a job the printer aborted goes back on hold, uncounted, to be released again or elsewhere;</item>
/// <item>a job stopped at the printer gives back the pages that didn't print, when the printer says
/// how many did (the same rules as <see cref="JobCanceler"/>).</item>
/// </list>
/// A job stuck behind a jam or an empty tray is neither: the printer carries on with it once that's
/// fixed, so it just stays "printing". Printers that give jobs no number, or forget them before
/// they can be asked, leave things as they were before 0.17: released means the printer took it.
/// </summary>
public sealed class PrintTracker(JobStore jobs, Spool spool, PrinterRegistry printers, EventLog events, ServerSettings settings, ILogger<PrintTracker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PrinterTimeout = TimeSpan.FromSeconds(15);
    /// <summary>A job nobody could find out about for this long is left as it is, and its document deleted.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Held while a job's outcome is being worked out, here or by <see cref="JobCanceler"/> stopping
    /// one, so the two don't both act on what the printer says about the same job.
    /// </summary>
    internal SemaphoreSlim Turn { get; } = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't check on the jobs at the printers");
            }
        }
    }

    /// <summary>Asks the printers about every job that's still printing as far as the server knows.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        var printing = jobs.ListPrinting();
        if (printing.Count == 0)
            return;
        // One printer that doesn't answer shouldn't hold up the news from the others.
        await Task.WhenAll(printing.GroupBy(j => j.ReleasedPrinterId).Select(async atPrinter =>
        {
            foreach (var job in atPrinter)
                if (!await CheckOneAsync(job, ct))
                    break;
        }));
    }

    /// <summary>False if the printer couldn't be reached, so its other jobs aren't tried this round.</summary>
    private async Task<bool> CheckOneAsync(JobRecord job, CancellationToken ct)
    {
        await Turn.WaitAsync(ct);
        try
        {
            // It may have been stopped from the tray while this waited.
            if (jobs.Get(job.Id) is not { Status: JobStatus.Released, PrintState: PrintState.Printing, PrinterJobId: { } printerJobId } current)
                return true;
            job = current;
            if (job.ReleasedPrinterId is null || printers.Find(job.ReleasedPrinterId) is not { } printer)
            {
                Finish(job, PrintState.Unconfirmed, null);
                return true;
            }

            (int State, int? Printed, string? Reason)? state;
            try
            {
                using var client = new IppClient(printer.TlsSkipVerify, PrinterTimeout);
                state = await JobCanceler.JobStateAsync(client, printer, job.Username ?? job.FormerOwner ?? "tapqueue", printerJobId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (job.ReleasedAt < DateTimeOffset.UtcNow - GiveUpAfter)
                {
                    logger.LogWarning("Gave up asking {Printer} about job {JobId} \"{Name}\" after {Hours} hours: {Error}",
                        printer.Id, job.Id, job.Name, GiveUpAfter.TotalHours, ex.Message);
                    Finish(job, PrintState.Unconfirmed, null);
                }
                return false;
            }

            switch (state)
            {
                // Printers forget finished jobs, some of them at once.
                case null:
                    Finish(job, PrintState.Unconfirmed, null);
                    break;
                case { State: IppJobState.Completed }:
                    Finish(job, PrintState.Printed, state.Value.Printed);
                    logger.LogInformation("Job {JobId} \"{Name}\" printed at {Printer}", job.Id, job.Name, printer.Id);
                    break;
                case { State: IppJobState.Aborted }:
                    Aborted(job, printer, state.Value.Printed, state.Value.Reason);
                    break;
                case { State: IppJobState.Canceled }:
                    StoppedAtPrinter(job, printer, state.Value.Printed);
                    break;
                default:
                    if (job.ReleasedAt < DateTimeOffset.UtcNow - GiveUpAfter)
                        Finish(job, PrintState.Unconfirmed, state.Value.Printed);
                    break;
            }
            return true;
        }
        finally
        {
            Turn.Release();
        }
    }

    private void Finish(JobRecord job, string state, int? printed)
    {
        jobs.FinishPrinting(job.Id, state, printed);
        spool.Delete(job.Id);
    }

    private void Aborted(JobRecord job, PrinterRecord printer, int? printed, string? reason)
    {
        var who = job.Username ?? job.FormerOwner ?? "Someone";
        var why = reason is null ? "" : $" ({reason})";
        // After a restore there's no document to hold: backups don't carry them.
        if (!spool.Exists(job.Id))
        {
            StoppedAtPrinter(job, printer, printed ?? 0);
            return;
        }
        var error = $"{printer.Name} gave up on this job{why}. It's on hold again and doesn't count against a page limit.";
        if (!jobs.ReturnToHold(job.Id, error, DateTimeOffset.UtcNow.AddHours(settings.HoldHours)))
            return;
        logger.LogWarning("{Printer} aborted job {JobId} \"{Name}\" for {User}{Why}; it's held again", printer.Id, job.Id, job.Name, who, why);
        events.Record(EventCategory.Job, EventLog.System, job.Username is null ? EventLog.Job(job.Id) : EventLog.User(job.Username),
            $"{printer.Name} gave up on {who}'s \"{job.Name}\" (job #{job.Id}){why}" +
            (printed > 0 ? $" after {Pages(printed.Value)}, which aren't counted" : "") + ". It's on hold again.");
    }

    /// <summary>Canceled at the printer itself, by someone standing there.</summary>
    private void StoppedAtPrinter(JobRecord job, PrinterRecord printer, int? printed)
    {
        var who = job.Username ?? job.FormerOwner ?? "Someone";
        var charged = job.ChargedPages;
        var refund = printed is null ? 0 : Math.Clamp(charged - printed.Value, 0, charged) - job.RefundedPages;
        if (refund == charged && jobs.TryTransition(job.Id, JobStatus.Released, JobStatus.Canceled))
        {
            // Nothing printed: the job counts as canceled.
        }
        else if (refund <= 0 || !jobs.Refund(job.Id, refund))
        {
            refund = 0;
        }
        Finish(job, PrintState.Stopped, printed);
        logger.LogInformation("Job {JobId} \"{Name}\" was stopped at {Printer}: {Printed} printed, {Refund} of {Charged} pages given back",
            job.Id, job.Name, printer.Id, printed?.ToString() ?? "unknown", refund, charged);
        // A job stopped from the tray is already in the activity log, with its pages given back.
        if (job.RefundedPages == 0)
            events.Record(EventCategory.Job, EventLog.System, job.Username is null ? EventLog.Job(job.Id) : EventLog.User(job.Username),
                $"{who}'s \"{job.Name}\" (job #{job.Id}) was stopped at {printer.Name}" + (printed is null
                    ? "; the printer doesn't say how much had printed, so nothing was given back."
                    : $" after {Pages(printed.Value)}; {Pages(refund)} of {charged} given back."));
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";
}
