using System.Diagnostics;
using System.Security.Cryptography;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// Installs the client build the server says every client should run. If this exe's SHA-256
/// differs from the server's build, it downloads the build next to itself, verifies it, swaps
/// the files (Windows allows renaming a running exe), starts the new exe and exits.
/// The new exe waits for this one to exit before it takes over (see <see cref="Program"/>).
/// </summary>
public sealed class ClientUpdater(TapQueueApi api, Action<string, string, ToolTipIcon> notify, Action exit)
{
    public const string WaitForArg = "--wait-for";
    public const string UpdatedFromArg = "--updated-from";

    private readonly string _exePath = Environment.ProcessPath!;
    private string? _ownSha256;
    private string? _failedSha256;
    private bool _busy;

    public string OldExePath => _exePath + ".old";

    public async Task CheckAsync(ClientBuildDto? build, string[] args)
    {
        if (build is null || _busy || build.Sha256 == _failedSha256)
            return;
        _busy = true;
        try
        {
            _ownSha256 ??= await HashFileAsync(_exePath, CancellationToken.None);
            if (string.Equals(build.Sha256, _ownSha256, StringComparison.OrdinalIgnoreCase))
                return;
            await InstallAsync(build, args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                                       or TapQueueApiException or TaskCanceledException or InvalidDataException
                                       or System.ComponentModel.Win32Exception)
        {
            _failedSha256 = build.Sha256; // don't retry this build every heartbeat; a newer one gets a fresh try
            notify("Couldn't update TapQueue", $"Version {build.Version}: {ex.Message}", ToolTipIcon.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallAsync(ClientBuildDto build, string[] args)
    {
        var newPath = _exePath + ".new";
        await using (var file = File.Create(newPath))
            await api.DownloadAsync(build.DownloadPath, file);

        var sha256 = await HashFileAsync(newPath, CancellationToken.None);
        if (!string.Equals(sha256, build.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(newPath);
            throw new InvalidDataException("The download was damaged (checksum mismatch).");
        }

        File.Delete(OldExePath);
        File.Move(_exePath, OldExePath);
        try
        {
            File.Move(newPath, _exePath);
            var start = new ProcessStartInfo(_exePath) { UseShellExecute = false };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            start.ArgumentList.Add(WaitForArg);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(UpdatedFromArg);
            start.ArgumentList.Add(Shared.TapQueueVersion.Current);
            Process.Start(start);
        }
        catch
        {
            // Put the running version back so the next start still works.
            File.Delete(_exePath);
            File.Move(OldExePath, _exePath);
            throw;
        }
        exit();
    }

    /// <summary>After an update: removes the previous exe, which couldn't be deleted while it was running.</summary>
    public void CleanUp()
    {
        try
        {
            File.Delete(OldExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked or not ours to delete; the next update overwrites it anyway.
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
}
