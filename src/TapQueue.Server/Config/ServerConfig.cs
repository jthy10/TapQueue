using System.Text.RegularExpressions;

namespace TapQueue.Server.Config;

public sealed class ServerConfig
{
    public ServerSection Server { get; set; } = new();
    public AuthSection Auth { get; set; } = new();
    public AdminSection Admin { get; set; } = new();
    public JobsSection Jobs { get; set; } = new();
    public TlsSection Tls { get; set; } = new();
    public BackupSection Backup { get; set; } = new();

    /// <summary>
    /// Queues and printers used to live in server.toml (before v0.2). The TOML loader ignores sections it
    /// doesn't know, so an old config would otherwise start with no printers and no hint why.
    /// </summary>
    public static void RejectRemovedSections(string tomlText)
    {
        var removed = Regex.Matches(tomlText, @"^\s*\[\[?\s*(queues|printers)\s*\]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        if (removed.Count > 0)
            throw new InvalidDataException(
                $"[[{string.Join("]] and [[", removed)}]] are no longer read from server.toml; they're stored in the database. " +
                "Add them with `tapqueue-admin queues add` and `tapqueue-admin printers add`, then delete those sections.");
    }

    public void Validate()
    {
        if (!System.Net.IPEndPoint.TryParse(Server.Listen, out var listen) || listen.Port == 0)
            throw new InvalidDataException($"server.listen \"{Server.Listen}\" must look like 0.0.0.0:8631.");
        if (Server.DiscoveryPort is < 0 or > 65535)
            throw new InvalidDataException($"server.discovery_port must be a port number, or 0 to turn discovery off.");
        Server.ParseTrustedProxies(); // throws if one isn't an address or a network
        if (Backup.Passphrase.Length is > 0 and < BackupSection.MinPassphraseLength)
            throw new InvalidDataException($"backup.passphrase must be at least {BackupSection.MinPassphraseLength} characters, or \"\" for backups that aren't encrypted.");
        if (Backup.CopyTo.Length > 0 && !Path.IsPathFullyQualified(Backup.CopyTo))
            throw new InvalidDataException($"backup.copy_to \"{Backup.CopyTo}\" must be a full path, like /mnt/nas/tapqueue.");
        if (Tls.Enabled && (!System.Net.IPEndPoint.TryParse(Tls.Listen, out var tls) || tls.Port == 0 || tls.Port == listen.Port))
            throw new InvalidDataException($"tls.listen \"{Tls.Listen}\" must look like 0.0.0.0:8632, on a different port from server.listen, or \"\" for no HTTPS.");
        if (string.IsNullOrWhiteSpace(Tls.CertFile) != string.IsNullOrWhiteSpace(Tls.KeyFile))
            throw new InvalidDataException("Set both tls.cert_file and tls.key_file, or neither for a self-signed certificate.");
        if (Tls.Require && !Tls.Enabled)
            throw new InvalidDataException("tls.require needs tls.listen: there'd be no way in but plain HTTP.");
        if (Auth.Mode is not ("dev" or "token"))
            throw new InvalidDataException($"auth.mode must be \"dev\" or \"token\", not \"{Auth.Mode}\".");
        if (string.IsNullOrWhiteSpace(Admin.Token) || Admin.Token == "change-me")
            throw new InvalidDataException("Set admin.token to a long random value (e.g. `openssl rand -hex 32`).");
    }
}

public sealed class ServerSection
{
    /// <summary>Address and port to listen on for both IPP and the REST API.</summary>
    public string Listen { get; set; } = "0.0.0.0:8631";

    /// <summary>Where the database and spooled jobs live.</summary>
    public string DataDir { get; set; } = "/var/lib/tapqueue";

    /// <summary>UDP port to answer "is there a TapQueue server here?" broadcasts on (the Windows installer asks). 0 = don't answer.</summary>
    public int DiscoveryPort { get; set; } = TapQueue.Shared.ServerDiscovery.DefaultPort;

    /// <summary>
    /// Reverse proxies in front of this server, as addresses ("10.0.0.5") or networks ("10.0.0.0/24").
    /// Only from these is X-Forwarded-For (and X-Forwarded-Proto) believed, so the server sees each
    /// PC's own address instead of the proxy's. Empty: every caller's address is the connection's.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    public List<System.Net.IPNetwork> ParseTrustedProxies() => TrustedProxies.Select(text =>
        System.Net.IPNetwork.TryParse(text, out var network) ? network
        : System.Net.IPAddress.TryParse(text, out var address) ? new System.Net.IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
        : throw new InvalidDataException($"server.trusted_proxies: \"{text}\" isn't an address like 10.0.0.5 or a network like 10.0.0.0/24.")).ToList();
}

public sealed class AuthSection
{
    /// <summary>
    /// "dev": clients identify with a username only and unknown users are created automatically.
    /// "token": clients must present the per-user token issued by `tapqueue-admin users add`.
    /// </summary>
    public string Mode { get; set; } = "token";

    /// <summary>Client sessions with no heartbeat for this long are dropped.</summary>
    public int SessionTimeoutMinutes { get; set; } = 10;
}

public sealed class AdminSection
{
    public string Token { get; set; } = "";
}

public sealed class JobsSection
{
    /// <summary>Held jobs that are not released within this many hours are deleted.</summary>
    public int HoldHours { get; set; } = 24;
}

public sealed class BackupSection
{
    public const int MinPassphraseLength = 12;

    /// <summary>
    /// A second folder every backup is copied to as it's made: another disk or a mounted network share,
    /// so losing this machine's disk doesn't lose the backups with it. Empty = only data_dir/backups.
    /// </summary>
    public string CopyTo { get; set; } = "";

    /// <summary>
    /// Encrypts backups (AES-256-GCM) with a key made from this. Restoring needs the same passphrase,
    /// so keep a copy somewhere that isn't this server. Empty = backups aren't encrypted.
    /// </summary>
    public string Passphrase { get; set; } = "";
}

public sealed class TlsSection
{
    /// <summary>Address and port for HTTPS and IPPS (IPP over TLS). Empty = no TLS.</summary>
    public string Listen { get; set; } = "0.0.0.0:8632";

    /// <summary>PEM certificate (with any intermediates after it). Empty = a self-signed one in data_dir/tls.</summary>
    public string CertFile { get; set; } = "";

    /// <summary>PEM private key for <see cref="CertFile"/>.</summary>
    public string KeyFile { get; set; } = "";

    /// <summary>More host names or addresses for the self-signed certificate, besides this machine's own.</summary>
    public List<string> Names { get; set; } = [];

    /// <summary>
    /// Refuse plain HTTP from other machines, except what finding the server needs (<c>/</c> and
    /// <c>/healthz</c>). For once every client and station uses https://.
    /// </summary>
    public bool Require { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Listen);
}
