namespace TapQueue.Client.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One tray icon per user session.
        using var mutex = new Mutex(true, @"Local\TapQueueClient", out var firstInstance);
        if (!firstInstance)
            return 0;

        ApplicationConfiguration.Initialize();

        ClientConfig config;
        try
        {
            (config, _) = ClientConfig.Load(args);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "TapQueue", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Application.Run(new TrayApp(config));
        return 0;
    }
}
