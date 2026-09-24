using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// Installs the client build the server says every PC should run. Run by the TapQueue service,
/// which can write to Program Files. If TapQueueClient.exe's SHA-256 differs from the server's
/// build, it downloads the build next to it, verifies it and swaps the files (Windows allows
/// renaming a running exe). Tray apps notice their exe changed and restart themselves
/// (<see cref="TrayApp"/>); the service restarts itself.
/// </summary>
public sealed class ClientUpdater(HttpClient http, ILogger logger)
{
    private readonly string _exePath = Environment.ProcessPath!;
    private string? _ownSha256;
    private string? _failedSha256;

    /// <summary>Why installing the server's build failed, reported to the server until an install works.</summary>
    public string? LastError { get; private set; }

    /// <summary>The SHA-256 of the running TapQueueClient.exe, so the server can tell whether this PC is up to date.</summary>
    public async Task<string> OwnSha256Async(CancellationToken ct) => _ownSha256 ??= await HashFileAsync(_exePath, ct);

    /// <summary>An admin asked for an update now: try the build again even if it failed before.</summary>
    public void RetryFailed() => _failedSha256 = null;

    private string NewExePath => _exePath + ".new";
    private string OldExePath => _exePath + ".old";

    /// <summary>
    /// Installs <paramref name="build"/> if it differs from the running exe. On
    /// <see cref="UpdateOutcome.Installed"/> this process should restart.
    /// </summary>
    public async Task<UpdateOutcome> InstallIfDifferentAsync(ClientBuildDto? build, CancellationToken ct)
    {
        if (build is null)
            return UpdateOutcome.NoBuild;
        if (build.Sha256 == _failedSha256)
            return UpdateOutcome.Failed;
        if (string.Equals(build.Sha256, await OwnSha256Async(ct), StringComparison.OrdinalIgnoreCase))
        {
            LastError = null;
            return UpdateOutcome.UpToDate;
        }

        logger.LogInformation("Installing TapQueue client {Version} (running {Current})", build.Version, Shared.TapQueueVersion.Current);
        try
        {
            await InstallAsync(build, ct);
            return UpdateOutcome.Installed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            _failedSha256 = build.Sha256; // don't retry this build every minute; a newer one (or "update now") gets a fresh try
            LastError = ex.Message;
            logger.LogError("Couldn't install TapQueue client {Version}: {Error}", build.Version, ex.Message);
            return UpdateOutcome.Failed;
        }
    }

    private async Task InstallAsync(ClientBuildDto build, CancellationToken ct)
    {
        var url = build.DownloadPath.TrimStart('/') + "?computer=" + Uri.EscapeDataString(Environment.MachineName);
        await using (var file = File.Create(NewExePath))
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(file, ct);
        }

        var sha256 = await HashFileAsync(NewExePath, ct);
        if (!string.Equals(sha256, build.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(NewExePath);
            throw new InvalidDataException("The download was damaged (checksum mismatch).");
        }

        File.Delete(OldExePath);
        File.Move(_exePath, OldExePath);
        try
        {
            File.Move(NewExePath, _exePath);
        }
        catch
        {
            File.Move(OldExePath, _exePath); // put the running version back
            throw;
        }
    }

    /// <summary>Removes the exe replaced by the last update, once nothing is running it.</summary>
    public void CleanUp()
    {
        try
        {
            File.Delete(OldExePath);
            File.Delete(NewExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A tray app is still running the old exe; the next update overwrites it anyway.
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
}

public enum UpdateOutcome
{
    /// <summary>The server has no client build published.</summary>
    NoBuild,
    UpToDate,
    Installed,
    /// <summary>Installing failed (<see cref="ClientUpdater.LastError"/> says why).</summary>
    Failed,
}
