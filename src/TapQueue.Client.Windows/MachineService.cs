using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using System.Net.Http.Json;
using System.Threading.Channels;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// The "TapQueue" Windows service (<c>TapQueueClient.exe --service</c>, installed by the setup
/// program, runs as LocalSystem). Once a minute it checks in with the server (saying which PC this is and
/// which client it runs, so it shows under Workstations), gets its queues and client build, keeps the PC's printers in step, and installs a newly published client, straight away when someone chooses
/// "Check for updates" in the tray menu (<see cref="UpdateCheckPipe"/>).
/// Logs go to the Windows Application event log (source "TapQueue").
/// </summary>
public sealed class MachineService(ClientConfig config, IHostApplicationLifetime lifetime, ILogger<MachineService> logger) : BackgroundService
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (config, path) = ClientConfig.Load(args);
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "TapQueue");
        builder.Services.Configure<EventLogSettings>(o => o.SourceName = "TapQueue");
        builder.Services.AddSingleton(config);
        builder.Services.AddHostedService<MachineService>();
        var host = builder.Build();
        host.Services.GetRequiredService<ILogger<MachineService>>().LogInformation(
            "TapQueue service {Version} | config: {Path} | server: {Server}", TapQueueVersion.Current, path, config.ServerUrl);
        await host.RunAsync();
        return Environment.ExitCode;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        var printers = new PrinterSync(logger);
        var updater = new ClientUpdater(http, logger);
        updater.CleanUp();

        var checks = Channel.CreateUnbounded<UpdateCheckPipe.Request>();
        using var stopPipe = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pipe = Task.Run(() => UpdateCheckPipe.ServeAsync(checks.Writer, logger, stopPipe.Token), CancellationToken.None);

        string? lastError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Checks asked for from the tray menu since the last check-in: this check-in answers them.
            var asked = new List<UpdateCheckPipe.Request>();
            while (checks.Reader.TryRead(out var request))
                asked.Add(request);
            if (asked.Count > 0)
                updater.RetryFailed();

            UpdateCheckReply reply;
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
                    await printers.SyncAsync(setup.Queues, http.BaseAddress);

                build = setup.ClientBuild;
                var outcome = await updater.InstallIfDifferentAsync(build, stoppingToken);
                reply = new UpdateCheckReply(outcome, build?.Version, outcome == UpdateOutcome.Failed ? updater.LastError : null);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
                                       && !stoppingToken.IsCancellationRequested)
            {
                if (ex.Message != lastError) // log each new problem once, not every minute
                    logger.LogWarning("Can't reach {Server}: {Error}", config.ServerUrl, ex.Message);
                lastError = ex.Message;
                reply = new UpdateCheckReply(UpdateOutcome.Failed, null, $"Can't reach the TapQueue server: {ex.Message}");
            }

            foreach (var request in asked)
                request.Reply.TrySetResult(reply);

            if (reply.Outcome == UpdateOutcome.Installed)
            {
                logger.LogInformation("Installed TapQueue client {Version}; restarting the service", build!.Version);
                // Let the tray apps that asked hear about it before the pipe closes.
                try
                {
                    await Task.WhenAll(asked.Select(r => r.Sent.Task)).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                }
                // Stopping with a non-zero exit code makes Windows restart the service (the
                // installer sets restart-on-failure and failureflag), which runs the new exe.
                Environment.ExitCode = 1;
                lifetime.StopApplication();
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
            request.Reply.TrySetCanceled();
        await stopPipe.CancelAsync();
        await pipe;
    }

    /// <summary>
    /// Says which PC this is and what it runs, and gets the queues, the client build and any command
    /// back. Servers older than 0.3 only have the anonymous GET.
    /// </summary>
    private static async Task<ClientSetupResponse> CheckInAsync(HttpClient http, ClientUpdater updater, CancellationToken ct)
    {
        var report = new ClientSetupRequest(Environment.MachineName, TapQueueVersion.Current, await updater.OwnSha256Async(ct), updater.LastError);
        using var response = await http.PostAsJsonAsync("api/v1/client/setup", report, TapQueueJson.Options, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
            return await http.GetFromJsonAsync<ClientSetupResponse>("api/v1/client/setup", TapQueueJson.Options, ct)
                   ?? throw new InvalidDataException("Empty response from the server.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClientSetupResponse>(TapQueueJson.Options, ct)
               ?? throw new InvalidDataException("Empty response from the server.");
    }
}
