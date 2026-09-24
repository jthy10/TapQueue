using Microsoft.Extensions.Hosting;
using System.ServiceProcess;

namespace TapQueue.Client.Windows;

/// <summary>
/// Windows restarts the service (the installer sets restart-on-failure and failureflag) only if it
/// stops with a non-zero service exit code. That's ServiceBase.ExitCode, which Windows is told when
/// the service stops, not the process exit code (Environment.ExitCode): setting only the latter left
/// the service stopped after every update.
/// </summary>
public sealed class WindowsServiceRestarter(IHostLifetime hostLifetime, IHostApplicationLifetime lifetime) : IServiceRestarter
{
    public void Restart()
    {
        if (hostLifetime is ServiceBase service)
            service.ExitCode = 1;
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }
}
