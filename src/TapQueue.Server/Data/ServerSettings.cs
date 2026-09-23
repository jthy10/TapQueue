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

    public static readonly string[] Keys = [HoldHoursKey, SessionTimeoutKey, QuotaOverrunKey];

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

    /// <summary>True if <paramref name="key"/> was set here rather than coming from server.toml.</summary>
    public bool IsSaved(string key) => Saved().ContainsKey(key);

    public void Set(string key, int value) => Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Goes back to server.toml's value, or the default for settings that aren't in it.</summary>
    public void Reset(string key) => Set(key, null);

    /// <summary>Saves a value, or with null goes back to server.toml's or the default.</summary>
    public void Set(string key, string? value)
    {
        if (!Keys.Contains(key))
            throw new ArgumentException($"Unknown setting \"{key}\".", nameof(key));
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
