using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/portal: the web page at /my, where people see, release and delete their own jobs from any
/// browser or phone, without the tray app. They sign in with the same name and password as anywhere
/// else (a local user's TapQueue password, or a domain account's, checked against AD), and get a
/// cookie that only this page uses. Everything after sign-in is the same as the tray app's own
/// endpoints (<see cref="ClientApi.MapForSignedInUser"/>): a person's own jobs, cards and balance,
/// and nobody else's.
/// </summary>
public static class PortalApi
{
    public const string CookieName = "tapqueue_portal";

    /// <summary>
    /// The page sends this with every request, and a change made with the cookie but without it is
    /// refused: a form on another site can't set it, so it can't release or delete someone's jobs.
    /// </summary>
    public const string PageHeader = "X-TapQueue-Portal";

    public static void MapPortalApi(this IEndpointRouteBuilder app)
    {
        var open = app.MapGroup("/api/v1/portal").AddEndpointFilter(RequireEnabled);
        open.MapPost("/sign-in", SignIn);
        open.MapPost("/sign-out", SignOut);

        var me = app.MapGroup("/api/v1/portal").AddEndpointFilter(RequireEnabled).AddEndpointFilter(RequireSignedIn);
        me.MapGet("/me", (HttpContext http, ServerSettings settings, Accounts accounts) =>
        {
            var user = ClientApi.CurrentUser(http);
            return new PortalMeDto(user.Username, user.DisplayName, user is { FromDirectory: false, HasPassword: true },
                settings.BadgeSelfEnroll, accounts.Settings().Mode);
        });
        me.MapPost("/password", ChangePassword);
        me.MapForSignedInUser();
    }

    private static async ValueTask<object?> RequireEnabled(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.RequestServices.GetRequiredService<ServerSettings>().UserPortal
            ? await next(context)
            : Results.Json(new ErrorResponse("This TapQueue server doesn't offer its web page. Use the TapQueue icon on your PC, or tap your badge at a printer."),
                statusCode: StatusCodes.Status404NotFound);

    private static async ValueTask<object?> RequireSignedIn(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.Cookies[CookieName] is not { Length: > 0 } cookie
            || http.RequestServices.GetRequiredService<PortalSessionStore>().Use(Tokens.Hash(cookie), http.ClientIp()) is not { } userId
            || http.RequestServices.GetRequiredService<UserStore>().FindById(userId) is not { } user)
            return Results.Json(new ErrorResponse("Sign in to see your print jobs."), statusCode: StatusCodes.Status401Unauthorized);
        if (!HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey(PageHeader))
            return Results.Json(new ErrorResponse($"Changes need the {PageHeader} header."), statusCode: StatusCodes.Status403Forbidden);

        http.Items[nameof(UserRecord)] = user;
        // What the shared endpoints use a tray session for: where a release was asked from, for the activity log.
        http.Items[nameof(SessionRecord)] = new SessionRecord(0, user.Id, null, "the web page", http.ClientIp());
        return await next(context);
    }

    private static async Task<IResult> SignIn(AdminSignInRequest request, HttpContext http, UserStore users, PortalSessionStore sessions, DirectoryStore directory,
        IDirectorySourceFactory ad, SignInThrottle throttle, EventLog events, ILogger<PortalSessionStore> logger)
    {
        if (!http.Request.Headers.ContainsKey(PageHeader))
            return Results.Json(new ErrorResponse($"Sign in from the TapQueue page (the {PageHeader} header is missing)."), statusCode: StatusCodes.Status403Forbidden);
        var name = request.Username?.Trim() ?? "";
        if (name.Length is 0 or > 255 || string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new ErrorResponse("Enter your username and password."));
        var ip = http.ClientIp();
        if (throttle.IsLocked(name, ip))
            return Results.Json(new ErrorResponse(throttle.LockedMessage), statusCode: StatusCodes.Status429TooManyRequests);

        UserRecord? user;
        try
        {
            user = await Task.Run(() => AdminAuthApi.Check(name, request.Password, users, directory, ad), http.RequestAborted);
        }
        catch (DirectoryException ex)
        {
            logger.LogWarning("Couldn't check {User}'s domain password for the web page: {Error}", name, ex.Message);
            return Results.Json(new ErrorResponse("TapQueue can't reach your domain controller to check your password. Try again in a minute."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (user is null || user.Disabled)
        {
            throttle.Failed(name, ip);
            logger.LogWarning("Rejected web page sign-in for \"{User}\" from {Ip}", name, ip);
            events.Record(EventCategory.SignIn, name, null, $"Web page sign-in as {name} from {ip} rejected: wrong name or password, or the account is disabled.");
            return Results.Json(new ErrorResponse("Wrong username or password."), statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Succeeded(name, ip);
        var token = Tokens.New();
        sessions.Start(Tokens.Hash(token), user.Id, ip, http.Request.Headers.UserAgent.ToString());
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
            MaxAge = PortalSessionStore.IdleTimeout,
        });
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed in to the web page from {ip}.");
        return Results.NoContent();
    }

    private static IResult SignOut(HttpContext http, PortalSessionStore sessions)
    {
        if (http.Request.Cookies[CookieName] is { Length: > 0 } cookie)
            sessions.End(Tokens.Hash(cookie));
        http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    /// <summary>A local user changing their own TapQueue password. They stay signed in here; everywhere else is signed out.</summary>
    private static IResult ChangePassword(ChangePasswordRequest request, HttpContext http, UserStore users, PortalSessionStore sessions, SignInThrottle throttle, EventLog events)
    {
        var user = ClientApi.CurrentUser(http);
        if (user.FromDirectory)
            return Results.BadRequest(new ErrorResponse("You sign in with your domain password; change it the way you usually do."));
        var ip = http.ClientIp();
        if (throttle.IsLocked(user.Username, ip))
            return Results.Json(new ErrorResponse(throttle.LockedMessage), statusCode: StatusCodes.Status429TooManyRequests);
        if (!PasswordHasher.Verify(request.CurrentPassword ?? "", users.PasswordHash(user.Id)))
        {
            throttle.Failed(user.Username, ip);
            return Results.BadRequest(new ErrorResponse("Your current password is wrong."));
        }
        if (PasswordHasher.Problem(request.NewPassword) is { } problem)
            return Results.BadRequest(new ErrorResponse(problem));

        var keep = Tokens.Hash(http.Request.Cookies[CookieName]!);
        users.SetPasswordHash(user.Id, PasswordHasher.Hash(request.NewPassword));
        sessions.Start(keep, user.Id, ip, http.Request.Headers.UserAgent.ToString());
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} changed their TapQueue password on the web page.");
        return Results.NoContent();
    }
}
