using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Updates;

/// <summary>Where the server looks for newer releases of itself. Tests swap in a fake.</summary>
public interface IReleaseFeed
{
    /// <summary>The newest published server release, or null if there's none.</summary>
    Task<ServerReleaseDto?> LatestServerReleaseAsync(CancellationToken ct);
}

/// <summary>Where the server gets client and station builds to hand out. Tests swap in a fake.</summary>
public interface IBuildFeed
{
    /// <summary>The newest published client release with both platforms' archives, or null if there's none.</summary>
    Task<ServerReleaseDto?> LatestClientReleaseAsync(CancellationToken ct);

    /// <summary>
    /// The newest server release that has a station archive and isn't newer than <paramref name="serverVersion"/>:
    /// stations and the server are released together, and a station shouldn't run ahead of its server.
    /// </summary>
    Task<ServerReleaseDto?> LatestStationReleaseAsync(string serverVersion, CancellationToken ct);

    /// <summary>The commit a release was built from, for version labels like 0.9.7+1a2b3c4; null if it can't be told.</summary>
    Task<string?> CommitAsync(string tag, CancellationToken ct);

    /// <summary>Saves a file attached to the release tagged <paramref name="tag"/> to <paramref name="destination"/>.</summary>
    Task DownloadAsync(string tag, string asset, string destination, CancellationToken ct);
}

/// <summary>Server releases on GitHub: tags server-vX.Y.Z with the server archive and SHA256SUMS attached.</summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IBuildFeed, IDisposable
{
    public const string Repository = "jthy10/TapQueue";

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://api.github.com/"),
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            UserAgent = { new ProductInfoHeaderValue("tapqueue-server", ReleaseVersion.Of(TapQueueVersion.Current)) },
            Accept = { new MediaTypeWithQualityHeaderValue("application/vnd.github+json") },
        },
    };

    /// <summary>Release archives are tens of megabytes; anything past this isn't one.</summary>
    private const long MaxDownloadBytes = 400L * 1024 * 1024;

    // Downloads redirect to GitHub's file servers; no API headers, and time for a slow line.
    private readonly HttpClient _downloads = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("tapqueue-server", ReleaseVersion.Of(TapQueueVersion.Current)) } },
    };

    public async Task<ServerReleaseDto?> LatestServerReleaseAsync(CancellationToken ct)
    {
        using var json = await ReleasesAsync(ct);
        return PickLatest(json.RootElement);
    }

    public async Task<ServerReleaseDto?> LatestClientReleaseAsync(CancellationToken ct)
    {
        using var json = await ReleasesAsync(ct);
        return PickLatest(json.RootElement, "client-v", v => [ClientArchive(v, ClientPlatform.Windows), ClientArchive(v, ClientPlatform.Linux), "SHA256SUMS"]);
    }

    public async Task<ServerReleaseDto?> LatestStationReleaseAsync(string serverVersion, CancellationToken ct)
    {
        using var json = await ReleasesAsync(ct);
        return PickLatest(json.RootElement, "server-v", v => [StationArchive(v), "SHA256SUMS"],
            v => !ReleaseVersion.TryParse(ReleaseVersion.Of(serverVersion), out var running) || v <= running);
    }

    public async Task<string?> CommitAsync(string tag, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"repos/{Repository}/commits/{Uri.EscapeDataString(tag)}", ct);
        if (!response.IsSuccessStatusCode)
            return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return Text(json.RootElement, "sha");
    }

    public async Task DownloadAsync(string tag, string asset, string destination, CancellationToken ct)
    {
        var url = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";
        using var response = await _downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {asset}.");
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new HttpRequestException($"{asset} is too big to be a TapQueue release file.");
        await using var from = await response.Content.ReadAsStreamAsync(ct);
        await using var to = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            if ((total += read) > MaxDownloadBytes)
                throw new HttpRequestException($"{asset} is too big to be a TapQueue release file.");
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    public static string ClientArchive(string version, string platform) =>
        $"TapQueue_client_{version}_{platform}.{(platform == ClientPlatform.Windows ? "zip" : "tar.gz")}";

    public static string StationArchive(string version) => $"TapQueue_station_{version}_linux-x64.tar.gz";

    private async Task<JsonDocument> ReleasesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"repos/{Repository}/releases?per_page=100", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    /// <summary>
    /// The newest release in GitHub's list that's a published server release with the archive an
    /// upgrade installs. Drafts, pre-releases, client releases and anything without the files are skipped.
    /// </summary>
    public static ServerReleaseDto? PickLatest(JsonElement releases) =>
        PickLatest(releases, "server-v", v => [$"TapQueue_server_{v}_linux-x64.tar.gz", "SHA256SUMS"]);

    /// <summary>
    /// The newest published release whose tag is <paramref name="tagPrefix"/> and a version, that has
    /// every file <paramref name="needs"/> names for that version and that <paramref name="allow"/> lets through.
    /// </summary>
    public static ServerReleaseDto? PickLatest(JsonElement releases, string tagPrefix, Func<string, string[]> needs, Func<Version, bool>? allow = null)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            return null;
        (Version Version, ServerReleaseDto Dto)? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease"))
                continue;
            if (Text(release, "tag_name") is not { } tag || !tag.StartsWith(tagPrefix, StringComparison.Ordinal))
                continue;
            var version = tag[tagPrefix.Length..];
            if (!ReleaseVersion.TryParse(version, out var parsed) || allow?.Invoke(parsed) == false)
                continue;
            var assets = release.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => Text(x, "name")).ToHashSet()
                : [];
            if (!needs(version).All(assets.Contains))
                continue;
            if (best is null || parsed > best.Value.Version)
            {
                var published = Text(release, "published_at") is { } p && DateTimeOffset.TryParse(p, out var at) ? at : (DateTimeOffset?)null;
                var url = Text(release, "html_url") ?? $"https://github.com/{Repository}/releases/tag/{tag}";
                best = (parsed, new ServerReleaseDto(version, url, published));
            }
        }
        return best?.Dto;
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        _http.Dispose();
        _downloads.Dispose();
    }
}

/// <summary>Release versions: X.Y.Z, the same shape update-server.sh accepts.</summary>
public static partial class ReleaseVersion
{
    [GeneratedRegex(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$")]
    private static partial Regex Shape();

    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        return text is not null && Shape().IsMatch(text) && Version.TryParse(text, out version!);
    }

    /// <summary>"0.8.0+1a2b3c4" -> "0.8.0".</summary>
    public static string Of(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    /// <summary>True if <paramref name="candidate"/> is a release newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var c) && (!TryParse(Of(current), out var r) || c > r);
}
