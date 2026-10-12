using System.Globalization;
using System.Text;
using System.Text.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Notifications;

/// <summary>How bad a printer's state is, for deciding whether it's worth another email.</summary>
public static class AlertSeverity
{
    /// <summary>Still prints, but wants attention soon (toner low).</summary>
    public const int Warning = 1;
    /// <summary>Unreachable, or reports something that needs a person (paper jam, out of paper).</summary>
    public const int Problem = 2;

    public static int Of(PrinterStatus status) => status.Level switch
    {
        PrinterHealthLevel.Ok => 0,
        PrinterHealthLevel.Warning => Warning,
        _ => Problem,
    };
}

/// <summary>
/// Emails the people in the notification settings when a printer has a problem, a release station
/// stops checking in or a client program crashes, and again when a printer or station recovers.
/// It looks every 30 seconds. A printer or station has to stay wrong for the configured delay first,
/// so one that blips doesn't send anything; everything found in one look goes out as one email.
/// An email the mail server won't take is kept and tried again, less and less often, for a day.
/// </summary>
public sealed class NotificationService(NotificationStore store, PrinterRegistry printers, StationStore stations, CrashStore crashes,
    IMailSender mail, EventLog events, ILogger<NotificationService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>A PC that crashes at every start gets one email an hour for each of its programs, not one per crash.</summary>
    public static readonly TimeSpan CrashQuietTime = TimeSpan.FromHours(1);

    private static readonly TimeSpan StationOfflineAfter = TimeSpan.FromSeconds(StationStatus.OfflineAfterSeconds);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan LongestRetryWait = TimeSpan.FromMinutes(30);
    private const int MaxWaiting = 50;
    private const int MaxNotesPerEmail = 25;

    private sealed record Note(string Title, string Details);
    private sealed record Outgoing(MailMessage Message, DateTimeOffset QueuedAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Outgoing> _outbox = [];
    private readonly Dictionary<string, DateTimeOffset> _crashQuietUntil = new(StringComparer.OrdinalIgnoreCase);
    private int _failures;
    private DateTimeOffset _retryAt;

    /// <summary>When an email last went out, since the server started.</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Why the last email couldn't be sent; null once one goes through.</summary>
    public string? LastError { get; private set; }

    /// <summary>Emails that couldn't be sent yet and will be tried again.</summary>
    public int Waiting => _outbox.Count;

    /// <summary>What an email went out about and hasn't recovered yet.</summary>
    public List<OpenAlertDto> OpenAlerts()
    {
        var state = store.State();
        return (state.Printers ?? []).Select(p => new OpenAlertDto(EventLog.Printer(p.Key), p.Value.Summary, p.Value.Since))
            .Concat((state.Stations ?? []).Select(s => new OpenAlertDto(EventLog.Station(s.Key), s.Value.Summary, s.Value.Since)))
            .OrderBy(a => a.Since).ToList();
    }

    /// <summary>A test email got through (or didn't), which says more than the last alert did.</summary>
    public void Sent(DateTimeOffset now, string? error)
    {
        LastError = error;
        if (error is not null)
            return;
        LastSentAt = now;
        // What's waiting has a good chance now.
        _retryAt = now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await CheckAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checking what to send notifications about failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Looks at everything once, as of <paramref name="now"/>, and sends what's due.</summary>
    public async Task CheckAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = store.Config();
            var before = store.State();
            if (!config.Enabled)
            {
                // Turned on again later, it starts from what's wrong then, not from what was said before.
                if (before != new NotificationState())
                    store.SaveState(new NotificationState());
                _outbox.Clear();
                return;
            }

            var notes = new List<Note>();
            var after = new NotificationState(
                CheckPrinters(config, before.Printers, now, notes),
                CheckStations(config, before.Stations, now, notes),
                CheckCrashes(config, before.LastCrashId, now, notes));
            // Saved before sending: an email that fails is tried again from the outbox, not found again.
            if (JsonSerializer.Serialize(after, TapQueueJson.Options) != JsonSerializer.Serialize(before, TapQueueJson.Options))
                store.SaveState(after);
            if (notes.Count > 0)
            {
                _outbox.Add(new Outgoing(Compose(notes), now));
                if (_outbox.Count > MaxWaiting)
                    _outbox.RemoveRange(0, _outbox.Count - MaxWaiting);
            }
            await FlushAsync(config, now, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, PrinterAlert>? CheckPrinters(NotificationConfig config, Dictionary<string, PrinterAlert>? before, DateTimeOffset now, List<Note> notes)
    {
        if (!config.PrinterProblems && !config.PrinterWarnings)
            return null;
        var open = new Dictionary<string, PrinterAlert>(StringComparer.OrdinalIgnoreCase);
        foreach (var printer in printers.All)
        {
            var alert = before?.FirstOrDefault(p => p.Key.Equals(printer.Id, StringComparison.OrdinalIgnoreCase)).Value;
            if (printers.StatusOf(printer.Id) is not { } status)
            {
                // Not checked yet (the server just started): what was said about it still stands.
                if (alert is not null)
                    open[printer.Id] = alert;
                continue;
            }

            var severity = AlertSeverity.Of(status);
            // What it counts as for these settings: a warning nobody asked to hear about is as good as fine.
            var wanted = severity == AlertSeverity.Problem ? config.PrinterProblems : severity == AlertSeverity.Warning && config.PrinterWarnings;
            var counts = wanted ? severity : 0;
            var told = alert?.Severity ?? 0;
            var since = status.HealthSince ?? status.CheckedAt;

            if (counts > told)
            {
                if (now - since < config.Delay)
                {
                    if (alert is not null)
                        open[printer.Id] = alert;
                    continue;
                }
                var title = status.Online ? $"{printer.Name}: {status.Problem}" : $"{printer.Name} is unreachable";
                notes.Add(new Note(title, Lines(
                    printer.Location.Length > 0 ? $"Where: {printer.Location}" : null,
                    $"Since: {Local(since)}",
                    status.Online ? null : $"Why: {status.StateMessage}",
                    status.CanPrint ? "It still prints." : "Jobs released to it stay on hold until it's fixed.")));
                open[printer.Id] = new PrinterAlert(severity, title, since);
            }
            else if (counts < told)
            {
                // Only worth saying if the printer got better, not if the settings stopped asking.
                if (severity < told)
                    notes.Add(severity == 0
                        ? new Note($"{printer.Name} is ready again", Lines($"It was: {alert!.Summary}", $"From {Local(alert.Since)} to {Local(since)}"))
                        : new Note($"{printer.Name} is printing again", Lines($"It still says: {status.Problem}", $"It was: {alert!.Summary}")));
                if (counts > 0)
                    open[printer.Id] = new PrinterAlert(counts, $"{printer.Name}: {status.Problem}", since);
            }
            else if (alert is not null)
            {
                open[printer.Id] = alert;
            }
        }
        return open.Count > 0 ? open : null;
    }

    private Dictionary<string, StationAlert>? CheckStations(NotificationConfig config, Dictionary<string, StationAlert>? before, DateTimeOffset now, List<Note> notes)
    {
        if (!config.StationsOffline)
            return null;
        var open = new Dictionary<string, StationAlert>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in stations.List())
        {
            var alert = before?.FirstOrDefault(s => s.Key.Equals(station.Id, StringComparison.OrdinalIgnoreCase)).Value;
            // One that never connected was only just added; there's nothing to miss yet.
            if (station.LastHeartbeatAt is not { } heartbeat)
                continue;
            if (now - heartbeat < StationOfflineAfter)
            {
                if (alert is not null)
                    notes.Add(new Note($"Station {station.DisplayName} is back online", Lines($"It was offline from {Local(alert.Since)}.")));
                continue;
            }
            if (alert is not null)
            {
                open[station.Id] = alert;
                continue;
            }
            // While the server was down nobody could check in, so the time before it started doesn't count.
            var lastSeen = heartbeat > ServerClock.StartedAt ? heartbeat : ServerClock.StartedAt;
            if (now - lastSeen < StationOfflineAfter + config.Delay)
                continue;
            var printer = printers.Find(station.PrinterId);
            var title = $"Station {station.DisplayName} is offline";
            notes.Add(new Note(title, Lines(
                station.Location.Length > 0 ? $"Where: {station.Location}" : null,
                $"Printer: {printer?.Name ?? station.PrinterId}",
                $"Last heard from: {Local(heartbeat)}",
                "Cards tapped there don't release anything until it's back.")));
            open[station.Id] = new StationAlert(title, heartbeat);
        }
        return open.Count > 0 ? open : null;
    }

    private long? CheckCrashes(NotificationConfig config, long? lastId, DateTimeOffset now, List<Note> notes)
    {
        if (!config.CrashReports)
            return null;
        var newest = crashes.List(null, 50);
        var newestId = newest.FirstOrDefault()?.Id ?? 0;
        // The first look since this was turned on: reports already there aren't news.
        if (lastId is not { } last)
            return newestId;
        foreach (var crash in newest.Where(c => c.Id > last).Reverse())
        {
            var key = $"{crash.Computer}|{crash.Program}";
            if (_crashQuietUntil.TryGetValue(key, out var until) && now < until)
                continue;
            _crashQuietUntil[key] = now + CrashQuietTime;
            var what = crash.Program == CrashProgram.Service ? "TapQueue service" : crash.Program == CrashProgram.Tray ? "tray app" : crash.Program;
            notes.Add(new Note($"The {what} on {crash.Computer} crashed", Lines(
                crash.Message,
                $"Client {crash.Version ?? "?"} on {ClientPlatform.DisplayName(crash.Platform)}, from {crash.Ip}",
                $"The full report (number {crash.Id}) is in the admin console under Workstations.")));
        }
        return Math.Max(last, newestId);
    }

    private static MailMessage Compose(List<Note> notes)
    {
        var body = new StringBuilder();
        foreach (var note in notes.Take(MaxNotesPerEmail))
            body.Append(note.Title).Append('\n').Append(note.Details).Append("\n\n");
        if (notes.Count > MaxNotesPerEmail)
            body.Append($"And {notes.Count - MaxNotesPerEmail} more: see Activity in the admin console.\n\n");
        body.Append(Footer);
        return new MailMessage(notes.Count == 1 ? $"TapQueue: {notes[0].Title}" : $"TapQueue: {notes.Count} things need a look", body.ToString());
    }

    public static string Footer =>
        $"-- \nSent by the TapQueue server on {Environment.MachineName}. Who gets these emails, and what for, is set in the admin console under Notifications.\n";

    private async Task FlushAsync(NotificationConfig config, DateTimeOffset now, CancellationToken ct)
    {
        _outbox.RemoveAll(m => now - m.QueuedAt > GiveUpAfter);
        if (now < _retryAt)
            return;
        while (_outbox.Count > 0)
        {
            try
            {
                await mail.SendAsync(config, _outbox[0].Message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LastError = ex is MailException ? ex.Message : $"Sending failed: {ex.Message}";
                // Said once, not at every try.
                if (_failures == 0)
                {
                    logger.LogWarning("Couldn't send a notification email: {Error}", LastError);
                    events.Record(EventCategory.Admin, EventLog.System, null, $"Couldn't send a notification email: {LastError} It will be tried again for a day.");
                }
                var wait = TimeSpan.FromMinutes(Math.Pow(2, Math.Min(_failures, 10)));
                _retryAt = now + (wait < LongestRetryWait ? wait : LongestRetryWait);
                _failures++;
                return;
            }
            logger.LogInformation("Sent notification email \"{Subject}\" to {Count} recipients", _outbox[0].Message.Subject, config.To.Length);
            if (_failures > 0)
                events.Record(EventCategory.Admin, EventLog.System, null, "Notification emails are going out again.");
            _outbox.RemoveAt(0);
            _failures = 0;
            LastError = null;
            LastSentAt = now;
        }
    }

    private static string Lines(params string?[] lines) => string.Join('\n', lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "  " + l));

    /// <summary>On the server's clock, which is what whoever reads the email most likely shares.</summary>
    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
