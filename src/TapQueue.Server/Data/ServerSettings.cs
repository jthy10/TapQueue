using TapQueue.Server.Config;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <summary>
/// Settings an admin can change while the server runs. server.toml gives the defaults; a value
/// saved here (from the console or `tapqueue-admin server set`) wins until it's reset.
/// Everything else in server.toml (listen address, data directory, auth mode) needs a restart
/// and stays in the file.
/// </summary>
public sealed class ServerSettings(Database database, ServerConfig config)
{
    public const string HoldHoursKey = "holdHours";
    public const string SessionTimeoutKey = "sessionTimeoutMinutes";
    public const string QuotaOverrunKey = "quotaOverrun";
    public const string AddressMatchingKey = "addressMatching";
    public const string HeldMegabytesKey = "heldMegabytesPerUser";

    public const string TapWaitKey = "tapWaitSeconds";
    public const string PrinterBusyKey = "printerBusySeconds";
    public const string PrinterSendKey = "printerSendSeconds";
    public const string JobHistoryDaysKey = "jobHistoryDays";
    public const string ActivityDaysKey = "activityDays";
    public const string TimeZoneKey = "timeZone";
    public const string SignInAttemptsKey = "signInAttempts";
    public const string SignInLockoutKey = "signInLockoutMinutes";

    public static readonly string[] Keys =
    [
        HoldHoursKey, SessionTimeoutKey, QuotaOverrunKey, AddressMatchingKey, HeldMegabytesKey,
        TapWaitKey, PrinterBusyKey, PrinterSendKey, JobHistoryDaysKey, ActivityDaysKey, TimeZoneKey, SignInAttemptsKey, SignInLockoutKey,
    ];

    /// <summary>A whole-number setting that isn't in server.toml: what it's called in sentences, its default and what it may be.</summary>
    public sealed record Number(string Key, string Name, string Unit, int Default, int Min, int Max, string Allowed);

    /// <summary>
    /// The settings that are just a number with a default. <see cref="JobHistoryDaysKey"/> also takes 0
    /// (keep for ever) below its minimum, which keeps a month of jobs for monthly page limits to count.
    /// </summary>
    public static readonly Number[] Numbers =
    [
        new(TapWaitKey, "a tap waits for the printer for", "second", 5, 0, 120, "0 to 120"),
        new(PrinterBusyKey, "a busy printer is retried for", "second", 120, 0, 3600, "0 to 3600"),
        new(PrinterSendKey, "sending one job to a printer may take", "second", 300, 10, 3600, "10 to 3600"),
        new(JobHistoryDaysKey, "job history kept for", "day", 0, 32, 3650, "0 (for ever), or 32 to 3650"),
        new(ActivityDaysKey, "activity log kept for", "day", 90, 1, 3650, "0 (for ever), or 1 to 3650"),
        new(SignInAttemptsKey, "wrong passwords before sign-in is refused:", "attempt", 5, 3, 100, "3 to 100"),
        new(SignInLockoutKey, "sign-in refused after wrong passwords for", "minute", 15, 1, 1440, "1 to 1440"),
    ];

    /// <summary>Whether <paramref name="value"/> is one <paramref name="number"/> may have.</summary>
    public static bool Allows(Number number, int value) =>
        value >= number.Min && value <= number.Max || value == 0 && number.Key is JobHistoryDaysKey or ActivityDaysKey;

    /// <summary>The saved value of one of <see cref="Numbers"/>, or its default.</summary>
    public int Get(string key)
    {
        var number = Numbers.First(n => n.Key == key);
        return Int(key) is { } saved && Allows(number, saved) ? saved : number.Default;
    }

    /// <summary>
    /// How long a station's tap waits for the jobs to reach the printer before it answers "printing"
    /// and the station reads the next card. The release carries on either way.
    /// </summary>
    public TimeSpan TapWait => TimeSpan.FromSeconds(Get(TapWaitKey));

    /// <summary>How long a printer that answers "busy" is tried again before the job goes back on hold.</summary>
    public TimeSpan PrinterBusyTimeout => TimeSpan.FromSeconds(Get(PrinterBusyKey));

    /// <summary>How long sending one job to a printer may take before it's given up and held again.</summary>
    public TimeSpan PrinterSendTimeout => TimeSpan.FromSeconds(Get(PrinterSendKey));

    /// <summary>Finished jobs (released, canceled, expired) are deleted after this many days; 0 keeps them for ever.</summary>
    public int JobHistoryDays => Get(JobHistoryDaysKey);

    /// <summary>The activity log is kept this many days; 0 keeps it for ever.</summary>
    public int ActivityDays => Get(ActivityDaysKey);

    /// <summary>Wrong passwords for one name from one address before sign-in is refused there (<see cref="Api.SignInThrottle"/>).</summary>
    public int SignInAttempts => Get(SignInAttemptsKey);

    /// <summary>How long wrong passwords count, and so how long sign-in stays refused after the last one.</summary>
    public TimeSpan SignInLockout => TimeSpan.FromMinutes(Get(SignInLockoutKey));

    /// <summary>
    /// The time zone days start in: page limit periods, reports, and the times of the daily backup and
    /// directory sync. An IANA name like "Europe/Oslo"; the server machine's own zone unless set, or
    /// if the saved one isn't known on this machine any more.
    /// </summary>
    public TimeZoneInfo TimeZone =>
        Saved().GetValueOrDefault(TimeZoneKey) is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>The current time on the clock in <see cref="TimeZone"/>.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);

    private readonly Lock _lock = new();
    private Dictionary<string, string>? _saved;

    /// <summary>Held jobs that are not released within this many hours are deleted.</summary>
    public int HoldHours => Int(HoldHoursKey) ?? config.Jobs.HoldHours;

    /// <summary>Client sessions with no heartbeat for this long are dropped.</summary>
    public int SessionTimeoutMinutes => Int(SessionTimeoutKey) ?? config.Auth.SessionTimeoutMinutes;

    public TimeSpan SessionTimeout => TimeSpan.FromMinutes(SessionTimeoutMinutes);

    /// <summary>
    /// Whether a job may take someone over their page limit (<see cref="Shared.Api.QuotaOverrun"/>).
    /// Not in server.toml; the default is to allow it.
    /// </summary>
    public string QuotaOverrun =>
        Saved().TryGetValue(QuotaOverrunKey, out var value) && Shared.Api.QuotaOverrun.All.Contains(value) ? value : Shared.Api.QuotaOverrun.Allow;

    /// <summary>
    /// Whether keyless jobs may be matched by the address they came from (<see cref="Shared.Api.AddressMatching"/>).
    /// Not in server.toml; on by default, so PCs keep printing while their clients upgrade.
    /// </summary>
    public string AddressMatching =>
        Saved().TryGetValue(AddressMatchingKey, out var value) && Shared.Api.AddressMatching.All.Contains(value) ? value : Shared.Api.AddressMatching.On;

    public const int DefaultHeldMegabytes = 2048;

    /// <summary>
    /// How many megabytes of documents one person may have waiting at once, so nobody can fill the
    /// disk with held jobs; 0 for no limit. Not in server.toml.
    /// </summary>
    public int HeldMegabytesPerUser => Int(HeldMegabytesKey) is >= 0 and var mb ? mb : DefaultHeldMegabytes;

    /// <summary>True if <paramref name="key"/> was set here rather than coming from server.toml.</summary>
    public bool IsSaved(string key) => Saved().ContainsKey(key);

    public void Set(string key, int value) => Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Goes back to server.toml's value, or the default for settings that aren't in it.</summary>
    public void Reset(string key) => Set(key, null);

    private const string EnrollmentCodeHashKey = "enrollmentCodeHash";

    /// <summary>
    /// The hash of the code a new PC's TapQueue service must send to be let in (<see cref="Tokens.Hash"/>);
    /// null while any PC that can reach the server may register itself. Only the hash is kept, like a token.
    /// </summary>
    public string? EnrollmentCodeHash => Saved().GetValueOrDefault(EnrollmentCodeHashKey);

    /// <summary>Requires the code that hashes to <paramref name="hash"/> from new PCs, or with null none.</summary>
    public void SetEnrollmentCodeHash(string? hash) => Save(EnrollmentCodeHashKey, hash);

    private const string BackupTimeKey = "backupTime";
    private const string BackupKeepKey = "backupKeep";
    private const string BackupOff = "off";

    /// <summary>When the daily backup runs, in <see cref="TimeZone"/>; null when it's turned off. 02:00 unless changed.</summary>
    public TimeOnly? BackupTime =>
        Saved().GetValueOrDefault(BackupTimeKey) is not { } saved ? new TimeOnly(2, 0)
        : TimeOnly.TryParseExact(saved, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) ? time
        : null;

    public void SetBackupTime(TimeOnly? time) =>
        Save(BackupTimeKey, time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? BackupOff);

    /// <summary>How many daily backups are kept; older ones are deleted.</summary>
    public int BackupKeep => Int(BackupKeepKey) ?? 7;

    public void SetBackupKeep(int keep) => Save(BackupKeepKey, keep.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Saves a value, or with null goes back to server.toml's or the default.</summary>
    public void Set(string key, string? value)
    {
        if (!Keys.Contains(key))
            throw new ArgumentException($"Unknown setting \"{key}\".", nameof(key));
        Save(key, value);
    }

    private void Save(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null)
                database.Execute("DELETE FROM settings WHERE key = $k", ("$k", key));
            else
                database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
                    ("$k", key), ("$v", value));
            _saved = null;
        }
    }

    private int? Int(string key) => Saved().TryGetValue(key, out var value) && int.TryParse(value, out var n) ? n : null;

    private Dictionary<string, string> Saved()
    {
        lock (_lock)
            return _saved ??= database.Query("SELECT key, value FROM settings", r => (r.GetString(0), r.GetString(1)))
                .ToDictionary(p => p.Item1, p => p.Item2);
    }
}
