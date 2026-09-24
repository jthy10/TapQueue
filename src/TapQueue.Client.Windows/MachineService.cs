using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using TapQueue.Shared;

namespace TapQueue.Client.Windows;

/// <summary>
/// The "TapQueue" Windows service (<c>TapQueueClient.exe --service</c>, installed by the setup
/// program, runs as LocalSystem): the shared <see cref="ClientService"/> with Windows printers and
/// the <c>\\.\pipe\TapQueue</c> update check. Logs go to the Windows Application event log (source "TapQueue").
/// </summary>
public static class MachineService
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (config, path) = ClientConfig.Load(args);
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "TapQueue");
        builder.Services.Configure<EventLogSettings>(o => o.SourceName = "TapQueue");
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IPrinterInstaller, PrinterInstaller>();
        builder.Services.AddSingleton<IUpdateCheckListener, UpdateCheckPipe>();
        builder.Services.AddHostedService<ClientService>();
        var host = builder.Build();
        host.Services.GetRequiredService<ILogger<ClientService>>().LogInformation(
            "TapQueue service {Version} | config: {Path} | server: {Server}", TapQueueVersion.Current, path, config.ServerUrl);
        await host.RunAsync();
        return Environment.ExitCode;
    }
}
