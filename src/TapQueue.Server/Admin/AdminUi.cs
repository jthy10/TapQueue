using Microsoft.AspNetCore.StaticFiles;

namespace TapQueue.Server.Admin;

/// <summary>
/// Serves the admin console (wwwroot, embedded in the binary) at /admin. Every path without a file
/// extension gets index.html, and the console's router picks the page. The page itself is public; it asks
/// the admin to sign in (<see cref="Api.AdminAuthApi"/>) before it shows anything. See docs/admin-ui.md.
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
        app.MapGet("/admin/{**path}", (string? path, HttpContext http) =>
        {
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
            return Results.Stream(stream, contentType.StartsWith("text/") ? contentType + "; charset=utf-8" : contentType);
        });
    }

    private static Stream? Embedded(string path) =>
        typeof(AdminUi).Assembly.GetManifestResourceStream(ResourcePrefix + path);

    private static Stream? FromDisk(string dir, string path)
    {
        var file = Path.GetFullPath(Path.Combine(dir, path));
        return file.StartsWith(Path.GetFullPath(dir)) && File.Exists(file) ? File.OpenRead(file) : null;
    }
}
