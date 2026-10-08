using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;

namespace TapQueue.Client;

/// <summary>
/// Windows only prints over https:// to a server whose certificate the whole PC trusts, so a
/// pinned self-signed certificate goes into the computer's trusted root certificates. That trusts
/// it for more than TapQueue, so only a certificate that can't be used against anything else gets
/// in: one that can't sign other certificates and only names the server's own network.
/// </summary>
public static class SystemTrust
{
    /// <summary>Why <paramref name="certificate"/> shouldn't be trusted system-wide for <paramref name="server"/>, or null if it's fine.</summary>
    /// <param name="pcDomain">This PC's DNS domain; names in it are fine too. Null to look it up.</param>
    public static string? Refusal(X509Certificate2 certificate, Uri server, string? pcDomain = null)
    {
        pcDomain ??= IPGlobalProperties.GetIPGlobalProperties().DomainName;
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basic is { CertificateAuthority: true })
            return "it's a certificate authority, which could vouch for any website";
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            return "it can sign other certificates";
        if (!certificate.MatchesHostname(server.IdnHost, allowWildcards: false, allowCommonName: true))
            return $"it isn't for {server.Host}";

        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var names = san?.EnumerateDnsNames().ToList() ?? [];
        // The server's domain (tapqueue.corp.example → corp.example; none for an address, a bare name
        // or something like tapqueue.example, whose "domain" would be a whole top-level domain) and the PC's own.
        var host = server.IdnHost.TrimEnd('.').ToLowerInvariant();
        var serverDomain = IPAddress.TryParse(host, out _) || !host.Contains('.') ? null : host[(host.IndexOf('.') + 1)..];
        string[] domains = [.. new[] { serverDomain is not null && serverDomain.Contains('.') ? serverDomain : null, pcDomain.Trim('.').ToLowerInvariant() }
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => "." + d)];
        foreach (var name in names.Select(n => n.TrimEnd('.').ToLowerInvariant()))
        {
            if (name.Contains('*'))
                return $"it has a wildcard name ({name})";
            var local = name == host || name == "localhost" || !name.Contains('.')
                || domains.Any(d => name.EndsWith(d, StringComparison.Ordinal));
            if (!local)
                return $"it also names {name}, outside the server's and this PC's domain";
        }
        return null;
    }
}
