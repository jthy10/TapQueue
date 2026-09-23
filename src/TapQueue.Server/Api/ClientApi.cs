using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
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
            new ClientSetupResponse(queues.List().Select(ToDto).ToList(), builds.Latest()?.ToDto()));
        app.MapGet("/api/v1/client/builds/{sha256}", DownloadBuild);

        var me = app.MapGroup("/api/v1").AddEndpointFilter(RequireSession);
        me.MapPost("/client/heartbeat", (ClientBuildStore builds) => new ClientHeartbeatResponse(builds.Latest()?.ToDto()));
        me.MapGet("/printers", (PrinterRegistry printers) => printers.All.Select(printers.ToDto));
        me.MapGet("/me/jobs", (HttpContext http, JobStore jobs, bool? all) =>
            jobs.ListForUser(CurrentUser(http).Id, heldOnly: all != true).Select(j => j.ToDto()));
        me.MapDelete("/me/jobs/{id:long}", CancelJob);
        me.MapPost("/me/release", Release);
    }

    private static IResult CreateSession(ClientSessionRequest request, HttpContext http, ServerConfig config,
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
            return Results.Json(new ErrorResponse("Unknown user or wrong token."), statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = Tokens.New();
        sessions.Create(Tokens.Hash(token), user.Id, request.WindowsUser, request.Hostname, http.ClientIp(), request.ClientVersion);
        logger.LogInformation("{User} signed in from {Host} ({Ip}) as Windows user {WindowsUser}, client {Version}",
            user.Username, request.Hostname, http.ClientIp(), request.WindowsUser, request.ClientVersion ?? "unknown");

        return Results.Ok(new ClientSessionResponse(
            token,
            user.ToDto(),
            queues.List().Select(ToDto).ToList(),
            printers.All.Select(printers.ToDto).ToList(),
            HeartbeatSeconds,
            builds.Latest()?.ToDto()));
    }

    private static QueueDto ToDto(QueueRecord q) => new(q.Id, q.Name, q.Description, $"/ipp/{q.Id}");

    /// <param name="computer">Sent by the client so the log says which PC is updating.</param>
    private static IResult DownloadBuild(string sha256, string? computer, HttpContext http, ClientBuildStore builds, ILoggerFactory loggers)
    {
        if (builds.Find(sha256) is not { } build || builds.FileFor(build.Sha256) is not { } path)
            return Results.NotFound(new ErrorResponse("No such client build."));
        loggers.CreateLogger("TapQueue.Server.Api.ClientApi").LogInformation(
            "{Computer} ({Ip}) is downloading client {Version} to update itself",
            string.IsNullOrWhiteSpace(computer) ? "A PC" : computer.Trim(), http.ClientIp(), build.Version);
        return Results.File(path, "application/vnd.microsoft.portable-executable", "TapQueueClient.exe");
    }

    private static IResult CancelJob(long id, HttpContext http, JobStore jobs, Spool spool)
    {
        var job = jobs.Get(id);
        if (job is null || job.UserId != CurrentUser(http).Id)
            return Results.NotFound(new ErrorResponse("No such job."));
        if (!jobs.TryTransition(id, JobStatus.Held, JobStatus.Canceled))
            return Results.Conflict(new ErrorResponse("Job is no longer held."));
        spool.Delete(id);
        return Results.NoContent();
    }

    private static async Task<IResult> Release(ReleaseRequest request, HttpContext http, PrinterRegistry printers, ReleaseService release)
    {
        var printer = printers.Find(request.PrinterId);
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\"."));
        return Results.Ok(await release.ReleaseAsync(CurrentUser(http), printer, request.JobIds, http.RequestAborted));
    }

    private static UserRecord CurrentUser(HttpContext http) => (UserRecord)http.Items[nameof(UserRecord)]!;

    private static async ValueTask<object?> RequireSession(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var config = services.GetRequiredService<ServerConfig>();

        var token = BearerToken(http);
        var session = token is null
            ? null
            : services.GetRequiredService<SessionStore>().Touch(Tokens.Hash(token), http.ClientIp(), TimeSpan.FromMinutes(config.Auth.SessionTimeoutMinutes));
        var user = session is null ? null : services.GetRequiredService<UserStore>().FindById(session.UserId);
        if (user is null)
            return Results.Json(new ErrorResponse("Session expired. Sign in again."), statusCode: StatusCodes.Status401Unauthorized);

        http.Items[nameof(UserRecord)] = user;
        return await next(context);
    }

    internal static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}
