namespace TapQueue.Server.ActiveDirectory;

public enum EntryKind { User, Group, OrganizationalUnit, Other }

/// <summary>One object read from AD, with only the attributes the sync uses.</summary>
/// <param name="Guid">objectGUID, which stays the same when the object is renamed or moved.</param>
/// <param name="UserAccountControl">Flags; 0x2 means the account is disabled.</param>
/// <param name="AccountExpires">A Windows file time; 0 or long.MaxValue means never.</param>
/// <param name="Badge">The value of the configured badge attribute, if any.</param>
public sealed record DirectoryEntry(
    string Dn,
    string Guid,
    EntryKind Kind,
    string Name,
    string? SamAccountName = null,
    string? DisplayName = null,
    string? Description = null,
    int UserAccountControl = 0,
    long AccountExpires = 0,
    string? Badge = null)
{
    private const int AccountDisable = 0x2;

    public bool Disabled => (UserAccountControl & AccountDisable) != 0;

    public bool ExpiredAt(DateTimeOffset now) =>
        AccountExpires is > 0 and < long.MaxValue && DateTimeOffset.FromFileTime(AccountExpires) <= now;
}

/// <summary>
/// A connection to the directory. <see cref="LdapDirectorySource"/> talks to AD; tests use an in-memory one.
/// Methods return null or nothing for objects that don't exist.
/// </summary>
public interface IDirectorySource : IDisposable
{
    /// <summary>The domain's root, like DC=lab,DC=tapqueue,DC=internal.</summary>
    string DefaultNamingContext { get; }

    DirectoryEntry? FindByGuid(string guid);

    DirectoryEntry? FindByDn(string dn);

    /// <summary>Every user account anywhere under <paramref name="dn"/>.</summary>
    IEnumerable<DirectoryEntry> UsersUnder(string dn);

    /// <summary>The DNs in a group's member attribute: users and groups, however many there are.</summary>
    IReadOnlyList<string> MemberDns(string groupDn);

    /// <summary>
    /// The user someone signs in as: a sAMAccountName (optionally DOMAIN\name) or a userPrincipalName
    /// (name@domain). Null if there's no such user.
    /// </summary>
    DirectoryEntry? FindUserBySignInName(string name);

    /// <summary>OUs, groups or users whose name contains <paramref name="text"/>, for picking the sync's scope.</summary>
    IReadOnlyList<DirectoryEntry> Search(string text, EntryKind kind, int limit);
}

/// <summary>Opens a <see cref="IDirectorySource"/> with the saved settings. Throws <see cref="DirectoryException"/> if it can't.</summary>
public interface IDirectorySourceFactory
{
    IDirectorySource Connect(DirectoryConfig config);

    /// <summary>
    /// Checks a person's own name and password against AD (for <see cref="Shared.Api.ClientSignIn.Domain"/>).
    /// Returns their entry, or null if there's no such user or AD refuses the password. Throws
    /// <see cref="DirectoryException"/> if AD can't be reached.
    /// </summary>
    DirectoryEntry? Authenticate(DirectoryConfig config, string name, string password);
}

/// <summary>AD couldn't be reached or refused us, or the scope names something that isn't there. The message says which.</summary>
public sealed class DirectoryException(string message, Exception? inner = null) : Exception(message, inner);
