using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TapQueue.Server.Config;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// Connects to AD over LDAPS (636). Plain LDAP isn't offered: Server 2025 refuses simple binds
/// without signing, and the bind password would cross the network readable. If the settings carry a
/// CA certificate, only certificates it issued are trusted; otherwise the OS's trusted CAs are.
/// </summary>
public sealed class LdapDirectorySourceFactory(ServerConfig serverConfig) : IDirectorySourceFactory
{
    public IDirectorySource Connect(DirectoryConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new DirectoryException("No domain controller is set.");
        if (string.IsNullOrWhiteSpace(config.BindDn) || string.IsNullOrEmpty(config.Password))
            throw new DirectoryException("The bind account and its password are required.");

        var connection = new LdapConnection(new LdapDirectoryIdentifier(config.Host.Trim(), config.Port));
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.SecureSocketLayer = true;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(config.CaCertificate))
                TrustOnly(connection, config.CaCertificate);
            connection.AuthType = AuthType.Basic;
            connection.Bind(new NetworkCredential(config.BindDn.Trim(), config.Password));
            return new LdapDirectorySource(connection, config.BadgeAttribute);
        }
        catch (LdapException ex)
        {
            connection.Dispose();
            throw new DirectoryException(ex.ErrorCode switch
            {
                49 => $"{config.Host} refused the bind account's name or password.",
                81 => $"Couldn't connect to {config.Host}:{config.Port} over LDAPS. Check the host, that port {config.Port} is open, and that its certificate is trusted (and issued for that name).",
                _ => $"{config.Host}: {ex.Message} {ex.ServerErrorMessage}".Trim(),
            }, ex);
        }
    }

    private void TrustOnly(LdapConnection connection, string caPem)
    {
        X509Certificate2 ca;
        try
        {
            ca = X509Certificate2.CreateFromPem(caPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new DirectoryException("The CA certificate isn't a PEM certificate (-----BEGIN CERTIFICATE-----).", ex);
        }

        if (OperatingSystem.IsWindows())
        {
            connection.SessionOptions.VerifyServerCertificate = (_, certificate) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            };
            return;
        }

        // OpenLDAP (under System.DirectoryServices.Protocols on Linux) reads trusted CAs from a directory.
        var directory = Path.Combine(serverConfig.Server.DataDir, "directory-ca");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), ca.ExportCertificatePem() + "\n");
        connection.SessionOptions.TrustedCertificatesDirectory = directory;
        connection.SessionOptions.StartNewTlsSessionContext();
    }
}

public sealed class LdapDirectorySource(LdapConnection connection, string? badgeAttribute) : IDirectorySource
{
    private const string UserFilter = "(&(objectCategory=person)(objectClass=user))";
    private const int PageSize = 500;

    private string? _namingContext;

    private string[] Attributes =>
    [
        "objectGUID", "objectClass", "name", "sAMAccountName", "displayName", "description", "userAccountControl", "accountExpires",
        .. string.IsNullOrWhiteSpace(badgeAttribute) ? Array.Empty<string>() : [badgeAttribute.Trim()],
    ];

    public string DefaultNamingContext => _namingContext ??=
        Send(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext")).Entries[0] is { } rootDse
            ? First(rootDse, "defaultNamingContext") ?? throw new DirectoryException("The domain controller didn't say which domain it serves.")
            : throw new DirectoryException("The domain controller didn't answer.");

    public DirectoryEntry? FindByGuid(string guid) =>
        System.Guid.TryParse(guid, out var parsed) ? FindByDn($"<GUID={parsed:D}>") : null;

    public DirectoryEntry? FindByDn(string dn)
    {
        try
        {
            var response = Send(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, Attributes));
            return response.Entries.Count == 0 ? null : Map(response.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    public IEnumerable<DirectoryEntry> UsersUnder(string dn)
    {
        var request = new SearchRequest(dn, UserFilter, SearchScope.Subtree, Attributes);
        var paging = new PageResultRequestControl(PageSize);
        request.Controls.Add(paging);
        while (true)
        {
            var response = Send(request);
            foreach (SearchResultEntry entry in response.Entries)
                yield return Map(entry);
            var next = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (next is null || next.Cookie.Length == 0)
                yield break;
            paging.Cookie = next.Cookie;
        }
    }

    /// <summary>AD hands out at most 1500 values of an attribute at a time; big groups are read in ranges.</summary>
    public IReadOnlyList<string> MemberDns(string groupDn)
    {
        var members = new List<string>();
        var start = 0;
        while (true)
        {
            var response = Send(new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, $"member;range={start}-*"));
            if (response.Entries.Count == 0)
                return members;
            var entry = response.Entries[0];
            var name = entry.Attributes.AttributeNames.Cast<string>().FirstOrDefault(n => n.StartsWith("member", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return members;
            members.AddRange(entry.Attributes[name].GetValues(typeof(string)).Cast<string>());
            // "member;range=0-1499" means there's more; "member;range=1500-*" (or plain "member") is the last of it.
            var range = name.Split("range=", 2);
            if (range.Length < 2 || range[1].EndsWith('*'))
                return members;
            start = int.Parse(range[1].Split('-')[1]) + 1;
        }
    }

    public IReadOnlyList<DirectoryEntry> Search(string text, EntryKind kind, int limit)
    {
        var term = string.IsNullOrWhiteSpace(text) ? "*" : $"*{Escape(text.Trim())}*";
        var filter = kind switch
        {
            EntryKind.OrganizationalUnit => $"(&(objectClass=organizationalUnit)(name={term}))",
            EntryKind.Group => $"(&(objectClass=group)(|(name={term})(sAMAccountName={term})))",
            EntryKind.User => $"(&{UserFilter}(|(name={term})(sAMAccountName={term})(displayName={term})))",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var request = new SearchRequest(DefaultNamingContext, filter, SearchScope.Subtree, Attributes) { SizeLimit = limit };
        SearchResponse response;
        try
        {
            response = Send(request);
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial && ex.Response.ResultCode == ResultCode.SizeLimitExceeded)
        {
            response = partial;
        }
        return response.Entries.Cast<SearchResultEntry>().Select(Map).Where(e => e.Kind == kind).ToList();
    }

    private SearchResponse Send(SearchRequest request)
    {
        try
        {
            return (SearchResponse)connection.SendRequest(request);
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"Lost the connection to the domain controller: {ex.Message}", ex);
        }
    }

    private DirectoryEntry Map(SearchResultEntry entry)
    {
        var classes = Values(entry, "objectClass");
        var kind = classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? EntryKind.Other
            : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? EntryKind.Group
            : classes.Contains("organizationalUnit", StringComparer.OrdinalIgnoreCase) ? EntryKind.OrganizationalUnit
            : classes.Contains("user", StringComparer.OrdinalIgnoreCase) ? EntryKind.User
            : EntryKind.Other;
        var guidBytes = entry.Attributes["objectGUID"]?.GetValues(typeof(byte[])).Cast<byte[]>().FirstOrDefault();
        return new DirectoryEntry(
            entry.DistinguishedName,
            guidBytes is { Length: 16 } ? new Guid(guidBytes).ToString("D") : "",
            kind,
            First(entry, "name") ?? entry.DistinguishedName,
            First(entry, "sAMAccountName"),
            First(entry, "displayName"),
            First(entry, "description"),
            int.TryParse(First(entry, "userAccountControl"), out var uac) ? uac : 0,
            long.TryParse(First(entry, "accountExpires"), out var expires) ? expires : 0,
            string.IsNullOrWhiteSpace(badgeAttribute) ? null : First(entry, badgeAttribute.Trim()));
    }

    private static string? First(SearchResultEntry entry, string attribute) => Values(entry, attribute).FirstOrDefault();

    private static string[] Values(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) ? entry.Attributes[attribute].GetValues(typeof(string)).Cast<string>().ToArray() : [];

    /// <summary>RFC 4515: characters with a meaning in filters are written as \hh.</summary>
    internal static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '*' or '(' or ')' or '\\' or '\0')
                escaped.Append('\\').Append(((int)c).ToString("x2"));
            else
                escaped.Append(c);
        }
        return escaped.ToString();
    }

    public void Dispose() => connection.Dispose();
}
