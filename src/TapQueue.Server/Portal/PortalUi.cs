using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.StaticFiles;
using TapQueue.Server.Admin;

namespace TapQueue.Server.Portal;

/// <summary>
/// Serves the web page people see their own jobs on (wwwroot, embedded in the binary) at /my. Like
/// the admin console it's plain HTML, CSS and one script with no build step, it's public until it
/// asks for a sign-in (<see cref="Api.PortalApi"/>), and a browser on another machine that opens it
/// over plain HTTP is sent to HTTPS when the server has it, so passwords and the sign-in cookie don't
/// cross the network in the clear.
/// </summary>
public static class PortalUi
{
    private const string ResourcePrefix = "portal/";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapPortalUi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/my/{**path}", (string? path, HttpContext http, IServer server) =>
        {
            if (AdminUi.HttpsLocation(http.Request, http.Connection.RemoteIpAddress, ServerApp.BoundPort(server, "https")) is { } https)
                return Results.Redirect(https, permanent: false, preserveMethod: true);
            // The page's relative links (portal.css, portal.js) need the trailing slash.
            if (http.Request.Path == "/my")
                return Results.Redirect("/my/");

            path = string.IsNullOrEmpty(path) || !Path.HasExtension(path) ? "index.html" : path;
            if (path.Contains("..") || !ContentTypes.TryGetContentType(path, out var contentType)
                || typeof(PortalUi).Assembly.GetManifestResourceStream(ResourcePrefix + path) is not { } stream)
                return Results.NotFound();
            // The page changes with every server upgrade; make the browser check instead of keeping an old copy.
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.XFrameOptions = "DENY";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "same-origin";
            if (http.Request.IsHttps)
                http.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            return Results.Stream(stream, contentType.StartsWith("text/") ? contentType + "; charset=utf-8" : contentType);
        });
    }
}
