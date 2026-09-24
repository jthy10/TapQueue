using TapQueue.Shared;

namespace TapQueue.Client.Linux;

/// <summary>
/// tapqueue-client is several programs, like TapQueueClient.exe on Windows:
///   --service            the TapQueue service (systemd, root): printers and updates for the whole PC
///   --remove-printers    removes the printers the service added (run when uninstalling)
///   --discover [FILE]    finds TapQueue servers on the network, one "url|name|version" per line (run by the installer)
///   --version            prints the version
/// </summary>
internal static class Program
{
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

        Console.Error.WriteLine("Usage: tapqueue-client --service | --remove-printers | --discover [FILE] | --version");
        return 2;
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
}
