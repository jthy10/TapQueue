using TapQueue.Server.Admin;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Server.Users;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// Endpoints used by tapqueue-admin and the admin console. Authenticated with admin.token from server.toml,
/// except in dev mode, where the console (which has no sign-in yet) calls them without it.
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(RequireAdmin);

        admin.MapGet("/server", ServerInfo);
        admin.MapPatch("/server/settings", UpdateServerSettings);
        admin.MapServerApi();
        admin.MapGet("/users", (UserStore users, GroupStore groups) =>
        {
            var memberships = groups.Memberships();
            return users.List().Select(u => u.ToAdminDto(memberships.GetValueOrDefault(u.Id) ?? []));
        });
        admin.MapPost("/users", CreateUser);
        admin.MapPatch("/users/{username}", UpdateUser);
        admin.MapDelete("/users/{username}", DeleteUser);
        admin.MapPost("/users/{username}/token", ResetToken);
        admin.MapGet("/jobs", (JobStore jobs, string? status) => jobs.List(status).Select(j => j.ToDto()));
        admin.MapGet("/jobs/{id:long}", (long id, JobStore jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job.ToDto()) : Results.NotFound(new ErrorResponse($"No job {id}.")));
        admin.MapDelete("/jobs/{id:long}", CancelJob);
        admin.MapGet("/printers", async (PrinterRegistry printers, bool? refresh, CancellationToken ct) =>
        {
            var all = printers.All;
            if (refresh == true)
                await Task.WhenAll(all.Select(p => printers.ProbeAsync(p, ct)));
            return all.Select(printers.ToAdminDto);
        });
        admin.MapPost("/printers", CreatePrinter);
        admin.MapPatch("/printers/{id}", UpdatePrinter);
        admin.MapDelete("/printers/{id}", DeletePrinter);

        admin.MapGet("/queues", (QueueStore queues) => queues.List().Select(ToAdminDto));
        admin.MapPost("/queues", CreateQueue);
        admin.MapPatch("/queues/{id}", UpdateQueue);
        admin.MapDelete("/queues/{id}", (string id, QueueStore queues, EventLog events) =>
        {
            if (!queues.Delete(id))
                return Results.NotFound(new ErrorResponse($"No queue \"{id}\"."));
            events.Admin(EventLog.Queue(id), $"Removed queue {id}.");
            return Results.NoContent();
        });
        admin.MapPost("/release", Release);

        admin.MapGet("/badges", (BadgeStore badges, UserStore users, string? username) =>
        {
            if (username is null)
                return Results.Ok(badges.List().Select(b => b.ToDto()));
            return users.FindByUsername(username) is { } user
                ? Results.Ok(badges.List(user.Id).Select(b => b.ToDto()))
                : Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        });
        admin.MapPost("/badges", CreateBadge);
        admin.MapPatch("/badges/{id:long}", UpdateBadge);
        admin.MapDelete("/badges/{id:long}", (long id, BadgeStore badges, EventLog events) =>
        {
            if (badges.Get(id) is not { } badge || !badges.Delete(id))
                return Results.NotFound(new ErrorResponse($"No badge {id}."));
            events.Admin(EventLog.User(badge.Username), $"Removed card {badge.CardHint} from {badge.Username}.");
            return Results.NoContent();
        });
        admin.MapGet("/badges/unknown", (UnknownTaps taps) => taps.Recent());
        admin.MapGet("/events", (EventLog events, string? category, string? subject, long? before, int? limit) =>
            events.List(category, subject, before, limit ?? 100));

        admin.MapStationsApi();

        admin.MapGroupsApi();
        admin.MapUsersBulkApi();

        admin.MapGet("/client-builds", (ClientBuildStore builds) => builds.List().Select(b => b.ToDto()));
        admin.MapPost("/client-builds", PublishClientBuild);
        admin.MapGet("/clients", (SessionStore sessions, ServerSettings settings) => sessions.ListActive(settings.SessionTimeout));
    }

    private static ServerInfoDto ServerInfo(ServerConfig config, ServerSettings settings, Database database, JobStore jobs) => new(
        TapQueueVersion.Current, ServerClock.StartedAt, config.Auth.Mode, config.Server.Listen, config.Server.DataDir,
        database.SchemaVersion(), settings.HoldHours, settings.SessionTimeoutMinutes, jobs.CountHeld(),
        ServerSettings.Keys.Where(settings.IsSaved).ToList(), AdminServerApi.CanRestart);

    private static IResult UpdateServerSettings(UpdateServerSettingsRequest request, ServerConfig config, ServerSettings settings,
        Database database, JobStore jobs, EventLog events)
    {
        if (request.HoldHours is < 1 or > 720)
            return Results.BadRequest(new ErrorResponse("holdHours must be 1 to 720 (30 days)."));
        // Clients check in every minute, so anything shorter would sign everyone out between heartbeats.
        if (request.SessionTimeoutMinutes is < 2 or > 1440)
            return Results.BadRequest(new ErrorResponse("sessionTimeoutMinutes must be 2 to 1440 (a day)."));
        foreach (var key in request.Reset ?? [])
            if (!ServerSettings.Keys.Contains(key))
                return Results.BadRequest(new ErrorResponse($"Can't reset \"{key}\"; only {string.Join(", ", ServerSettings.Keys)}."));

        var changes = new List<string>();
        void Apply(string key, int? value, int current, string name, string unit)
        {
            if (request.Reset?.Contains(key) == true)
            {
                settings.Set(key, null);
                changes.Add($"{name} back to server.toml's ({Plural(key == ServerSettings.HoldHoursKey ? settings.HoldHours : settings.SessionTimeoutMinutes, unit)})");
            }
            else if (value is { } v && (v != current || !settings.IsSaved(key)))
            {
                settings.Set(key, v);
                changes.Add($"{name} {Plural(v, unit)}");
            }
        }
        Apply(ServerSettings.HoldHoursKey, request.HoldHours, settings.HoldHours, "held jobs kept for", "hour");
        Apply(ServerSettings.SessionTimeoutKey, request.SessionTimeoutMinutes, settings.SessionTimeoutMinutes, "session timeout", "minute");
        if (changes.Count > 0)
            events.Admin(null, $"Server settings: {string.Join("; ", changes)}.");
        return Results.Ok(ServerInfo(config, settings, database, jobs));

        static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
    }

    /// <summary>The request body is TapQueueClient.exe. Clients start installing it on their next heartbeat.</summary>
    private static async Task<IResult> PublishClientBuild(HttpContext http, string? version, ClientBuildStore builds, EventLog events, ILoggerFactory loggers)
    {
        version = Clean(version);
        if (version is null)
            return Results.BadRequest(new ErrorResponse("version is required, e.g. ?version=0.2.0+1a2b3c4."));
        BuildRecord build;
        try
        {
            build = await builds.PublishAsync(version, http.Request.Body, http.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        loggers.CreateLogger("TapQueue.Server.Api.AdminApi").LogInformation(
            "Published Windows client {Version} ({Size} bytes, sha256 {Sha256}); clients will update on their next heartbeat",
            build.Version, build.SizeBytes, build.Sha256);
        events.Admin(null, $"Published Windows client {build.Version}; clients install it on their next heartbeat.");
        return Results.Ok(build.ToDto());
    }

    private static IResult CancelJob(long id, JobStore jobs, Spool spool, EventLog events, ILoggerFactory loggers)
    {
        if (jobs.Get(id) is not { } job)
            return Results.NotFound(new ErrorResponse($"No job {id}."));
        if (!jobs.TryTransition(id, JobStatus.Held, JobStatus.Canceled))
            return Results.Conflict(new ErrorResponse($"Job {id} is {job.Status}, not held, so it can't be canceled."));
        spool.Delete(id);
        loggers.CreateLogger("TapQueue.Server.Api.AdminApi").LogInformation("Admin canceled job {Id} ({Name}) of {User}", id, job.Name, job.Username ?? "nobody");
        events.Admin(job.Username is null ? EventLog.Job(id) : EventLog.User(job.Username), $"Canceled \"{job.Name}\" (job #{id}) of {job.Username ?? "nobody"}.");
        return Results.NoContent();
    }

    private static async Task<IResult> CreatePrinter(CreatePrinterRequest request, PrinterStore store, PrinterRegistry printers, EventLog events, CancellationToken ct)
    {
        var id = request.Id?.Trim();
        if (!Ids.IsValid(id))
            return Results.BadRequest(new ErrorResponse($"Printer id must be {Ids.Rule}."));
        if (!PrinterRecord.IsValidUri(request.Uri))
            return Results.BadRequest(new ErrorResponse("Printer uri must be ipp://, ipps://, http:// or https://, e.g. ipp://192.0.2.10/ipp/print."));
        if (store.Get(id!) is not null)
            return Results.Conflict(new ErrorResponse($"Printer \"{id}\" already exists."));

        var printer = store.Create(new PrinterRecord(id!, Clean(request.Name) ?? id!, Clean(request.Location) ?? "",
            request.Uri.Trim(), request.TlsSkipVerify ?? false));
        events.Admin(EventLog.Printer(printer.Id), $"Added printer {printer.Name} ({printer.Id}) at {printer.Uri}.");
        await printers.ProbeAsync(printer, ct);
        return Results.Ok(printers.ToAdminDto(printer));
    }

    private static async Task<IResult> UpdatePrinter(string id, UpdatePrinterRequest request, PrinterStore store, PrinterRegistry printers, EventLog events, CancellationToken ct)
    {
        if (store.Get(id) is not { } printer)
            return Results.NotFound(new ErrorResponse($"No printer \"{id}\"."));
        if (request.Uri is not null && !PrinterRecord.IsValidUri(request.Uri))
            return Results.BadRequest(new ErrorResponse("Printer uri must be ipp://, ipps://, http:// or https://, e.g. ipp://192.0.2.10/ipp/print."));

        printer = store.Update(printer with
        {
            Uri = request.Uri?.Trim() ?? printer.Uri,
            Name = Clean(request.Name) ?? printer.Name,
            Location = request.Location?.Trim() ?? printer.Location,
            TlsSkipVerify = request.TlsSkipVerify ?? printer.TlsSkipVerify,
        })!;
        printers.Forget(printer.Id);
        events.Admin(EventLog.Printer(printer.Id), $"Changed printer {printer.Name} ({printer.Id}).");
        await printers.ProbeAsync(printer, ct);
        return Results.Ok(printers.ToAdminDto(printer));
    }

    private static IResult DeletePrinter(string id, PrinterStore store, PrinterRegistry printers, EventLog events)
    {
        var stations = store.StationsUsing(id);
        if (stations.Count > 0)
            return Results.Conflict(new ErrorResponse(
                $"{(stations.Count == 1 ? "Station" : "Stations")} {string.Join(", ", stations)} {(stations.Count == 1 ? "releases" : "release")} to printer \"{id}\". " +
                "Move them with `tapqueue-admin stations move` or remove them first."));
        if (!store.Delete(id))
            return Results.NotFound(new ErrorResponse($"No printer \"{id}\"."));
        printers.Forget(id);
        events.Admin(EventLog.Printer(id), $"Removed printer {id}.");
        return Results.NoContent();
    }

    private static IResult CreateQueue(CreateQueueRequest request, QueueStore queues, EventLog events)
    {
        var id = request.Id?.Trim();
        if (!Ids.IsValid(id))
            return Results.BadRequest(new ErrorResponse($"Queue id must be {Ids.Rule}. It becomes part of the URL clients print to."));
        if (Clean(request.Name) is not { } name)
            return Results.BadRequest(new ErrorResponse("Queue name is required. It's the printer name users see in Windows."));
        if (queues.Get(id!) is not null)
            return Results.Conflict(new ErrorResponse($"Queue \"{id}\" already exists."));

        var queue = queues.Create(new QueueRecord(id!, name, Clean(request.Description) ?? QueueRecord.DefaultDescription,
            Clean(request.Location) ?? "", request.Color ?? false, request.Duplex ?? false, Clean(request.DefaultMedia) ?? QueueRecord.Letter));
        events.Admin(EventLog.Queue(queue.Id), $"Added queue \"{queue.Name}\" ({queue.Id}).");
        return Results.Ok(ToAdminDto(queue));
    }

    private static IResult UpdateQueue(string id, UpdateQueueRequest request, QueueStore queues, EventLog events)
    {
        if (queues.Get(id) is not { } queue)
            return Results.NotFound(new ErrorResponse($"No queue \"{id}\"."));
        queue = queues.Update(queue with
        {
            Name = Clean(request.Name) ?? queue.Name,
            Description = request.Description?.Trim() ?? queue.Description,
            Location = request.Location?.Trim() ?? queue.Location,
            Color = request.Color ?? queue.Color,
            Duplex = request.Duplex ?? queue.Duplex,
            DefaultMedia = Clean(request.DefaultMedia) ?? queue.DefaultMedia,
        })!;
        events.Admin(EventLog.Queue(queue.Id), $"Changed queue \"{queue.Name}\" ({queue.Id}).");
        return Results.Ok(ToAdminDto(queue));
    }

    private static QueueAdminDto ToAdminDto(QueueRecord q) =>
        new(q.Id, q.Name, q.Description, q.Location, q.Color, q.Duplex, q.DefaultMedia, $"/ipp/{q.Id}");

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult CreateBadge(CreateBadgeRequest request, UserStore users, BadgeStore badges, UnknownTaps unknownTaps, EventLog events)
    {
        var card = BadgeStore.Normalize(request.Card ?? "");
        if (card.Length == 0)
            return Results.BadRequest(new ErrorResponse("card is required"));
        var user = users.FindByUsername(request.Username ?? "");
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
        if (badges.FindByCard(card) is { } existing)
            return Results.Conflict(new ErrorResponse($"That card is already linked to {existing.Username} (badge {existing.Id})."));

        var badge = badges.Add(user.Id, card, request.Label?.Trim() ?? "");
        unknownTaps.Remove(card);
        events.Admin(EventLog.User(user.Username), $"Linked card {badge.CardHint} to {user.Username}.");
        return Results.Ok(badge.ToDto());
    }

    private static IResult UpdateBadge(long id, UpdateBadgeRequest request, BadgeStore badges, UserStore users, EventLog events)
    {
        if (badges.Get(id) is not { } badge)
            return Results.NotFound(new ErrorResponse($"No badge {id}."));
        var userId = badge.UserId;
        if (request.Username is not null)
        {
            if (users.FindByUsername(request.Username) is not { } user)
                return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
            userId = user.Id;
        }
        var updated = badges.Update(id, userId, request.Label?.Trim() ?? badge.Label)!;
        events.Admin(EventLog.User(updated.Username), updated.UserId == badge.UserId
            ? $"Changed card {badge.CardHint} of {badge.Username}."
            : $"Moved card {badge.CardHint} from {badge.Username} to {updated.Username}.");
        return Results.Ok(updated.ToDto());
    }

    private static IResult CreateUser(CreateUserRequest request, UserStore users, UserLifecycle lifecycle)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
            return Results.BadRequest(new ErrorResponse("username is required"));
        if (users.FindByUsername(username) is not null)
            return Results.Conflict(new ErrorResponse($"User \"{username}\" already exists."));

        var (user, token) = lifecycle.Create(username, request.DisplayName);
        return Results.Ok(new UserTokenResponse(user.ToDto(), token));
    }

    private static IResult UpdateUser(string username, UpdateUserRequest request, UserStore users, GroupStore groups, UserLifecycle lifecycle)
    {
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        if (Clean(request.DisplayName) is { } displayName)
            user = lifecycle.Rename(user, displayName);
        if (request.Disabled is { } disabled)
            user = lifecycle.SetDisabled(user, disabled);
        return Results.Ok(user.ToAdminDto(groups.Memberships().GetValueOrDefault(user.Id) ?? []));
    }

    private static IResult DeleteUser(string username, UserStore users, UserLifecycle lifecycle)
    {
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        lifecycle.Delete(user);
        return Results.NoContent();
    }

    private static IResult ResetToken(string username, UserStore users, EventLog events)
    {
        var user = users.FindByUsername(username);
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        var token = Tokens.New();
        users.SetTokenHash(user.Id, Tokens.Hash(token));
        events.Admin(EventLog.User(user.Username), $"Reset {user.Username}'s client token; the old one stopped working.");
        return Results.Ok(new UserTokenResponse(user.ToDto(), token));
    }

    private static async Task<IResult> Release(AdminReleaseRequest request, UserStore users, PrinterRegistry printers,
        ReleaseService release, CancellationToken ct)
    {
        var user = users.FindByUsername(request.Username);
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
        var printer = printers.Find(request.PrinterId);
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\"."));
        return Results.Ok(await release.ReleaseAsync(user, printer, request.JobIds, EventLog.AdminActor, "from the admin console", ct));
    }

    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var config = http.RequestServices.GetRequiredService<ServerConfig>();
        var token = ClientApi.BearerToken(http);
        if (AdminUi.IsEnabled(config) && token is null)
            return await next(context);
        if (token is null || !Tokens.FixedTimeEquals(token, config.Admin.Token))
            return Results.Json(new ErrorResponse("Admin token required."), statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }
}
