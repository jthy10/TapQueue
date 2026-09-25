using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/admins: who may use the admin console, with which role in which area; and local users'
/// console passwords. Full admins only (see <see cref="AdminAccess.Requirement"/>).
/// </summary>
public static class AdminsApi
{
    public static void MapAdminsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/admins", (AdminStore admins, AdminRoster roster) =>
        {
            var grants = admins.Grants();
            return new AdminsDto(grants.Select(g => g.ToDto()).ToList(), roster.People(grants), roster.FullAdmins(grants));
        });
        admin.MapPost("/admins/grants", Grant);
        admin.MapDelete("/admins/grants/{id:long}", Revoke);
        admin.MapPut("/users/{username}/password", SetPassword);
    }

    private static IResult Grant(GrantAdminRequest request, AdminStore admins, AdminRoster roster, UserStore users, GroupStore groups,
        EventLog events)
    {
        if (!AdminRole.All.Contains(request.Role))
            return Results.BadRequest(new ErrorResponse($"role must be one of: {string.Join(", ", AdminRole.All)}."));
        var areas = request.Areas is { Count: > 0 } ? request.Areas.Distinct().ToList() : [AdminArea.All];
        if (areas.FirstOrDefault(a => a != AdminArea.All && !AdminArea.Each.Contains(a)) is { } unknown)
            return Results.BadRequest(new ErrorResponse($"Unknown area \"{unknown}\"; use {string.Join(", ", AdminArea.Each)} or {AdminArea.All}."));
        if (areas.Contains(AdminArea.All))
            areas = [AdminArea.All];

        long? userId = null;
        string? groupId = null;
        string who;
        string subject;
        if (!string.IsNullOrWhiteSpace(request.Username) == !string.IsNullOrWhiteSpace(request.GroupId))
            return Results.BadRequest(new ErrorResponse("Give either a username or a groupId."));
        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            if (users.FindByUsername(request.Username.Trim()) is not { } user)
                return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
            (userId, who, subject) = (user.Id, user.Username, EventLog.User(user.Username));
        }
        else
        {
            if (groups.Get(request.GroupId!.Trim()) is not { } group)
                return Results.NotFound(new ErrorResponse($"No group \"{request.GroupId}\"."));
            (groupId, who, subject) = (group.Id, $"group \"{group.Name}\"", EventLog.Group(group.Id));
        }

        // Setting a role can lower one; check what that does to the full admins first.
        var after = admins.Grants()
            .Where(g => !(g.UserId == userId && g.GroupId == groupId && (areas.Contains(g.Area) || areas.Contains(AdminArea.All))))
            .Concat(areas.Select(a => new AdminGrant(0, userId, null, groupId, null, a, request.Role, DateTimeOffset.UtcNow)))
            .ToList();
        if (roster.WouldLockOut(admins.Grants(), after))
            return Results.Conflict(new ErrorResponse(AdminRoster.LockOutMessage));

        foreach (var area in areas)
            admins.Set(userId, groupId, area, request.Role);
        events.Admin(subject, $"Made {who} {Article(request.Role)} {request.Role} in {string.Join(", ", areas.Select(AdminAccess.AreaName))}.");
        return Results.Ok(admins.Grants().Where(g => g.UserId == userId && g.GroupId == groupId).Select(g => g.ToDto()));
    }

    private static IResult Revoke(long id, AdminStore admins, AdminRoster roster, EventLog events)
    {
        if (admins.Grant(id) is not { } grant)
            return Results.NotFound(new ErrorResponse($"No admin grant {id}."));
        var before = admins.Grants();
        if (roster.WouldLockOut(before, before.Where(g => g.Id != id).ToList()))
            return Results.Conflict(new ErrorResponse(AdminRoster.LockOutMessage));

        admins.Revoke(id);
        var who = grant.Username ?? $"group \"{grant.GroupName}\"";
        events.Admin(grant.Username is not null ? EventLog.User(grant.Username) : EventLog.Group(grant.GroupId!),
            $"Removed {who}'s {grant.Role} role in {AdminAccess.AreaName(grant.Area)}.");
        return Results.NoContent();
    }

    private static IResult SetPassword(string username, SetPasswordRequest request, UserStore users, AdminRoster roster, EventLog events)
    {
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        if (user.FromDirectory)
            return Results.BadRequest(new ErrorResponse($"{user.Username} signs in with their Active Directory password; set it in AD."));
        if (request.Password is not null && PasswordHasher.Problem(request.Password) is { } problem)
            return Results.BadRequest(new ErrorResponse(problem));
        if (request.Password is null && roster.WouldLockOut(new RosterChange(UserGone: user.Id)))
            return Results.Conflict(new ErrorResponse(AdminRoster.LockOutMessage));

        users.SetPasswordHash(user.Id, request.Password is null ? null : PasswordHasher.Hash(request.Password));
        events.Admin(EventLog.User(user.Username), request.Password is null
            ? $"Removed {user.Username}'s admin console password."
            : $"Set {user.Username}'s admin console password.");
        return Results.NoContent();
    }

    private static string Article(string role) => role == AdminRole.Viewer ? "a" : "an";
}
