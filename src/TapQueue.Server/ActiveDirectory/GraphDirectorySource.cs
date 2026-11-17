using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TapQueue.Shared.Api;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// Microsoft Entra ID (Azure AD) as the directory, read through Microsoft Graph with an app
/// registration's client secret (the application permissions User.Read.All and Group.Read.All, or
/// Directory.Read.All). It answers the same questions <see cref="LdapDirectorySource"/> does, so the
/// sync (<see cref="DirectorySync"/>) doesn't know the difference:
/// <list type="bullet">
/// <item>An object's "DN" is <c>user:&lt;id&gt;</c> or <c>group:&lt;id&gt;</c>, and its GUID is its object id.</item>
/// <item>A username is the part of the userPrincipalName before the @, which is also what Windows
/// calls the person on a PC joined to Entra ID; for accounts synced from on-premises AD it's their
/// sAMAccountName.</item>
/// <item>Entra ID has no OUs: the scope is groups (with nested groups) and single users.</item>
/// <item>The card attribute can be employeeId, or any other property of a user Graph returns.</item>
/// </list>
/// Entra ID won't check a password for anyone else, so its users sign in to the console and the web
/// page with single sign-on (<see cref="Sso.OidcSignIn"/>), and to the tray as their PC's user.
/// </summary>
public sealed class GraphDirectorySource : IDirectorySource
{
    public const string UserPrefix = "user:";
    public const string GroupPrefix = "group:";
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private readonly HttpClient _http;
    private readonly string _badgeAttribute;
    private readonly string _userSelect;

    private GraphDirectorySource(HttpClient http, string tenantId, string badgeAttribute)
    {
        _http = http;
        DefaultNamingContext = tenantId;
        _badgeAttribute = badgeAttribute;
        _userSelect = "id,userPrincipalName,displayName,accountEnabled,onPremisesSamAccountName" + (badgeAttribute.Length > 0 ? "," + badgeAttribute : "");
    }

    public string DefaultNamingContext { get; }

    /// <summary>Gets a token for Graph with the app's secret. Throws <see cref="DirectoryException"/> saying what Entra ID said if it won't give one.</summary>
    public static GraphDirectorySource Connect(DirectoryConfig config, OutboundHttp outbound)
    {
        if (!config.Reachable)
            throw new DirectoryException("The tenant, the application (client) ID and its client secret are required.");
        var http = outbound.Create(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(config.TenantId.Trim())}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = config.ClientId.Trim(),
                    ["client_secret"] = config.Password,
                    ["scope"] = "https://graph.microsoft.com/.default",
                }),
            };
            using var response = http.Send(request);
            using var json = JsonDocument.Parse(response.Content.ReadAsStream());
            if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("access_token", out var token))
                throw new DirectoryException("Entra ID wouldn't sign the application in: " +
                    (Text(json.RootElement, "error_description")?.Split('\n')[0] ?? $"{(int)response.StatusCode} {response.ReasonPhrase}") +
                    " Check the tenant, the application (client) ID and the client secret.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
            return new GraphDirectorySource(http, config.TenantId.Trim(), config.BadgeAttribute.Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            http.Dispose();
            throw new DirectoryException($"Couldn't reach Entra ID (login.microsoftonline.com): {ex.Message}", ex);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public DirectoryEntry? FindByGuid(string guid) => IsId(guid) ? User(guid) ?? Group(guid) : null;

    public DirectoryEntry? FindByDn(string dn) =>
        dn.StartsWith(UserPrefix, StringComparison.Ordinal) ? User(dn[UserPrefix.Length..])
        : dn.StartsWith(GroupPrefix, StringComparison.Ordinal) ? Group(dn[GroupPrefix.Length..])
        : null;

    public IEnumerable<DirectoryEntry> UsersUnder(string dn) =>
        throw new DirectoryException("Entra ID has no OUs. Put groups or single users in the scope instead.");

    public IReadOnlyList<string> MemberDns(string groupDn)
    {
        if (!groupDn.StartsWith(GroupPrefix, StringComparison.Ordinal))
            return [];
        var members = new List<string>();
        foreach (var member in Pages($"groups/{Uri.EscapeDataString(groupDn[GroupPrefix.Length..])}/members?$select=id&$top=999"))
        {
            if (Text(member, "id") is not { } id)
                continue;
            var type = Text(member, "@odata.type");
            if (type == "#microsoft.graph.user") members.Add(UserPrefix + id);
            else if (type == "#microsoft.graph.group") members.Add(GroupPrefix + id);
        }
        return members;
    }

    public DirectoryEntry? FindUserBySignInName(string name)
    {
        var wanted = name.Trim();
        // A full name@domain is asked for directly; a bare name is looked up among the names Windows would use for them.
        if (wanted.Contains('@'))
            return User(wanted);
        var quoted = wanted.Replace("'", "''");
        return Pages($"users?$select={_userSelect}&$top=5&$filter=" +
                Uri.EscapeDataString($"startswith(userPrincipalName,'{quoted}@') or onPremisesSamAccountName eq '{quoted}'"), advanced: true)
            .Select(ToUser).FirstOrDefault(u => string.Equals(u.SamAccountName, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<DirectoryEntry> Search(string text, EntryKind kind, int limit)
    {
        var quoted = text.Trim().Replace("'", "''");
        return kind switch
        {
            EntryKind.Group => Pages($"groups?$select=id,displayName,description,mailNickname&$top={limit}" +
                (quoted.Length > 0 ? "&$filter=" + Uri.EscapeDataString($"startswith(displayName,'{quoted}')") : "")).Take(limit).Select(ToGroup).ToList(),
            EntryKind.User => Pages($"users?$select={_userSelect}&$top={limit}" +
                (quoted.Length > 0 ? "&$filter=" + Uri.EscapeDataString($"startswith(displayName,'{quoted}') or startswith(userPrincipalName,'{quoted}')") : ""))
                .Take(limit).Select(ToUser).ToList(),
            _ => [], // no OUs here
        };
    }

    private DirectoryEntry? User(string idOrUpn) =>
        Get($"users/{Uri.EscapeDataString(idOrUpn)}?$select={_userSelect}") is { } user ? ToUser(user) : null;

    private DirectoryEntry? Group(string id) =>
        Get($"groups/{Uri.EscapeDataString(id)}?$select=id,displayName,description,mailNickname") is { } group ? ToGroup(group) : null;

    private DirectoryEntry ToUser(JsonElement user)
    {
        var id = Text(user, "id") ?? "";
        var upn = Text(user, "userPrincipalName") ?? "";
        // Guests are "name_their.domain#EXT#@tenant": not a name anyone signs in to a PC with.
        var name = Text(user, "onPremisesSamAccountName") is { Length: > 0 } sam ? sam
            : upn.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) ? null
            : upn.Split('@')[0];
        var enabled = !user.TryGetProperty("accountEnabled", out var e) || e.ValueKind != JsonValueKind.False;
        return new DirectoryEntry(UserPrefix + id, id, EntryKind.User, Text(user, "displayName") ?? name ?? upn, string.IsNullOrEmpty(name) ? null : name,
            Text(user, "displayName"), null, enabled ? 0 : 0x2, 0, _badgeAttribute.Length > 0 ? Text(user, _badgeAttribute) : null);
    }

    private static DirectoryEntry ToGroup(JsonElement group)
    {
        var id = Text(group, "id") ?? "";
        var name = Text(group, "displayName") ?? id;
        return new DirectoryEntry(GroupPrefix + id, id, EntryKind.Group, name, Text(group, "mailNickname") ?? name, name, Text(group, "description"));
    }

    /// <summary>One object, or null if Graph says there's none.</summary>
    private JsonElement? Get(string path)
    {
        using var response = Send(path, advanced: false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        return Read(response).Clone();
    }

    /// <summary>Every item of a list, following @odata.nextLink through however many pages there are.</summary>
    private IEnumerable<JsonElement> Pages(string path, bool advanced = false)
    {
        string? next = path;
        while (next is not null)
        {
            JsonElement page;
            using (var response = Send(next, advanced))
                page = Read(response).Clone();
            if (page.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    yield return item;
            next = Text(page, "@odata.nextLink");
        }
    }

    private HttpResponseMessage Send(string pathOrUrl, bool advanced)
    {
        var url = pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : Graph + pathOrUrl;
        // Paging links come from Graph itself; anything else would be sending the token somewhere it shouldn't go.
        if (!url.StartsWith(Graph, StringComparison.OrdinalIgnoreCase))
            throw new DirectoryException("Microsoft Graph sent a link that isn't Graph's.");
        using var request = new HttpRequestMessage(HttpMethod.Get, advanced ? url + (url.Contains('?') ? "&" : "?") + "$count=true" : url);
        if (advanced)
            request.Headers.Add("ConsistencyLevel", "eventual");
        try
        {
            return _http.Send(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DirectoryException($"Couldn't reach Microsoft Graph: {ex.Message}", ex);
        }
    }

    private static JsonElement Read(HttpResponseMessage response)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(response.Content.ReadAsStream());
        }
        catch (JsonException ex)
        {
            throw new DirectoryException($"Microsoft Graph answered {(int)response.StatusCode} with something that isn't JSON.", ex);
        }
        using (json)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = json.RootElement.TryGetProperty("error", out var error) ? Text(error, "message") : null;
                throw new DirectoryException(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? $"Microsoft Graph refused: {message ?? "access denied"}. The application needs the User.Read.All and Group.Read.All application permissions, with admin consent."
                    : $"Microsoft Graph answered {(int)response.StatusCode}: {message ?? response.ReasonPhrase}");
            }
            return json.RootElement.Clone();
        }
    }

    private static bool IsId(string text) => Guid.TryParse(text, out _);

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Opens whichever directory is set up: Active Directory over LDAPS, or Entra ID through Microsoft
/// Graph (<see cref="DirectoryKind"/>).
/// </summary>
public sealed class DirectorySourceFactory(LdapDirectorySourceFactory ldap, OutboundHttp outbound) : IDirectorySourceFactory
{
    public IDirectorySource Connect(DirectoryConfig config) =>
        config.Kind == DirectoryKind.Entra ? GraphDirectorySource.Connect(config, outbound) : ldap.Connect(config);

    /// <summary>Only Active Directory checks a password for us; Entra ID's people use single sign-on, so nobody's password is theirs here.</summary>
    public DirectoryEntry? Authenticate(DirectoryConfig config, string name, string password) =>
        config.Kind == DirectoryKind.Entra ? null : ldap.Authenticate(config, name, password);
}
