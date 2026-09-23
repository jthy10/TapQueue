using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record UserRecord(long Id, string Username, string DisplayName, string? TokenHash)
{
    public UserDto ToDto() => new(Id, Username, DisplayName);
}

public sealed class UserStore(Database database)
{
    private const string Columns = "id, username, display_name, token_hash";

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

    public void SetTokenHash(long userId, string tokenHash) =>
        database.Execute("UPDATE users SET token_hash = $t WHERE id = $id", ("$t", tokenHash), ("$id", userId));

    private static UserRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3));
}
