using System.Text.RegularExpressions;

namespace TapQueue.Shared;

/// <summary>
/// Checks a program a TapQueue server offers a PC or a station against TapQueue's own release
/// before it's installed. The server only passes builds on: each release publishes the SHA-256 of
/// its programs (<see cref="ProgramsFile"/>), and the PC or station fetches that itself, straight
/// from where releases are published, and installs the build only if its SHA-256 is listed there.
/// A server that was taken over can then offer any release it likes, but not a program of its own.
/// </summary>
public static partial class ReleaseCheck
{
    /// <summary>Where TapQueue's releases are downloaded from: &lt;this&gt;/&lt;tag&gt;/&lt;file&gt;.</summary>
    public const string DefaultUrl = "https://github.com/jthy10/TapQueue/releases/download";

    /// <summary>
    /// The release file that lists its programs, one "sha256  name" line each: TapQueueClient.exe and
    /// tapqueue-client in a client release, tapqueue-station in a server release. scripts/program-sums.sh writes it.
    /// </summary>
    public const string ProgramsFile = "SHA256SUMS.programs";

    public const string ClientTag = "client-v";
    public const string StationTag = "server-v";

    private const int MaxListBytes = 64 * 1024;

    [GeneratedRegex(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$")]
    private static partial Regex VersionShape();

    /// <param name="Ok">The program is in the release it claims to be from.</param>
    /// <param name="Problem">Why not, for the person who looks at why an update didn't install.</param>
    /// <param name="TryAgain">The release couldn't be asked (no way out to it just now), so it's worth another try later.</param>
    public sealed record Result(bool Ok, string? Problem = null, bool TryAgain = false);

    /// <summary>An HttpClient for asking where releases are published, with this machine's ordinary trust (not the TapQueue server's pinned certificate).</summary>
    public static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <param name="releasesUrl">Empty for <see cref="DefaultUrl"/>; a site that builds its own TapQueue points this at its own releases.</param>
    /// <param name="tagPrefix"><see cref="ClientTag"/> or <see cref="StationTag"/>.</param>
    /// <param name="version">What the server calls the build, like 0.17.0+1a2b3c4; only the 0.17.0 is used.</param>
    /// <param name="program">The program's file name in the release, like TapQueueClient.exe.</param>
    public static async Task<Result> VerifyAsync(HttpClient http, string? releasesUrl, string tagPrefix, string version, string program, string sha256,
        CancellationToken ct)
    {
        var plus = version.IndexOf('+');
        var release = plus >= 0 ? version[..plus] : version;
        if (!VersionShape().IsMatch(release))
            return new Result(false, $"The server's build is called \"{version}\", which isn't a TapQueue release, so it wasn't installed.");

        var from = string.IsNullOrWhiteSpace(releasesUrl) ? DefaultUrl : releasesUrl.Trim().TrimEnd('/');
        var url = $"{from}/{tagPrefix}{release}/{ProgramsFile}";
        string list;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(false, $"TapQueue release {release} doesn't list its programs ({url} isn't there), so the server's build {version} can't be checked and wasn't installed.");
            if (!response.IsSuccessStatusCode)
                return new Result(false, $"Couldn't check the server's build {version} against TapQueue release {release}: {url} answered {(int)response.StatusCode}. Not installed; trying again later.", TryAgain: true);
            if (response.Content.Headers.ContentLength > MaxListBytes)
                return new Result(false, $"{url} is too big to be a list of programs, so the server's build {version} wasn't installed.");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxListBytes + 1];
            var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            if (read > MaxListBytes)
                return new Result(false, $"{url} is too big to be a list of programs, so the server's build {version} wasn't installed.");
            list = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new Result(false, $"Couldn't reach {new Uri(url).Host} to check the server's build {version} against TapQueue release {release} ({ex.Message}). " +
                "Not installed; trying again later. This computer needs to be able to reach it to update.", TryAgain: true);
        }

        foreach (var line in list.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == program && parts[0].Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return new Result(true);
        }
        return new Result(false, $"The server's build {version} isn't the {program} of TapQueue release {release}, so it wasn't installed.");
    }
}
