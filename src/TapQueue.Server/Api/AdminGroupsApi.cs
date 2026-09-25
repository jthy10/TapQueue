using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Server.Users;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/groups: groups, their members, and which queues and printers they may use.</summary>
public static class AdminGroupsApi
{
    public static void MapGroupsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/groups", (GroupStore groups) => groups.List().Select(g => g.ToDto()));
        admin.MapGet("/groups/{id}", (string id, GroupStore groups) =>
            groups.Get(id) is { } group ? Results.Ok(group.ToDto()) : NotFound(id));
        admin.MapPost("/groups", Create);
        admin.MapPatch("/groups/{id}", Update);
        admin.MapDelete("/groups/{id}", Delete);
        admin.MapGet("/groups/{id}/members", (string id, GroupStore groups) =>
            groups.Get(id) is null ? NotFound(id) : Results.Ok(groups.Members(id)));
        admin.MapGet("/groups/{id}/membership", (string id, GroupStore groups) =>
            groups.Get(id) is null ? NotFound(id) : Results.Ok(groups.MembersWithVia(id).Select(m => new GroupMemberDto(m.Username, m.Via))));
        admin.MapPut("/groups/{id}/members/{username}", AddMember);
        admin.MapDelete("/groups/{id}/members/{username}", RemoveMember);
    }

    private static IResult NotFound(string id) => Results.NotFound(new ErrorResponse($"No group \"{id}\"."));

    private static IResult Create(CreateGroupRequest request, GroupStore groups, QueueStore queues, PrinterStore printers, EventLog events)
    {
        var id = request.Id?.Trim();
        if (!Ids.IsValid(id))
            return Results.BadRequest(new ErrorResponse($"Group id must be {Ids.Rule}."));
        if (AdminApi.Clean(request.Name) is not { } name)
            return Results.BadRequest(new ErrorResponse("Group name is required."));
        if (groups.Get(id!) is not null)
            return Results.Conflict(new ErrorResponse($"Group \"{id}\" already exists."));
        if (UnknownIds(request.QueueIds, request.PrinterIds, queues, printers) is { } unknown)
            return unknown;

        groups.Create(id!, name, request.Description?.Trim() ?? "");
        groups.SetQueues(id!, request.AllQueues ?? true, request.QueueIds ?? []);
        groups.SetPrinters(id!, request.AllPrinters ?? true, request.PrinterIds ?? []);
        var group = groups.Get(id!)!;
        events.Admin(EventLog.Group(group.Id), $"Added group \"{group.Name}\" ({group.Id}); {Describe(group)}.");
        return Results.Ok(group.ToDto());
    }

    private static IResult Update(string id, UpdateGroupRequest request, GroupStore groups, QueueStore queues, PrinterStore printers, EventLog events)
    {
        if (groups.Get(id) is not { } group)
            return NotFound(id);
        if (UnknownIds(request.QueueIds, request.PrinterIds, queues, printers) is { } unknown)
            return unknown;
        // What an AD group may use and its limits are TapQueue's; its name and description are AD's.
        if (group.FromDirectory && ((AdminApi.Clean(request.Name) is { } n && n != group.Name)
                || (request.Description is { } d && d.Trim() != group.Description)))
            return Results.Conflict(new ErrorResponse($"\"{group.Name}\" is an Active Directory group; rename it in AD."));

        groups.Update(group.Id, AdminApi.Clean(request.Name) ?? group.Name, request.Description?.Trim() ?? group.Description);
        if (request.AllQueues is not null || request.QueueIds is not null)
            groups.SetQueues(group.Id, request.AllQueues ?? group.AllQueues, request.QueueIds ?? group.QueueIds);
        if (request.AllPrinters is not null || request.PrinterIds is not null)
            groups.SetPrinters(group.Id, request.AllPrinters ?? group.AllPrinters, request.PrinterIds ?? group.PrinterIds);
        var updated = groups.Get(group.Id)!;
        events.Admin(EventLog.Group(group.Id), $"Changed group \"{updated.Name}\" ({updated.Id}); {Describe(updated)}.");
        return Results.Ok(updated.ToDto());
    }

    private static IResult Delete(string id, GroupStore groups, AdminStore admins, AdminRoster roster, EventLog events)
    {
        if (groups.Get(id) is not { } group)
            return NotFound(id);
        if (group.FromDirectory)
            return Results.Conflict(new ErrorResponse(
                $"\"{group.Name}\" is an Active Directory group and would be back at the next sync; remove it from the sync's scope instead."));
        if (admins.GroupHasGrants(id) && AdminAccess.CurrentPermissions is { IsFullAdmin: false })
            return AdminAccess.Forbidden($"\"{group.Name}\" gives admin rights, so only a full admin can delete it.");
        if (roster.WouldLockOut(new RosterChange(GroupGone: id)))
            return Results.Conflict(new ErrorResponse(AdminRoster.LockOutMessage));
        if (!groups.Delete(id))
            return NotFound(id);
        events.Admin(EventLog.Group(group.Id), $"Removed group \"{group.Name}\" ({group.Id}) and its {group.MemberCount} memberships.");
        return Results.NoContent();
    }

    private static IResult AddMember(string id, string username, GroupStore groups, UserStore users, UserLifecycle lifecycle)
    {
        if (groups.Get(id) is not { } group)
            return NotFound(id);
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        try
        {
            lifecycle.AddToGroup(user, group);
        }
        catch (UserChangeRefusedException ex)
        {
            return AdminApi.Refused(ex);
        }
        return Results.NoContent();
    }

    private static IResult RemoveMember(string id, string username, GroupStore groups, UserStore users, UserLifecycle lifecycle)
    {
        if (groups.Get(id) is not { } group)
            return NotFound(id);
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        try
        {
            lifecycle.RemoveFromGroup(user, group);
        }
        catch (UserChangeRefusedException ex)
        {
            return AdminApi.Refused(ex);
        }
        return Results.NoContent();
    }

    private static IResult? UnknownIds(IReadOnlyList<string>? queueIds, IReadOnlyList<string>? printerIds, QueueStore queues, PrinterStore printers)
    {
        if (queueIds?.FirstOrDefault(q => queues.Get(q) is null) is { } queue)
            return Results.BadRequest(new ErrorResponse($"No queue \"{queue}\"."));
        if (printerIds?.FirstOrDefault(p => printers.Get(p) is null) is { } printer)
            return Results.BadRequest(new ErrorResponse($"No printer \"{printer}\"."));
        return null;
    }

    private static string Describe(GroupRecord g) =>
        $"prints to {(g.AllQueues ? "every queue" : List(g.QueueIds, "queue"))}, releases at {(g.AllPrinters ? "every printer" : List(g.PrinterIds, "printer"))}";

    private static string List(IReadOnlyList<string> ids, string noun) =>
        ids.Count == 0 ? $"no {noun}" : string.Join(", ", ids);
}
