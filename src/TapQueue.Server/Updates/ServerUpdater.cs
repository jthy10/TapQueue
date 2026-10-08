using System.Globalization;
using TapQueue.Server.Api;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Updates;

/// <summary>
/// Where the pieces install-server.sh sets up for upgrades live. The server runs as the tapqueue user
/// and can't replace itself, so it writes the version it wants to <see cref="RequestFile"/>;
/// tapqueue-update.path sees the file and runs <see cref="UpdaterScript"/> (update-server.sh) as
/// root, which reports to <see cref="StatusFile"/>. Tests point these at a temp directory.
/// </summary>
/// <param name="RunBySystemd">Whether systemd runs this server, so something starts it again after the upgrade.</param>
public sealed record UpdaterSetup(string RequestFile, string StatusFile, string UpdaterScript, string PathUnit, Func<bool> RunBySystemd)
{
    public static readonly UpdaterSetup Installed = new(
        "/var/lib/tapqueue/update-request",
        "/var/lib/tapqueue-update/status",
        "/opt/tapqueue/update-server.sh",
        "/etc/systemd/system/tapqueue-update.path",
        () => AdminServerApi.CanRestart);
}

/// <summary>
/// "Check for updates" on the console's Server page: asks GitHub for the newest server release and,
/// when an admin says so, upgrades to it now or at a time they pick. Downloading, checking the
/// SHA-256 and installing happen in update-server.sh, as root.
/// </summary>
public sealed class ServerUpdater(UpdaterSetup setup, IReleaseFeed feed, Database database, EventLog events, ILogger<ServerUpdater> logger)
{
    private const string ScheduledVersionKey = "update.version";
    private const string ScheduledAtKey = "update.at";
    private const string ScheduledByKey = "update.by";

    /// <summary>GitHub allows 60 unauthenticated requests an hour; a check this recent is answered from memory.</summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(1);

    /// <summary>An upgrade still "running" after this long has died without saying so.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    /// <summary>tapqueue-update.path starts the updater within a second; a request older than this wasn't picked up.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan LatestSchedule = TimeSpan.FromDays(30);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private ServerReleaseDto? _latest;
    private DateTimeOffset? _checkedAt;
    private string? _checkError;

    public static string Current => ReleaseVersion.Of(TapQueueVersion.Current);

    public async Task<ServerUpdateDto> CheckAsync(CancellationToken ct)
    {
        await _checking.WaitAsync(ct);
        try
        {
            if (_checkedAt is { } last && _checkError is null && DateTimeOffset.UtcNow - last < RecheckAfter)
                return Status();
            try
            {
                var latest = await feed.LatestServerReleaseAsync(ct);
                lock (_lock)
                {
                    _latest = latest;
                    _checkError = latest is null ? "GitHub lists no server releases." : null;
                }
                if (latest is not null && ReleaseVersion.IsNewer(latest.Version, Current))
                    logger.LogInformation("TapQueue server {Version} is available (running {Current})", latest.Version, Current);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Couldn't check GitHub for server updates: {Error}", ex.Message);
                lock (_lock)
                    _checkError = $"Couldn't reach GitHub to check for updates: {ex.Message}";
            }
            lock (_lock)
                _checkedAt = DateTimeOffset.UtcNow;
            return Status();
        }
        finally
        {
            _checking.Release();
        }
    }

    public ServerUpdateDto Status()
    {
        var cannot = CannotApplyReason();
        lock (_lock)
            return new ServerUpdateDto(Current, _latest, _checkedAt, _checkError,
                _latest is not null && ReleaseVersion.IsNewer(_latest.Version, Current),
                cannot is null, cannot, Scheduled(), LastRun());
    }

    /// <summary>Null if this server can upgrade itself; otherwise why not, and what to do instead.</summary>
    public string? CannotApplyReason()
    {
        if (!setup.RunBySystemd())
            return "This server wasn't started by systemd, so it can't upgrade itself. Upgrade it where you installed it.";
        if (!File.Exists(setup.UpdaterScript) || !File.Exists(setup.PathUnit))
            return "This server was installed before it could upgrade itself. Upgrade it once on the server with: " +
                $"curl -fsSL https://raw.githubusercontent.com/{GitHubReleaseFeed.Repository}/main/install.sh | sudo bash -s server";
        var dir = Path.GetDirectoryName(setup.RequestFile);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return $"{dir} is missing, so the server can't ask for an upgrade.";
        return null;
    }

    /// <summary>
    /// Upgrades to <paramref name="version"/> now, or schedules it for <paramref name="at"/>. Only the
    /// newest release the last check found, and only if it's newer than what's running.
    /// Returns an error message instead if it can't.
    /// </summary>
    public string? Apply(string version, DateTimeOffset? at, string by)
    {
        if (CannotApplyReason() is { } cannot)
            return cannot;
        if (!ReleaseVersion.TryParse(version, out _))
            return $"\"{version}\" isn't a release version like 1.2.3.";
        if (!ReleaseVersion.IsNewer(version, Current))
            return $"This server already runs {Current}; it only upgrades to a newer version.";
        lock (_lock)
            if (_latest?.Version != version)
                return $"{version} isn't the newest release the last check found. Check for updates again.";
        if (LastRun() is { State: "running" or "requested" } run)
            return $"An upgrade to {run.Version} is already under way.";

        var now = DateTimeOffset.UtcNow;
        if (at is { } when && when > now.AddMinutes(1))
        {
            if (when > now + LatestSchedule)
                return "Pick a time within the next 30 days.";
            SaveSchedule(version, when, by);
            events.Admin(null, $"Scheduled an upgrade of the server to {version} for {when.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
            logger.LogInformation("Upgrade to {Version} scheduled for {At:u}", version, when);
            return null;
        }

        ClearSchedule();
        Request(version);
        events.Admin(null, $"Started upgrading the server to {version}.");
        return null;
    }

    public bool CancelSchedule()
    {
        if (Scheduled() is not { } scheduled)
            return false;
        ClearSchedule();
        events.Admin(null, $"Called off the scheduled upgrade to {scheduled.Version}.");
        logger.LogInformation("Scheduled upgrade to {Version} called off", scheduled.Version);
        return true;
    }

    /// <summary>Starts a scheduled upgrade whose time has come; drops one this server is already past.</summary>
    public void RunDue(DateTimeOffset now)
    {
        if (Scheduled() is not { } scheduled)
            return;
        if (!ReleaseVersion.IsNewer(scheduled.Version, Current))
        {
            ClearSchedule();
            logger.LogInformation("Dropped the scheduled upgrade to {Version}: running {Current} already", scheduled.Version, Current);
            return;
        }
        if (scheduled.At > now)
            return;

        ClearSchedule();
        if (CannotApplyReason() is { } cannot)
        {
            logger.LogError("Scheduled upgrade to {Version} didn't start: {Reason}", scheduled.Version, cannot);
            events.Record(EventCategory.Admin, EventLog.System, null, $"The scheduled upgrade to {scheduled.Version} didn't start: {cannot}");
            return;
        }
        Request(scheduled.Version);
        events.Record(EventCategory.Admin, EventLog.System, null, $"Started the scheduled upgrade of the server to {scheduled.Version}.");
    }

    /// <summary>Writes the request in one go (a temp file renamed over it) so the updater never reads half of it.</summary>
    private void Request(string version)
    {
        var temp = setup.RequestFile + ".tmp";
        File.WriteAllText(temp, version + "\n");
        File.Move(temp, setup.RequestFile, overwrite: true);
        logger.LogWarning("Upgrading to TapQueue server {Version}: tapqueue-update downloads, checks and installs it, then restarts the server", version);
    }

    public ScheduledUpdateDto? Scheduled()
    {
        var saved = database.Query("SELECT key, value FROM settings WHERE key IN ($v, $a, $b)",
                r => (r.GetString(0), r.GetString(1)), ("$v", ScheduledVersionKey), ("$a", ScheduledAtKey), ("$b", ScheduledByKey))
            .ToDictionary(p => p.Item1, p => p.Item2);
        return saved.TryGetValue(ScheduledVersionKey, out var version)
            && saved.TryGetValue(ScheduledAtKey, out var at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, out var when)
            ? new ScheduledUpdateDto(version, when, saved.GetValueOrDefault(ScheduledByKey))
            : null;
    }

    private void SaveSchedule(string version, DateTimeOffset at, string by)
    {
        foreach (var (key, value) in new[] { (ScheduledVersionKey, version), (ScheduledAtKey, at.ToUniversalTime().ToString("O")), (ScheduledByKey, by) })
            database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
                ("$k", key), ("$v", value));
    }

    private void ClearSchedule() =>
        database.Execute("DELETE FROM settings WHERE key IN ($v, $a, $b)",
            ("$v", ScheduledVersionKey), ("$a", ScheduledAtKey), ("$b", ScheduledByKey));

    /// <summary>
    /// The upgrade waiting for the updater to pick up (state "requested"), or what update-server.sh last
    /// wrote to its status file: key=value lines for state, version, at and message.
    /// </summary>
    public UpdateRunDto? LastRun()
    {
        if (File.Exists(setup.RequestFile))
        {
            var requested = File.GetLastWriteTimeUtc(setup.RequestFile);
            var version = SafeRead(setup.RequestFile)?.Trim() ?? "";
            var asked = new DateTimeOffset(requested, TimeSpan.Zero);
            return DateTimeOffset.UtcNow - asked < RequestTimeout
                ? new UpdateRunDto("requested", version, asked, "Waiting for tapqueue-update to start.")
                : new UpdateRunDto("failed", version, asked,
                    "tapqueue-update never started. Check it on the server: systemctl status tapqueue-update.path tapqueue-update");
        }
        if (SafeRead(setup.StatusFile) is not { } text)
            return null;
        var fields = text.Split('\n')
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        var state = fields.GetValueOrDefault("state");
        if (state is not ("running" or "done" or "failed"))
            return null;
        DateTimeOffset? at = DateTimeOffset.TryParse(fields.GetValueOrDefault("at"), CultureInfo.InvariantCulture, out var t) ? t : null;
        if (state == "running" && at is { } started && DateTimeOffset.UtcNow - started > RunTimeout)
            return new UpdateRunDto("failed", fields.GetValueOrDefault("version") ?? "", at,
                "The upgrade stopped without finishing. See: journalctl -u tapqueue-update -n 100");
        return new UpdateRunDto(state, fields.GetValueOrDefault("version") ?? "", at, fields.GetValueOrDefault("message") ?? "");
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Starts scheduled upgrades when their time comes.</summary>
public sealed class UpdateScheduler(ServerUpdater updater, ILogger<UpdateScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                updater.RunDue(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Starting a scheduled upgrade failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
