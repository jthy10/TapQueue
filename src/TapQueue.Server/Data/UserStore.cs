using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record UserRecord(long Id, string Username, string DisplayName, string? TokenHash)
{
    public UserDto ToDto() => new(Id, Username, DisplayName);
}

public sealed class UserStore(Database database)
{
    public UserRecord? FindByUsername(string username)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, username, display_name, token_hash FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        return ReadOne(cmd);
    }

    public UserRecord? FindById(long id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, username, display_name, token_hash FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadOne(cmd);
    }

    public List<UserRecord> List()
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, username, display_name, token_hash FROM users ORDER BY username";
        using var reader = cmd.ExecuteReader();
        var users = new List<UserRecord>();
        while (reader.Read())
            users.Add(Map(reader));
        return users;
    }

    public UserRecord Create(string username, string displayName, string? tokenHash)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, display_name, token_hash, created_at)
            VALUES ($u, $d, $t, $now)
            RETURNING id, username, display_name, token_hash
            """;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$d", displayName);
        cmd.Parameters.AddWithValue("$t", (object?)tokenHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return ReadOne(cmd)!;
    }

    public void SetTokenHash(long userId, string tokenHash)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE users SET token_hash = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", tokenHash);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    private static UserRecord? ReadOne(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static UserRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
}
