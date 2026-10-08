using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Tests;

public sealed class JobOwnerResolverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly UserStore _users;
    private readonly SessionStore _sessions;
    private readonly ServerSettings _settings;
    private readonly DirectoryStore _directory;
    private readonly ServerConfig _config = new();

    public JobOwnerResolverTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        _users = new UserStore(db);
        _sessions = new SessionStore(db);
        _settings = new ServerSettings(db, _config);
        _directory = new DirectoryStore(db);
    }

    private JobOwnerResolver Resolver => new(_sessions, _users, _config, _settings, _directory);

    [Theory]
    [InlineData(@"CORP\jake", "jake")]
    [InlineData("jake@corp.local", "jake")]
    [InlineData("  jake ", "jake")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesWindowsUsernames(string? input, string? expected) =>
        Assert.Equal(expected, JobOwnerResolver.NormalizeWindowsUser(input));

    [Fact]
    public void SignedInClientOnSameMachineOwnsTheJob()
    {
        var alice = _users.Create("alice", "Alice", null);
        _sessions.Create("hash1", alice.Id, "alice", "PC1", "10.0.0.50");

        // The IPP username doesn't matter when exactly one user is signed in on that machine.
        Assert.Equal(alice.Id, Resolver.Resolve("10.0.0.50", "someone-else").UserId);
    }

    [Fact]
    public void SharedMachinePicksSessionByWindowsUser()
    {
        var alice = _users.Create("alice", "Alice", null);
        var bob = _users.Create("bob", "Bob", null);
        _sessions.Create("h1", alice.Id, "alice", "TS1", "10.0.0.60");
        _sessions.Create("h2", bob.Id, "bob", "TS1", "10.0.0.60");

        Assert.Equal(bob.Id, Resolver.Resolve("10.0.0.60", @"CORP\bob").UserId);
        Assert.Null(Resolver.Resolve("10.0.0.60", "mallory").UserId);
    }

    [Fact]
    public void TokenModeDoesNotTrustTheIppUsername()
    {
        _config.Auth.Mode = "token";
        _users.Create("jake", "Jake", null);
        var (userId, hint) = Resolver.Resolve("10.0.0.70", "jake");
        Assert.Null(userId);
        Assert.Equal("jake", hint);
    }

    [Fact]
    public void DevModeFallsBackToTheIppUsername()
    {
        _config.Auth.Mode = "dev";
        var jake = _users.Create("jake", "Jake", null);
        Assert.Equal(jake.Id, Resolver.Resolve("10.0.0.70", "JAKE").UserId);
    }

    [Fact]
    public void DomainSignInHasNoIppUsernameFallback()
    {
        _config.Auth.Mode = "dev";
        _directory.SaveConfig(new DirectoryConfig(ClientSignIn: Shared.Api.ClientSignIn.Domain));
        _users.Create("jake", "Jake", null);
        Assert.Null(Resolver.Resolve("10.0.0.70", "jake").UserId);
    }

    [Fact]
    public void KeyedJobsGoToTheSessionThePcVouchedForWithThatPcUser()
    {
        var alice = _users.Create("alice", "Alice", null);
        var bob = _users.Create("bob", "Bob", null);
        _sessions.Create("h1", alice.Id, "alice", "TS1", "10.0.0.60");
        _sessions.Create("h2", bob.Id, "bob", "TS1", "10.0.0.60");
        _sessions.Verify("h1", "TS1", @"CORP\alice", _settings.SessionTimeout);
        _sessions.Verify("h2", "TS1", @"CORP\bob", _settings.SessionTimeout);

        Assert.Equal(bob.Id, Resolver.Resolve("192.0.2.1", "bob", "TS1").UserId);
        Assert.Equal(alice.Id, Resolver.Resolve("192.0.2.1", "alice@corp.local", "ts1").UserId);
        Assert.Null(Resolver.Resolve("192.0.2.1", "mallory", "TS1").UserId);
        Assert.Null(Resolver.Resolve("10.0.0.60", "bob", "OTHER-PC").UserId);
    }

    [Fact]
    public void KeyedJobsNeedTheirPcUserSignedInEvenWithOneSessionThere()
    {
        var alice = _users.Create("alice", "Alice", null);
        _sessions.Create("h1", alice.Id, "alice", "PC1", "10.0.0.50");
        _sessions.Verify("h1", "PC1", "alice", _settings.SessionTimeout);

        Assert.Null(Resolver.Resolve("10.0.0.50", "someone-else", "PC1").UserId);
        // Only a job with no user name at all goes to the one person there.
        Assert.Equal(alice.Id, Resolver.Resolve("10.0.0.50", null, "PC1").UserId);
    }

    [Fact]
    public void KeyedJobsIgnoreSessionsTheServiceDidntVouchFor()
    {
        var alice = _users.Create("alice", "Alice", null);
        _sessions.Create("h1", alice.Id, "alice", "PC1", "10.0.0.50");
        Assert.Null(Resolver.Resolve("10.0.0.50", "alice", "PC1").UserId);
    }

    [Fact]
    public void KeylessJobsArentMatchedByAddressWhenThatIsOff()
    {
        var alice = _users.Create("alice", "Alice", null);
        _sessions.Create("hash1", alice.Id, "alice", "PC1", "10.0.0.50");
        _settings.Set(ServerSettings.AddressMatchingKey, Shared.Api.AddressMatching.Off);
        Assert.Null(Resolver.Resolve("10.0.0.50", "alice").UserId);
    }

    [Fact]
    public void ForgettingAWorkstationDropsWhatItVouchedFor()
    {
        var workstations = new WorkstationStore(new Database(Path.Combine(_dir, "test.db")));
        var alice = _users.Create("alice", "Alice", null);
        workstations.CheckIn("PC1", "10.0.0.50", Shared.ClientPlatform.Windows, new Shared.Api.ClientSetupRequest("PC1", null, null, null));
        _sessions.Create("h1", alice.Id, "alice", "PC1", "10.0.0.50");
        _sessions.Verify("h1", "PC1", "alice", _settings.SessionTimeout);

        Assert.True(workstations.Delete("pc1"));
        Assert.Null(Resolver.Resolve("10.0.0.50", "alice", "PC1").UserId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
