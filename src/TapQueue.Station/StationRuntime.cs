using System.Net.Http.Json;
using System.Security.Cryptography;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Station;

/// <summary>
/// The settings the station runs with: station.toml, overridden by whatever the server holds for it.
/// Repeat time and minimum length apply straight away; a different reader or device needs a restart.
/// </summary>
internal sealed record EffectiveSettings(string Reader, string Device, int RepeatSeconds, int MinCardLength, int Version)
{
    public static EffectiveSettings From(StationConfig local, StationSettingsDto? server)
    {
        var reader = server?.Reader ?? local.Reader;
        var device = server?.Device ?? (server?.Reader is null ? local.Device : "");
        // A keyboard reader needs a device; without one it would read standard input, which under
        // systemd is nothing. Keep station.toml's reader rather than go deaf.
        if (reader == "keyboard" && device.Length == 0)
            (reader, device) = (local.Reader, local.Device);
        return new(reader, device, server?.RepeatSeconds ?? local.RepeatSeconds, server?.MinCardLength ?? local.MinCardLength, server?.Version ?? 0);
    }

    public bool NeedsRestartFor(EffectiveSettings next) => next.Reader != Reader || next.Device != Device;
}

/// <summary>
/// Sends a heartbeat every few seconds and acts on the reply: new settings, a restart command, or a
/// newer published build. Restarting means exiting; systemd (Restart=always) starts the station again.
/// </summary>
internal sealed class StationRuntime(HttpClient http, StationConfig local, Action<string> log)
{
    private static readonly TimeSpan UpdateRetry = TimeSpan.FromMinutes(10);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly string? _ownSha256 = HashOf(Environment.ProcessPath);
    private DateTimeOffset _lastUpdateAttempt = DateTimeOffset.MinValue;
    private bool _unsupportedLogged;

    public EffectiveSettings Settings { get; private set; } = EffectiveSettings.From(local, null);

    /// <summary>The badge reader, once it exists, for reporting its status.</summary>
    public IBadgeReader? Reader { get; set; }

    /// <summary>Gets the server's settings before the reader starts. Falls back to station.toml if the server is older.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (await BeatAsync(ct) is { } reply)
            Settings = EffectiveSettings.From(local, reply.Settings);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(StationStatus.HeartbeatSeconds));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (await BeatAsync(ct) is not { } reply)
                continue;

            if (reply.Command == StationCommand.Restart)
                Restart("the server asked this station to restart");

            if (reply.Settings.Version != Settings.Version)
            {
                var next = EffectiveSettings.From(local, reply.Settings);
                if (Settings.NeedsRestartFor(next))
                    Restart($"the reader changed to {next.Reader}{(next.Device.Length > 0 ? $" {next.Device}" : "")}");
                log($"New settings from the server: repeat {next.RepeatSeconds} s, minimum card length {next.MinCardLength}.");
                Settings = next;
            }

            if (reply.Build is { } build && _ownSha256 is not null && !build.Sha256.Equals(_ownSha256, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.UtcNow - _lastUpdateAttempt > UpdateRetry)
            {
                _lastUpdateAttempt = DateTimeOffset.UtcNow;
                if (await SelfUpdate.InstallAsync(http, local.ServerUrl, build, log, ct))
                    Restart($"installed station build {build.Version}");
            }
        }
    }

    private async Task<StationHeartbeatResponse?> BeatAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("heartbeat", new StationHeartbeatRequest(
                TapQueueVersion.Current, _startedAt, Settings.Reader, Reader?.Status ?? "starting", _ownSha256, Settings.Version), TapQueueJson.Options, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!_unsupportedLogged)
                    log("The server is older and doesn't take station heartbeats; using station.toml only.");
                _unsupportedLogged = true;
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<StationHeartbeatResponse>(TapQueueJson.Options, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // Taps report connection problems; heartbeats just try again.
        }
    }

    private void Restart(string why)
    {
        log($"Restarting: {why}.");
        Environment.Exit(0);
    }

    private static string? HashOf(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}

/// <summary>
/// Installs a published station build. The service can't write /opt (ProtectSystem=strict), so builds go
/// in its state directory, which the systemd unit starts from when there's one there.
/// </summary>
internal static class SelfUpdate
{
    public static async Task<bool> InstallAsync(HttpClient http, string serverUrl, StationBuildDto build, Action<string> log, CancellationToken ct)
    {
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
        if (string.IsNullOrEmpty(stateDir))
        {
            log($"Station build {build.Version} is published, but this station wasn't started by systemd, so it can't update itself.");
            return false;
        }

        var target = Path.Combine(stateDir, "tapqueue-station");
        var download = target + ".download";
        try
        {
            log($"Downloading station build {build.Version}...");
            await using (var stream = await http.GetStreamAsync(new Uri(new Uri(serverUrl), build.DownloadPath), ct))
            await using (var file = File.Create(download))
                await stream.CopyToAsync(file, ct);

            await using (var file = File.OpenRead(download))
            {
                var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
                if (!sha.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"the download's SHA-256 is {sha}, not {build.Sha256}");
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(download, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // Make sure it runs here before switching to it, so a bad build can't leave the station dead.
            using var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(download, "--version") { RedirectStandardOutput = true })!;
            var output = await check.StandardOutput.ReadToEndAsync(ct);
            await check.WaitForExitAsync(ct);
            if (check.ExitCode != 0 || !output.StartsWith("tapqueue-station "))
                throw new InvalidDataException($"it didn't run (--version exited {check.ExitCode})");

            File.Move(download, target, overwrite: true);
            log($"Installed station build {output.Trim()}.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"Couldn't install station build {build.Version}: {ex.Message}. Trying again later.");
            File.Delete(download);
            return false;
        }
    }
}
