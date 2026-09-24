using TapQueue.Server.Data;
using TapQueue.Server.Users;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/users/bulk, /users/import and /users/export: working with many users at once.</summary>
public static class AdminUsersBulkApi
{
    public static void MapUsersBulkApi(this RouteGroupBuilder admin)
    {
        admin.MapPost("/users/bulk", Bulk);
        admin.MapGet("/users/export", (UserImport import) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(import.Export()), "text/csv", $"tapqueue-users-{DateTime.Now:yyyy-MM-dd}.csv"));
        admin.MapPost("/users/import", Import);
    }

    /// <summary>The body is the CSV file. Without ?apply=true it's a preview and nothing changes.</summary>
    private static async Task<IResult> Import(HttpContext http, bool? apply, UserImport import)
    {
        using var reader = new StreamReader(http.Request.Body);
        var csv = await reader.ReadToEndAsync(http.RequestAborted);
        try
        {
            return Results.Ok(import.Run(csv, apply == true));
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static IResult Bulk(BulkUsersRequest request, UserStore users, GroupStore groups, UserLifecycle lifecycle)
    {
        GroupRecord? group = null;
        if (request.Action is BulkUserAction.AddToGroup or BulkUserAction.RemoveFromGroup)
        {
            group = groups.Get(request.GroupId ?? "");
            if (group is null)
                return Results.BadRequest(new ErrorResponse($"No group \"{request.GroupId}\"."));
        }
        else if (request.Action is not (BulkUserAction.Disable or BulkUserAction.Enable or BulkUserAction.Delete))
        {
            return Results.BadRequest(new ErrorResponse($"Unknown action \"{request.Action}\"."));
        }

        var errors = new List<string>();
        var changed = 0;
        foreach (var username in request.Usernames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (users.FindByUsername(username) is not { } user)
            {
                errors.Add($"No user \"{username}\".");
                continue;
            }
            try
            {
                switch (request.Action)
                {
                    case BulkUserAction.Disable: lifecycle.SetDisabled(user, true); break;
                    case BulkUserAction.Enable: lifecycle.SetDisabled(user, false); break;
                    case BulkUserAction.Delete: lifecycle.Delete(user); break;
                    case BulkUserAction.AddToGroup: lifecycle.AddToGroup(user, group!); break;
                    case BulkUserAction.RemoveFromGroup: lifecycle.RemoveFromGroup(user, group!); break;
                }
            }
            catch (DirectoryOwnedException ex)
            {
                errors.Add(ex.Message);
                continue;
            }
            changed++;
        }
        return Results.Ok(new BulkUsersResponse(changed, errors));
    }
}
