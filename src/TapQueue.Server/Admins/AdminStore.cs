using Microsoft.Data.Sqlite;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Admins;

/// <param name="UserId">Set for a grant to one user, otherwise <paramref name="GroupId"/> for a group's members.</param>
public sealed record AdminGrant(long Id, long? UserId, string? Username, string? GroupId, string? GroupName, string Area, string Role, DateTimeOffset CreatedAt)
{
    public AdminGrantDto ToDto() => new(Id, Username, GroupId, GroupName, Area, Role, CreatedAt);
}

/// <summary>A signed-in admin console session.</summary>
public sealed record AdminSession(long Id, long UserId);

/// <summary>Admin grants (who has which role where) and the console's sign-in sessions.</summary>
public sealed class AdminStore(Database database)
{
    /// <summary>A session ends after this long without a request.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

    private const string GrantSelect = """
        SELECT a.id, a.user_id, u.username, a.group_id, g.name, a.area, a.role, a.created_at
        FROM admin_grants a
        LEFT JOIN users u ON u.id = a.user_id
        LEFT JOIN groups g ON g.id = a.group_id
        """;

    public List<AdminGrant> Grants() =>
        database.Query(GrantSelect + " ORDER BY COALESCE(u.username, g.name) COLLATE NOCASE, a.area", MapGrant);

    public AdminGrant? Grant(long id) => database.QueryOne(GrantSelect + " WHERE a.id = $id", MapGrant, ("$id", id));

    /// <summary>The grants that apply to a user: their own and their groups'.</summary>
    public List<AdminGrant> GrantsFor(long userId) =>
        database.Query(GrantSelect + """
             WHERE a.user_id = $u OR a.group_id IN (SELECT group_id FROM group_members WHERE user_id = $u)
            """, MapGrant, ("$u", userId));

    public AdminPermissions PermissionsFor(long userId) =>
        AdminPermissions.From(GrantsFor(userId).Select(g => (g.Area, g.Role)));

    /// <summary>
    /// Gives the user or group <paramref name="role"/> in <paramref name="area"/>, replacing their grant
    /// there. A grant for <see cref="AdminArea.All"/> replaces all their grants. Grants add up by the
    /// highest role, so an all-areas grant can't be narrowed by a per-area one; revoke it instead.
    /// </summary>
    public void Set(long? userId, string? groupId, string area, string role)
    {
        var now = DateTimeOffset.UtcNow;
        database.ExecuteAtomically("""
            DELETE FROM admin_grants WHERE (user_id = $u OR group_id = $g) AND (area = $area OR $area = '*');
            INSERT INTO admin_grants (user_id, group_id, area, role, created_at) VALUES ($u, $g, $area, $role, $now);
            """, ("$u", userId), ("$g", groupId), ("$area", area), ("$role", role), ("$now", now));
    }

    public bool Revoke(long id) => database.Execute("DELETE FROM admin_grants WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Does the group give anyone admin rights? Then only full admins may change who's in it.</summary>
    public bool GroupHasGrants(string groupId) =>
        database.Scalar("SELECT 1 FROM admin_grants WHERE group_id = $g LIMIT 1", ("$g", groupId)) is not null;

    /// <summary>Everyone with at least one grant, directly or through a group.</summary>
    public List<long> UserIdsWithGrants() =>
        database.Query("""
            SELECT user_id FROM admin_grants WHERE user_id IS NOT NULL
            UNION
            SELECT m.user_id FROM admin_grants a JOIN group_members m ON m.group_id = a.group_id
            """, r => r.GetInt64(0));

    public void StartSession(string tokenHash, long userId, string ip, string? userAgent)
    {
        var now = DateTimeOffset.UtcNow;
        database.Execute("""
            INSERT INTO admin_sessions (token_hash, user_id, ip, user_agent, created_at, last_seen_at)
            VALUES ($t, $u, $ip, $ua, $now, $now)
            """, ("$t", tokenHash), ("$u", userId), ("$ip", ip), ("$ua", userAgent), ("$now", now));
    }

    /// <summary>
    /// The session for a cookie, marking it seen. Null if it's unknown, idle too long (it's removed then)
    /// or its user has been disabled.
    /// </summary>
    public AdminSession? UseSession(string tokenHash)
    {
        var now = DateTimeOffset.UtcNow;
        database.Execute("DELETE FROM admin_sessions WHERE last_seen_at < $cutoff", ("$cutoff", now - IdleTimeout));
        return database.QueryOne("""
            UPDATE admin_sessions SET last_seen_at = $now
            WHERE token_hash = $t AND user_id IN (SELECT id FROM users WHERE disabled_at IS NULL)
            RETURNING id, user_id
            """, r => new AdminSession(r.GetInt64(0), r.GetInt64(1)), ("$now", now), ("$t", tokenHash));
    }

    public bool EndSession(string tokenHash) =>
        database.Execute("DELETE FROM admin_sessions WHERE token_hash = $t", ("$t", tokenHash)) == 1;

    /// <summary>Ends every session of the user except <paramref name="keepTokenHash"/>'s.</summary>
    public void EndSessionsFor(long userId, string? keepTokenHash = null) =>
        database.Execute("DELETE FROM admin_sessions WHERE user_id = $u AND token_hash IS NOT $keep", ("$u", userId), ("$keep", keepTokenHash));

    private static AdminGrant MapGrant(SqliteDataReader r) =>
        new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4),
            r.GetString(5), r.GetString(6), r.GetTime(7));
}
