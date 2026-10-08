using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TapQueue.Shared;

/// <summary>
/// Decides whether to trust a TapQueue server's https:// certificate, for clients, stations and
/// tapqueue-admin. A certificate this machine already trusts (from a public or company CA) is
/// accepted as usual. Any other, like the self-signed one a server makes for itself, is accepted
/// only if it's the pinned one: the SHA-256 fingerprint set in the config file, or else the
/// certificate saved the first time this machine connected (trust on first use).
/// </summary>
/// <param name="serverUrl">The configured server; a certificate saved for another server isn't used.</param>
/// <param name="fingerprint">SHA-256 fingerprint from the config file, with or without colons. Empty for none.</param>
/// <param name="savedPath">Where the certificate seen on first use is kept. Null to never save or read one.</param>
/// <param name="learn">Save the certificate on first use. Only the program that owns <paramref name="savedPath"/> does.</param>
public sealed class ServerCertificatePin(string serverUrl, string? fingerprint, string? savedPath, bool learn)
{
    private readonly string _server = new Uri(serverUrl).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    private readonly Lock _lock = new();

    public string? Fingerprint { get; } = Normalize(fingerprint);

    public string? SavedPath { get; } = savedPath;

    /// <summary>
    /// The server's certificate, when the last connection trusted it because it's pinned rather than
    /// because this machine trusts its issuer. Windows has to be told to trust it before it prints there.
    /// </summary>
    public X509Certificate2? Pinned { get; private set; }

    /// <summary>Called with the certificate when it's saved on first use, so it can be logged.</summary>
    public Action<X509Certificate2>? Learned { get; set; }

    /// <summary>Why the last certificate was refused, in words for the person reading the error.</summary>
    public string? Refusal { get; private set; }

    /// <summary>
    /// A handler for <see cref="HttpClient"/> that checks the server's certificate this way, and
    /// says why when it refuses one rather than "The SSL connection could not be established".
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            certificate is not null && Check(new X509Certificate2(certificate), errors);
        return new ExplainRefusal(this, handler);
    }

    private sealed class ExplainRefusal(ServerCertificatePin pin, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError && pin.Refusal is { } refusal)
            {
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, refusal, ex);
            }
        }
    }

    public bool Check(X509Certificate2 certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            Pinned = null;
            return true;
        }
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            Refusal = "The server sent no certificate.";
            return false;
        }

        var seen = FingerprintOf(certificate);
        lock (_lock)
        {
            var saved = LoadSaved();
            bool trusted;
            if (Fingerprint is not null)
                trusted = Normalize(seen) == Fingerprint;
            else if (saved is not null)
                trusted = FingerprintOf(saved) == seen;
            else
                trusted = learn && SavedPath is not null;

            if (!trusted)
            {
                Refusal = Fingerprint is not null || saved is not null
                    ? $"The server's certificate (SHA-256 {seen}) isn't the one this computer trusts ({(Fingerprint is not null ? "server_cert_fingerprint" : SavedPath)}). " +
                      "If the server has a new certificate, delete that file or set server_cert_fingerprint to the new fingerprint."
                    : SavedPath is not null
                    ? $"The server's certificate isn't trusted yet (SHA-256 {seen}). It is once the TapQueue service has connected to it."
                    : $"The server's certificate isn't trusted (SHA-256 {seen}). If that's your server's (`tapqueue-admin server` on the server shows it), " +
                      "set server_cert_fingerprint to it.";
                return false;
            }
            Refusal = null;
            if (learn && SavedPath is not null && (saved is null || FingerprintOf(saved) != seen))
            {
                Save(certificate);
                Learned?.Invoke(certificate);
            }
            Pinned = certificate;
            return true;
        }
    }

    /// <summary>"AB:CD:…", the way openssl and browsers show SHA-256 fingerprints.</summary>
    public static string FingerprintOf(X509Certificate2 certificate) =>
        string.Join(':', SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2")));

    /// <summary>64 upper-case hex digits, or null for none. Colons, spaces and case don't matter.</summary>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;
        var hex = new string(fingerprint.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "server_cert_fingerprint must be a SHA-256 fingerprint: 64 hex digits, colons allowed (see `tapqueue-admin server`).");
        return hex;
    }

    /// <summary>The saved certificate, if it was saved for this server.</summary>
    private X509Certificate2? LoadSaved()
    {
        if (SavedPath is null || !File.Exists(SavedPath))
            return null;
        try
        {
            var text = File.ReadAllText(SavedPath);
            // The first line names the server it came from.
            var server = text.Split('\n', 2)[0].Trim();
            return server == _server ? X509Certificate2.CreateFromPem(text) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private void Save(X509Certificate2 certificate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
        var temp = SavedPath + ".tmp";
        File.WriteAllText(temp, $"{_server}\n{certificate.ExportCertificatePem()}\n");
        File.Move(temp, SavedPath!, overwrite: true);
    }
}
