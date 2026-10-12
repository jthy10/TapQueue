using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Updates;

/// <summary>
/// "Downgrade" on the console's Server page: takes the whole site back to an older release, for
/// when an update turned out badly. The server hands out that release's clients and station build
/// (PCs and stations install whatever build it hands out, older ones too), and then asks the
/// updater to install that release of the server. update-server.sh has the server step its database
/// back first, since the older program can't read a newer one; a backup is made before that.
/// Only releases from <see cref="OldestVersion"/> on, the first one that could be gone back to, and
/// only complete ones, so the server, PCs and stations all end up on the same version.
/// </summary>
public sealed class ServerDowngrade(IReleaseFeed feed, BuildUpdater builds, ServerUpdater updater, EventLog events, ILogger<ServerDowngrade> logger)
{
    public const string OldestVersion = "0.17.0";

    /// <summary>GitHub allows 60 unauthenticated requests an hour; a check this recent is answered from memory.</summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _working = new(1, 1);
    private IReadOnlyList<ServerReleaseDto> _versions = [];
    private DateTimeOffset? _checkedAt;
    private string? _checkError;

    public async Task<ServerDowngradeDto> CheckAsync(CancellationToken ct)
    {
        await _working.WaitAsync(ct);
        try
        {
            if (_checkedAt is { } last && _checkError is null && DateTimeOffset.UtcNow - last < RecheckAfter)
                return Status();
            try
            {
                var versions = await feed.OlderReleasesAsync(ServerUpdater.Current, ct);
                lock (_lock)
                    (_versions, _checkError) = (versions, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Couldn't ask GitHub for older releases: {Error}", ex.Message);
                lock (_lock)
                    _checkError = $"Couldn't reach GitHub to look for older releases: {ex.Message}";
            }
            lock (_lock)
                _checkedAt = DateTimeOffset.UtcNow;
            return Status();
        }
        finally
        {
            _working.Release();
        }
    }

    public ServerDowngradeDto Status()
    {
        var cannot = updater.CannotApplyReason();
        lock (_lock)
            return new ServerDowngradeDto(ServerUpdater.Current, OldestVersion, _versions, _checkedAt, _checkError, cannot is null, cannot);
    }

    /// <summary>
    /// Takes the clients, stations and then the server back to <paramref name="version"/>, which must
    /// be one the last check listed. Returns an error message instead if it can't.
    /// </summary>
    public async Task<string?> ApplyAsync(string version, CancellationToken ct)
    {
        if (updater.CannotApplyReason() is { } cannot)
            return cannot;
        if (!ReleaseVersion.TryParse(version, out var target) || !ReleaseVersion.TryParse(OldestVersion, out var oldest) || target < oldest)
            return $"{OldestVersion} is the oldest release TapQueue can go back to.";
        lock (_lock)
            if (!_versions.Any(v => v.Version == version))
                return $"{version} isn't one of the releases the last check found to go back to. Check again.";
        if (updater.LastRun() is { State: "running" or "requested" } run)
            return $"A change to {run.Version} is already under way.";
        if (updater.ReleasingJobs() is > 0 and var releasing)
            return $"{releasing} job{(releasing == 1 ? " is" : "s are")} printing right now; downgrading restarts the server. Try again in a moment.";

        await _working.WaitAsync(ct);
        try
        {
            // The fleet first: the older server then finds the builds it should hand out already there,
            // and if they can't be fetched nothing has changed yet.
            await builds.PublishOlderAsync(version, ct);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        finally
        {
            _working.Release();
        }
        updater.RequestDowngrade(version);
        events.Admin(null, $"Started taking TapQueue back to {version}: its clients and station build are handed out, and the server is being downgraded.");
        return null;
    }
}
