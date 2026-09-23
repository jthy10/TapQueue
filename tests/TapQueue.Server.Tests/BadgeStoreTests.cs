using TapQueue.Server.Data;

namespace TapQueue.Server.Tests;

public sealed class BadgeStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly UserStore _users;
    private readonly BadgeStore _badges;

    public BadgeStoreTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        _users = new UserStore(db);
        _badges = new BadgeStore(db);
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
    public void RemovingABadgeUnlinksIt()
    {
        var bob = _users.Create("bob", "Bob", null);
        _badges.Add(bob.Id, "1234");
        Assert.Single(_badges.List(bob.Id));
        Assert.True(_badges.Delete(_badges.List(bob.Id)[0].Id));
        Assert.Empty(_badges.List());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
