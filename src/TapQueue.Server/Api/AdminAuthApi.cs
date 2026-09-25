using System.Collections.Concurrent;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Admins;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin-auth: signing in to the admin console. Local users use their console password, AD users
/// their domain password; either way they need a role (see <see cref="AdminStore"/>). A session is an
/// HTTP-only cookie that ends after <see cref="AdminStore.IdleTimeout"/> without use.
/// </summary>
public static class AdminAuthApi
{
    public static void MapAdminAuthApi(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/admin-auth");
        auth.MapPost("/sign-in", SignIn);
        auth.MapPost("/sign-out", SignOut);
        auth.MapGet("/me", Me);
        auth.MapPost("/password", ChangePassword);
    }

    private static async Task<IResult> SignIn(AdminSignInRequest request, HttpContext http, UserStore users, AdminStore admins,
        DirectoryStore directory, IDirectorySourceFactory ad, SignInThrottle throttle, EventLog events, ILogger<AdminStore> logger)
    {
        if (!http.Request.Headers.ContainsKey(AdminAccess.ConsoleHeader))
            return AdminAccess.Forbidden($"Sign in from the admin console (the {AdminAccess.ConsoleHeader} header is missing).");
        var name = request.Username?.Trim() ?? "";
        if (name.Length == 0 || string.IsNullOrEmpty(request.Password))
            return Results.BadRequest(new ErrorResponse("Enter your username and password."));
        var ip = http.ClientIp();
        if (throttle.IsLocked(name, ip))
            return Results.Json(new ErrorResponse("Too many wrong passwords. Wait 15 minutes and try again."), statusCode: StatusCodes.Status429TooManyRequests);

        UserRecord? user;
        try
        {
            user = await Task.Run(() => Check(name, request.Password, users, directory, ad), http.RequestAborted);
        }
        catch (DirectoryException ex)
        {
            logger.LogWarning("Couldn't check {User}'s domain password for the admin console: {Error}", name, ex.Message);
            return Results.Json(new ErrorResponse("TapQueue can't reach your domain controller to check your password. Try again in a minute."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (user is null || user.Disabled)
        {
            throttle.Failed(name, ip);
            logger.LogWarning("Rejected admin console sign-in for \"{User}\" from {Ip}", name, ip);
            events.Record(EventCategory.SignIn, name, null, $"Admin console sign-in as {name} from {ip} rejected: wrong name or password, or the account is disabled.");
            return AdminAccess.Unauthorized("Wrong username or password.");
        }
        if (!admins.PermissionsFor(user.Id).Any)
        {
            events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username),
                $"{user.Username} tried to sign in to the admin console from {ip}, but has no admin role.");
            return AdminAccess.Forbidden($"{user.Username} doesn't have a role in the TapQueue admin console. Ask a TapQueue admin.");
        }

        throttle.Succeeded(name, ip);
        var token = Tokens.New();
        admins.StartSession(Tokens.Hash(token), user.Id, ip, http.Request.Headers.UserAgent.ToString());
        http.Response.Cookies.Append(AdminAccess.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed in to the admin console from {ip}.");
        return Results.Ok(MeFor(user, admins.PermissionsFor(user.Id), http));
    }

    /// <summary>The user the name and password belong to, or null. Throws <see cref="DirectoryException"/> if AD can't be asked.</summary>
    private static UserRecord? Check(string name, string password, UserStore users, DirectoryStore directory, IDirectorySourceFactory ad)
    {
        var user = users.FindByUsername(name);
        if (user is { FromDirectory: false })
            return PasswordHasher.Verify(password, users.PasswordHash(user.Id)) ? user : null;

        // AD users sign in with their domain password, by TapQueue username, sAMAccountName, DOMAIN\name or UPN.
        var config = directory.Config();
        if (config is not { Host.Length: > 0, Password.Length: > 0 })
        {
            PasswordHasher.VerifyNothing(password);
            return null;
        }
        var entry = ad.Authenticate(config, user?.Username ?? name, password);
        var synced = entry is null ? null : users.FindByExternalId(UserSource.ActiveDirectory, entry.Guid);
        // A different AD account than the TapQueue user of that name is not them.
        return user is not null && synced?.Id != user.Id ? null : synced;
    }

    private static IResult SignOut(HttpContext http, AdminStore admins, UserStore users, EventLog events)
    {
        if (http.Request.Cookies[AdminAccess.CookieName] is { Length: > 0 } cookie)
        {
            var hash = Tokens.Hash(cookie);
            if (admins.UseSession(hash) is { } session && users.FindById(session.UserId) is { } user)
                events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed out of the admin console.");
            admins.EndSession(hash);
        }
        http.Response.Cookies.Delete(AdminAccess.CookieName, new CookieOptions { Path = "/" });
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http, ServerConfig config, UserStore users)
    {
        if (AdminAccess.Authenticate(http, config, out var failure) is not { } caller)
            return failure!;
        if (caller.UserId is { } id && users.FindById(id) is { } user)
            return Results.Ok(MeFor(user, caller.Permissions, http));
        return Results.Ok(new AdminMeDto(config.Auth.Mode, null, null, caller.Permissions.Roles, caller.Permissions.IsFullAdmin, false));
    }

    private static AdminMeDto MeFor(UserRecord user, AdminPermissions permissions, HttpContext http) =>
        new(http.RequestServices.GetRequiredService<ServerConfig>().Auth.Mode, user.Username, user.DisplayName,
            permissions.Roles, permissions.IsFullAdmin, user is { FromDirectory: false, HasPassword: true });

    /// <summary>A signed-in local admin changes their own password. Their other sessions end.</summary>
    private static IResult ChangePassword(ChangePasswordRequest request, HttpContext http, ServerConfig config, UserStore users, AdminStore admins,
        SignInThrottle throttle, EventLog events)
    {
        if (AdminAccess.Authenticate(http, config, out var failure) is not { } caller)
            return failure!;
        if (caller.UserId is not { } id || users.FindById(id) is not { } user || caller.SessionTokenHash is null)
            return Results.BadRequest(new ErrorResponse("Sign in to change your password."));
        if (user.FromDirectory)
            return Results.BadRequest(new ErrorResponse("You sign in with your domain password; change it in Windows."));
        var ip = http.ClientIp();
        if (throttle.IsLocked(user.Username, ip))
            return Results.Json(new ErrorResponse("Too many wrong passwords. Wait 15 minutes and try again."), statusCode: StatusCodes.Status429TooManyRequests);
        if (!PasswordHasher.Verify(request.CurrentPassword ?? "", users.PasswordHash(user.Id)))
        {
            throttle.Failed(user.Username, ip);
            return Results.BadRequest(new ErrorResponse("Your current password is wrong."));
        }
        if (PasswordHasher.Problem(request.NewPassword) is { } problem)
            return Results.BadRequest(new ErrorResponse(problem));

        users.SetPasswordHash(user.Id, PasswordHasher.Hash(request.NewPassword));
        // SetPasswordHash signed them out everywhere; keep this browser signed in.
        admins.StartSession(caller.SessionTokenHash, user.Id, ip, http.Request.Headers.UserAgent.ToString());
        events.Record(EventCategory.Admin, user.Username, EventLog.User(user.Username), $"{user.Username} changed their admin console password.");
        return Results.NoContent();
    }
}

/// <summary>
/// Slows down password guessing: after 5 wrong passwords for a name, or 20 from one address, within
/// 15 minutes, sign-in is refused until 15 minutes after the last one. Kept in memory; a restart forgets it.
/// </summary>
public sealed class SignInThrottle
{
    private const int PerName = 5;
    private const int PerAddress = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public bool IsLocked(string name, string ip) =>
        Recent("name:" + name.ToLowerInvariant()) >= PerName || Recent("ip:" + ip) >= PerAddress;

    public void Failed(string name, string ip)
    {
        Add("name:" + name.ToLowerInvariant());
        Add("ip:" + ip);
    }

    public void Succeeded(string name, string ip) => _failures.TryRemove("name:" + name.ToLowerInvariant(), out _);

    private void Add(string key)
    {
        var list = _failures.GetOrAdd(key, _ => []);
        lock (list)
            list.Add(DateTimeOffset.UtcNow);
    }

    private int Recent(string key)
    {
        if (!_failures.TryGetValue(key, out var list))
            return 0;
        lock (list)
        {
            list.RemoveAll(t => t < DateTimeOffset.UtcNow - Window);
            return list.Count;
        }
    }
}
