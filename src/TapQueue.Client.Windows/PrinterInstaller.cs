using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TapQueue.Client.Windows;

/// <summary>
/// Adds a TapQueue queue to Windows as an IPP printer using the built-in Microsoft IPP Class
/// Driver, so no driver has to be installed. The name users see is the queue name set on the server.
/// Run by the TapQueue service, so the printers are added for every user of the PC.
/// </summary>
public sealed class PrinterInstaller : IPrinterInstaller
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

    public Task<(bool Success, string Output)> EnsureInstalledAsync(string name, Uri ippUrl, bool reinstall) =>
        RunAsync(InstallScript, new()
        {
            ["TQ_PRINTER_NAME"] = name,
            ["TQ_PRINTER_URL"] = ippUrl.ToString(),
            ["TQ_REINSTALL"] = reinstall ? "1" : "0",
        });

    public Task<(bool Success, string Output)> RemoveAsync(string name) =>
        RunAsync(RemoveScript, new() { ["TQ_PRINTER_NAME"] = name });

    /// <summary>The thumbprint of the certificate this service added to the trusted roots, so it's the only one it removes.</summary>
    private static readonly string TrustedPath = Path.Combine(ClientConfig.StateDirectory, "trusted-certificate.txt");

    /// <summary>
    /// The IPP class driver reaches the server with the PC's own certificate checks, so a self-signed
    /// server certificate goes into the computer's trusted root certificates (if <see cref="SystemTrust"/>
    /// allows it). One it added before for another certificate is taken out.
    /// </summary>
    public Task<(bool Success, string Output)> TrustServerCertificateAsync(X509Certificate2? certificate, Uri? server)
    {
        if (certificate is not null && server is not null && SystemTrust.Refusal(certificate, server) is { } refusal)
            return Task.FromResult((false, $"Windows won't be told to trust it, because {refusal}. Give the server a certificate from your own CA instead."));
        try
        {
            var previous = File.Exists(TrustedPath) ? File.ReadAllText(TrustedPath).Trim() : "";
            if (previous == (certificate?.Thumbprint ?? ""))
                return Task.FromResult((true, ""));

            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadWrite);
            var done = new List<string>();
            if (previous.Length > 0)
            {
                foreach (var old in root.Certificates.Find(X509FindType.FindByThumbprint, previous, validOnly: false))
                    root.Remove(old);
                File.Delete(TrustedPath);
                done.Add("removed the certificate it trusted before from the trusted roots");
            }
            if (certificate is not null)
            {
                if (root.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
                {
                    done.Add("already in the trusted roots");
                }
                else
                {
                    // The public certificate only; there's no key to add.
                    root.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
                    Directory.CreateDirectory(ClientConfig.StateDirectory);
                    File.WriteAllText(TrustedPath, certificate.Thumbprint);
                    done.Add($"added {certificate.Subject} to the trusted roots so Windows can print over https");
                }
            }
            return Task.FromResult((true, string.Join("; ", done)));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult((false, ex.Message));
        }
    }

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
