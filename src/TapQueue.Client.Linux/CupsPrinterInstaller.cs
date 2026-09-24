using System.Diagnostics;
using System.Text;

namespace TapQueue.Client.Linux;

/// <summary>
/// Adds a TapQueue queue to CUPS as a driverless IPP Everywhere printer (<c>lpadmin -m everywhere</c>),
/// so no driver has to be installed. CUPS printer names can't have spaces, so the printer is
/// named after the queue with those replaced, and described (what print dialogs show) by the
/// queue name itself. Run by the TapQueue service as root, so the printers are there for every user.
/// </summary>
public sealed class CupsPrinterInstaller : IPrinterInstaller
{
    public async Task<(bool Success, string Output)> EnsureInstalledAsync(string name, Uri ippUrl, bool reinstall)
    {
        var cupsName = CupsName(name);
        if (!reinstall && (await RunAsync("lpstat", "-v", cupsName)).Success)
            return (true, "already installed");

        // The server's http:// address; CUPS wants the same place as ipp://.
        var device = new UriBuilder(ippUrl) { Scheme = ippUrl.Scheme == "https" ? "ipps" : "ipp", Port = ippUrl.Port };
        var (success, output) = await RunAsync("lpadmin", "-p", cupsName, "-E", "-v", device.Uri.ToString(), "-m", "everywhere",
            "-D", name, "-o", "printer-is-shared=false");
        return (success, success ? $"installed as {cupsName}" + (output.Length > 0 ? $": {output}" : "") : output);
    }

    public async Task<(bool Success, string Output)> RemoveAsync(string name)
    {
        var cupsName = CupsName(name);
        if (!(await RunAsync("lpstat", "-v", cupsName)).Success)
            return (true, "not installed");
        var (success, output) = await RunAsync("lpadmin", "-x", cupsName);
        return (success, success ? "removed" : output);
    }

    /// <summary>
    /// CUPS printer names may be up to 127 printable characters other than space, tab, "/", "\", "#",
    /// "?", "'" and "\"". Each run of those becomes one "_".
    /// </summary>
    public static string CupsName(string queueName)
    {
        var name = new StringBuilder();
        foreach (var c in queueName.Trim())
        {
            var allowed = c > ' ' && c != 0x7f && c is not ('/' or '\\' or '#' or '?' or '\'' or '"');
            if (allowed)
                name.Append(c);
            else if (name.Length > 0 && name[^1] != '_')
                name.Append('_');
        }
        var result = name.ToString().TrimEnd('_');
        if (result.Length == 0)
            result = "TapQueue";
        return result.Length > 127 ? result[..127] : result;
    }

    private static async Task<(bool Success, string Output)> RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["LC_ALL"] = "C";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {program}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, $"Can't run {program} ({ex.Message}). Is CUPS installed?");
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, "Timed out changing the printer.");
            }
            var output = ((await stdout) + (await stderr)).Trim();
            return (process.ExitCode == 0, output);
        }
    }
}
