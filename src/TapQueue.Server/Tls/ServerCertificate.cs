using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using TapQueue.Server.Config;
using TapQueue.Shared;

namespace TapQueue.Server.Tls;

/// <summary>
/// The certificate the HTTPS/IPPS listener presents: the one in tls.cert_file and tls.key_file,
/// picked up again when those files change (so a renewed Let's Encrypt certificate needs no
/// restart), or else a self-signed one the server makes once and keeps in data_dir/tls.
/// </summary>
public sealed class ServerCertificate
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(10);

    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly Lock _lock = new();
    private Loaded _current;
    private DateTimeOffset _checkedAt;

    private sealed record Loaded(X509Certificate2 Certificate, SslStreamCertificateContext Context, DateTime CertWritten, DateTime KeyWritten);

    public ServerCertificate(TlsSection tls, string dataDir)
    {
        SelfSigned = string.IsNullOrWhiteSpace(tls.CertFile);
        if (SelfSigned)
        {
            var dir = Path.Combine(dataDir, "tls");
            _certPath = Path.Combine(dir, "server.crt");
            _keyPath = Path.Combine(dir, "server.key");
            var names = DefaultNames().Concat(tls.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!File.Exists(_certPath) || !File.Exists(_keyPath))
                CreateSelfSigned(names, _certPath, _keyPath);
            _current = Load(_certPath, _keyPath);
            Missing = names.Where(n => !Covers(_current.Certificate, n)).ToList();
        }
        else
        {
            _certPath = tls.CertFile;
            _keyPath = tls.KeyFile;
            _current = Load(_certPath, _keyPath);
        }
        _checkedAt = DateTimeOffset.UtcNow;
    }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    public bool SelfSigned { get; }

    /// <summary>Configured names (tls.names) the existing self-signed certificate doesn't cover.</summary>
    public IReadOnlyList<string> Missing { get; } = [];

    public string CertPath => _certPath;

    public X509Certificate2 Current => Refresh().Certificate;

    public string Fingerprint => ServerCertificatePin.FingerprintOf(Current);

    /// <summary>The DNS names and IP addresses the certificate is good for.</summary>
    public IReadOnlyList<string> Names => NamesOf(Current);

    /// <summary>What each TLS handshake uses.</summary>
    public SslServerAuthenticationOptions HandshakeOptions() => new() { ServerCertificateContext = Refresh().Context };

    private Loaded Refresh()
    {
        if (SelfSigned || DateTimeOffset.UtcNow - _checkedAt < RecheckEvery)
            return _current;
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _checkedAt < RecheckEvery)
                return _current;
            _checkedAt = DateTimeOffset.UtcNow;
            try
            {
                if (File.GetLastWriteTimeUtc(_certPath) == _current.CertWritten && File.GetLastWriteTimeUtc(_keyPath) == _current.KeyWritten)
                    return _current;
                _current = Load(_certPath, _keyPath);
                Logger.LogInformation("Loaded the new TLS certificate from {Path}: {Subject}, valid until {NotAfter:yyyy-MM-dd}",
                    _certPath, _current.Certificate.Subject, _current.Certificate.NotAfter);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
            {
                // E.g. caught halfway through a renewal, with the new certificate but the old key. Try again shortly.
                Logger.LogWarning("Keeping the current TLS certificate: can't load {Path}: {Error}", _certPath, ex.Message);
            }
            return _current;
        }
    }

    private static Loaded Load(string certPath, string keyPath)
    {
        try
        {
            var certWritten = File.GetLastWriteTimeUtc(certPath);
            var keyWritten = File.GetLastWriteTimeUtc(keyPath);
            using var fromPem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            // A PKCS#12 round trip gives a key that every platform's TLS stack can use.
            var certificate = X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), null);
            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(certPath);
            var intermediates = new X509Certificate2Collection(chain.Where(c => c.Thumbprint != certificate.Thumbprint).ToArray());
            var context = SslStreamCertificateContext.Create(certificate, intermediates, offline: true);
            return new Loaded(certificate, context, certWritten, keyWritten);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Can't read the TLS certificate {certPath} or key {keyPath}: {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"{certPath} and {keyPath} must be a PEM certificate and its private key: {ex.Message}");
        }
    }

    /// <summary>This machine's name, its full DNS name, localhost, and every address it has.</summary>
    private static IEnumerable<string> DefaultNames()
    {
        var host = Dns.GetHostName();
        yield return host.ToLowerInvariant();
        string? fqdn = null;
        try
        {
            fqdn = Dns.GetHostEntry(host).HostName;
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        if (!string.IsNullOrEmpty(fqdn) && !IPAddress.TryParse(fqdn, out _))
            yield return fqdn.TrimEnd('.').ToLowerInvariant();
        yield return "localhost";
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
                    continue;
                yield return address.ToString();
            }
        }
    }

    /// <summary>
    /// Writes a self-signed certificate for <paramref name="names"/>, valid for ten years. It's an
    /// end-entity certificate (not a CA), so a PC that trusts it trusts this server and nothing else.
    /// </summary>
    public static void CreateSelfSigned(IReadOnlyList<string> names, string certPath, string keyPath)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}, O=TapQueue", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
        // The key first, readable only by the server, so the pair is never seen without it.
        WritePrivate(keyPath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certPath, certificate.ExportCertificatePem() + "\n");
    }

    private static void WritePrivate(string path, string text)
    {
        using (File.Create(path)) { }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(path, text + "\n");
    }

    public static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
            return [certificate.GetNameInfo(X509NameType.DnsName, false)];
        return [.. san.EnumerateDnsNames(), .. san.EnumerateIPAddresses().Select(a => a.ToString())];
    }

    private static bool Covers(X509Certificate2 certificate, string name) =>
        certificate.MatchesHostname(name, allowWildcards: true, allowCommonName: false);
}
