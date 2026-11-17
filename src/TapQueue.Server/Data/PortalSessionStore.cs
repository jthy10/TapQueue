namespace TapQueue.Server.Data;

/// <summary>
/// Sign-ins to the web page people see their own jobs on (<see cref="Api.PortalApi"/>), as hashed
/// cookies. Kept apart from <see cref="SessionStore"/>'s tray sessions on purpose: a tray session
/// says "this person is at this PC" and decides whose jobs are whose, and someone looking at their
/// queue on a phone isn't at any PC.
/// </summary>
public sealed class PortalSessionStore(Database database)
{
    /// <summary>A phone that hasn't opened the page for this long signs in again.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(7);

    public void Start(string tokenHash, long userId, string ip, string? userAgent)
    {
        var now = DateTimeOffset.UtcNow;
        database.Execute("""
            INSERT INTO portal_sessions (token_hash, user_id, ip, user_agent, created_at, last_seen_at)
            VALUES ($t, $u, $ip, $ua, $now, $now)
            """, ("$t", tokenHash), ("$u", userId), ("$ip", ip), ("$ua", userAgent is { Length: > 300 } ? userAgent[..300] : userAgent), ("$now", now));
    }

    /// <summary>
    /// The user a cookie belongs to, marking the session seen. Null if it's unknown, idle too long (it's
    /// removed then) or its user has been disabled.
    /// </summary>
    public long? Use(string tokenHash, string ip)
    {
        var now = DateTimeOffset.UtcNow;
        database.Execute("DELETE FROM portal_sessions WHERE last_seen_at < $cutoff", ("$cutoff", now - IdleTimeout));
        return database.Scalar("""
            UPDATE portal_sessions SET last_seen_at = $now, ip = $ip
            WHERE token_hash = $t AND user_id IN (SELECT id FROM users WHERE disabled_at IS NULL)
            RETURNING user_id
            """, ("$now", now), ("$ip", ip), ("$t", tokenHash)) as long?;
    }

    public void End(string tokenHash) => database.Execute("DELETE FROM portal_sessions WHERE token_hash = $t", ("$t", tokenHash));

    /// <summary>Ends every session of the user except <paramref name="keepTokenHash"/>'s.</summary>
    public void EndFor(long userId, string? keepTokenHash = null) =>
        database.Execute("DELETE FROM portal_sessions WHERE user_id = $u AND token_hash IS NOT $keep", ("$u", userId), ("$keep", keepTokenHash));
}
