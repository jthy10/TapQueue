using System.Diagnostics;
using System.Text;

namespace TapQueue.Client.Windows;

/// <summary>
/// Adds a TapQueue queue to Windows as an IPP printer using the built-in Microsoft IPP Class
/// Driver, so no driver has to be installed. The name users see is the queue name set on the server.
/// Run by the TapQueue service, so the printers are added for every user of the PC.
/// </summary>
public static class PrinterInstaller
{
    // Values are passed in environment variables, never spliced into the script text.
    private const string InstallScript = """
        $ErrorActionPreference = 'Stop'
        $name = $env:TQ_PRINTER_NAME
        $url = $env:TQ_PRINTER_URL
        $reinstall = $env:TQ_REINSTALL -eq '1'

        $existing = Get-Printer -Name $name -ErrorAction SilentlyContinue
        if ($existing -and $reinstall) { Remove-Printer -Name $name; $existing = $null }
        if ($existing) { 'already installed'; exit 0 }

        $before = @(Get-Printer | ForEach-Object Name)
        try {
            Add-Printer -IppURL $url -Name $name
        } catch {
            # Some builds don't accept -Name alongside -IppURL; add it, then rename what appeared.
            Add-Printer -IppURL $url
        }
        if (-not (Get-Printer -Name $name -ErrorAction SilentlyContinue)) {
            $new = Get-Printer | Where-Object { $before -notcontains $_.Name } | Select-Object -First 1
            if (-not $new) { throw "Windows did not create a printer for $url" }
            Rename-Printer -Name $new.Name -NewName $name
        }
        'installed'
        """;

    private const string RemoveScript = """
        $ErrorActionPreference = 'Stop'
        $name = $env:TQ_PRINTER_NAME
        if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { Remove-Printer -Name $name; 'removed' } else { 'not installed' }
        """;

    public static Task<(bool Success, string Output)> EnsureInstalledAsync(string name, Uri ippUrl, bool reinstall) =>
        RunAsync(InstallScript, new()
        {
            ["TQ_PRINTER_NAME"] = name,
            ["TQ_PRINTER_URL"] = ippUrl.ToString(),
            ["TQ_REINSTALL"] = reinstall ? "1" : "0",
        });

    public static Task<(bool Success, string Output)> RemoveAsync(string name) =>
        RunAsync(RemoveScript, new() { ["TQ_PRINTER_NAME"] = name });

    private static async Task<(bool Success, string Output)> RunAsync(string script, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in environment)
            start.Environment[key] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
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
