using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Updates;

/// <summary>
/// "Check for updates" on the console's Updates page: asks GitHub for the newest client release, or
/// the station release that goes with this server, and when an admin says so downloads it, checks it
/// against the release's SHA256SUMS and publishes it, the same as uploading the programs by hand.
/// PCs and stations then install it by themselves.
/// </summary>
public sealed class BuildUpdater(IBuildFeed feed, ClientBuildStore clientBuilds, StationBuildStore stationBuilds, ServerConfig config,
    EventLog events, ILogger<BuildUpdater> logger)
{
    /// <summary>GitHub allows 60 unauthenticated requests an hour; a check this recent is answered from memory.</summary>
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(1);

    private sealed class Checked
    {
        public ServerReleaseDto? Latest;
        public DateTimeOffset? At;
        public string? Error;
    }

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _working = new(1, 1);
    private readonly Checked _clients = new();
    private readonly Checked _stations = new();

    public Task<BuildReleaseDto> CheckClientsAsync(CancellationToken ct) =>
        CheckAsync(_clients, feed.LatestClientReleaseAsync, "GitHub lists no client releases.", ClientStatus, ct);

    public Task<BuildReleaseDto> CheckStationsAsync(CancellationToken ct) =>
        CheckAsync(_stations, c => feed.LatestStationReleaseAsync(ServerUpdater.Current, c),
            $"GitHub lists no station release for server {ServerUpdater.Current} or older.", StationStatus, ct);

    public BuildReleaseDto ClientStatus()
    {
        var published = ClientPlatform.All.Select(p => clientBuilds.Latest(p)?.Version).ToList();
        return Status(_clients, published.OfType<string>().ToList(), published.Any(v => v is null));
    }

    public BuildReleaseDto StationStatus()
    {
        var published = stationBuilds.Latest()?.Version;
        return Status(_stations, published is null ? [] : [published], published is null);
    }

    /// <summary>
    /// Publishes the Windows and Linux clients of release <paramref name="version"/>. A platform that
    /// already hands out that exact program is left alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">It can't be done; the message says why.</exception>
    public Task<BuildReleaseDto> PublishClientsAsync(string version, CancellationToken ct) => PublishAsync(_clients, version, async dir =>
    {
        await PublishClientsOfAsync(version, dir, ct);
        return ClientStatus();
    }, ct);

    /// <summary>Publishes the station build of release <paramref name="version"/>.</summary>
    /// <exception cref="InvalidOperationException">It can't be done; the message says why.</exception>
    public Task<BuildReleaseDto> PublishStationAsync(string version, CancellationToken ct) => PublishAsync(_stations, version, async dir =>
    {
        await PublishStationOfAsync(version, dir, ct);
        return StationStatus();
    }, ct);

    /// <summary>
    /// Publishes the clients and the station build of an older release, for a downgrade
    /// (<see cref="ServerDowngrade"/>, which has checked the version is one to go back to). All of
    /// them are fetched and checked before any is handed out.
    /// </summary>
    /// <exception cref="InvalidOperationException">It can't be done; the message says why.</exception>
    public Task PublishOlderAsync(string version, CancellationToken ct) => WithDownloadsAsync(version, async dir =>
    {
        var clients = await FetchClientsAsync(version, dir, ct);
        var station = await FetchStationAsync(version, dir, ct);
        await HandOutAsync(clients, "PCs install it when they next check in", ct);
        await HandOutStationAsync(station, ct);
        return true;
    }, ct);

    private async Task PublishClientsOfAsync(string version, string dir, CancellationToken ct) =>
        await HandOutAsync(await FetchClientsAsync(version, dir, ct), "PCs install it when they next check in", ct);

    private async Task PublishStationOfAsync(string version, string dir, CancellationToken ct) =>
        await HandOutStationAsync(await FetchStationAsync(version, dir, ct), ct);

    /// <summary>Downloads and checks both clients of a release. Nothing is published yet, so PCs don't end up on different versions.</summary>
    private async Task<List<(string Platform, string Path, string Label)>> FetchClientsAsync(string version, string dir, CancellationToken ct)
    {
        var tag = $"client-v{version}";
        var sums = await ChecksumsAsync(tag, Directory.CreateDirectory(Path.Combine(dir, "client")).FullName, ct);
        var programs = new List<(string Platform, string Path, string Label)>();
        foreach (var platform in ClientPlatform.All)
        {
            var archive = await FetchAsync(tag, GitHubReleaseFeed.ClientArchive(version, platform), sums, dir, ct);
            var program = Path.Combine(dir, $"client-{platform}");
            var label = Extract(archive, platform == ClientPlatform.Windows ? "TapQueueClient.exe" : "tapqueue-client", program)
                        ?? await LabelAsync(tag, version, ct);
            programs.Add((platform, program, label));
        }
        return programs;
    }

    private async Task HandOutAsync(List<(string Platform, string Path, string Label)> programs, string then, CancellationToken ct)
    {
        foreach (var (platform, path, label) in programs)
        {
            // A platform that already hands out that exact program is left alone.
            if (clientBuilds.Latest(platform) is { } current && current.Sha256 == Sha256(path))
                continue;
            await using var program = File.OpenRead(path);
            var build = await clientBuilds.PublishAsync(platform, label, program, ct);
            logger.LogInformation("Published {Platform} client {Version} from GitHub (sha256 {Sha256})", ClientPlatform.DisplayName(platform), build.Version, build.Sha256);
            events.Admin(null, $"Published {ClientPlatform.DisplayName(platform)} client {build.Version} from the GitHub release; {then}.");
        }
    }

    private async Task<(string Path, string Label)> FetchStationAsync(string version, string dir, CancellationToken ct)
    {
        var tag = $"server-v{version}";
        var sums = await ChecksumsAsync(tag, Directory.CreateDirectory(Path.Combine(dir, "server")).FullName, ct);
        var archive = await FetchAsync(tag, GitHubReleaseFeed.StationArchive(version), sums, dir, ct);
        var path = Path.Combine(dir, "station");
        return (path, Extract(archive, "tapqueue-station", path) ?? await LabelAsync(tag, version, ct));
    }

    private async Task HandOutStationAsync((string Path, string Label) station, CancellationToken ct)
    {
        if (stationBuilds.Latest() is { } current && current.Sha256 == Sha256(station.Path))
            return;
        await using var program = File.OpenRead(station.Path);
        var build = await stationBuilds.PublishAsync(station.Label, program, ct);
        logger.LogInformation("Published station build {Version} from GitHub (sha256 {Sha256})", build.Version, build.Sha256);
        events.Admin(null, $"Published station build {build.Version} from the GitHub release; stations install it within seconds.");
    }

    private async Task<BuildReleaseDto> CheckAsync(Checked state, Func<CancellationToken, Task<ServerReleaseDto?>> latest, string none,
        Func<BuildReleaseDto> status, CancellationToken ct)
    {
        await _working.WaitAsync(ct);
        try
        {
            lock (_lock)
                if (state.At is { } last && state.Error is null && DateTimeOffset.UtcNow - last < RecheckAfter)
                    return status();
            try
            {
                var found = await latest(ct);
                lock (_lock)
                {
                    state.Latest = found;
                    state.Error = found is null ? none : null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Couldn't check GitHub for builds: {Error}", ex.Message);
                lock (_lock)
                    state.Error = $"Couldn't reach GitHub to check for updates: {ex.Message}";
            }
            lock (_lock)
                state.At = DateTimeOffset.UtcNow;
            return status();
        }
        finally
        {
            _working.Release();
        }
    }

    private BuildReleaseDto Status(Checked state, List<string> published, bool somethingMissing)
    {
        lock (_lock)
            return new BuildReleaseDto(state.Latest, state.At, state.Error,
                state.Latest is not null && (somethingMissing || published.Any(v => ReleaseVersion.IsNewer(state.Latest.Version, v))),
                published);
    }

    private Task<BuildReleaseDto> PublishAsync(Checked state, string version, Func<string, Task<BuildReleaseDto>> publish, CancellationToken ct)
    {
        // Only what an admin just looked at: the version is part of a download address, and a release
        // can't be swapped for another between the check and the click.
        lock (_lock)
            if (!ReleaseVersion.TryParse(version, out _) || state.Latest?.Version != version)
                throw new InvalidOperationException($"{version} isn't the release the last check found. Check for updates again.");
        return WithDownloadsAsync(version, publish, ct);
    }

    /// <summary>Runs <paramref name="publish"/> with a folder to download into, one publish at a time.</summary>
    private async Task<T> WithDownloadsAsync<T>(string version, Func<string, Task<T>> publish, CancellationToken ct)
    {
        await _working.WaitAsync(ct);
        var dir = Path.Combine(config.Server.DataDir, $"release-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            return await publish(dir);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            logger.LogWarning("Couldn't publish release {Version} from GitHub: {Error}", version, ex.Message);
            throw new InvalidOperationException($"Couldn't get release {version} from GitHub: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            _working.Release();
        }
    }

    /// <summary>The release's SHA256SUMS: file name to hash.</summary>
    private async Task<Dictionary<string, string>> ChecksumsAsync(string tag, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, "SHA256SUMS");
        await feed.DownloadAsync(tag, "SHA256SUMS", path, ct);
        return (await File.ReadAllLinesAsync(path, ct))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1].TrimStart('*'), parts => parts[0].ToLowerInvariant());
    }

    private async Task<string> FetchAsync(string tag, string asset, Dictionary<string, string> sums, string dir, CancellationToken ct)
    {
        if (!sums.TryGetValue(asset, out var expected))
            throw new InvalidDataException($"The release's SHA256SUMS doesn't list {asset}.");
        var path = Path.Combine(dir, asset);
        await feed.DownloadAsync(tag, asset, path, ct);
        if (Sha256(path) != expected)
            throw new InvalidDataException($"{asset} doesn't match the release's SHA256SUMS; it wasn't published.");
        return path;
    }

    /// <summary>"0.12.1+8173198", what the program itself reports, for archives without a version.txt.</summary>
    private async Task<string> LabelAsync(string tag, string version, CancellationToken ct) =>
        await feed.CommitAsync(tag, ct) is { Length: >= 7 } commit ? $"{version}+{commit[..7]}" : version;

    /// <summary>
    /// Copies <paramref name="program"/> out of a release archive (.zip or .tar.gz) to
    /// <paramref name="destination"/> and returns the version.txt packaged with it, if there is one.
    /// </summary>
    internal static string? Extract(string archive, string program, string destination)
    {
        string? version = null;
        var found = false;
        void Take(string name, Func<Stream> open)
        {
            if (name == program && !found)
            {
                using var from = open();
                using var to = File.Create(destination);
                from.CopyTo(to);
                found = true;
            }
            else if (name == "version.txt")
            {
                using var reader = new StreamReader(open());
                version = reader.ReadToEnd().Trim();
            }
        }

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
                Take(entry.Name, entry.Open);
        }
        else
        {
            using var tar = new TarReader(new GZipStream(File.OpenRead(archive), CompressionMode.Decompress));
            while (tar.GetNextEntry() is { } entry)
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data)
                    Take(Path.GetFileName(entry.Name), () => data);
        }
        if (!found)
            throw new InvalidDataException($"{Path.GetFileName(archive)} has no {program} in it.");
        return string.IsNullOrWhiteSpace(version) || version.Length > 64 ? null : version;
    }

    private static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
