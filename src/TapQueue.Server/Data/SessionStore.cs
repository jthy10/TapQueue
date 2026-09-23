namespace TapQueue.Server.Data;

public sealed record SessionRecord(long Id, long UserId, string? WindowsUser, string? Hostname, string RemoteIp);

/// <summary>
/// A session is a signed-in user client. Sessions are how the server knows which TapQueue user
/// is sitting at the machine a print job came from.
/// </summary>
public sealed class SessionStore(Database database)
{
    public void Create(string tokenHash, long userId, string? windowsUser, string? hostname, string remoteIp)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (token_hash, user_id, windows_user, hostname, remote_ip, created_at, last_seen_at)
            VALUES ($t, $u, $w, $h, $ip, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$t", tokenHash);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$w", (object?)windowsUser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", remoteIp);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Looks up a live session by token and marks it as seen from <paramref name="remoteIp"/>.</summary>
    public SessionRecord? Touch(string tokenHash, string remoteIp, TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET last_seen_at = $now, remote_ip = $ip
            WHERE token_hash = $t AND last_seen_at >= $cutoff
            RETURNING id, user_id, windows_user, hostname, remote_ip
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$ip", remoteIp);
        cmd.Parameters.AddWithValue("$t", tokenHash);
        cmd.Parameters.AddWithValue("$cutoff", (now - timeout).ToString("O"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<SessionRecord> ActiveForIp(string remoteIp, TimeSpan timeout)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, windows_user, hostname, remote_ip FROM sessions
            WHERE remote_ip = $ip AND last_seen_at >= $cutoff
            """;
        cmd.Parameters.AddWithValue("$ip", remoteIp);
        cmd.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - timeout).ToString("O"));
        using var reader = cmd.ExecuteReader();
        var sessions = new List<SessionRecord>();
        while (reader.Read())
            sessions.Add(Map(reader));
        return sessions;
    }

    public int DeleteExpired(TimeSpan timeout)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE last_seen_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - timeout).ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    private static SessionRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
}
