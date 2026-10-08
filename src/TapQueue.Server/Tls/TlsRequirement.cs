using System.Net;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tls;

/// <summary>
/// With tls.require, plain HTTP from other machines is refused. What a PC needs to find the server
/// still answers, and so does this machine itself (tapqueue-admin on the server talks to
/// localhost, where nobody else can listen in).
/// </summary>
public static class TlsRequirement
{
    public static bool Allows(bool isHttps, IPAddress? remote, PathString path) =>
        isHttps
        || remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
        || path == "/" || path == "/healthz" || path.StartsWithSegments("/icons");

    public static void UseTlsRequirement(this WebApplication app, Func<int> tlsPort)
    {
        app.Use(async (http, next) =>
        {
            if (Allows(http.Request.IsHttps, http.Connection.RemoteIpAddress, http.Request.Path))
            {
                await next(http);
                return;
            }
            var https = new UriBuilder("https", http.Request.Host.Host, tlsPort(), http.Request.Path).Uri;
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new ErrorResponse($"This server only accepts HTTPS. Use {https}"));
        });
    }
}
