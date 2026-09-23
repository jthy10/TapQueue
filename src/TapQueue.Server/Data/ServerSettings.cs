using TapQueue.Server.Config;

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

    public static readonly string[] Keys = [HoldHoursKey, SessionTimeoutKey];

    private readonly Lock _lock = new();
    private Dictionary<string, string>? _saved;

    /// <summary>Held jobs that are not released within this many hours are deleted.</summary>
    public int HoldHours => Int(HoldHoursKey) ?? config.Jobs.HoldHours;

    /// <summary>Client sessions with no heartbeat for this long are dropped.</summary>
    public int SessionTimeoutMinutes => Int(SessionTimeoutKey) ?? config.Auth.SessionTimeoutMinutes;

    public TimeSpan SessionTimeout => TimeSpan.FromMinutes(SessionTimeoutMinutes);

    /// <summary>True if <paramref name="key"/> was set here rather than coming from server.toml.</summary>
    public bool IsSaved(string key) => Saved().ContainsKey(key);

    /// <summary>Saves a value, or with null goes back to server.toml's.</summary>
    public void Set(string key, int? value)
    {
        if (!Keys.Contains(key))
            throw new ArgumentException($"Unknown setting \"{key}\".", nameof(key));
        lock (_lock)
        {
            if (value is null)
                database.Execute("DELETE FROM settings WHERE key = $k", ("$k", key));
            else
                database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
                    ("$k", key), ("$v", value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
