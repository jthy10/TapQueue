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
            return Results.Json(new ErrorResponse(throttle.LockedMessage), statusCode: StatusCodes.Status429TooManyRequests);

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
        {
            if (users.PasswordHash(user.Id) is { } hash)
                return PasswordHasher.Verify(password, hash) ? user : null;
            // No console password: take as long as checking one, so the answer doesn't say who has one.
            PasswordHasher.VerifyNothing(password);
            return null;
        }

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
            return Results.Json(new ErrorResponse(throttle.LockedMessage), statusCode: StatusCodes.Status429TooManyRequests);
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
/// Slows down password guessing. After a few wrong passwords for a name from one address (5 unless
/// changed), sign-in as that name is refused from that address until the lockout time (15 minutes)
/// after the last one; an address that gets four times as many wrong, whatever the names, is
/// refused altogether. Counted per address, not per name alone: otherwise anyone on the network
/// could keep an admin out of the console by typing wrong passwords for their name.
/// Kept in memory; a restart forgets it. Entries nobody adds to are swept out now and then, so
/// guessing at many names doesn't use up memory.
/// </summary>
public sealed class SignInThrottle
{
    /// <summary>An address may get this many times <see cref="ServerSettings.SignInAttempts"/> wrong over all names.</summary>
    internal const int AddressFactor = 4;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly Func<int> _attempts;
    private readonly Func<TimeSpan> _window;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();
    private long _lastSweepTicks;

    public SignInThrottle(ServerSettings settings) : this(() => settings.SignInAttempts, () => settings.SignInLockout)
    {
    }

    internal SignInThrottle(Func<int>? attempts = null, Func<TimeSpan>? window = null)
    {
        _attempts = attempts ?? (() => 5);
        _window = window ?? (() => TimeSpan.FromMinutes(15));
    }

    /// <summary>How many names and addresses have recent failures (for tests).</summary>
    internal int Tracked => _failures.Count;

    /// <summary>What to tell someone who's locked out.</summary>
    public string LockedMessage
    {
        get
        {
            var minutes = (int)Math.Ceiling(_window().TotalMinutes);
            return $"Too many wrong passwords. Wait {(minutes == 1 ? "a minute" : $"{minutes} minutes")} and try again.";
        }
    }

    public bool IsLocked(string name, string ip) =>
        Recent(Pair(name, ip)) >= _attempts() || Recent("ip:" + ip) >= _attempts() * AddressFactor;

    public void Failed(string name, string ip)
    {
        Add(Pair(name, ip));
        Add("ip:" + ip);
    }

    public void Succeeded(string name, string ip) => _failures.TryRemove(Pair(name, ip), out _);

    // A name can't hold a newline, so no name and address can be read as another pair.
    private static string Pair(string name, string ip) => $"name:{name.ToLowerInvariant()}\n{ip}";

    private void Add(string key)
    {
        while (true)
        {
            var list = _failures.GetOrAdd(key, _ => []);
            lock (list)
            {
                // A sweep may have just taken this list out; then start a new one.
                if (!_failures.TryGetValue(key, out var current) || current != list)
                    continue;
                list.Add(DateTimeOffset.UtcNow);
            }
            break;
        }
        Sweep(DateTimeOffset.UtcNow);
    }

    /// <summary>Drops the names and addresses with no failures left in the window, at most once a minute.</summary>
    internal void Sweep(DateTimeOffset now, bool force = false)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (!force && (now.UtcTicks - last < SweepEvery.Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last))
            return;
        var window = _window();
        foreach (var (key, list) in _failures)
        {
            lock (list)
            {
                list.RemoveAll(t => t < now - window);
                if (list.Count == 0)
                    _failures.TryRemove(new KeyValuePair<string, List<DateTimeOffset>>(key, list));
            }
        }
    }

    private int Recent(string key)
    {
        if (!_failures.TryGetValue(key, out var list))
            return 0;
        var window = _window();
        lock (list)
        {
            list.RemoveAll(t => t < DateTimeOffset.UtcNow - window);
            return list.Count;
        }
    }
}
