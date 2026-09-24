using Microsoft.Extensions.Logging.Abstractions;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.ActiveDirectory;

public sealed class DirectorySyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly FakeDirectory _ad = new();
    private readonly DirectoryStore _store;
    private readonly UserStore _users;
    private readonly GroupStore _groups;
    private readonly BadgeStore _badges;
    private readonly DirectorySync _sync;
    private readonly DirectoryEntry _staffOu;

    public DirectorySyncTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        _store = new DirectoryStore(db);
        _users = new UserStore(db);
        _groups = new GroupStore(db);
        _badges = new BadgeStore(db);
        _sync = new DirectorySync(_store, _ad, _users, _groups, _badges, new EventLog(db), NullLogger<DirectorySync>.Instance);
        _staffOu = _ad.Ou("Staff");
        _store.SaveConfig(new DirectoryConfig(Enabled: true, Host: "dc01", BindDn: "svc", Password: "x"));
    }

    private void AddScope(string kind, DirectoryEntry entry) => _store.AddScope(kind, entry.Guid, entry.Dn, entry.Name);

    private DirectorySyncResultDto Sync(bool dryRun = false, bool force = false) => _sync.Run(DirectorySync.AdminTrigger, dryRun, force);

    private UserRecord User(string username) => _users.FindByUsername(username)!;

    [Fact]
    public void CreatesUsersFromTheScopeAndIsQuietTheSecondTime()
    {
        _ad.User("alice", _staffOu.Dn, "Alice Anders");
        _ad.User("bob", _staffOu.Dn, disabled: true);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);

        var first = Sync();
        var second = Sync();

        Assert.Equal(DirectoryRunOutcome.Succeeded, first.Outcome);
        Assert.Equal("Alice Anders", User("alice").DisplayName);
        Assert.Equal(UserSource.ActiveDirectory, User("alice").Source);
        Assert.Equal((DisabledBy.Directory, DirectoryState.Disabled), (User("bob").DisabledBy, User("bob").DirectoryState));
        Assert.Empty(second.Changes);
        Assert.Equal("Everything is up to date.", second.Summary);
    }

    [Fact]
    public void PreviewChangesNothing()
    {
        _ad.User("alice", _staffOu.Dn);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);

        var preview = Sync(dryRun: true);

        Assert.False(preview.Applied);
        Assert.Equal(DirectoryChangeAction.CreateUser, preview.Changes.Single().Action);
        Assert.Null(_users.FindByUsername("alice"));
    }

    [Fact]
    public void LinksALocalUserWithTheSameUsernameKeepingTheirCards()
    {
        var local = _users.Create("alice", "alice", null);
        _badges.Add(local.Id, "CARD-1");
        _ad.User("alice", _staffOu.Dn, "Alice Anders");
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);

        var result = Sync();

        Assert.Contains(result.Changes, c => c.Action == DirectoryChangeAction.LinkUser);
        Assert.Equal(local.Id, User("alice").Id);
        Assert.Equal(UserSource.ActiveDirectory, User("alice").Source);
        Assert.Equal("Alice Anders", User("alice").DisplayName);
        Assert.Single(_badges.List(local.Id));
    }

    [Fact]
    public void FollowsRenamesByObjectGuid()
    {
        var alice = _ad.User("alice", _staffOu.Dn);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();
        var id = User("alice").Id;

        _ad.Replace(alice with { Dn = $"CN=alice.smith,{_staffOu.Dn}", SamAccountName = "alice.smith", DisplayName = "Alice Smith" });
        Sync();

        Assert.Null(_users.FindByUsername("alice"));
        Assert.Equal(id, User("alice.smith").Id);
        Assert.Equal("Alice Smith", User("alice.smith").DisplayName);
    }

    [Fact]
    public void DisablesAndReEnablesWithAdButLeavesAnAdminsDisableAlone()
    {
        var alice = _ad.User("alice", _staffOu.Dn);
        var bob = _ad.User("bob", _staffOu.Dn);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();
        _users.SetDisabled(User("bob").Id, true, DisabledBy.Admin);

        _ad.Replace(alice with { UserAccountControl = 0x202 });
        Sync();
        Assert.Equal((DisabledBy.Directory, DirectoryState.Disabled), (User("alice").DisabledBy, User("alice").DirectoryState));

        _ad.Replace(alice with { UserAccountControl = 0x200, AccountExpires = DateTimeOffset.UtcNow.AddDays(-1).ToFileTime() });
        Sync();
        Assert.Equal(DirectoryState.Expired, User("alice").DirectoryState);

        _ad.Replace(alice with { AccountExpires = 0 });
        Sync();
        Assert.False(User("alice").Disabled);
        Assert.Equal(DisabledBy.Admin, User("bob").DisabledBy);
    }

    [Fact]
    public void UsersWhoLeaveTheScopeAreDisabledNotDeleted()
    {
        var alice = _ad.User("alice", _staffOu.Dn);
        _ad.User("bob", _staffOu.Dn);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();

        _ad.Remove(alice);
        Sync();

        Assert.Equal((DisabledBy.Directory, DirectoryState.Missing), (User("alice").DisabledBy, User("alice").DirectoryState));
        Assert.False(User("bob").Disabled);
    }

    [Fact]
    public void StopsInsteadOfDisablingTooManyUsersUnlessForced()
    {
        var people = Enumerable.Range(1, 10).Select(i => _ad.User($"user{i}", _staffOu.Dn)).ToList();
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();
        people.Take(6).ToList().ForEach(_ad.Remove);

        var stopped = Sync();
        Assert.Equal(DirectoryRunOutcome.Stopped, stopped.Outcome);
        Assert.False(User("user1").Disabled);

        var forced = Sync(force: true);
        Assert.Equal(DirectoryRunOutcome.Succeeded, forced.Outcome);
        Assert.True(User("user1").Disabled);
    }

    [Fact]
    public void AnUnreachableDirectoryChangesNothing()
    {
        _ad.User("alice", _staffOu.Dn);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();
        _ad.ConnectError = "dc01 refused the bind account's name or password.";

        var failed = Sync();

        Assert.Equal(DirectoryRunOutcome.Failed, failed.Outcome);
        Assert.Contains("refused", failed.Error);
        Assert.False(User("alice").Disabled);
        Assert.Equal(DirectoryRunOutcome.Failed, _store.Runs().First().Outcome);
    }

    [Fact]
    public void ScopeGroupsBecomeGroupsWithNestedMembersAndKeepTheirPermissions()
    {
        var alice = _ad.User("alice", _staffOu.Dn);
        var bob = _ad.User("bob", _staffOu.Dn);
        var carol = _ad.User("carol", _ad.Ou("Other").Dn);
        var it = _ad.Group("IT-Dept", _staffOu.Dn, bob);
        var admins = _ad.Group("Print Admins", _staffOu.Dn, alice, it);
        AddScope(DirectoryScopeKind.Group, admins);
        Sync();

        var group = _groups.List().Single();
        Assert.Equal(("print-admins", "Print Admins", UserSource.ActiveDirectory), (group.Id, group.Name, group.Source));
        Assert.Equal([("alice", null), ("bob", "IT-Dept")], _groups.MembersWithVia(group.Id));
        _groups.SetQueues(group.Id, false, []);

        _ad.AddMember(it, carol);
        var result = Sync();

        Assert.Contains(result.Changes, c => c.Action == DirectoryChangeAction.Members && c.Description.Contains("added carol"));
        Assert.Equal(3, _groups.Get(group.Id)!.MemberCount);
        Assert.False(_groups.Get(group.Id)!.AllQueues);
    }

    [Fact]
    public void GroupsRemovedFromTheScopeAreRemoved()
    {
        var alice = _ad.User("alice", _staffOu.Dn);
        var staff = _ad.Group("Staff", _staffOu.Dn, alice);
        AddScope(DirectoryScopeKind.Group, staff);
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);
        Sync();

        _store.RemoveScope(_store.Scope().Single(s => s.Kind == DirectoryScopeKind.Group).Id);
        Sync();

        Assert.Empty(_groups.List());
        Assert.False(User("alice").Disabled);
    }

    [Fact]
    public void AdOwnsTheCardsOfUsersWithABadgeValue()
    {
        _store.SaveConfig(_store.Config() with { BadgeAttribute = "employeeNumber" });
        _ad.User("alice", _staffOu.Dn, badge: "1001");
        _ad.User("bob", _staffOu.Dn);
        var bobLocal = _users.Create("bob", "bob", null);
        var carol = _users.Create("carol", "carol", null);
        _badges.Add(bobLocal.Id, "2002");
        _badges.Add(carol.Id, "1001");
        AddScope(DirectoryScopeKind.OrganizationalUnit, _staffOu);

        Sync();

        var aliceCard = _badges.FindByCard("1001")!;
        Assert.Equal(("alice", UserSource.ActiveDirectory), (aliceCard.Username, aliceCard.Source));
        Assert.Equal("bob", _badges.FindByCard("2002")!.Username); // no value in AD: hand-enrolled card stays
    }

    [Fact]
    public void GroupIdsArePlainAndUnique()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "staff" };
        Assert.Equal("staff-2", DirectorySync.UniqueId("Staff", taken));
        Assert.Equal("print-admins--it", DirectorySync.UniqueId("Print Admins (IT)", taken));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
