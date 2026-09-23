using System.Net.ServerSentEvents;
using TapQueue.Server.Data;
using TapQueue.Server.Logging;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/server/log and /server/restart: the server's live log, and restarting it.</summary>
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
