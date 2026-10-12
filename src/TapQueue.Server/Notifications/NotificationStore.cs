using System.Text.Json;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Notifications;

/// <summary>
/// The mail server alerts go through, who gets them and what's worth an email. Kept in the database
/// (settings key "notifications") so it's set from the console; the password never leaves the server.
/// </summary>
/// <param name="Security">One of <see cref="MailSecurity"/>.</param>
/// <param name="TlsSkipVerify">Accept any certificate, for a relay with a self-signed one.</param>
/// <param name="Username">Empty for a relay that doesn't ask anyone to sign in.</param>
/// <param name="DelayMinutes">How long a printer or station must stay that way before an email goes out.</param>
public sealed record NotificationConfig(
    bool Enabled = false,
    string Host = "",
    int Port = 587,
    string Security = MailSecurity.StartTls,
    bool TlsSkipVerify = false,
    string Username = "",
    string Password = "",
    string From = "",
    string FromName = "TapQueue",
    string[]? Recipients = null,
    bool PrinterProblems = true,
    bool PrinterWarnings = false,
    bool StationsOffline = true,
    bool CrashReports = true,
    int DelayMinutes = 5)
{
    public string[] To => Recipients ?? [];

    /// <summary>Enough is filled in to send something.</summary>
    public bool CanSend => Host.Length > 0 && From.Length > 0 && To.Length > 0;

    public TimeSpan Delay => TimeSpan.FromMinutes(DelayMinutes);

    public NotificationConfigDto ToDto() => new(Enabled, Host, Port, Security, TlsSkipVerify, Username, Password.Length > 0, From, FromName, To,
        PrinterProblems, PrinterWarnings, StationsOffline, CrashReports, DelayMinutes);
}

/// <summary>A printer an email went out about. <paramref name="Severity"/> is <see cref="AlertSeverity"/>.</summary>
public sealed record PrinterAlert(int Severity, string Summary, DateTimeOffset Since);

public sealed record StationAlert(string Summary, DateTimeOffset Since);

/// <summary>
/// What has been emailed about and hasn't recovered, so a restart doesn't send it all again, and the
/// newest crash report already looked at.
/// </summary>
public sealed record NotificationState(
    Dictionary<string, PrinterAlert>? Printers = null,
    Dictionary<string, StationAlert>? Stations = null,
    long? LastCrashId = null);

public sealed class NotificationStore(Database database)
{
    private const string ConfigKey = "notifications";
    private const string StateKey = "notificationState";

    public NotificationConfig Config() => Load<NotificationConfig>(ConfigKey) ?? new NotificationConfig();

    public void SaveConfig(NotificationConfig config) => Save(ConfigKey, config);

    public NotificationState State() => Load<NotificationState>(StateKey) ?? new NotificationState();

    public void SaveState(NotificationState state) => Save(StateKey, state);

    private T? Load<T>(string key) where T : class =>
        database.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) is string json
            ? JsonSerializer.Deserialize<T>(json, TapQueueJson.Options)
            : null;

    private void Save<T>(string key, T value) =>
        database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
            ("$k", key), ("$v", JsonSerializer.Serialize(value, TapQueueJson.Options)));
}
