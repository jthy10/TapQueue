using TapQueue.Server.Data;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Users;

/// <summary>
/// Admin changes to users that touch more than the users table: creating, renaming, disabling and
/// deleting, and group membership. Each one is logged and recorded in the activity log. Used by the
/// single-user endpoints, bulk actions and CSV import alike.
/// </summary>
public sealed class UserLifecycle(UserStore users, GroupStore groups, JobStore jobs, Spool spool, EventLog events, ILogger<UserLifecycle> logger)
{
    /// <summary>Creates a user with a fresh client token and returns both.</summary>
    public (UserRecord User, string Token) Create(string username, string? displayName)
    {
        var token = Tokens.New();
        var user = users.Create(username, string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(), Tokens.Hash(token));
        events.Admin(EventLog.User(user.Username), $"Added user {user.Username} ({user.DisplayName}).");
        return (user, token);
    }

    public UserRecord Rename(UserRecord user, string displayName)
    {
        if (displayName == user.DisplayName) return user;
        var renamed = users.SetDisplayName(user.Id, displayName)!;
        events.Admin(EventLog.User(user.Username), $"Renamed {user.Username} to {displayName}.");
        return renamed;
    }

    public UserRecord SetDisabled(UserRecord user, bool disabled)
    {
        if (disabled == user.Disabled) return user;
        var updated = users.SetDisabled(user.Id, disabled)!;
        logger.LogInformation(disabled ? "Admin disabled {User}; they're signed out and can't print or release" : "Admin re-enabled {User}", user.Username);
        events.Admin(EventLog.User(user.Username), disabled
            ? $"Disabled {user.Username}. They're signed out and can't print or release."
            : $"Re-enabled {user.Username}.");
        return updated;
    }

    /// <summary>Cancels the user's held jobs, then deletes them with their cards. Job history keeps their name.</summary>
    public void Delete(UserRecord user)
    {
        var canceled = 0;
        foreach (var job in jobs.ListForUser(user.Id, heldOnly: true))
        {
            if (!jobs.TryTransition(job.Id, JobStatus.Held, JobStatus.Canceled)) continue;
            spool.Delete(job.Id);
            canceled++;
        }
        users.Delete(user.Id);
        logger.LogInformation("Admin deleted user {User} ({Canceled} held jobs canceled)", user.Username, canceled);
        events.Admin(EventLog.User(user.Username), $"Deleted user {user.Username}" + (canceled > 0 ? $" and canceled their {canceled} held jobs." : "."));
    }

    public void AddToGroup(UserRecord user, GroupRecord group)
    {
        if (groups.AddMember(group.Id, user.Id))
            events.Admin(EventLog.User(user.Username), $"Added {user.Username} to group \"{group.Name}\".");
    }

    public void RemoveFromGroup(UserRecord user, GroupRecord group)
    {
        if (groups.RemoveMember(group.Id, user.Id))
            events.Admin(EventLog.User(user.Username), $"Removed {user.Username} from group \"{group.Name}\".");
    }
}
