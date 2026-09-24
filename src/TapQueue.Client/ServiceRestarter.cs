using Microsoft.Extensions.Hosting;

namespace TapQueue.Client;

/// <summary>
/// Stops the TapQueue service in a way its service manager treats as a failure, so it starts it
/// again, running the newly installed program.
/// </summary>
public interface IServiceRestarter
{
    void Restart();
}

/// <summary>systemd: the unit has Restart=on-failure, and a non-zero exit status is a failure.</summary>
public sealed class ExitCodeRestarter(IHostApplicationLifetime lifetime) : IServiceRestarter
{
    public void Restart()
    {
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }
}
