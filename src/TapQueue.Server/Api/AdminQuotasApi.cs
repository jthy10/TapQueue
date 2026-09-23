using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin: page limits on users and groups, and how much of them people have used.</summary>
public static class AdminQuotasApi
{
    public static void MapQuotasApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/quotas", (UserStore users, QuotaPolicy quotas) =>
        {
            var now = DateTimeOffset.UtcNow;
            return users.List().Select(u => Usage(u, quotas, now)).Where(q => q.Applies.Count > 0);
        });
        admin.MapGet("/users/{username}/quota", (string username, UserStore users, QuotaPolicy quotas) =>
            users.FindByUsername(username) is { } user ? Results.Ok(Usage(user, quotas, DateTimeOffset.UtcNow)) : NoUser(username));
        admin.MapPut("/users/{username}/quota", SetUserQuota);
        admin.MapDelete("/users/{username}/quota", (string username, UserStore users, QuotaPolicy quotas, EventLog events) =>
            SetUserQuota(username, null, users, quotas, events));
        admin.MapPut("/groups/{id}/quota", SetGroupQuota);
        admin.MapDelete("/groups/{id}/quota", (string id, GroupStore groups, EventLog events) => SetGroupQuota(id, null, groups, events));
    }

    private static IResult NoUser(string username) => Results.NotFound(new ErrorResponse($"No user \"{username}\"."));

    private static UserQuotaDto Usage(UserRecord user, QuotaPolicy quotas, DateTimeOffset now) =>
        new(user.Username, user.Quota, quotas.UsageFor(user.Id, now).Select(u => new QuotaUsageDto(
            u.Limit.Pages, u.Limit.Period, u.Limit.Source, u.Used, u.Remaining, u.PeriodStart, u.ResetsAt)).ToList());

    private static IResult SetUserQuota(string username, QuotaDto? quota, UserStore users, QuotaPolicy quotas, EventLog events)
    {
        if (users.FindByUsername(username) is not { } user)
            return NoUser(username);
        if (Invalid(quota) is { } invalid)
            return invalid;
        users.SetQuota(user.Id, quota);
        events.Admin(EventLog.User(user.Username), quota is null
            ? $"Removed {user.Username}'s own page limit; their groups' limits apply."
            : $"Limited {user.Username} to {Describe(quota)}, whatever their groups allow.");
        return Results.Ok(Usage(users.FindById(user.Id)!, quotas, DateTimeOffset.UtcNow));
    }

    private static IResult SetGroupQuota(string id, QuotaDto? quota, GroupStore groups, EventLog events)
    {
        if (groups.Get(id) is not { } group)
            return Results.NotFound(new ErrorResponse($"No group \"{id}\"."));
        if (Invalid(quota) is { } invalid)
            return invalid;
        groups.SetQuota(group.Id, quota);
        events.Admin(EventLog.Group(group.Id), quota is null
            ? $"Removed the page limit on group \"{group.Name}\"."
            : $"Limited members of group \"{group.Name}\" to {Describe(quota)}.");
        return Results.Ok(groups.Get(group.Id)!.ToDto());
    }

    private static IResult? Invalid(QuotaDto? quota) => quota switch
    {
        null => null,
        { Pages: < 0 or > 1_000_000 } => Results.BadRequest(new ErrorResponse("pages must be 0 to 1000000.")),
        _ when !QuotaPeriod.All.Contains(quota.Period) =>
            Results.BadRequest(new ErrorResponse($"period must be {string.Join(", ", QuotaPeriod.All)}.")),
        _ => null,
    };

    private static string Describe(QuotaDto quota) =>
        $"{(quota.Pages == 1 ? "1 page" : $"{quota.Pages} pages")} {QuotaPolicy.Describe(quota.Period)}";
}
