using TapQueue.Server.ActiveDirectory;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.ActiveDirectory;

public sealed class DirectoryReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);

    private static DirectoryScopeItem Scope(string kind, DirectoryEntry entry) => new(0, kind, entry.Guid, entry.Dn, entry.Name, Now);

    [Fact]
    public void ReadsUsersUnderAnOuIncludingChildOus()
    {
        var ad = new FakeDirectory();
        var staff = ad.Ou("Staff");
        var admins = ad.Ou("Admins", staff.Dn);
        ad.User("alice", staff.Dn);
        ad.User("bob", admins.Dn);
        ad.User("carol", ad.Ou("Elsewhere").Dn);

        var snapshot = new DirectoryReader(ad).Read([Scope(DirectoryScopeKind.OrganizationalUnit, staff)], Now);

        Assert.Equal(["alice", "bob"], snapshot.Users.Select(u => u.Username));
    }

    [Fact]
    public void FlattensNestedGroupsAndRecordsWhichSubgroupMembersCameThrough()
    {
        var ad = new FakeDirectory();
        var ou = ad.Ou("Users");
        var alice = ad.User("alice", ou.Dn);
        var bob = ad.User("bob", ou.Dn);
        var carol = ad.User("carol", ou.Dn);
        var helpdesk = ad.Group("Helpdesk", ou.Dn, carol);
        var it = ad.Group("IT-Dept", ou.Dn, bob, helpdesk);
        var admins = ad.Group("Print-Admins", ou.Dn, alice, it);

        var group = new DirectoryReader(ad).Read([Scope(DirectoryScopeKind.Group, admins)], Now).Groups.Single();

        Assert.Equal(
            [(alice.Guid, null), (bob.Guid, "IT-Dept"), (carol.Guid, "IT-Dept")],
            group.Members.ToList());
    }

    [Fact]
    public void DirectMembershipWinsOverNestedAndCyclesAreReadOnce()
    {
        var ad = new FakeDirectory();
        var ou = ad.Ou("Users");
        var alice = ad.User("alice", ou.Dn);
        var a = ad.Group("A", ou.Dn);
        var b = ad.Group("B", ou.Dn, alice, a);
        ad.AddMember(a, b);
        ad.AddMember(a, alice);

        var group = new DirectoryReader(ad).Read([Scope(DirectoryScopeKind.Group, a)], Now).Groups.Single();

        Assert.Equal([(alice.Guid, null)], group.Members.ToList());
        Assert.Equal(2, ad.MemberReads);
    }

    [Fact]
    public void ReadsDisabledExpiredAndBadge()
    {
        var ad = new FakeDirectory();
        var ou = ad.Ou("Users");
        ad.User("alice", ou.Dn, badge: " 12345 ");
        ad.User("bob", ou.Dn, disabled: true);
        ad.User("carol", ou.Dn, expires: Now.AddDays(-1));
        ad.User("dave", ou.Dn, expires: Now.AddDays(1));

        var users = new DirectoryReader(ad).Read([Scope(DirectoryScopeKind.OrganizationalUnit, ou)], Now).Users.ToDictionary(u => u.Username);

        Assert.Equal("12345", users["alice"].Badge);
        Assert.True(users["bob"].Disabled);
        Assert.True(users["carol"].Expired);
        Assert.False(users["dave"].Expired);
    }

    [Fact]
    public void AScopeItemThatIsGoneFailsTheWholeRead()
    {
        var ad = new FakeDirectory();
        var ou = ad.Ou("Users");
        ad.User("alice", ou.Dn);
        var gone = ad.Ou("Gone");
        ad.Remove(gone);

        var ex = Assert.Throws<DirectoryException>(() => new DirectoryReader(ad).Read(
            [Scope(DirectoryScopeKind.OrganizationalUnit, ou), Scope(DirectoryScopeKind.OrganizationalUnit, gone)], Now));

        Assert.Contains("no longer in Active Directory", ex.Message);
    }

    [Fact]
    public void SingleUsersAndOverlappingScopesGiveEachUserOnce()
    {
        var ad = new FakeDirectory();
        var ou = ad.Ou("Users");
        var alice = ad.User("alice", ou.Dn);
        var group = ad.Group("Staff", ou.Dn, alice);

        var snapshot = new DirectoryReader(ad).Read(
            [Scope(DirectoryScopeKind.OrganizationalUnit, ou), Scope(DirectoryScopeKind.Group, group), Scope(DirectoryScopeKind.User, alice)], Now);

        Assert.Equal(["alice"], snapshot.Users.Select(u => u.Username));
    }

    [Theory]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("(x)", "\\28x\\29")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("Zoë", "Zoë")]
    public void EscapesLdapFilterValues(string value, string escaped) =>
        Assert.Equal(escaped, LdapDirectorySource.Escape(value));
}
