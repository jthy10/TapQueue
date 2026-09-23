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
/// program, runs as LocalSystem). Once a minute it asks the server for its queues and client
/// build, keeps the PC's printers in step, and installs a newly published client.
/// Logs go to the Windows Application event log (source "TapQueue").
/// </summary>
public sealed class MachineService(ClientConfig config, ILogger<MachineService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

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
        return 0;
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
                var setup = await http.GetFromJsonAsync<ClientSetupResponse>("api/v1/client/setup", TapQueueJson.Options, stoppingToken)
                            ?? throw new InvalidDataException("Empty response from the server.");
                if (lastError is not null)
                    logger.LogInformation("Reached {Server} again", config.ServerUrl);
                lastError = null;

                if (config.InstallPrinters)
                    await printers.SyncAsync(setup.Queues, http.BaseAddress);

                if (await updater.InstallIfDifferentAsync(setup.ClientBuild, stoppingToken))
                {
                    logger.LogInformation("Installed TapQueue client {Version}; restarting the service", setup.ClientBuild!.Version);
                    // Exiting with an error makes Windows restart the service (the installer sets
                    // restart-on-failure), which then runs the new exe.
                    Environment.Exit(1);
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
                                       && !stoppingToken.IsCancellationRequested)
            {
                if (ex.Message != lastError) // log each new problem once, not every minute
                    logger.LogWarning("Can't reach {Server}: {Error}", config.ServerUrl, ex.Message);
                lastError = ex.Message;
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
