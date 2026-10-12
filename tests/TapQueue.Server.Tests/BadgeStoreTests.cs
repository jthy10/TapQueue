using TapQueue.Server.Data;

namespace TapQueue.Server.Tests;

public sealed class BadgeStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly UserStore _users;
    private readonly BadgeStore _badges;
    private readonly Database _db;

    public BadgeStoreTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        _users = new UserStore(db);
        _db = db;
        _badges = new BadgeStore(db, new BadgeKey(Path.Combine(_dir, "secrets", "badge.key")));
    }

    [Theory]
    [InlineData(" 04a1b2c3 ", "04A1B2C3")]
    [InlineData("0012 3456\r", "00123456")]
    [InlineData("", "")]
    public void NormalizesCardNumbers(string input, string expected) =>
        Assert.Equal(expected, BadgeStore.Normalize(input));

    [Fact]
    public void FindsTheOwnerRegardlessOfCase()
    {
        var alice = _users.Create("alice", "Alice", null);
        var badge = _badges.Add(alice.Id, "04a1b2c3");

        Assert.Equal("…B2C3", badge.CardHint);
        var used = _badges.Use("04A1B2C3");
        Assert.Equal(alice.Id, used?.UserId);
        Assert.NotNull(used?.LastUsedAt);
        Assert.Null(_badges.Use("99999999"));
        Assert.Null(_badges.Use("  "));
    }

    [Fact]
    public void CardNumbersAreHashedWithTheServersKeyNotJustSha256()
    {
        var alice = _users.Create("alice", "Alice", null);
        _badges.Add(alice.Id, "1234");

        var stored = (string)_db.Scalar("SELECT card_hash FROM badges")!;

        Assert.StartsWith(BadgeKey.Prefix, stored);
        Assert.DoesNotContain(Tokens.Hash("1234"), stored);
        // Another server's key gives another hash, so one database's hashes say nothing about another's.
        Assert.NotEqual(stored, new BadgeKey(Path.Combine(_dir, "other", "badge.key")).Hash("1234"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "secrets", "badge.key")));
    }

    [Fact]
    public void CardsStoredBeforeTheKeyAreConvertedAndStillWork()
    {
        var alice = _users.Create("alice", "Alice", null);
        // As servers before 0.16 stored them.
        _db.Execute("INSERT INTO badges (card_hash, card_hint, user_id, created_at) VALUES ($h, '…B2C3', $u, $now)",
            ("$h", Tokens.Hash("04A1B2C3")), ("$u", alice.Id), ("$now", DateTimeOffset.UtcNow));

        var badges = new BadgeStore(_db, new BadgeKey(Path.Combine(_dir, "secrets", "badge.key")));

        Assert.StartsWith(BadgeKey.Prefix, (string)_db.Scalar("SELECT card_hash FROM badges")!);
        Assert.Equal(alice.Id, badges.Use("04a1b2c3")?.UserId);
        Assert.False(badges.KeyMismatch);
    }

    [Fact]
    public void ADatabaseWithoutItsKeyIsNoticed()
    {
        var alice = _users.Create("alice", "Alice", null);
        _badges.Add(alice.Id, "1234");

        var badges = new BadgeStore(_db, new BadgeKey(Path.Combine(_dir, "another-server", "badge.key")));

        Assert.True(badges.KeyMismatch);
        Assert.Null(badges.Use("1234"));
    }

    [Fact]
    public void RemovingABadgeUnlinksIt()
    {
        var bob = _users.Create("bob", "Bob", null);
        _badges.Add(bob.Id, "1234");
        Assert.Single(_badges.List(bob.Id));
        Assert.True(_badges.Delete(_badges.List(bob.Id)[0].Id));
        Assert.Empty(_badges.List());
    }

    [Fact]
    public void MovesACardToSomeoneElse()
    {
        var alice = _users.Create("alice", "Alice", null);
        var bob = _users.Create("bob", "Bob", null);
        var badge = _badges.Add(alice.Id, "04a1b2c3", "blue fob");

        var moved = _badges.Update(badge.Id, bob.Id, "spare");

        Assert.Equal("bob", moved?.Username);
        Assert.Equal("spare", moved?.Label);
        Assert.Equal(bob.Id, _badges.Use("04a1b2c3")?.UserId);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
