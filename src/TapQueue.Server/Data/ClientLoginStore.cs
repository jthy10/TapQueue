namespace TapQueue.Server.Data;

public sealed record ClientLogin(long Id, long UserId);

/// <summary>
/// Remembered domain sign-ins (<see cref="Shared.Api.ClientSignIn.Domain"/>). A tray app that signed in
/// with a password keeps the token and signs in with it instead, until the person signs out, an admin
/// signs the PC out, the user is disabled, or it goes unused for <see cref="UnusedLifetime"/>. Only the
/// token's hash is stored, never the password.
/// </summary>
public sealed class ClientLoginStore(Database database)
{
    /// <summary>A remembered sign-in no tray has used or stayed signed in with for this long is forgotten.</summary>
    public static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(30);

    public long Create(string tokenHash, long userId, string? windowsUser, string? hostname)
    {
        var now = DateTimeOffset.UtcNow;
        return (long)database.Scalar("""
            INSERT INTO client_logins (token_hash, user_id, windows_user, hostname, created_at, last_used_at)
            VALUES ($t, $u, $w, $h, $now, $now) RETURNING id
            """, ("$t", tokenHash), ("$u", userId), ("$w", windowsUser), ("$h", hostname), ("$now", now))!;
    }

    /// <summary>The login, marking it used; null if it was forgotten.</summary>
    public ClientLogin? Use(string tokenHash) =>
        database.QueryOne("UPDATE client_logins SET last_used_at = $now WHERE token_hash = $t RETURNING id, user_id",
            r => new ClientLogin(r.GetInt64(0), r.GetInt64(1)), ("$now", DateTimeOffset.UtcNow), ("$t", tokenHash));

    public bool Forget(string tokenHash) => database.Execute("DELETE FROM client_logins WHERE token_hash = $t", ("$t", tokenHash)) == 1;

    /// <summary>
    /// Forgets logins last used before <paramref name="cutoff"/>, unless a session still comes from one (a
    /// tray that stays signed in for weeks doesn't use its login again). Returns how many.
    /// </summary>
    public int DeleteUnusedSince(DateTimeOffset cutoff) =>
        database.Execute("""
            DELETE FROM client_logins
            WHERE last_used_at < $cutoff AND id NOT IN (SELECT login_id FROM sessions WHERE login_id IS NOT NULL)
            """, ("$cutoff", cutoff));

    public bool Forget(long id) => database.Execute("DELETE FROM client_logins WHERE id = $id", ("$id", id)) == 1;
}
