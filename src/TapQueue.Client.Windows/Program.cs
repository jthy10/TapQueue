using System.Diagnostics;

namespace TapQueue.Client.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Started by an update: let the old version exit (and release the mutex) first.
        var (appArgs, waitFor, updatedFrom) = SplitUpdateArgs(args);
        if (waitFor is { } pid)
            WaitForExit(pid);

        // One tray icon per user session.
        using var mutex = new Mutex(true, @"Local\TapQueueClient", out var firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();

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

        Application.Run(new TrayApp(config, appArgs, updatedFrom));
        return 0;
    }

    /// <summary>Separates the arguments <see cref="ClientUpdater"/> adds from the user's own.</summary>
    private static (string[] AppArgs, int? WaitFor, string? UpdatedFrom) SplitUpdateArgs(string[] args)
    {
        var appArgs = new List<string>();
        int? waitFor = null;
        string? updatedFrom = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == ClientUpdater.WaitForArg && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
                waitFor = pid;
            else if (args[i] == ClientUpdater.UpdatedFromArg && i + 1 < args.Length)
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
