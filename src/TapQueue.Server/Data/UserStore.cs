using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="Quota">Their own page limit, which overrides their groups'; null if they have none.</param>
public sealed record UserRecord(long Id, string Username, string DisplayName, string? TokenHash, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string Source,
    QuotaDto? Quota = null)
{
    public bool Disabled => DisabledAt is not null;

    public UserDto ToDto() => new(Id, Username, DisplayName);

    public UserAdminDto ToAdminDto(IReadOnlyList<string> groups) => new(Id, Username, DisplayName, CreatedAt, DisabledAt, groups, Source, Quota);
}

public sealed class UserStore(Database database)
{
    private const string Columns = "id, username, display_name, token_hash, created_at, disabled_at, source, quota_pages, quota_period";

    public UserRecord? FindByUsername(string username) =>
        database.QueryOne($"SELECT {Columns} FROM users WHERE username = $u", Map, ("$u", username));

    public UserRecord? FindById(long id) =>
        database.QueryOne($"SELECT {Columns} FROM users WHERE id = $id", Map, ("$id", id));

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

    /// <summary>Disabling also signs the user out everywhere, so their PCs stop matching new jobs to them.</summary>
    public UserRecord? SetDisabled(long userId, bool disabled)
    {
        if (disabled)
            database.Execute("DELETE FROM sessions WHERE user_id = $id", ("$id", userId));
        return database.QueryOne($"""
            UPDATE users SET disabled_at = CASE WHEN $disabled THEN COALESCE(disabled_at, $now) END WHERE id = $id
            RETURNING {Columns}
            """, Map, ("$disabled", disabled), ("$now", DateTimeOffset.UtcNow), ("$id", userId));
    }

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
            r.IsDBNull(7) ? null : new QuotaDto(r.GetInt32(7), r.GetString(8)));
}
