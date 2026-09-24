using Avalonia;
using System.Diagnostics;
using TapQueue.Shared;

namespace TapQueue.Client.Linux;

/// <summary>
/// tapqueue-client is several programs, like TapQueueClient.exe on Windows:
///   (no arguments)       the tray app, started for each user when they sign in to the desktop
///   --service            the TapQueue service (systemd, root): printers and updates for the whole PC
///   --remove-printers    removes the printers the service added (run when uninstalling)
///   --discover [FILE]    finds TapQueue servers on the network, one "url|name|version" per line (run by the installer)
///   --version            prints the version
/// </summary>
internal static class Program
{
    public const string WaitForArg = "--wait-for";
    public const string UpdatedFromArg = "--updated-from";

    private static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(TapQueueVersion.Current);
            return 0;
        }
        if (args.Contains("--service"))
            return LinuxService.RunAsync(args.Where(a => a != "--service").ToArray()).GetAwaiter().GetResult();
        if (args.Contains("--remove-printers"))
            return PrinterSync.RemoveAllAsync(new CupsPrinterInstaller()).GetAwaiter().GetResult();
        if (Array.IndexOf(args, "--discover") is var discover and >= 0)
            return DiscoverAsync(discover + 1 < args.Length ? args[discover + 1] : null).GetAwaiter().GetResult();

        // Restarted after an update: let the old version exit (and release the lock) first.
        var (appArgs, waitFor, updatedFrom) = SplitUpdateArgs(args);
        if (waitFor is { } pid)
            WaitForExit(pid);

        // One tray icon per user.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
            return 0;

        ClientConfig config;
        try
        {
            (config, _) = ClientConfig.Load(appArgs);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"TapQueue: {ex.Message}");
            Notifications.ShowAsync("TapQueue can't start", ex.Message, urgent: true).GetAwaiter().GetResult();
            return 1;
        }

        TrayApp.Start = new TrayApp.Options(config, appArgs, updatedFrom);
        return AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(appArgs, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>One server per line, "url|name|version", to FILE or standard output.</summary>
    private static async Task<int> DiscoverAsync(string? outputPath)
    {
        var servers = await ServerDiscovery.FindAsync(TimeSpan.FromSeconds(3));
        var lines = servers.Select(s => $"{s.Url}|{s.Name}|{s.Version}");
        if (outputPath is null or "-")
            foreach (var line in lines)
                Console.WriteLine(line);
        else
            await File.WriteAllLinesAsync(outputPath, lines);
        return 0;
    }

    /// <summary>Separates the arguments <see cref="TrayApp"/> adds when it restarts after an update.</summary>
    private static (string[] AppArgs, int? WaitFor, string? UpdatedFrom) SplitUpdateArgs(string[] args)
    {
        var appArgs = new List<string>();
        int? waitFor = null;
        string? updatedFrom = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == WaitForArg && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
                waitFor = pid;
            else if (args[i] == UpdatedFromArg && i + 1 < args.Length)
                updatedFrom = args[i + 1];
            else
            {
                appArgs.Add(args[i]);
                continue;
            }
            i++;
        }
        return ([.. appArgs], waitFor, updatedFrom);
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}

/// <summary>A lock file in the user's runtime directory, held while their tray app runs.</summary>
internal static class SingleInstance
{
    public static FileStream? TryAcquire()
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = Path.GetTempPath();
        try
        {
            // .NET takes an advisory lock (flock) for FileShare.None, released when the process exits.
            return new FileStream(Path.Combine(dir, $"tapqueue-client-{Environment.UserName}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
