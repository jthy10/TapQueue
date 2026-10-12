using TapQueue.Shared;

namespace TapQueue.Client;

/// <summary>
/// client.toml, the settings for the whole PC. Looked for at --config, then in
/// <see cref="ConfigDirectory"/>, then next to the program.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>The TapQueue server, e.g. https://tapqueue.example.local:8632.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// SHA-256 fingerprint of the server's self-signed certificate (see <see cref="ServerCertificatePin"/>).
    /// Empty: trust the certificate the TapQueue service sees the first time it connects.
    /// </summary>
    public string ServerCertFingerprint { get; set; } = "";

    /// <summary>TapQueue username. Leave empty to use the name the person signed in to the PC with.</summary>
    public string Username { get; set; } = "";

    /// <summary>Per-user token from `tapqueue-admin users add`. Not needed when the server runs in dev auth mode.</summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// The server's enrollment code, if it asks new PCs for one (`tapqueue-admin server enrollment-code new`).
    /// Only used until this PC has its key; it can be removed afterwards.
    /// </summary>
    public string EnrollmentCode { get; set; } = "";

    /// <summary>Let the TapQueue service add the server's print queues to this PC as printers.</summary>
    public bool InstallPrinters { get; set; } = true;

    /// <summary>
    /// Where client.toml lives. Only admins can write here. Windows: %ProgramData%\TapQueue.
    /// Linux: /etc/tapqueue, next to the server's and station's settings.
    /// </summary>
    public static string ConfigDirectory { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TapQueue")
        : "/etc/tapqueue";

    /// <summary>
    /// Where the TapQueue service keeps what it has done to the PC, such as the printers it added.
    /// Windows: %ProgramData%\TapQueue. Linux: /var/lib/tapqueue-client.
    /// </summary>
    public static string StateDirectory { get; } = OperatingSystem.IsWindows() ? ConfigDirectory : "/var/lib/tapqueue-client";

    /// <summary>The server's certificate as the TapQueue service first saw it, for the tray apps to check against.</summary>
    public static string SavedCertificatePath { get; } = System.IO.Path.Combine(StateDirectory, "server-certificate.pem");

    /// <summary>
    /// How to trust the server's https:// certificate. Only the TapQueue service (<paramref name="learn"/>)
    /// saves the one it sees first; the tray apps use what it saved.
    /// </summary>
    public ServerCertificatePin CertificatePin(bool learn) =>
        new(ServerUrl, ServerCertFingerprint, SavedCertificatePath, learn);

    /// <summary>An HttpClient for the server that checks its certificate with <paramref name="pin"/>.</summary>
    public HttpClient CreateHttpClient(ServerCertificatePin pin, TimeSpan timeout) =>
        new(pin.CreateHandler()) { BaseAddress = new Uri(ServerUrl.TrimEnd('/') + "/"), Timeout = timeout };

    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? Environment.UserName : Username.Trim();

    public static (ClientConfig Config, string Path) Load(string[] args)
    {
        var path = FindPath(args) ?? throw new FileNotFoundException(
            "No client.toml found. Create one at " + System.IO.Path.Combine(ConfigDirectory, "client.toml"));

        var config = TomlConfig.Load<ClientConfig>(path);
        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"server_url in {path} must be an http:// or https:// address.");
        ServerCertificatePin.Normalize(config.ServerCertFingerprint); // throws if it isn't one
        return (config, path);
    }

    private static string? FindPath(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        if (index >= 0 && index + 1 < args.Length)
            return args[index + 1];

        string[] candidates =
        [
            System.IO.Path.Combine(ConfigDirectory, "client.toml"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "client.toml"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
