using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Threading.Channels;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>
/// The TapQueue service, one per PC, run by the system (a Windows service as LocalSystem, a
/// systemd service as root). Once a minute it checks in with the server (saying which PC this is,
/// its platform and which client it runs, so it shows under Workstations), gets its queues and
/// client build, keeps the PC's printers in step, and installs a newly published client, straight
/// away when someone chooses "Check for updates" in the tray menu (<see cref="UpdateCheck"/>).
/// "Refresh printers" in the tray menu makes it check in straight away and reinstall every printer.
/// The server gives the PC a key on its first check-in (<see cref="WorkstationKey"/>); the printers
/// then carry a print key made from it, and the service vouches for each tray app on the PC that
/// signs in (<see cref="UpdateCheck.VerifySessionCommand"/>), so jobs are matched by PC and PC user.
/// The platform's program sets up the host (logging, service manager) around it.
/// </summary>
public sealed class ClientService(
    ClientConfig config,
    IPrinterInstaller installer,
    IUpdateCheckListener listener,
    IServiceRestarter restarter,
    ILogger<ClientService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reports from earlier crashes that couldn't be sent then.
        _ = Task.Run(() => CrashReporter.SendPendingAsync(CrashProgram.Service, config, TimeSpan.FromSeconds(30)), CancellationToken.None);
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "The TapQueue service crashed; restarting");
            CrashReporter.Report(CrashProgram.Service, ex, config);
            restarter.Restart();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // The service is the one that trusts a self-signed server certificate the first time it sees it.
        var pin = config.CertificatePin(learn: true);
        pin.Learned = certificate => logger.LogWarning(
            "Trusting {Server}'s certificate from now on: {Subject}, SHA-256 {Fingerprint}. Saved in {Path}",
            config.ServerUrl, certificate.Subject, ServerCertificatePin.FingerprintOf(certificate), pin.SavedPath);
        using var http = config.CreateHttpClient(pin, TimeSpan.FromMinutes(10));
        var printers = new PrinterSync(installer, logger);
        var updater = new ClientUpdater(http, logger);

        var checks = Channel.CreateUnbounded<UpdateCheck.Request>();
        using var stopListening = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var listening = Task.Run(() => listener.ServeAsync(checks.Writer, (token, pcUser, ct) => VerifySessionAsync(http, token, pcUser, ct),
            logger, stopListening.Token), CancellationToken.None);

        string? lastError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            updater.CleanUp(); // tray apps still on an older build have restarted by now

            // Checks asked for from the tray menu since the last check-in: this check-in answers them.
            var asked = new List<UpdateCheck.Request>();
            while (checks.Reader.TryRead(out var request))
                asked.Add(request);
            var refreshing = asked.Any(r => r.RefreshPrinters);
            if (asked.Any(r => !r.RefreshPrinters))
                updater.RetryFailed();

            UpdateCheckReply reply;
            PrinterRefreshReply printersReply;
            ClientBuildDto? build = null;
            try
            {
                var setup = await CheckInAsync(http, updater, stoppingToken);
                if (lastError is not null)
                    logger.LogInformation("Reached {Server} again", config.ServerUrl);
                lastError = null;

                if (setup.Command == WorkstationCommand.Update)
                {
                    logger.LogInformation("An admin asked this PC to update now");
                    updater.RetryFailed();
                }

                if (config.InstallPrinters)
                {
                    if (refreshing)
                        logger.LogInformation("Reinstalling this PC's printers (asked from this PC)");
                    var printerError = await printers.SyncAsync(setup.Queues, http.BaseAddress!, reinstallAll: refreshing, pin.Pinned);
                    printersReply = new PrinterRefreshReply(printerError is null, setup.Queues.Count, printerError);
                }
                else
                {
                    printersReply = new PrinterRefreshReply(false, 0, "TapQueue is set not to add printers on this PC (install_printers = false in client.toml).");
                }

                build = setup.ClientBuild;
                var outcome = await updater.InstallIfDifferentAsync(build, stoppingToken);
                reply = new UpdateCheckReply(outcome, build?.Version, outcome == UpdateOutcome.Failed ? updater.LastError : null);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or IOException)
                                       && !stoppingToken.IsCancellationRequested)
            {
                if (ex.Message != lastError) // log each new problem once, not every minute
                    logger.LogWarning("Can't reach {Server}: {Error}", config.ServerUrl, ex.Message);
                lastError = ex.Message;
                reply = new UpdateCheckReply(UpdateOutcome.Failed, null, $"Can't reach the TapQueue server: {ex.Message}");
                printersReply = new PrinterRefreshReply(false, 0, reply.Error);
            }

            foreach (var request in asked)
            {
                if (request.RefreshPrinters) request.PrintersReply.TrySetResult(printersReply);
                else request.Reply.TrySetResult(reply);
            }

            if (reply.Outcome == UpdateOutcome.Installed)
            {
                logger.LogInformation("Installed TapQueue client {Version}; restarting the service", build!.Version);
                // Let the tray apps that asked hear about it before the connection closes.
                try
                {
                    await Task.WhenAll(asked.Select(r => r.Sent.Task)).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                }
                // The service manager starts it again, running the new program.
                restarter.Restart();
                break;
            }

            // Sleep until the next check-in, or until someone asks for a check from the tray menu.
            try
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(TimeSpan.FromSeconds(WorkstationStatus.CheckInSeconds), delay.Token);
                await Task.WhenAny(tick, checks.Reader.WaitToReadAsync(delay.Token).AsTask());
                delay.Cancel();
                stoppingToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        checks.Writer.TryComplete();
        while (checks.Reader.TryRead(out var request))
        {
            request.Reply.TrySetCanceled();
            request.PrintersReply.TrySetCanceled();
        }
        await stopListening.CancelAsync();
        await listening;
    }

    /// <summary>
    /// Says which PC this is and what it runs, and gets the queues, the client build and any command
    /// back. Servers older than 0.3 only have the anonymous GET. Sends this PC's key ("" asks for one;
    /// servers older than 0.8 ignore it) and keeps the one it's given.
    /// </summary>
    private async Task<ClientSetupResponse> CheckInAsync(HttpClient http, ClientUpdater updater, CancellationToken ct)
    {
        var key = WorkstationKey.Load(config.ServerUrl) ?? "";
        var report = new ClientSetupRequest(Environment.MachineName, TapQueueVersion.Current, await updater.OwnSha256Async(ct), updater.LastError,
            ClientPlatform.Current, key);
        using var response = await http.PostAsJsonAsync("api/v1/client/setup", report, TapQueueJson.Options, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
            return await http.GetFromJsonAsync<ClientSetupResponse>("api/v1/client/setup", TapQueueJson.Options, ct)
                   ?? throw new InvalidDataException("Empty response from the server.");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new HttpRequestException(await ErrorAsync(response, ct), null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        var setup = await response.Content.ReadFromJsonAsync<ClientSetupResponse>(TapQueueJson.Options, ct)
                    ?? throw new InvalidDataException("Empty response from the server.");
        if (setup.WorkstationKey is { Length: > 0 } given)
        {
            new WorkstationKey(config.ServerUrl, given).Save();
            logger.LogInformation("The server gave this PC its key; re-adding printers with it, so jobs from here are matched by PC user");
        }
        return setup;
    }

    /// <summary>A tray app on this PC signed in as <paramref name="pcUser"/>: vouches for its session with the server (see <see cref="UpdateCheck"/>).</summary>
    private async Task<SessionVerifyReply> VerifySessionAsync(HttpClient http, string sessionToken, string pcUser, CancellationToken ct)
    {
        if (WorkstationKey.Load(config.ServerUrl) is not { } key)
            return new SessionVerifyReply(false, "This PC hasn't got its TapQueue key from the server yet.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.PostAsJsonAsync("api/v1/client/workstation-session",
                new WorkstationSessionRequest(Environment.MachineName, key, sessionToken, pcUser), TapQueueJson.Options, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Vouched for {PcUser}'s TapQueue sign-in on this PC", pcUser);
                return new SessionVerifyReply(true, null);
            }
            var error = await ErrorAsync(response, timeout.Token);
            logger.LogWarning("Couldn't vouch for {PcUser}'s TapQueue sign-in: {Error}", pcUser, error);
            return new SessionVerifyReply(false, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new SessionVerifyReply(false, $"Can't reach the TapQueue server: {ex.Message}");
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if ((await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options, ct))?.Error is { } error)
                return error;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
        }
        return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
