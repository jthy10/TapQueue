using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record SessionRecord(long Id, long UserId, string? WindowsUser, string? Hostname, string RemoteIp);

/// <summary>
/// A session is a signed-in user client. Sessions are how the server knows which TapQueue user
/// is sitting at the machine a print job came from.
/// </summary>
public sealed class SessionStore(Database database)
{
    private const string Columns = "id, user_id, windows_user, hostname, remote_ip";

    public void Create(string tokenHash, long userId, string? windowsUser, string? hostname, string remoteIp, string? clientVersion = null) =>
        database.Execute("""
            INSERT INTO sessions (token_hash, user_id, windows_user, hostname, remote_ip, client_version, created_at, last_seen_at)
            VALUES ($t, $u, $w, $h, $ip, $v, $now, $now)
            """,
            ("$t", tokenHash), ("$u", userId), ("$w", windowsUser), ("$h", hostname), ("$ip", remoteIp), ("$v", clientVersion),
            ("$now", DateTimeOffset.UtcNow));

    /// <summary>Looks up a live session by token and marks it as seen from <paramref name="remoteIp"/>.</summary>
    public SessionRecord? Touch(string tokenHash, string remoteIp, TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        return database.QueryOne($"""
            UPDATE sessions SET last_seen_at = $now, remote_ip = $ip
            WHERE token_hash = $t AND last_seen_at >= $cutoff AND signed_out_at IS NULL
            RETURNING {Columns}
            """, Map, ("$now", now), ("$ip", remoteIp), ("$t", tokenHash), ("$cutoff", now - timeout));
    }

    /// <summary>True if an admin signed this session out, as opposed to it lapsing.</summary>
    public bool WasSignedOut(string tokenHash) =>
        database.Scalar("SELECT 1 FROM sessions WHERE token_hash = $t AND signed_out_at IS NOT NULL", ("$t", tokenHash)) is not null;

    /// <summary>
    /// Ends a session. Jobs from that PC stop being matched to its user, and the client is told it was
    /// signed out rather than signing in again by itself. Returns null if there's no such live session.
    /// </summary>
    public SessionRecord? SignOut(long id) =>
        database.QueryOne($"UPDATE sessions SET signed_out_at = $now WHERE id = $id AND signed_out_at IS NULL RETURNING {Columns}",
            Map, ("$now", DateTimeOffset.UtcNow), ("$id", id));

    public List<SessionRecord> ActiveForIp(string remoteIp, TimeSpan timeout) =>
        database.Query($"SELECT {Columns} FROM sessions WHERE remote_ip = $ip AND last_seen_at >= $cutoff AND signed_out_at IS NULL", Map,
            ("$ip", remoteIp), ("$cutoff", DateTimeOffset.UtcNow - timeout));

    /// <summary>Live sessions, newest first, for `tapqueue-admin clients`.</summary>
    public List<ClientSessionDto> ListActive(TimeSpan timeout) =>
        database.Query("""
            SELECT s.id, u.username, s.hostname, s.windows_user, s.client_version, s.remote_ip, s.last_seen_at
            FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.last_seen_at >= $cutoff AND s.signed_out_at IS NULL
            ORDER BY s.last_seen_at DESC
            """, r => new ClientSessionDto(r.GetInt64(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetString(5), r.GetTime(6)),
            ("$cutoff", DateTimeOffset.UtcNow - timeout));

    public int DeleteExpired(TimeSpan timeout) =>
        database.Execute("DELETE FROM sessions WHERE last_seen_at < $cutoff", ("$cutoff", DateTimeOffset.UtcNow - timeout));

    private static SessionRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetString(4));
}
