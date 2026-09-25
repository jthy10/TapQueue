using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="Quota">Their own page limit, which overrides their groups'; null if they have none.</param>
/// <param name="DisabledBy">Who disabled them (<see cref="DisabledBy"/>), null if they're enabled.</param>
/// <param name="DirectoryState">Why the directory disabled them (<see cref="DirectoryState"/>), if it did.</param>
/// <param name="ExternalId">Their id in the directory that syncs them (AD's objectGUID).</param>
/// <param name="HasPassword">They have an admin console password (local users only; AD users use their domain password).</param>
public sealed record UserRecord(long Id, string Username, string DisplayName, string? TokenHash, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string Source,
    QuotaDto? Quota = null, string? DisabledBy = null, string? DirectoryState = null, string? ExternalId = null, bool HasPassword = false)
{
    public bool Disabled => DisabledAt is not null;

    /// <summary>Synced from Active Directory, which owns their name, enabled state and AD group memberships.</summary>
    public bool FromDirectory => Source == UserSource.ActiveDirectory;

    public UserDto ToDto() => new(Id, Username, DisplayName);

    public UserAdminDto ToAdminDto(IReadOnlyList<string> groups) =>
        new(Id, Username, DisplayName, CreatedAt, DisabledAt, groups, Source, Quota, DisabledBy, DirectoryState, HasPassword);
}

public static class UserSource
{
    public const string Local = "local";
    public const string ActiveDirectory = "ad";
}

public sealed class UserStore(Database database)
{
    private const string Columns =
        "id, username, display_name, token_hash, created_at, disabled_at, source, quota_pages, quota_period, disabled_by, directory_state, external_id, password_hash IS NOT NULL";

    public UserRecord? FindByUsername(string username) =>
        database.QueryOne($"SELECT {Columns} FROM users WHERE username = $u", Map, ("$u", username));

    public UserRecord? FindById(long id) =>
        database.QueryOne($"SELECT {Columns} FROM users WHERE id = $id", Map, ("$id", id));

    public UserRecord? FindByExternalId(string source, string externalId) =>
        database.QueryOne($"SELECT {Columns} FROM users WHERE source = $s AND external_id = $x COLLATE NOCASE", Map, ("$s", source), ("$x", externalId));

    public List<UserRecord> List() =>
        database.Query($"SELECT {Columns} FROM users ORDER BY username", Map);

    public UserRecord Create(string username, string displayName, string? tokenHash) =>
        database.QueryOne($"""
            INSERT INTO users (username, display_name, token_hash, created_at)
            VALUES ($u, $d, $t, $now)
            RETURNING {Columns}
            """, Map, ("$u", username), ("$d", displayName), ("$t", tokenHash), ("$now", DateTimeOffset.UtcNow))!;

    /// <summary>Sets the user's own page limit, or with null removes it.</summary>
    public void SetQuota(long userId, QuotaDto? quota) =>
        database.Execute("UPDATE users SET quota_pages = $p, quota_period = $period WHERE id = $id",
            ("$p", quota?.Pages), ("$period", quota?.Period), ("$id", userId));

    public void SetTokenHash(long userId, string tokenHash) =>
        database.Execute("UPDATE users SET token_hash = $t WHERE id = $id", ("$t", tokenHash), ("$id", userId));

    public UserRecord? SetDisplayName(long userId, string displayName) =>
        database.QueryOne($"UPDATE users SET display_name = $d WHERE id = $id RETURNING {Columns}", Map, ("$d", displayName), ("$id", userId));

    /// <summary>
    /// Disabling also signs the user out everywhere (and forgets remembered domain sign-ins), so their PCs
    /// stop matching new jobs to them.
    /// <paramref name="by"/> is one of <see cref="DisabledBy"/>, and <paramref name="directoryState"/> why
    /// the directory disabled them; both are cleared when they're enabled.
    /// </summary>
    public UserRecord? SetDisabled(long userId, bool disabled, string by = Data.DisabledBy.Admin, string? directoryState = null)
    {
        if (disabled)
        {
            database.Execute("DELETE FROM sessions WHERE user_id = $id", ("$id", userId));
            database.Execute("DELETE FROM client_logins WHERE user_id = $id", ("$id", userId));
            database.Execute("DELETE FROM admin_sessions WHERE user_id = $id", ("$id", userId));
        }
        return database.QueryOne($"""
            UPDATE users SET disabled_at = CASE WHEN $disabled THEN COALESCE(disabled_at, $now) END,
                             disabled_by = CASE WHEN $disabled THEN $by END,
                             directory_state = CASE WHEN $disabled THEN $state END
            WHERE id = $id
            RETURNING {Columns}
            """, Map, ("$disabled", disabled), ("$now", DateTimeOffset.UtcNow), ("$by", by), ("$state", directoryState), ("$id", userId));
    }

    /// <summary>The admin console password's hash (<see cref="Admins.PasswordHasher"/>); null if they have none.</summary>
    public string? PasswordHash(long userId) =>
        database.Scalar("SELECT password_hash FROM users WHERE id = $id", ("$id", userId)) as string;

    /// <summary>Sets or (with null) removes the admin console password, signing them out of the console everywhere.</summary>
    public void SetPasswordHash(long userId, string? hash)
    {
        database.Execute("UPDATE users SET password_hash = $h WHERE id = $id", ("$h", hash), ("$id", userId));
        database.Execute("DELETE FROM admin_sessions WHERE user_id = $id", ("$id", userId));
    }

    /// <summary>Hands the user to a directory (or back to TapQueue, with <see cref="UserSource.Local"/> and null).</summary>
    public void SetSource(long userId, string source, string? externalId) =>
        database.Execute("UPDATE users SET source = $s, external_id = $x WHERE id = $id", ("$s", source), ("$x", externalId), ("$id", userId));

    /// <summary>For renames in the directory. The caller checks the new name is free.</summary>
    public void SetUsername(long userId, string username) =>
        database.Execute("UPDATE users SET username = $u WHERE id = $id", ("$u", username), ("$id", userId));

    /// <summary>Changes why the directory disabled a user it already disabled.</summary>
    public void SetDirectoryState(long userId, string state) =>
        database.Execute("UPDATE users SET directory_state = $s WHERE id = $id", ("$s", state), ("$id", userId));

    /// <summary>
    /// Deletes the user with their cards and sessions. Their jobs stay for history, with the username kept
    /// in former_owner; the caller cancels held ones first.
    /// </summary>
    public bool Delete(long userId)
    {
        database.Execute("""
            UPDATE jobs SET former_owner = (SELECT username FROM users WHERE id = $id) WHERE user_id = $id
            """, ("$id", userId));
        return database.Execute("DELETE FROM users WHERE id = $id", ("$id", userId)) == 1;
    }

    private static UserRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetTime(4), r.GetTimeOrNull(5), r.GetString(6),
            r.IsDBNull(7) ? null : new QuotaDto(r.GetInt32(7), r.GetString(8)),
            r.GetStringOrNull(9), r.GetStringOrNull(10), r.GetStringOrNull(11), r.GetBoolean(12));
}

public static class DisabledBy
{
    /// <summary>An admin, in the console, the CLI or a CSV import.</summary>
    public const string Admin = "admin";
    /// <summary>The directory sync, because AD says so (<see cref="DirectoryState"/>).</summary>
    public const string Directory = "directory";
}

/// <summary>Why the directory sync disabled someone.</summary>
public static class DirectoryState
{
    /// <summary>Their account is disabled in AD.</summary>
    public const string Disabled = "disabled";
    /// <summary>Their account expired in AD (accountExpires is in the past).</summary>
    public const string Expired = "expired";
    /// <summary>They're no longer in the sync's scope: deleted in AD, moved out of the OUs, or out of the groups.</summary>
    public const string Missing = "missing";
}
