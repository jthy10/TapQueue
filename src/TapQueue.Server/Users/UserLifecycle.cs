using TapQueue.Server.Data;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Users;

/// <summary>
/// Admin changes to users that touch more than the users table: creating, renaming, disabling and
/// deleting, and group membership. Each one is logged and recorded in the activity log. Used by the
/// single-user endpoints, bulk actions and CSV import alike.
///
/// Active Directory owns the users and groups it syncs: their names, whether AD has them disabled,
/// and AD groups' members. Changing those here would be undone at the next sync, so it's refused with
/// a <see cref="DirectoryOwnedException"/>. Admins can still disable an AD user, give them cards,
/// limits and local groups, and delete one that's no longer in the sync's scope.
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
        if (user.FromDirectory)
            throw new DirectoryOwnedException($"{user.Username}'s name comes from Active Directory; change it there.");
        var renamed = users.SetDisplayName(user.Id, displayName)!;
        events.Admin(EventLog.User(user.Username), $"Renamed {user.Username} to {displayName}.");
        return renamed;
    }

    public UserRecord SetDisabled(UserRecord user, bool disabled)
    {
        if (!disabled && user.DisabledBy == DisabledBy.Directory)
            throw new DirectoryOwnedException(user.DirectoryState == DirectoryState.Missing
                ? $"{user.Username} is no longer in the Active Directory sync's scope; add them back to it, or delete them."
                : $"{user.Username} is {user.DirectoryState ?? "disabled"} in Active Directory; enable them there.");
        // Taking over a directory disable, so re-enabling them in AD doesn't undo the admin's.
        if (disabled && user.DisabledBy == DisabledBy.Directory)
            return users.SetDisabled(user.Id, true, DisabledBy.Admin)!;
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
        if (user.FromDirectory && user.DirectoryState != DirectoryState.Missing)
            throw new DirectoryOwnedException(
                $"{user.Username} comes from Active Directory and would be back at the next sync; remove them from its scope first, or disable them.");
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
        RefuseDirectoryGroup(group);
        if (groups.AddMember(group.Id, user.Id))
            events.Admin(EventLog.User(user.Username), $"Added {user.Username} to group \"{group.Name}\".");
    }

    public void RemoveFromGroup(UserRecord user, GroupRecord group)
    {
        RefuseDirectoryGroup(group);
        if (groups.RemoveMember(group.Id, user.Id))
            events.Admin(EventLog.User(user.Username), $"Removed {user.Username} from group \"{group.Name}\".");
    }

    private static void RefuseDirectoryGroup(GroupRecord group)
    {
        if (group.FromDirectory)
            throw new DirectoryOwnedException($"\"{group.Name}\" is an Active Directory group; change its members in AD.");
    }
}

/// <summary>Something Active Directory owns was about to be changed in TapQueue. The message says where to change it.</summary>
public sealed class DirectoryOwnedException(string message) : InvalidOperationException(message);
