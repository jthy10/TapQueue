using System.Net;

namespace TapQueue.Server;

/// <summary>
/// Behind a reverse proxy every connection comes from the proxy, so sign-in throttling, address
/// matching and the activity log would see one address for everybody. For connections from
/// server.trusted_proxies, the caller's address is taken from X-Forwarded-For instead, and whether
/// they used HTTPS from X-Forwarded-Proto. From anyone else the headers are ignored: anybody can send them.
/// </summary>
public static class TrustedProxies
{
    public static void UseTrustedProxies(this WebApplication app, IReadOnlyList<IPNetwork> proxies)
    {
        app.Use((http, next) =>
        {
            if (Resolve(http.Connection.RemoteIpAddress, http.Request.Headers["X-Forwarded-For"].ToString(),
                    http.Request.Headers["X-Forwarded-Proto"].ToString(), proxies) is { } forwarded)
            {
                http.Connection.RemoteIpAddress = forwarded.Client;
                if (forwarded.Scheme is not null)
                    http.Request.Scheme = forwarded.Scheme;
            }
            return next(http);
        });
    }

    /// <summary>
    /// Who a connection from <paramref name="remote"/> is really from, or null to leave it as it is.
    /// X-Forwarded-For lists the client first and each proxy after it; every proxy adds the address it
    /// was called from. Read from the end, the first address that isn't a trusted proxy is the client:
    /// anything before it was written by the client itself and proves nothing.
    /// </summary>
    internal static (IPAddress Client, string? Scheme)? Resolve(IPAddress? remote, string forwardedFor, string forwardedProto, IReadOnlyList<IPNetwork> proxies)
    {
        if (remote is null || !IsTrusted(remote, proxies))
            return null;
        var hops = Split(forwardedFor);
        var schemes = Split(forwardedProto);
        IPAddress? client = null;
        var at = hops.Length;
        while (at > 0 && ParseAddress(hops[at - 1]) is { } hop)
        {
            (client, at) = (hop, at - 1);
            if (!IsTrusted(hop, proxies))
                break;
        }
        if (client is null)
            return null;
        // One scheme a hop, in the same order; a single proxy sends just the one.
        var scheme = schemes.Length == hops.Length ? schemes[at] : schemes.Length == 1 ? schemes[0] : null;
        return (client, scheme is "http" or "https" ? scheme : null);
    }

    private static string[] Split(string header) =>
        header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsTrusted(IPAddress address, IReadOnlyList<IPNetwork> proxies)
    {
        var plain = Unmap(address);
        return proxies.Any(p => p.Contains(plain) || p.Contains(address));
    }

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>"192.0.2.7", "192.0.2.7:51234", "2001:db8::1" or "[2001:db8::1]:51234".</summary>
    private static IPAddress? ParseAddress(string text)
    {
        if (IPAddress.TryParse(text, out var address))
            return Unmap(address);
        return IPEndPoint.TryParse(text, out var endpoint) ? Unmap(endpoint.Address) : null;
    }
}
