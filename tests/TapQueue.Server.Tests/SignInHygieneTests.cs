using TapQueue.Server.Api;
using TapQueue.Server.Data;

namespace TapQueue.Server.Tests;

/// <summary>Remembered sign-ins and the sign-in throttle don't keep what nobody uses.</summary>
public sealed class SignInHygieneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-signin").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RememberedSignInsNobodyUsesAreForgotten()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        var alice = new UserStore(db).Create("alice", "Alice", null);
        var logins = new ClientLoginStore(db);
        var stale = logins.Create("stale", alice.Id, "alice", "PC1");
        var stillSignedIn = logins.Create("signed-in", alice.Id, "alice", "PC2");
        logins.Create("recent", alice.Id, "alice", "PC3");
        var longAgo = DateTimeOffset.UtcNow - ClientLoginStore.UnusedLifetime - TimeSpan.FromDays(1);
        db.Execute("UPDATE client_logins SET last_used_at = $t WHERE id IN ($a, $b)", ("$t", longAgo), ("$a", stale), ("$b", stillSignedIn));
        new SessionStore(db).Create("session", alice.Id, "alice", "PC2", "192.0.2.2", loginId: stillSignedIn);

        var forgotten = logins.DeleteUnusedSince(DateTimeOffset.UtcNow - ClientLoginStore.UnusedLifetime);

        Assert.Equal(1, forgotten);
        Assert.Null(logins.Use("stale"));
        Assert.NotNull(logins.Use("signed-in"));
        Assert.NotNull(logins.Use("recent"));
    }

    [Fact]
    public void ThrottleSweepsOutNamesWithNoRecentFailures()
    {
        var throttle = new SignInThrottle();
        for (var i = 0; i < 500; i++)
            throttle.Failed($"guess{i}", "192.0.2.9");
        Assert.Equal(501, throttle.Tracked);

        throttle.Sweep(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(16), force: true);
        Assert.Equal(0, throttle.Tracked);

        // Still counts after a sweep.
        for (var i = 0; i < 5; i++)
            throttle.Failed("alice", "192.0.2.9");
        Assert.True(throttle.IsLocked("alice", "198.51.100.1"));
    }
}
