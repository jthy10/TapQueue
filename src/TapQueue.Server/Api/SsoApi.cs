using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Server.Sso;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/sso: signing in to the admin console or the web page through the organisation's sign-in
/// provider (<see cref="OidcSignIn"/>), and /api/v1/admin/sso for setting that up (full admins).
/// The browser is sent to the provider from <c>/start</c> and comes back to <c>/callback</c>, which is
/// the address to register with the provider as the redirect URI.
/// </summary>
public static class SsoApi
{
    /// <summary>Ties a sign-in to the browser that started it, so a link to /callback made elsewhere signs nobody in.</summary>
    private const string StateCookie = "tapqueue_sso";

    public static void MapSsoApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sso", (SsoStore store) => store.Get() is { Usable: true } sso ? new SsoInfoDto(true, sso.Label) : new SsoInfoDto(false, ""));
        app.MapGet("/api/v1/sso/start", Start);
        app.MapGet("/api/v1/sso/callback", Callback);
    }

    public static void MapSsoAdminApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/sso", (HttpContext http, SsoStore store) => store.Get().ToDto(RedirectUri(http)));
        admin.MapPatch("/sso", Update);
        admin.MapPost("/sso/test", async (SsoStore store, OidcSignIn oidc, CancellationToken ct) =>
        {
            try
            {
                return new DirectoryTestResultDto(true, await oidc.TestAsync(store.Get().Authority, ct), null);
            }
            catch (SsoException ex)
            {
                return new DirectoryTestResultDto(false, ex.Message, null);
            }
        });
    }

    /// <summary>Where the provider sends people back to: this server, as the browser reached it.</summary>
    private static string RedirectUri(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/api/v1/sso/callback";

    private static string Home(string? target) => target == OidcSignIn.PortalTarget ? "/my/" : "/admin/";

    /// <summary>Back to the page they came from, which shows <paramref name="message"/> above its sign-in.</summary>
    private static IResult Fail(string? target, string message) => Results.Redirect($"{Home(target)}?sso={Uri.EscapeDataString(message)}");

    private static async Task<IResult> Start(string? @for, HttpContext http, OidcSignIn oidc, ServerSettings settings)
    {
        var target = @for == OidcSignIn.PortalTarget ? OidcSignIn.PortalTarget : OidcSignIn.ConsoleTarget;
        if (target == OidcSignIn.PortalTarget && !settings.UserPortal)
            return Results.NotFound();
        try
        {
            var (url, state) = await oidc.StartAsync(target, RedirectUri(http), http.RequestAborted);
            // Lax, not Strict: it has to come back with the provider's redirect, which starts on their site.
            http.Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = http.Request.IsHttps,
                Path = "/api/v1/sso",
                MaxAge = TimeSpan.FromMinutes(10),
            });
            return Results.Redirect(url);
        }
        catch (SsoException ex)
        {
            return Fail(target, ex.Message);
        }
    }

    private static async Task<IResult> Callback(string? code, string? state, string? error, string? error_description, HttpContext http, OidcSignIn oidc,
        UserStore users, AdminStore admins, PortalSessionStore portal, DirectoryStore directory, ServerSettings settings, SsoStore sso, EventLog events,
        ILogger<OidcSignIn> logger)
    {
        var cookie = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/api/v1/sso" });
        if (!string.IsNullOrEmpty(error))
            return Fail(null, $"The sign-in provider said: {(string.IsNullOrWhiteSpace(error_description) ? error : error_description.Split('\n')[0])}");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(cookie) || !Tokens.FixedTimeEquals(cookie, state))
            return Fail(null, "That sign-in didn't start in this browser. Try again.");

        SsoIdentity who;
        try
        {
            who = await oidc.FinishAsync(state, code, http.RequestAborted);
        }
        catch (SsoException ex)
        {
            return Fail(null, ex.Message);
        }

        var ip = http.ClientIp();
        // Someone the directory sync knows is found by their id there; anyone else by the name the provider gives
        // them, and by the part before the @ only inside the organisation's own domain (SsoSettings.Domain).
        var user = (who.ObjectId is { } oid ? users.FindByExternalId(UserSource.ActiveDirectory, oid) : null)
                   ?? sso.Get().UsernamesFor(who.Name).Select(users.FindByUsername).FirstOrDefault(u => u is not null);
        // A local user's name is whatever an admin typed: only the directory's people are who the provider says they are,
        // unless no directory is set up at all and names are all there is.
        var directoryKind = directory.Config();
        if (user is { FromDirectory: false } && directoryKind.Reachable)
        {
            logger.LogWarning("Single sign-on as {Name} from {Ip} matched the local user {User}, who isn't from the directory", who.Name, ip, user.Username);
            user = null;
        }
        if (user is null || user.Disabled)
        {
            events.Record(EventCategory.SignIn, who.Name, null, $"{who.Name} signed in with single sign-on from {ip}, but " +
                (user is null ? "isn't a TapQueue user." : "their TapQueue account is disabled."));
            return Fail(who.Target, user is null
                ? $"{who.Name} isn't set up in TapQueue. Ask your IT team."
                : "Your TapQueue account is disabled. Ask your IT team.");
        }

        var token = Tokens.New();
        var session = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/" };
        if (who.Target == OidcSignIn.PortalTarget)
        {
            if (!settings.UserPortal)
                return Results.NotFound();
            portal.Start(Tokens.Hash(token), user.Id, ip, http.Request.Headers.UserAgent.ToString());
            session.MaxAge = PortalSessionStore.IdleTimeout;
            http.Response.Cookies.Append(PortalApi.CookieName, token, session);
            events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed in to the web page from {ip} with single sign-on.");
            return Results.Redirect("/my/");
        }

        if (!admins.PermissionsFor(user.Id).Any)
        {
            events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username),
                $"{user.Username} tried to sign in to the admin console from {ip} with single sign-on, but has no admin role.");
            return Fail(who.Target, $"{user.Username} doesn't have a role in the TapQueue admin console. Ask a TapQueue admin.");
        }
        admins.StartSession(Tokens.Hash(token), user.Id, ip, http.Request.Headers.UserAgent.ToString());
        http.Response.Cookies.Append(AdminAccess.CookieName, token, session);
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed in to the admin console from {ip} with single sign-on.");
        return Results.Redirect("/admin/");
    }

    private static IResult Update(UpdateSsoRequest request, HttpContext http, SsoStore store, EventLog events)
    {
        var old = store.Get();
        var authority = request.Authority?.Trim().TrimEnd('/');
        if (authority is { Length: > 0 } && (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            return Results.BadRequest(new ErrorResponse("authority is the provider's https:// address, like https://login.microsoftonline.com/<tenant-id>/v2.0."));
        if (request.UsernameClaim is { } claim && (claim.Trim().Length is 0 or > 64 || claim.Any(char.IsWhiteSpace)))
            return Results.BadRequest(new ErrorResponse("usernameClaim is a claim's name, like preferred_username or email."));
        if (request.Label is { } label && label.Trim().Length is 0 or > 40)
            return Results.BadRequest(new ErrorResponse("label is what the button says after \"Sign in with\", up to 40 characters."));
        var domain = request.Domain?.Trim().TrimStart('@').ToLowerInvariant();
        if (domain is { Length: > 0 } && (domain.Length > 253 || Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.')))
            return Results.BadRequest(new ErrorResponse("domain is your organisation's own, like example.org, or empty."));

        var settings = old with
        {
            Enabled = request.Enabled ?? old.Enabled,
            Authority = authority ?? old.Authority,
            ClientId = request.ClientId?.Trim() ?? old.ClientId,
            ClientSecret = string.IsNullOrEmpty(request.ClientSecret) ? old.ClientSecret : request.ClientSecret,
            UsernameClaim = request.UsernameClaim?.Trim() ?? old.UsernameClaim,
            Label = request.Label?.Trim() ?? old.Label,
            Domain = domain ?? old.Domain,
        };
        // The saved secret goes to whatever provider this names, so naming another takes the secret again.
        if (settings.Authority != old.Authority && old.ClientSecret.Length > 0 && string.IsNullOrEmpty(request.ClientSecret))
            return Results.BadRequest(new ErrorResponse("Enter the client secret again to change the provider's address."));
        if (settings.Enabled && (settings.Authority.Length == 0 || settings.ClientId.Length == 0 || settings.ClientSecret.Length == 0))
            return Results.BadRequest(new ErrorResponse("Set the provider's address, the client ID and the client secret before turning single sign-on on."));
        if (settings == old)
            return Results.Ok(settings.ToDto(RedirectUri(http)));
        store.Save(settings);

        var changes = new List<string>();
        if (settings.Enabled != old.Enabled) changes.Add(settings.Enabled ? "on" : "off");
        if (settings.Authority != old.Authority) changes.Add($"provider {settings.Authority}");
        if (settings.ClientId != old.ClientId) changes.Add($"client ID {settings.ClientId}");
        if (settings.ClientSecret != old.ClientSecret) changes.Add("new client secret");
        if (settings.UsernameClaim != old.UsernameClaim) changes.Add($"people are named by the {settings.UsernameClaim} claim");
        if (settings.Label != old.Label) changes.Add($"the button says \"Sign in with {settings.Label}\"");
        if (settings.Domain != old.Domain)
            changes.Add(settings.Domain.Length > 0 ? $"name@{settings.Domain} is the TapQueue user \"name\"" : "names have to be TapQueue usernames exactly");
        events.Admin(null, $"Single sign-on: {string.Join("; ", changes)}.");
        return Results.Ok(settings.ToDto(RedirectUri(http)));
    }
}
