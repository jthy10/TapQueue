using System.Net.ServerSentEvents;
using TapQueue.Server.Data;
using TapQueue.Server.Admins;
using TapQueue.Server.Logging;
using TapQueue.Server.Updates;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/server/log, /server/restart and /server/update: the server's live log, restarting
/// it, and upgrading it to a newer release.
/// </summary>
public static class AdminServerApi
{
    /// <summary>
    /// The exit code that asks systemd to start the server again (EX_TEMPFAIL). tapqueue-server.service
    /// lists it in RestartForceExitStatus and SuccessExitStatus; older units restart it too, since
    /// they restart on any failure.
    /// </summary>
    public const int RestartExitCode = 75;

    /// <summary>systemd sets INVOCATION_ID for the services it runs. Without it, nothing would start the server again.</summary>
    public static bool CanRestart => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));

    public static void MapServerApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/server/log", (LogBuffer log, long? after, int? limit) =>
            log.Since(after ?? 0, Math.Clamp(limit ?? 500, 1, LogBuffer.Capacity)));
        admin.MapGet("/server/log/stream", StreamLog);
        admin.MapPost("/server/restart", Restart);
        // ?refresh=true asks GitHub (at most once a minute); without it, what the last check found.
        admin.MapGet("/server/update", async (ServerUpdater updater, bool? refresh, CancellationToken ct) =>
            refresh == true ? await updater.CheckAsync(ct) : updater.Status());
        admin.MapPost("/server/update", (ApplyServerUpdateRequest request, ServerUpdater updater, HttpContext http) =>
            updater.Apply(request.Version, request.At, http.Caller().Actor) is { } error
                ? Results.Conflict(new ErrorResponse(error))
                : Results.Ok(updater.Status()));
        // New PCs then need this code in client.toml to register. Shown once; making another replaces it.
        admin.MapPost("/server/enrollment-code", (ServerSettings settings, EventLog events) =>
        {
            var code = Tokens.New();
            settings.SetEnrollmentCodeHash(Tokens.Hash(code));
            events.Admin(null, "Made a new enrollment code for PCs. New PCs need it to register; PCs that already have a key aren't affected.");
            return new EnrollmentCodeResponse(code);
        });
        admin.MapDelete("/server/enrollment-code", (ServerSettings settings, EventLog events) =>
        {
            if (settings.EnrollmentCodeHash is not null)
            {
                settings.SetEnrollmentCodeHash(null);
                events.Admin(null, "Turned off the enrollment code for PCs: any PC that can reach the server can register again.");
            }
            return Results.NoContent();
        });
        admin.MapDelete("/server/update", (ServerUpdater updater) =>
        {
            updater.CancelSchedule();
            return Results.Ok(updater.Status());
        });
    }

    /// <summary>
    /// Server-sent events, one per log line, with the line's id as the event id so a browser that
    /// reconnects (EventSource does that by itself) carries on where it left off.
    /// </summary>
    private static IResult StreamLog(HttpContext http, LogBuffer log, IHostApplicationLifetime lifetime, long? after)
    {
        var resumeFrom = long.TryParse(http.Request.Headers["Last-Event-ID"], out var lastId) ? lastId : after ?? 0;
        // End the stream when the server stops, or shutting down waits for every open console.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        http.Response.RegisterForDispose(stop);
        return TypedResults.ServerSentEvents(Lines(log.WatchAsync(resumeFrom, stop.Token)));

        static async IAsyncEnumerable<SseItem<LogLineDto>> Lines(IAsyncEnumerable<LogLineDto> lines)
        {
            await foreach (var line in lines)
                yield return new SseItem<LogLineDto>(line, "log") { EventId = line.Id.ToString() };
        }
    }

    private static IResult Restart(IHostApplicationLifetime lifetime, EventLog events, ILoggerFactory loggers)
    {
        if (!CanRestart)
            return Results.Conflict(new ErrorResponse(
                "This server wasn't started by systemd, so nothing would start it again. Restart it where you started it."));

        events.Admin(null, "Restarted the server.");
        loggers.CreateLogger("TapQueue.Server.Api.AdminServerApi").LogWarning("Restarting: an admin asked for it");
        // Answer first, then stop; systemd starts it again after RestartSec.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Environment.ExitCode = RestartExitCode;
            lifetime.StopApplication();
        });
        return Results.Accepted();
    }
}
