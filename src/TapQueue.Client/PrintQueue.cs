using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>
/// What the tray app's "Printers and print queue" window shows of a person's jobs and printers, in
/// words. The same on Windows and Linux.
/// </summary>
public static class PrintQueue
{
    // The server's job states, as JobDto.Status spells them.
    public const string Held = "held";
    public const string Releasing = "releasing";
    public const string Released = "released";

    /// <summary>How long a job that was sent to a printer stays in the list.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromHours(24);

    /// <summary>
    /// The jobs worth showing: the ones waiting, then the ones sent to a printer in the last day,
    /// newest first. Deleted and expired jobs, and older ones, are left out.
    /// </summary>
    public static List<JobDto> Visible(IEnumerable<JobDto> jobs, DateTimeOffset now) =>
        jobs.Where(j => j.Status is Held or Releasing || (j.Status == Released && j.ReleasedAt >= now - RecentFor))
            .OrderBy(j => j.Status == Released)
            .ThenByDescending(j => j.Status == Released ? j.ReleasedAt : j.SubmittedAt)
            .ToList();

    public static bool IsHeld(JobDto job) => job.Status == Held;

    /// <summary>
    /// Held jobs can always be deleted. One that was sent to a printer can be tried once: the server
    /// stops it at the printer if it hasn't finished there, and says so if it has.
    /// </summary>
    public static bool CanDelete(JobDto job) => job.Status == Held || (job.Status == Released && job.RefundedPages == 0);

    public static string Status(JobDto job, IReadOnlyList<PrinterDto> printers)
    {
        var printer = printers.FirstOrDefault(p => p.Id == job.ReleasedPrinterId)?.Name ?? job.ReleasedPrinterId ?? "a printer";
        return job.Status switch
        {
            Held when job.Error is not null => "Waiting (last try failed)",
            Held => $"Waiting until {job.ExpiresAt.ToLocalTime():g}",
            Releasing => "Going to the printer…",
            Released when job.RefundedPages > 0 => $"Stopped at {printer}, {Count(job.RefundedPages)} given back",
            Released when job.PrintState == "stopped" => $"Stopped at {printer}",
            Released when job.PrintState == "printing" => $"Printing at {printer}",
            Released when job.PrintState == "printed" => $"Printed at {printer}",
            Released => $"Sent to {printer} at {job.ReleasedAt?.ToLocalTime():t}",
            _ => job.Status,
        };
    }

    /// <summary>Pages times copies, or "?" when the server couldn't count them.</summary>
    public static string Pages(JobDto job) =>
        job.Pages is not { } pages ? "?" : job.Copies > 1 ? $"{pages} × {job.Copies}" : pages.ToString();

    public static string Size(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    /// <summary>What to ask before deleting <paramref name="jobs"/> (all of which <see cref="CanDelete"/>).</summary>
    public static string DeleteQuestion(IReadOnlyList<JobDto> jobs)
    {
        var waiting = jobs.Count(IsHeld);
        var sent = jobs.Count - waiting;
        var parts = new List<string>();
        if (waiting > 0)
            parts.Add($"Delete {Jobs(waiting)} that {(waiting == 1 ? "is" : "are")} waiting? {(waiting == 1 ? "It" : "They")} won't be printed.");
        if (sent > 0)
            parts.Add($"{(waiting > 0 ? "And stop" : "Stop")} {Jobs(sent)} already sent to the printer? " +
                      "TapQueue asks the printer to stop, and pages that haven't printed are given back to your page limit. " +
                      "A job that has finished printing stays as it is.");
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Deletes jobs that are waiting and stops ones already sent to a printer. Returns what's worth
    /// telling the person: what was stopped and given back, and anything that couldn't be done.
    /// </summary>
    public static async Task<List<string>> DeleteAsync(TapQueueApi api, IReadOnlyList<JobDto> jobs, CancellationToken ct = default)
    {
        var notes = new List<string>();
        foreach (var job in jobs)
        {
            try
            {
                var result = await api.CancelJobAsync(job.Id, ct);
                if (!IsHeld(job) && result is not null)
                    notes.Add(result.Message);
            }
            catch (TapQueueApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
            {
                // A server older than 0.14 only deletes jobs that are waiting, and says just this.
                notes.Add(!IsHeld(job) && ex.Message == "Job is no longer held."
                    ? $"\"{job.Name}\" has already been sent to the printer, and your TapQueue server can't stop jobs there."
                    : ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or TapQueueApiException or TaskCanceledException)
            {
                notes.Add($"Couldn't delete \"{job.Name}\": {ex.Message}");
                break; // the rest would go the same way
            }
        }
        return notes;
    }

    public static string PrinterStatus(PrinterDto printer) =>
        !printer.Online ? "Offline" : string.IsNullOrWhiteSpace(printer.StateMessage) ? "Ready" : printer.StateMessage;

    /// <param name="installed">The printers this PC has, or null when that couldn't be found out.</param>
    /// <param name="name">The queue's name as the PC knows it.</param>
    public static string QueueStatus(IReadOnlySet<string>? installed, string name) =>
        installed is null ? "" : installed.Contains(name) ? "Installed on this PC" : "Not installed (try Refresh TapQueue)";

    private static string Jobs(int n) => n == 1 ? "1 job" : $"{n} jobs";

    private static string Count(int n) => n == 1 ? "1 page" : $"{n} pages";
}
