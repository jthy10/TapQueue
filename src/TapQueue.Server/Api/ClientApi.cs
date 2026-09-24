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
        me.MapDelete("/me/jobs/{id:long}", CancelJob);
        me.MapPost("/me/release", Release);
    }

    private static IResult CreateSession(ClientSessionRequest request, HttpContext http, ServerConfig config, EventLog events, AccessPolicy access,
        UserStore users, SessionStore sessions, QueueStore queues, PrinterRegistry printers, ClientBuildStore builds, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("TapQueue.Server.Api.ClientApi");
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
            return Results.BadRequest(new ErrorResponse("username is required"));

        var user = users.FindByUsername(username);
        if (config.Auth.Mode == "dev")
        {
            user ??= users.Create(username, username, tokenHash: null);
        }
        else if (user?.TokenHash is null || string.IsNullOrEmpty(request.Token) ||
                 !Tokens.FixedTimeEquals(user.TokenHash, Tokens.Hash(request.Token)))
        {
            logger.LogWarning("Rejected sign-in for \"{User}\" from {Ip}", username, http.ClientIp());
            events.Record(EventCategory.SignIn, username, EventLog.User(username), $"Sign-in as {username} from {request.Hostname ?? http.ClientIp()} rejected: unknown user or wrong token.");
            return Results.Json(new ErrorResponse("Unknown user or wrong token."), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (user.Disabled)
        {
            logger.LogWarning("Rejected sign-in for disabled user \"{User}\" from {Ip}", user.Username, http.ClientIp());
            events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username), $"{user.Username} tried to sign in on {request.Hostname ?? http.ClientIp()}, but their account is disabled.");
            return Results.Json(new ErrorResponse("Your TapQueue account is disabled. Ask an admin."), statusCode: StatusCodes.Status403Forbidden);
        }

        var system = ClientPlatform.DisplayName(ClientPlatform.Parse(request.Platform) ?? ClientPlatform.Windows);
        var token = Tokens.New();
        sessions.Create(Tokens.Hash(token), user.Id, request.WindowsUser, request.Hostname, http.ClientIp(), request.ClientVersion);
        events.Record(EventCategory.SignIn, user.Username, EventLog.User(user.Username),
            $"{user.Username} signed in on {request.Hostname ?? "an unknown PC"} ({http.ClientIp()}) as {system} user {request.WindowsUser ?? "?"}.");
        logger.LogInformation("{User} signed in from {Host} ({Ip}) as {System} user {PcUser}, client {Version}",
            user.Username, request.Hostname, http.ClientIp(), system, request.WindowsUser, request.ClientVersion ?? "unknown");

        return Results.Ok(new ClientSessionResponse(
            token,
            user.ToDto(),
            // Only what their groups allow, so the client doesn't install queues they can't print to.
            queues.List().Where(q => access.CanPrintTo(user.Id, q.Id)).Select(ToDto).ToList(),
            printers.All.Where(p => access.CanReleaseAt(user.Id, p.Id)).Select(printers.ToDto).ToList(),
            HeartbeatSeconds,
            builds.Latest(ClientPlatform.Parse(request.Platform) ?? ClientPlatform.Windows)?.ToDto()));
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
