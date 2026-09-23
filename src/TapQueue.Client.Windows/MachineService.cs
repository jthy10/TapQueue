using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// The "TapQueue" Windows service (<c>TapQueueClient.exe --service</c>, installed by the setup
/// program, runs as LocalSystem). Once a minute it checks in with the server (saying which PC this is and
/// which client it runs, so it shows under Workstations), gets its queues and client build, keeps the PC's printers in step, and installs a newly published client.
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

        string? lastError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
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

                if (await updater.InstallIfDifferentAsync(setup.ClientBuild, stoppingToken))
                {
                    logger.LogInformation("Installed TapQueue client {Version}; restarting the service", setup.ClientBuild!.Version);
                    // Stopping with a non-zero exit code makes Windows restart the service (the
                    // installer sets restart-on-failure and failureflag), which runs the new exe.
                    Environment.ExitCode = 1;
                    lifetime.StopApplication();
                    return;
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
                                       && !stoppingToken.IsCancellationRequested)
            {
                if (ex.Message != lastError) // log each new problem once, not every minute
                    logger.LogWarning("Can't reach {Server}: {Error}", config.ServerUrl, ex.Message);
                lastError = ex.Message;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(WorkstationStatus.CheckInSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
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
