using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/workstations and /clients/{id}: PCs with the TapQueue service, and signing people out of them.</summary>
public static class AdminWorkstationsApi
{
    public static void MapWorkstationsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/workstations", List);
        admin.MapPost("/workstations/{hostname}/update", UpdateNow);
        admin.MapDelete("/workstations/{hostname}", (string hostname, WorkstationStore workstations, EventLog events) =>
        {
            if (!workstations.Delete(hostname))
                return NotFound(hostname);
            events.Admin(null, $"Forgot workstation {hostname}.");
            return Results.NoContent();
        });
        admin.MapDelete("/clients/{id:long}", SignOut);
    }

    private static IResult NotFound(string hostname) =>
        Results.NotFound(new ErrorResponse($"No workstation \"{hostname}\". PCs show up once the TapQueue service on them checks in."));

    private static List<WorkstationDto> List(WorkstationStore workstations, SessionStore sessions, ServerSettings settings, ClientBuildStore builds)
    {
        var latest = builds.Latest();
        var signedIn = sessions.ListActive(settings.SessionTimeout).ToLookup(s => s.Hostname ?? "", StringComparer.OrdinalIgnoreCase);
        return workstations.List().Select(w => new WorkstationDto(
            w.Hostname, w.LastIp, w.Version,
            UpToDate: latest is null || string.Equals(latest.Sha256, w.BinarySha256, StringComparison.OrdinalIgnoreCase),
            w.UpdateError, w.PendingCommand, w.Online, w.FirstSeenAt, w.LastSeenAt,
            signedIn[w.Hostname].ToList())).ToList();
    }

    private static IResult UpdateNow(string hostname, WorkstationStore workstations, ClientBuildStore builds, EventLog events)
    {
        if (builds.Latest() is not { } build)
            return Results.Conflict(new ErrorResponse("No client build is published yet. Publish one on Updates first."));
        if (workstations.SetCommand(hostname, WorkstationCommand.Update) is not { } workstation)
            return NotFound(hostname);
        events.Admin(null, $"Asked {workstation.Hostname} to update to client {build.Version} now.");
        return Results.Accepted();
    }

    private static IResult SignOut(long id, SessionStore sessions, UserStore users, EventLog events, ILoggerFactory loggers)
    {
        if (sessions.SignOut(id) is not { } session)
            return Results.NotFound(new ErrorResponse("That session has already ended."));
        var username = users.FindById(session.UserId)?.Username ?? "(deleted user)";
        var pc = session.Hostname ?? session.RemoteIp;
        loggers.CreateLogger("TapQueue.Server.Api.AdminWorkstationsApi").LogInformation("Admin signed {User} out on {Host}", username, pc);
        events.Admin(EventLog.User(username), $"Signed {username} out on {pc}. Jobs printed from there no longer go to them.");
        return Results.NoContent();
    }
}
