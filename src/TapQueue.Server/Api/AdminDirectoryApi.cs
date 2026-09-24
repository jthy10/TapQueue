using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/directory: the Active Directory sync's settings, scope, history, and running it.</summary>
public static class AdminDirectoryApi
{
    private const int SearchLimit = 50;

    public static void MapDirectoryApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/directory", Status);
        admin.MapPatch("/directory/config", UpdateConfig);
        admin.MapPost("/directory/test", Test);
        admin.MapGet("/directory/search", Search);
        admin.MapPost("/directory/scope", AddScope);
        admin.MapDelete("/directory/scope/{id:long}", RemoveScope);
        admin.MapPost("/directory/sync", Sync);
        admin.MapGet("/directory/runs", (DirectoryStore store) => store.Runs(100));
    }

    private static DirectoryStatusDto Status(DirectoryStore store, DirectorySync sync, DirectorySyncService schedule) =>
        new(store.Config().ToDto(), store.Scope().Select(s => s.ToDto()).ToList(), store.Runs(), schedule.NextRunAt(), sync.Running);

    private static IResult UpdateConfig(UpdateDirectoryConfigRequest request, DirectoryStore store, DirectorySync sync, DirectorySyncService schedule, EventLog events)
    {
        if (request.Port is < 1 or > 65535)
            return Results.BadRequest(new ErrorResponse("port must be 1 to 65535 (LDAPS is 636)."));
        if (request.SyncTime is { } time && !DirectoryConfig.IsValidTime(time))
            return Results.BadRequest(new ErrorResponse("syncTime must be HH:mm, like 01:00."));
        if (request.MaxDisablePercent is < 1 or > 100)
            return Results.BadRequest(new ErrorResponse("maxDisablePercent must be 1 to 100."));
        if (request.BadgeAttribute is { } attribute && !attribute.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return Results.BadRequest(new ErrorResponse("badgeAttribute must be an attribute name, like employeeNumber."));
        if (request.CaCertificate is { Length: > 0 } pem && !pem.Contains("-----BEGIN CERTIFICATE-----"))
            return Results.BadRequest(new ErrorResponse("caCertificate must be a PEM certificate (-----BEGIN CERTIFICATE-----)."));

        var old = store.Config();
        var config = old with
        {
            Enabled = request.Enabled ?? old.Enabled,
            Host = request.Host?.Trim() ?? old.Host,
            Port = request.Port ?? old.Port,
            BindDn = request.BindDn?.Trim() ?? old.BindDn,
            Password = string.IsNullOrEmpty(request.Password) ? old.Password : request.Password,
            CaCertificate = request.CaCertificate?.Trim() ?? old.CaCertificate,
            BadgeAttribute = request.BadgeAttribute?.Trim() ?? old.BadgeAttribute,
            SyncTime = request.SyncTime ?? old.SyncTime,
            MaxDisablePercent = request.MaxDisablePercent ?? old.MaxDisablePercent,
        };
        if (config == old)
            return Results.Ok(Status(store, sync, schedule));

        store.SaveConfig(config);
        schedule.Reschedule();
        var changes = new List<string>();
        if (config.Enabled != old.Enabled) changes.Add(config.Enabled ? $"daily sync on at {config.SyncTime}" : "daily sync off");
        else if (config.SyncTime != old.SyncTime) changes.Add($"daily sync at {config.SyncTime}");
        if (config.Host != old.Host || config.Port != old.Port) changes.Add($"domain controller {config.Host}:{config.Port}");
        if (config.BindDn != old.BindDn) changes.Add($"bind account {config.BindDn}");
        if (config.Password != old.Password) changes.Add("new bind password");
        if (config.CaCertificate != old.CaCertificate) changes.Add(config.CaCertificate.Length == 0 ? "trusts the server's CAs" : "new CA certificate");
        if (config.BadgeAttribute != old.BadgeAttribute) changes.Add(config.BadgeAttribute.Length == 0 ? "cards managed in TapQueue" : $"cards from {config.BadgeAttribute}");
        if (config.MaxDisablePercent != old.MaxDisablePercent) changes.Add($"stops before disabling more than {config.MaxDisablePercent}%");
        events.Admin(null, $"Active Directory sync settings: {string.Join("; ", changes)}.");
        return Results.Ok(Status(store, sync, schedule));
    }

    private static async Task<DirectoryTestResultDto> Test(DirectoryStore store, IDirectorySourceFactory sources) =>
        await Task.Run(() =>
        {
            try
            {
                using var source = sources.Connect(store.Config());
                var root = source.DefaultNamingContext;
                return new DirectoryTestResultDto(true, $"Connected and signed in. The domain is {root}.", root);
            }
            catch (DirectoryException ex)
            {
                return new DirectoryTestResultDto(false, ex.Message, null);
            }
        });

    /// <summary>OUs, groups or users whose name contains q (all of them, up to 50, without it).</summary>
    private static async Task<IResult> Search(string? kind, string? q, DirectoryStore store, IDirectorySourceFactory sources) =>
        await Task.Run(() =>
        {
            var entryKind = kind switch
            {
                DirectoryScopeKind.OrganizationalUnit => EntryKind.OrganizationalUnit,
                DirectoryScopeKind.Group => EntryKind.Group,
                DirectoryScopeKind.User => EntryKind.User,
                _ => (EntryKind?)null,
            };
            if (entryKind is null)
                return Results.BadRequest(new ErrorResponse($"kind must be {string.Join(", ", DirectoryScopeKind.All)}."));
            try
            {
                using var source = sources.Connect(store.Config());
                var inScope = store.Scope().Select(s => s.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Results.Ok(source.Search(q ?? "", entryKind.Value, SearchLimit)
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new DirectoryObjectDto(kind!, e.Guid, e.Dn, e.Name, e.SamAccountName, e.DisplayName, inScope.Contains(e.Guid))));
            }
            catch (DirectoryException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status502BadGateway);
            }
        });

    private static async Task<IResult> AddScope(AddDirectoryScopeRequest request, DirectoryStore store, IDirectorySourceFactory sources, EventLog events) =>
        await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(request.Guid) && string.IsNullOrWhiteSpace(request.Dn))
                return Results.BadRequest(new ErrorResponse("Give the guid (from a search) or the dn of an OU, group or user."));
            DirectoryEntry? entry;
            try
            {
                using var source = sources.Connect(store.Config());
                entry = string.IsNullOrWhiteSpace(request.Guid) ? source.FindByDn(request.Dn!.Trim()) : source.FindByGuid(request.Guid.Trim());
            }
            catch (DirectoryException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status502BadGateway);
            }
            var kind = entry?.Kind switch
            {
                EntryKind.OrganizationalUnit => DirectoryScopeKind.OrganizationalUnit,
                EntryKind.Group => DirectoryScopeKind.Group,
                EntryKind.User => DirectoryScopeKind.User,
                _ => null,
            };
            if (entry is null)
                return Results.NotFound(new ErrorResponse($"Nothing in Active Directory at {request.Dn ?? request.Guid}."));
            if (kind is null)
                return Results.BadRequest(new ErrorResponse($"{entry.Dn} isn't an OU, group or user."));
            if (store.AddScope(kind, entry.Guid, entry.Dn, entry.Name) is not { } item)
                return Results.Conflict(new ErrorResponse($"{entry.Name} is already in the scope."));
            events.Admin(null, $"Added {Describe(kind)} {entry.Name} ({entry.Dn}) to the Active Directory sync's scope.");
            return Results.Ok(item.ToDto());
        });

    private static IResult RemoveScope(long id, DirectoryStore store, EventLog events)
    {
        if (store.GetScope(id) is not { } item || !store.RemoveScope(id))
            return Results.NotFound(new ErrorResponse($"No scope item {id}."));
        events.Admin(null, $"Removed {Describe(item.Kind)} {item.Name} ({item.Dn}) from the Active Directory sync's scope; the next sync disables users only it brought in.");
        return Results.NoContent();
    }

    private static async Task<IResult> Sync(DirectorySyncRequest? request, DirectorySync sync) =>
        await Task.Run(() =>
        {
            try
            {
                return Results.Ok(sync.Run(DirectorySync.AdminTrigger, request?.DryRun ?? false, request?.Force ?? false));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
        });

    private static string Describe(string kind) => kind switch
    {
        DirectoryScopeKind.OrganizationalUnit => "OU",
        DirectoryScopeKind.Group => "group",
        _ => "user",
    };
}
