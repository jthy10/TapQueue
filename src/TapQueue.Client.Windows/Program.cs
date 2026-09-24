using System.Diagnostics;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>
/// TapQueueClient.exe is three programs:
///   (no arguments)       the tray app, started for each user at sign-in
///   --service            the TapQueue Windows service: printers and updates for the whole PC
///   --remove-printers    removes the printers the service added (run by the uninstaller)
///   --discover FILE      finds TapQueue servers on the network and writes them to FILE (run by the installer)
/// </summary>
internal static class Program
{
    public const string WaitForArg = "--wait-for";
    public const string UpdatedFromArg = "--updated-from";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--service"))
        {
            var serviceArgs = args.Where(a => a != "--service").ToArray();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                CrashReporter.Report(CrashProgram.Service, (Exception)e.ExceptionObject, serviceArgs);
            try
            {
                return MachineService.RunAsync(serviceArgs).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Before or while starting (e.g. an unreadable client.toml). Exiting without telling
                // Windows the service stopped counts as a failure, so Windows tries it again.
                CrashReporter.Report(CrashProgram.Service, ex, serviceArgs);
                return 1;
            }
        }
        if (args.Contains("--remove-printers"))
            return PrinterSync.RemoveAllAsync(new PrinterInstaller()).GetAwaiter().GetResult();
        if (Array.IndexOf(args, "--discover") is var discover and >= 0 && discover + 1 < args.Length)
            return DiscoverAsync(args[discover + 1]).GetAwaiter().GetResult();

        // Restarted after an update: let the old version exit (and release the mutex) first.
        var (appArgs, waitFor, updatedFrom) = SplitUpdateArgs(args);
        if (waitFor is { } pid)
            WaitForExit(pid);

        // One tray icon per user session.
        using var mutex = new Mutex(true, @"Local\TapQueueClient", out var firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashReporter.Report(CrashProgram.Tray, (Exception)e.ExceptionObject, appArgs);
        // An error in a menu or window handler: report it and keep the tray app running.
        Application.ThreadException += (_, e) => CrashReporter.Report(CrashProgram.Tray, e.Exception, appArgs);

        ClientConfig config;
        try
        {
            (config, _) = ClientConfig.Load(appArgs);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "TapQueue", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        _ = Task.Run(() => CrashReporter.SendPendingAsync(CrashProgram.Tray, config, TimeSpan.FromSeconds(30)));
        Application.Run(new TrayApp(config, appArgs, updatedFrom));
        return 0;
    }

    /// <summary>
    /// One server per line, "url|name|version", for the installer's server page. It's a GUI exe with
    /// no console, so the list goes to a file.
    /// </summary>
    private static async Task<int> DiscoverAsync(string outputPath)
    {
        var servers = await ServerDiscovery.FindAsync(TimeSpan.FromSeconds(3));
        await File.WriteAllLinesAsync(outputPath, servers.Select(s => $"{s.Url}|{s.Name}|{s.Version}"));
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
