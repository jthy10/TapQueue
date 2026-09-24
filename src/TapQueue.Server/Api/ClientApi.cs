using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>Endpoints used by the user client on each workstation.</summary>
public static class ClientApi
{
    private const int HeartbeatSeconds = 60;

    public static void MapClientApi(this IEndpointRouteBuilder app)
    {
        // Whether the tray signs in as the PC's user or asks for a domain account.
        app.MapGet("/api/v1/client/sign-in", (DirectoryStore directory) => new ClientSignInInfoDto(directory.Config().ClientSignIn));
        app.MapPost("/api/v1/client/session", CreateSession);
        // For the TapQueue service on each PC, which runs as the machine rather than a user.
        // Queue names and client builds are not secret: anyone who can reach the server can print to it.
        app.MapGet("/api/v1/client/setup", (QueueStore queues, ClientBuildStore builds) =>
            new ClientSetupResponse(queues.List().Select(ToDto).ToList(), builds.Latest(ClientPlatform.Windows)?.ToDto()));
        // The same, from services that say which PC they are and what they run (0.3 on), so they show
        // up under Workstations and can be told to update now.
        app.MapPost("/api/v1/client/setup", CheckIn);
        app.MapGet("/api/v1/client/builds/{sha256}", DownloadBuild);
        // Anonymous like the setup check-in: a program that crashes may not have signed in (or be able to).
        app.MapPost("/api/v1/client/crash", ReportCrash);

        var me = app.MapGroup("/api/v1").AddEndpointFilter(RequireSession);
        // The build is only used by Windows tray apps older than 0.3, which updated themselves.
        me.MapPost("/client/heartbeat", (ClientBuildStore builds) => new ClientHeartbeatResponse(builds.Latest(ClientPlatform.Windows)?.ToDto()));
        me.MapGet("/printers", (HttpContext http, PrinterRegistry printers, AccessPolicy access) =>
            printers.All.Where(p => access.CanReleaseAt(CurrentUser(http).Id, p.Id)).Select(printers.ToDto));
        me.MapGet("/me/jobs", (HttpContext http, JobStore jobs, bool? all) =>
            jobs.ListForUser(CurrentUser(http).Id, heldOnly: all != true).Select(j => j.ToDto()));
        me.MapPost("/client/sign-out", SignOut);
        me.MapDelete("/me/jobs/{id:long}", CancelJob);
        me.MapPost("/me/release", Release);
    }

    private static async Task<IResult> CreateSession(ClientSessionRequest request, HttpContext http, ServerConfig config, EventLog events, AccessPolicy access,
        UserStore users, SessionStore sessions, ClientLoginStore logins, DirectoryStore directory, IDirectorySourceFactory ad,
        QueueStore queues, PrinterRegistry printers, ClientBuildStore builds, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("TapQueue.Server.Api.ClientApi");
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
            return Results.BadRequest(new ErrorResponse("username is required"));
        var pc = request.Hostname ?? http.ClientIp();

        UserRecord? user;
        long? loginId = null;
        var rememberAfterSignIn = false;
        var how = "";
        var domain = directory.Config();
        if (domain.DomainSignIn)
        {
            if (!string.IsNullOrEmpty(request.RememberToken))
            {
                var login = logins.Use(Tokens.Hash(request.RememberToken));
                user = login is null ? null : users.FindById(login.UserId);
                if (user is null)
                    return Results.Json(new ErrorResponse("Your TapQueue sign-in has ended. Sign in again with your domain account."), statusCode: StatusCodes.Status401Unauthorized);
                loginId = login!.Id;
                how = " (remembered domain sign-in)";
            }
            else if (!string.IsNullOrEmpty(request.Password))
            {
                DirectoryEntry? entry;
                try
                {
                    entry = await Task.Run(() => ad.Authenticate(domain, username, request.Password), http.RequestAborted);
                }
                catch (DirectoryException ex)
                {
                    logger.LogWarning("Couldn't check {User}'s domain password from {Host}: {Error}", username, pc, ex.Message);
                    return Results.Json(new ErrorResponse("TapQueue can't reach your domain controller to check your password. Try again in a minute."),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (entry is null)
                {
                    logger.LogWarning("Rejected domain sign-in for \"{User}\" from {Ip}", username, http.ClientIp());
                    events.Record(EventCategory.SignIn, username, null, $"Domain sign-in as {username} on {pc} rejected: wrong name or password, or the account is disabled in AD.");
                    return Results.Json(new ErrorResponse("Wrong username or password."), statusCode: StatusCodes.Status401Unauthorized);
                }
                user = users.FindByExternalId(UserSource.ActiveDirectory, entry.Guid);
                if (user is null)
                {
                    events.Record(EventCategory.SignIn, entry.SamAccountName ?? username, null,
                        $"{entry.SamAccountName ?? username} signed in to the domain on {pc}, but isn't a TapQueue user (not in the Active Directory sync's scope).");
                    return Results.Json(new ErrorResponse($"{entry.SamAccountName ?? username} isn't set up to print with TapQueue. Ask your IT team."),
                        statusCode: StatusCodes.Status403Forbidden);
                }
                rememberAfterSignIn = true;
                how = " with their domain password";
            }
            else
            {
                return Results.Json(new ErrorResponse("Sign in with your domain account."), statusCode: StatusCodes.Status401Unauthorized);
            }
        }
        else
        {
            user = users.FindByUsername(username);
            if (config.Auth.Mode == "dev")
            {
                user ??= users.Create(username, username, tokenHash: null);
            }
            else if (user?.TokenHash is null || string.IsNullOrEmpty(request.Token) ||
                     !Tokens.FixedTimeEquals(user.TokenHash, Tokens.Hash(request.Token)))
            {
                logger.LogWarning("Rejected sign-in for \"{User}\" from {Ip}", username, http.ClientIp());
                events.Record(EventCategory.SignIn, username, EventLog.User(username), $"Sign-in as {username} from {pc} rejected: unknown user or wrong token.");
                return Results.Json(new ErrorResponse("Unknown user or wrong token."), statusCode: StatusCodes.Status401Unauthorized);
            }
        }

        if (user.Disabled)
        {
            logger.LogWarning("Rejected sign-in for disabled user \"{User}\" from {Ip}", user.Username, http.ClientIp());
            events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} tried to sign in on {pc}, but their account is disabled.");
            return Results.Json(new ErrorResponse("Your TapQueue account is disabled. Ask an admin."), statusCode: StatusCodes.Status403Forbidden);
        }

        string? rememberToken = null;
        if (rememberAfterSignIn)
        {
            rememberToken = Tokens.New();
            loginId = logins.Create(Tokens.Hash(rememberToken), user.Id, request.WindowsUser, request.Hostname);
        }

        var system = ClientPlatform.DisplayName(ClientPlatform.Parse(request.Platform) ?? ClientPlatform.Windows);
        var token = Tokens.New();
        sessions.Create(Tokens.Hash(token), user.Id, request.WindowsUser, request.Hostname, http.ClientIp(), request.ClientVersion, loginId);
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username),
            $"{user.Username} signed in on {request.Hostname ?? "an unknown PC"} ({http.ClientIp()}) as {system} user {request.WindowsUser ?? "?"}{how}.");
        logger.LogInformation("{User} signed in from {Host} ({Ip}) as {System} user {PcUser}{How}, client {Version}",
            user.Username, request.Hostname, http.ClientIp(), system, request.WindowsUser, how, request.ClientVersion ?? "unknown");

        return Results.Ok(new ClientSessionResponse(
            token,
            user.ToDto(),
            // Only what their groups allow, so the client doesn't install queues they can't print to.
            queues.List().Where(q => access.CanPrintTo(user.Id, q.Id)).Select(ToDto).ToList(),
            printers.All.Where(p => access.CanReleaseAt(user.Id, p.Id)).Select(printers.ToDto).ToList(),
            HeartbeatSeconds,
            builds.Latest(ClientPlatform.Parse(request.Platform) ?? ClientPlatform.Windows)?.ToDto(),
            rememberToken));
    }

    /// <summary>"Sign out" in the tray: ends the session, and forgets the remembered domain sign-in so it needs the password again.</summary>
    private static IResult SignOut(ClientSignOutRequest request, HttpContext http, SessionStore sessions, ClientLoginStore logins, EventLog events)
    {
        var user = CurrentUser(http);
        var session = (SessionRecord)http.Items[nameof(SessionRecord)]!;
        sessions.End(session.Id);
        if (session.LoginId is { } loginId)
            logins.Forget(loginId);
        if (!string.IsNullOrEmpty(request.RememberToken))
            logins.Forget(Tokens.Hash(request.RememberToken));
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} signed out on {session.Hostname ?? http.ClientIp()}.");
        return Results.NoContent();
    }

    private static IResult CheckIn(ClientSetupRequest request, HttpContext http, QueueStore queues, ClientBuildStore builds, WorkstationStore workstations)
    {
        var computer = request.Computer?.Trim();
        if (string.IsNullOrEmpty(computer) || computer.Length > 255)
            return Results.BadRequest(new ErrorResponse("computer is required."));
        if (ClientPlatform.Parse(request.Platform) is not { } platform)
            return Results.BadRequest(new ErrorResponse($"Unknown platform \"{request.Platform}\". Known: {string.Join(", ", ClientPlatform.All)}."));
        var command = workstations.CheckIn(computer, http.ClientIp(), platform, request with { UpdateError = Clean(request.UpdateError) });
        return Results.Ok(new ClientSetupResponse(queues.List().Select(ToDto).ToList(), builds.Latest(platform)?.ToDto(), command));

        static string? Clean(string? error) => string.IsNullOrWhiteSpace(error) ? null : error.Trim()[..Math.Min(error.Trim().Length, 500)];
    }

    private static IResult ReportCrash(CrashReportRequest report, HttpContext http, CrashStore crashes, EventLog events, ILoggerFactory loggers)
    {
        if (string.IsNullOrWhiteSpace(report.Computer) || string.IsNullOrWhiteSpace(report.Program) || string.IsNullOrWhiteSpace(report.Message))
            return Results.BadRequest(new ErrorResponse("computer, program and message are required."));
        if (ClientPlatform.Parse(report.Platform) is not { } platform)
            return Results.BadRequest(new ErrorResponse($"Unknown platform \"{report.Platform}\"."));

        var crash = crashes.Add(report, platform, http.ClientIp());
        var what = crash.Program == CrashProgram.Service ? "TapQueue service" : crash.Program == CrashProgram.Tray ? "tray app" : crash.Program;
        loggers.CreateLogger("TapQueue.Server.Api.ClientApi").LogWarning(
            "{Computer} ({Ip}): {What} {Version} crashed: {Message} (crash report {Id})",
            crash.Computer, crash.Ip, what, crash.Version ?? "?", crash.Message, crash.Id);
        events.Record(EventCategory.Crash, crash.Computer, null,
            $"The {what} on {crash.Computer} ({ClientPlatform.DisplayName(platform)}, client {crash.Version ?? "?"}) crashed: {crash.Message}");
        return Results.Ok(new { crash.Id });
    }

    private static QueueDto ToDto(QueueRecord q) => new(q.Id, q.Name, q.Description, $"/ipp/{q.Id}");

    /// <param name="computer">Sent by the client so the log says which PC is updating.</param>
    private static IResult DownloadBuild(string sha256, string? computer, HttpContext http, ClientBuildStore builds, ILoggerFactory loggers)
    {
        if (builds.Find(sha256) is not { } build || builds.FileFor(build.Sha256) is not { } path)
            return Results.NotFound(new ErrorResponse("No such client build."));
        loggers.CreateLogger("TapQueue.Server.Api.ClientApi").LogInformation(
            "{Computer} ({Ip}) is downloading {Platform} client {Version} to update itself",
            string.IsNullOrWhiteSpace(computer) ? "A PC" : computer.Trim(), http.ClientIp(), ClientPlatform.DisplayName(build.Platform!), build.Version);
        return build.Platform == ClientPlatform.Windows
            ? Results.File(path, "application/vnd.microsoft.portable-executable", "TapQueueClient.exe")
            : Results.File(path, "application/octet-stream", "tapqueue-client");
    }

    private static IResult CancelJob(long id, HttpContext http, JobStore jobs, Spool spool, EventLog events)
    {
        var job = jobs.Get(id);
        if (job is null || job.UserId != CurrentUser(http).Id)
            return Results.NotFound(new ErrorResponse("No such job."));
        if (!jobs.TryTransition(id, JobStatus.Held, JobStatus.Canceled))
            return Results.Conflict(new ErrorResponse("Job is no longer held."));
        spool.Delete(id);
        events.Record(EventCategory.Job, job.Username!, EventLog.User(job.Username!), $"{job.Username} canceled \"{job.Name}\" (job #{job.Id}).");
        return Results.NoContent();
    }

    private static async Task<IResult> Release(ReleaseRequest request, HttpContext http, PrinterRegistry printers, ReleaseService release)
    {
        var printer = printers.Find(request.PrinterId);
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\"."));
        var user = CurrentUser(http);
        var session = (SessionRecord)http.Items[nameof(SessionRecord)]!;
        return Results.Ok(await release.ReleaseAsync(user, printer, request.JobIds, user.Username, $"from {session.Hostname ?? http.ClientIp()}", http.RequestAborted));
    }

    private static UserRecord CurrentUser(HttpContext http) => (UserRecord)http.Items[nameof(UserRecord)]!;

    private static async ValueTask<object?> RequireSession(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var settings = services.GetRequiredService<ServerSettings>();

        var token = BearerToken(http);
        var session = token is null
            ? null
            : services.GetRequiredService<SessionStore>().Touch(Tokens.Hash(token), http.ClientIp(), settings.SessionTimeout);
        var user = session is null ? null : services.GetRequiredService<UserStore>().FindById(session.UserId);
        if (user is null && token is not null && services.GetRequiredService<SessionStore>().WasSignedOut(Tokens.Hash(token)))
            // 403, not 401: the client stays signed out instead of signing straight back in.
            return Results.Json(new ErrorResponse("An admin signed you out of TapQueue on this PC."), statusCode: StatusCodes.Status403Forbidden);
        if (user is null)
            return Results.Json(new ErrorResponse("Session expired. Sign in again."), statusCode: StatusCodes.Status401Unauthorized);

        http.Items[nameof(UserRecord)] = user;
        http.Items[nameof(SessionRecord)] = session;
        return await next(context);
    }

    internal static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}
