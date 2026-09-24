using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TapQueue.Shared;

namespace TapQueue.Client.Linux;

/// <summary>
/// The TapQueue service on Linux (<c>tapqueue-client --service</c>, the tapqueue-client systemd
/// unit, runs as root): the shared <see cref="ClientService"/> with CUPS printers and the
/// <see cref="UpdateCheckSocket"/>. Logs go to the journal (<c>journalctl -u tapqueue-client</c>).
/// </summary>
public static class LinuxService
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (config, path) = ClientConfig.Load(args);
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IPrinterInstaller, CupsPrinterInstaller>();
        builder.Services.AddSingleton<IUpdateCheckListener, UpdateCheckSocket>();
        builder.Services.AddSingleton<IServiceRestarter, ExitCodeRestarter>();
        builder.Services.AddHostedService<ClientService>();
        var host = builder.Build();
        host.Services.GetRequiredService<ILogger<ClientService>>().LogInformation(
            "TapQueue service {Version} | config: {Path} | server: {Server}", TapQueueVersion.Current, path, config.ServerUrl);
        await host.RunAsync();
        return Environment.ExitCode;
    }
}
