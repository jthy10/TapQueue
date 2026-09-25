using TapQueue.Server.Api;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Admins;

/// <summary>Who is calling the admin API.</summary>
/// <param name="Actor">The name the activity log records their changes under.</param>
/// <param name="UserId">The signed-in user; null for the admin token and dev mode's open console.</param>
/// <param name="SessionTokenHash">The console session they're using, if they signed in.</param>
public sealed record AdminCaller(string Actor, long? UserId, AdminPermissions Permissions, string? SessionTokenHash = null);

/// <summary>What an admin API endpoint needs.</summary>
/// <param name="Area">Null for any signed-in admin (with <paramref name="FullAdmin"/> false) or full admins only.</param>
public sealed record AdminRequirement(string? Area, string Role, bool FullAdmin = false)
{
    public static readonly AdminRequirement AnyAdmin = new(null, AdminRole.Viewer);
    public static readonly AdminRequirement FullAdminOnly = new(null, AdminRole.Admin, FullAdmin: true);

    public bool IsMetBy(AdminPermissions permissions) =>
        FullAdmin ? permissions.IsFullAdmin : Area is null ? permissions.Any : permissions.Allows(Area, Role);
}

/// <summary>
/// Sign-in and roles for /api/v1/admin. The CLI uses admin.token from server.toml (full access); the
/// console signs in as a TapQueue user and gets what their grants add up to. In dev mode a request with
/// neither is let through as a full admin, so a new server can be set up before anyone has a role.
/// Which role each endpoint needs is decided here, from its path, in one place: see <see cref="Requirement"/>.
/// </summary>
public static class AdminAccess
{
    public const string CookieName = "tapqueue_admin";

    /// <summary>
    /// The console sends this with every request. A cookie-authenticated change without it is refused, so
    /// another site can't make a signed-in admin's browser change things (a cross-site form can't set it).
    /// </summary>
    public const string ConsoleHeader = "X-TapQueue-Console";

    private const string CallerKey = "tapqueue.admin";

    private static readonly AsyncLocal<AdminPermissions?> Current = new();

    /// <summary>
    /// The permissions of the admin whose request is running; null outside admin requests (the directory
    /// sync, the server itself), which aren't limited by roles.
    /// </summary>
    public static AdminPermissions? CurrentPermissions => Current.Value;

    public static AdminCaller Caller(this HttpContext http) => (AdminCaller)http.Items[CallerKey]!;

    /// <summary>
    /// The role an admin API request needs, from its method and path (relative to /api/v1/admin). Reads
    /// need a viewer; changes need an admin, except the day-to-day ones an operator may do. Paths this
    /// doesn't know need a full admin, so a new endpoint is locked down until it's added here.
    /// </summary>
    public static AdminRequirement Requirement(string method, string path)
    {
        // Routes match regardless of case, so the rules must too.
        var parts = path.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var area = parts.FirstOrDefault() switch
        {
            "jobs" or "release" => AdminArea.Jobs,
            "users" or "groups" or "badges" or "quotas" => AdminArea.People,
            "directory" => AdminArea.Directory,
            "printers" or "queues" or "stations" or "workstations" or "clients" => AdminArea.Fleet,
            "client-builds" or "station-builds" => AdminArea.Updates,
            "server" or "crashes" => AdminArea.Server,
            _ => null,
        };

        // Everyone in the console needs these: the header shows the server's version and mode, and
        // Overview and every page's history show the activity log.
        if (read && (parts is ["server"] || parts is ["events", ..]))
            return AdminRequirement.AnyAdmin;
        // Passwords and admins: nobody may give themselves (or take over someone with) more rights.
        if (area is null || parts is ["users", _, "password"])
            return AdminRequirement.FullAdminOnly;
        if (read)
            return new AdminRequirement(area, AdminRole.Viewer);
        return new AdminRequirement(area, IsOperatorWork(method, parts) ? AdminRole.Operator : AdminRole.Admin);
    }

    private static bool IsOperatorWork(string method, string[] parts) => (method.ToUpperInvariant(), parts) switch
    {
        ("DELETE", ["jobs", _]) => true,
        ("POST", ["release"]) => true,
        (_, ["badges", ..]) => true,
        ("POST", ["directory", "sync"]) => true,
        ("POST", ["stations", _, "restart"]) => true,
        ("POST", ["workstations", _, "update"]) => true,
        ("DELETE", ["workstations", _]) => true,
        ("DELETE", ["clients", _]) => true,
        ("DELETE", ["crashes", ..]) => true,
        _ => false,
    };

    /// <summary>The endpoint filter on /api/v1/admin.</summary>
    public static async ValueTask<object?> Require(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var config = http.RequestServices.GetRequiredService<ServerConfig>();
        var caller = Authenticate(http, config, out var failure);
        if (caller is null)
            return failure;

        var path = http.Request.Path.Value!["/api/v1/admin".Length..];
        var needs = Requirement(http.Request.Method, path);
        if (!needs.IsMetBy(caller.Permissions))
            return Forbidden(Explain(needs));

        http.Items[CallerKey] = caller;
        Current.Value = caller.Permissions;
        using (EventLog.ActingAs(caller.Actor))
            return await next(context);
    }

    /// <summary>The caller from the admin token or console cookie (or dev mode); null with the response to send if there's none.</summary>
    public static AdminCaller? Authenticate(HttpContext http, ServerConfig config, out IResult? failure)
    {
        failure = null;
        var token = ClientApi.BearerToken(http);
        if (token is not null)
        {
            if (Tokens.FixedTimeEquals(token, config.Admin.Token))
                return new AdminCaller(EventLog.AdminActor, null, AdminPermissions.Full);
            failure = Unauthorized("Admin token required.");
            return null;
        }

        if (http.Request.Cookies[CookieName] is { Length: > 0 } cookie)
        {
            var admins = http.RequestServices.GetRequiredService<AdminStore>();
            var tokenHash = Tokens.Hash(cookie);
            if (admins.UseSession(tokenHash) is { } session
                && http.RequestServices.GetRequiredService<UserStore>().FindById(session.UserId) is { } user)
            {
                if (!HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey(ConsoleHeader))
                {
                    failure = Forbidden($"Changes from the admin console need the {ConsoleHeader} header.");
                    return null;
                }
                return new AdminCaller(user.Username, user.Id, admins.PermissionsFor(user.Id), tokenHash);
            }
        }

        if (config.Auth.Mode == "dev")
            return new AdminCaller(EventLog.AdminActor, null, AdminPermissions.Full);
        failure = Unauthorized("Sign in to the admin console, or use admin.token.");
        return null;
    }

    private static string Explain(AdminRequirement needs) =>
        needs.FullAdmin ? "Only a full admin (admin in every area) can do that."
        : needs.Area is null ? "You don't have a role in the admin console."
        : $"You need to be {(needs.Role == AdminRole.Viewer ? "a viewer" : $"an {needs.Role}")} or more in {AreaName(needs.Area)} to do that.";

    public static string AreaName(string area) => area switch
    {
        AdminArea.All => "every area",
        AdminArea.Jobs => "Jobs",
        AdminArea.People => "People",
        AdminArea.Directory => "Active Directory",
        AdminArea.Fleet => "Fleet",
        AdminArea.Updates => "Updates",
        AdminArea.Server => "Server",
        _ => area,
    };

    public static IResult Unauthorized(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status403Forbidden);
}
