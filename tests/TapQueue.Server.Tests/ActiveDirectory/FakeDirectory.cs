using TapQueue.Server.ActiveDirectory;

namespace TapQueue.Server.Tests.ActiveDirectory;

/// <summary>An in-memory AD: OUs, users and groups by DN, with member lists.</summary>
public sealed class FakeDirectory : IDirectorySource, IDirectorySourceFactory
{
    public const string Root = "DC=lab,DC=example,DC=org";

    private readonly Dictionary<string, DirectoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, connecting fails with this, like an unreachable DC.</summary>
    public string? ConnectError { get; set; }

    public int MemberReads { get; private set; }

    public string DefaultNamingContext => Root;

    public DirectoryEntry Ou(string name, string? parent = null) =>
        Add(new DirectoryEntry($"OU={name},{parent ?? Root}", Guid.NewGuid().ToString(), EntryKind.OrganizationalUnit, name));

    public DirectoryEntry User(string username, string ouDn, string? displayName = null, bool disabled = false, DateTimeOffset? expires = null, string? badge = null) =>
        Add(new DirectoryEntry($"CN={username},{ouDn}", Guid.NewGuid().ToString(), EntryKind.User, username, username, displayName ?? username.ToUpperInvariant(),
            UserAccountControl: 0x200 | (disabled ? 0x2 : 0), AccountExpires: expires?.ToFileTime() ?? 0, Badge: badge));

    public DirectoryEntry Group(string name, string ouDn, params DirectoryEntry[] members)
    {
        var group = Add(new DirectoryEntry($"CN={name},{ouDn}", Guid.NewGuid().ToString(), EntryKind.Group, name, name, Description: $"{name} description"));
        _members[group.Dn] = members.Select(m => m.Dn).ToList();
        return group;
    }

    public void AddMember(DirectoryEntry group, DirectoryEntry member) => _members[group.Dn].Add(member.Dn);

    public void Replace(DirectoryEntry entry)
    {
        var old = _entries.Values.Single(e => e.Guid == entry.Guid);
        _entries.Remove(old.Dn);
        _entries[entry.Dn] = entry;
    }

    public void Remove(DirectoryEntry entry) => _entries.Remove(entry.Dn);

    public DirectoryEntry Get(string guid) => _entries.Values.Single(e => e.Guid == guid);

    private DirectoryEntry Add(DirectoryEntry entry) => _entries[entry.Dn] = entry;

    public DirectoryEntry? FindByGuid(string guid) => _entries.Values.FirstOrDefault(e => string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase));

    public DirectoryEntry? FindByDn(string dn) => _entries.GetValueOrDefault(dn);

    public IEnumerable<DirectoryEntry> UsersUnder(string dn) =>
        _entries.Values.Where(e => e.Kind == EntryKind.User && e.Dn.EndsWith("," + dn, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> MemberDns(string groupDn)
    {
        MemberReads++;
        return _members.GetValueOrDefault(groupDn) ?? [];
    }

    public IReadOnlyList<DirectoryEntry> Search(string text, EntryKind kind, int limit) =>
        _entries.Values.Where(e => e.Kind == kind && e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();

    public IDirectorySource Connect(DirectoryConfig config) =>
        ConnectError is null ? this : throw new DirectoryException(ConnectError);

    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    public void SetPassword(DirectoryEntry user, string password) => _passwords[user.Dn] = password;

    public DirectoryEntry? FindUserBySignInName(string name)
    {
        var n = name[(name.LastIndexOf('\\') + 1)..].Split('@')[0];
        return _entries.Values.FirstOrDefault(e => e.Kind == EntryKind.User && string.Equals(e.SamAccountName, n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Like AD, refuses disabled accounts even with the right password.</summary>
    public DirectoryEntry? Authenticate(DirectoryConfig config, string name, string password)
    {
        using var source = Connect(config);
        return FindUserBySignInName(name) is { Disabled: false } user && _passwords.GetValueOrDefault(user.Dn) is { Length: > 0 } expected && expected == password
            ? user
            : null;
    }

    public void Dispose() { }
}
