using TapQueue.Shared.Api;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>A user in the sync's scope, as AD has them.</summary>
public sealed record DirectoryUser(string Guid, string Username, string DisplayName, bool Disabled, bool Expired, string? Badge, string Dn);

/// <summary>A group in the scope with everyone in it, through nested groups too.</summary>
/// <param name="Members">User objectGUIDs, each with the nested group they came through (null if direct).</param>
public sealed record DirectoryGroup(string Guid, string SamAccountName, string Name, string Description, IReadOnlyList<(string UserGuid, string? Via)> Members);

/// <param name="Scope">Each scope item as AD has it now, so its shown name and DN can be kept up to date.</param>
public sealed record DirectorySnapshot(
    IReadOnlyList<DirectoryUser> Users,
    IReadOnlyList<DirectoryGroup> Groups,
    IReadOnlyList<(DirectoryScopeItem Item, DirectoryEntry Entry)> Scope,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads everything in the sync's scope: users under the OUs, members of the groups and the single
/// users. Nested groups are followed to the bottom, including groups outside the scope (their
/// members count; the groups themselves don't become TapQueue groups), and a group that contains
/// itself somewhere down the line is only read once.
///
/// A scope item that's gone from AD fails the whole read: syncing without it would disable its users.
/// </summary>
public sealed class DirectoryReader(IDirectorySource source)
{
    private readonly Dictionary<string, DirectoryEntry?> _byDn = new(StringComparer.OrdinalIgnoreCase);

    public DirectorySnapshot Read(IReadOnlyList<DirectoryScopeItem> scope, DateTimeOffset now)
    {
        var users = new Dictionary<string, DirectoryUser>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DirectoryGroup>();
        var found = new List<(DirectoryScopeItem, DirectoryEntry)>();
        var warnings = new List<string>();

        void AddUser(DirectoryEntry entry)
        {
            if (string.IsNullOrEmpty(entry.SamAccountName))
            {
                warnings.Add($"Skipped {entry.Dn}: it has no sAMAccountName.");
                return;
            }
            users.TryAdd(entry.Guid, new DirectoryUser(entry.Guid, entry.SamAccountName, Clean(entry.DisplayName) ?? entry.SamAccountName,
                entry.Disabled, entry.ExpiredAt(now), Clean(entry.Badge), entry.Dn));
        }

        foreach (var item in scope)
        {
            var entry = source.FindByGuid(item.Guid)
                ?? throw new DirectoryException($"{Describe(item.Kind)} \"{item.Name}\" ({item.Dn}) is no longer in Active Directory. Remove it from the scope, or add it again if it was recreated.");
            var expected = item.Kind switch
            {
                DirectoryScopeKind.OrganizationalUnit => EntryKind.OrganizationalUnit,
                DirectoryScopeKind.Group => EntryKind.Group,
                _ => EntryKind.User,
            };
            if (entry.Kind != expected)
                throw new DirectoryException($"{entry.Dn} is in the scope as {Describe(item.Kind).ToLowerInvariant()}, but it isn't one.");
            found.Add((item, entry));

            switch (item.Kind)
            {
                case DirectoryScopeKind.OrganizationalUnit:
                    foreach (var user in source.UsersUnder(entry.Dn))
                        AddUser(user);
                    break;
                case DirectoryScopeKind.User:
                    AddUser(entry);
                    break;
                case DirectoryScopeKind.Group:
                    var members = Flatten(entry);
                    foreach (var (member, _) in members)
                        AddUser(member);
                    groups.Add(new DirectoryGroup(entry.Guid, entry.SamAccountName ?? entry.Name, entry.Name, Clean(entry.Description) ?? "",
                        members.Where(m => !string.IsNullOrEmpty(m.User.SamAccountName)).Select(m => (m.User.Guid, m.Via)).ToList()));
                    break;
            }
        }
        return new DirectorySnapshot(users.Values.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList(), groups, found, warnings);
    }

    /// <summary>
    /// Every user in the group. Direct members are taken before nested groups are opened, so someone
    /// who is both shows as direct. Via is the group's direct subgroup they came through.
    /// </summary>
    private List<(DirectoryEntry User, string? Via)> Flatten(DirectoryEntry group)
    {
        var members = new List<(DirectoryEntry, string?)>();
        var seenUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Dn };
        var pending = new Queue<(DirectoryEntry Group, string? Via)>();
        pending.Enqueue((group, null));
        while (pending.TryDequeue(out var current))
        {
            foreach (var dn in source.MemberDns(current.Group.Dn))
            {
                if (Lookup(dn) is not { } member)
                    continue;
                if (member.Kind == EntryKind.User && seenUsers.Add(member.Guid))
                    members.Add((member, current.Via));
                else if (member.Kind == EntryKind.Group && seenGroups.Add(member.Dn))
                    pending.Enqueue((member, current.Via ?? member.Name));
            }
        }
        return members;
    }

    private DirectoryEntry? Lookup(string dn)
    {
        if (!_byDn.TryGetValue(dn, out var entry))
            _byDn[dn] = entry = source.FindByDn(dn);
        return entry;
    }

    private static string Describe(string kind) => kind switch
    {
        DirectoryScopeKind.OrganizationalUnit => "OU",
        DirectoryScopeKind.Group => "Group",
        _ => "User",
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
