using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.StaticFiles;

namespace TapQueue.Server.Admin;

/// <summary>
/// Serves the admin console (wwwroot, embedded in the binary) at /admin. Every path without a file
/// extension gets index.html, and the console's router picks the page. The page itself is public; it asks
/// the admin to sign in (<see cref="Api.AdminAuthApi"/>) before it shows anything. When the server has
/// HTTPS, a browser on another machine that opens the console over plain HTTP is sent to HTTPS, so the
/// sign-in cookie and the password never cross the network in the clear. See docs/admin-ui.md.
/// </summary>
public static class AdminUi
{
    /// <summary>Set to the wwwroot folder to serve the console from disk, so edits show up on reload.</summary>
    public const string DirVariable = "TAPQUEUE_ADMIN_UI_DIR";

    private const string ResourcePrefix = "admin/";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapAdminUi(this IEndpointRouteBuilder app)
    {
        var diskDir = Environment.GetEnvironmentVariable(DirVariable);
        app.MapGet("/admin/{**path}", (string? path, HttpContext http, IServer server) =>
        {
            if (HttpsLocation(http.Request, http.Connection.RemoteIpAddress, ServerApp.BoundPort(server, "https")) is { } https)
                return Results.Redirect(https, permanent: false, preserveMethod: true);
            // The page's relative links (styles.css, pages/…) need the trailing slash.
            if (http.Request.Path == "/admin")
                return Results.Redirect("/admin/");

            path = string.IsNullOrEmpty(path) || !Path.HasExtension(path) ? "index.html" : path;
            if (path.Contains("..") || !ContentTypes.TryGetContentType(path, out var contentType))
                return Results.NotFound();
            var stream = diskDir is null ? Embedded(path) : FromDisk(diskDir, path);
            if (stream is null)
                return Results.NotFound();
            // The console changes with every server upgrade; make the browser check instead of keeping an old copy.
            http.Response.Headers.CacheControl = "no-cache";
            // Another site may not frame the console (and trick an admin into clicking in it), and
            // browsers mustn't guess a file's type from its content.
            http.Response.Headers.XFrameOptions = "DENY";
            http.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "same-origin";
            if (http.Request.IsHttps)
                http.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            return Results.Stream(stream, contentType.StartsWith("text/") ? contentType + "; charset=utf-8" : contentType);
        });
    }

    /// <summary>
    /// Where to send a console request instead, or null to serve it: plain HTTP from another machine
    /// goes to the same path over HTTPS when the server listens for it. This machine itself is left
    /// alone (nobody else can listen in on localhost).
    /// </summary>
    internal static string? HttpsLocation(HttpRequest request, IPAddress? remote, int? tlsPort)
    {
        if (request.IsHttps || tlsPort is not { } port || remote is null
            || IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return null;
        return new UriBuilder("https", request.Host.Host, port, request.PathBase + request.Path) { Query = request.QueryString.Value }.Uri.ToString();
    }

    private static Stream? Embedded(string path) =>
        typeof(AdminUi).Assembly.GetManifestResourceStream(ResourcePrefix + path);

    internal static Stream? FromDisk(string dir, string path)
    {
        // With the separator, so /srv/wwwroot doesn't also let through /srv/wwwroot-old.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(dir, path));
        return file.StartsWith(root, StringComparison.Ordinal) && File.Exists(file) ? File.OpenRead(file) : null;
    }
}
