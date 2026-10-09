using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TapQueue.Shared;
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

    /// <summary>
    /// Makes the system trust the server's self-signed certificate, so the printers can reach it
    /// over https://, or with null stops trusting the one it trusted before. Does nothing by
    /// default: CUPS trusts a printer's certificate the first time it connects.
    /// </summary>
    /// <param name="server">The server the certificate is for; null when <paramref name="certificate"/> is.</param>
    Task<(bool Success, string Output)> TrustServerCertificateAsync(X509Certificate2? certificate, Uri? server) => Task.FromResult((true, ""));
}

/// <summary>
/// Keeps this PC's TapQueue printers matching the server's queues. Remembers what it added in
/// printers.txt in <see cref="ClientConfig.StateDirectory"/>, so renamed or removed queues are
/// cleaned up, and so the uninstaller can remove them (<c>--remove-printers</c>).
/// </summary>
/// <param name="statePath">Where to keep printers.txt instead; for tests.</param>
public sealed class PrinterSync(IPrinterInstaller installer, ILogger logger, string? statePath = null)
{
    private static readonly string DefaultStatePath = Path.Combine(ClientConfig.StateDirectory, "printers.txt");

    private readonly string _statePath = statePath ?? DefaultStatePath;

    private Dictionary<string, string>? _applied;
    private string? _trusted;

    /// <summary>
    /// Adds, repoints or removes printers. Does nothing if the queues haven't changed since last time,
    /// unless <paramref name="reinstallAll"/> ("Refresh printers" in the tray menu), which removes and
    /// adds every printer again. Returns the first failure, or null when everything worked.
    /// </summary>
    /// <param name="serverCertificate">The server's certificate when it's trusted only because it's pinned (<see cref="ServerCertificatePin.Pinned"/>).</param>
    public async Task<string?> SyncAsync(IReadOnlyList<QueueDto> queues, Uri serverUrl, bool reinstallAll = false, X509Certificate2? serverCertificate = null)
    {
        var trust = serverCertificate?.Thumbprint ?? "";
        if (trust != _trusted || reinstallAll)
        {
            var (trusted, output) = await installer.TrustServerCertificateAsync(serverCertificate, serverUrl);
            if (!trusted)
            {
                logger.LogWarning("Trusting the server's certificate failed: {Output}", output);
                return $"Couldn't make this PC trust the server's certificate: {output}";
            }
            if (output.Length > 0)
                logger.LogInformation("Server certificate: {Output}", output);
            _trusted = trust;
        }

        // The PC knows a printer by its name, so of two queues with one name only the first can be added.
        // (Servers from 0.10 on don't allow that; an older one mustn't take the service down with it.)
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var queue in queues)
        {
            if (!wanted.TryAdd(queue.Name, new Uri(serverUrl, queue.IppPath.TrimStart('/')).ToString()) && _applied is null)
                logger.LogWarning("The server has more than one queue named \"{Name}\"; this PC only gets the first as a printer", queue.Name);
        }
        if (!reinstallAll && _applied is not null && SameAs(_applied, wanted))
            return null;

        var installed = Load(_statePath);
        string? error = null;
        foreach (var (name, _) in installed.Where(p => !wanted.ContainsKey(p.Key)).ToList())
        {
            var (success, output) = await installer.RemoveAsync(name);
            Log(success, $"Removing printer \"{name}\"", output);
            if (success) installed.Remove(name);
            else error ??= $"Couldn't remove \"{name}\": {output}";
        }
        foreach (var (name, url) in wanted)
        {
            var moved = installed.TryGetValue(name, out var previous) && previous != url;
            var (success, output) = await installer.EnsureInstalledAsync(name, new Uri(url), reinstall: moved || reinstallAll);
            Log(success, $"Adding printer \"{name}\" ({url})", output);
            if (success) installed[name] = url;
            else error ??= $"Couldn't add \"{name}\": {output}";
        }
        Save(_statePath, installed);
        _applied = error is null ? wanted : null; // retry next time if anything failed
        return error;
    }

    /// <summary>Removes every printer this PC's TapQueue service added. Used when uninstalling.</summary>
    public static async Task<int> RemoveAllAsync(IPrinterInstaller installer)
    {
        var failures = 0;
        foreach (var name in Load(DefaultStatePath).Keys)
        {
            var (success, _) = await installer.RemoveAsync(name);
            if (!success) failures++;
        }
        if (!(await installer.TrustServerCertificateAsync(null, null)).Success)
            failures++;
        File.Delete(DefaultStatePath);
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

    private static Dictionary<string, string> Load(string path)
    {
        var printers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return printers;
        foreach (var line in File.ReadAllLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0)
                printers[line[..tab]] = line[(tab + 1)..];
        }
        return printers;
    }

    private static void Save(string path, Dictionary<string, string> printers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, printers.Select(p => $"{p.Key}\t{p.Value}"));
    }
}
