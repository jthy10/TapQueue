namespace TapQueue.Server.Data;

public sealed record ClientLogin(long Id, long UserId);

/// <summary>
/// Remembered domain sign-ins (<see cref="Shared.Api.ClientSignIn.Domain"/>). A tray app that signed in
/// with a password keeps the token and signs in with it instead, until the person signs out, an admin
/// signs the PC out, or the user is disabled. Only the token's hash is stored, never the password.
/// </summary>
public sealed class ClientLoginStore(Database database)
{
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

    public bool Forget(long id) => database.Execute("DELETE FROM client_logins WHERE id = $id", ("$id", id)) == 1;
}
