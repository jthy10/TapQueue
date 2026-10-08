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

/// <summary>Server releases on GitHub: tags server-vX.Y.Z with the server archive and SHA256SUMS attached.</summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
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

    public async Task<ServerReleaseDto?> LatestServerReleaseAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"repos/{Repository}/releases?per_page=100", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return PickLatest(json.RootElement);
    }

    /// <summary>
    /// The newest release in GitHub's list that's a published server release with the archive an
    /// upgrade installs. Drafts, pre-releases, client releases and anything without the files are skipped.
    /// </summary>
    public static ServerReleaseDto? PickLatest(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            return null;
        (Version Version, ServerReleaseDto Dto)? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease"))
                continue;
            if (Text(release, "tag_name") is not { } tag || !tag.StartsWith("server-v", StringComparison.Ordinal))
                continue;
            var version = tag["server-v".Length..];
            if (!ReleaseVersion.TryParse(version, out var parsed))
                continue;
            var assets = release.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => Text(x, "name")).ToHashSet()
                : [];
            if (!assets.Contains($"TapQueue_server_{version}_linux-x64.tar.gz") || !assets.Contains("SHA256SUMS"))
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

    public void Dispose() => _http.Dispose();
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
