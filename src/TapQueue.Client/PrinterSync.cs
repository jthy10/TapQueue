using Microsoft.Extensions.Logging;
using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>
/// Adds and removes the PC's printers: on Windows with PowerShell's printer cmdlets, on Linux with
/// CUPS. <paramref name="name"/> is the queue name users see.
/// </summary>
public interface IPrinterInstaller
{
    /// <summary>Adds the printer, or with <paramref name="reinstall"/> replaces one of that name.</summary>
    Task<(bool Success, string Output)> EnsureInstalledAsync(string name, Uri ippUrl, bool reinstall);

    Task<(bool Success, string Output)> RemoveAsync(string name);
}

/// <summary>
/// Keeps this PC's TapQueue printers matching the server's queues. Remembers what it added in
/// printers.txt in <see cref="ClientConfig.StateDirectory"/>, so renamed or removed queues are
/// cleaned up, and so the uninstaller can remove them (<c>--remove-printers</c>).
/// </summary>
public sealed class PrinterSync(IPrinterInstaller installer, ILogger logger)
{
    private static readonly string StatePath = Path.Combine(ClientConfig.StateDirectory, "printers.txt");

    private Dictionary<string, string>? _applied;

    /// <summary>Adds, repoints or removes printers. Does nothing if the queues haven't changed since last time.</summary>
    public async Task SyncAsync(IReadOnlyList<QueueDto> queues, Uri serverUrl)
    {
        var wanted = queues.ToDictionary(q => q.Name, q => new Uri(serverUrl, q.IppPath.TrimStart('/')).ToString(), StringComparer.OrdinalIgnoreCase);
        if (_applied is not null && SameAs(_applied, wanted))
            return;

        var installed = Load();
        var ok = true;
        foreach (var (name, _) in installed.Where(p => !wanted.ContainsKey(p.Key)).ToList())
        {
            var (success, output) = await installer.RemoveAsync(name);
            Log(success, $"Removing printer \"{name}\"", output);
            if (success) installed.Remove(name);
            ok &= success;
        }
        foreach (var (name, url) in wanted)
        {
            var moved = installed.TryGetValue(name, out var previous) && previous != url;
            var (success, output) = await installer.EnsureInstalledAsync(name, new Uri(url), reinstall: moved);
            Log(success, $"Adding printer \"{name}\" ({url})", output);
            if (success) installed[name] = url;
            ok &= success;
        }
        Save(installed);
        _applied = ok ? wanted : null; // retry next time if anything failed
    }

    /// <summary>Removes every printer this PC's TapQueue service added. Used when uninstalling.</summary>
    public static async Task<int> RemoveAllAsync(IPrinterInstaller installer)
    {
        var failures = 0;
        foreach (var name in Load().Keys)
        {
            var (success, _) = await installer.RemoveAsync(name);
            if (!success) failures++;
        }
        File.Delete(StatePath);
        return failures == 0 ? 0 : 1;
    }

    private void Log(bool success, string what, string output)
    {
        if (!success)
            logger.LogWarning("{What} failed: {Output}", what, output);
        else if (!output.Contains("already"))
            logger.LogInformation("{What}: {Output}", what, output);
    }

    private static bool SameAs(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var url) && url == p.Value);

    private static Dictionary<string, string> Load()
    {
        var printers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(StatePath))
            return printers;
        foreach (var line in File.ReadAllLines(StatePath))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0)
                printers[line[..tab]] = line[(tab + 1)..];
        }
        return printers;
    }

    private static void Save(Dictionary<string, string> printers)
    {
        Directory.CreateDirectory(ClientConfig.StateDirectory);
        File.WriteAllLines(StatePath, printers.Select(p => $"{p.Key}\t{p.Value}"));
    }
}
