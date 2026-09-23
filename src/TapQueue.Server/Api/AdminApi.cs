using TapQueue.Server.Admin;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
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
        admin.MapGet("/users", (UserStore users) => users.List().Select(u => u.ToAdminDto()));
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
        admin.MapDelete("/queues/{id}", (string id, QueueStore queues) =>
            queues.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse($"No queue \"{id}\".")));
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
        admin.MapDelete("/badges/{id:long}", (long id, BadgeStore badges) =>
            badges.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse($"No badge {id}.")));
        admin.MapGet("/badges/unknown", (UnknownTaps taps) => taps.Recent());

        admin.MapGet("/stations", (StationStore stations) => stations.List().Select(s => s.ToDto()));
        admin.MapPost("/stations", CreateStation);
        admin.MapPatch("/stations/{id}", UpdateStation);
        admin.MapPost("/stations/{id}/token", ResetStationToken);
        admin.MapDelete("/stations/{id}", (string id, StationStore stations) =>
            stations.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse($"No station \"{id}\".")));

        admin.MapGet("/client-builds", (ClientBuildStore builds) => builds.List().Select(b => b.ToDto()));
        admin.MapPost("/client-builds", PublishClientBuild);
        admin.MapGet("/clients", (SessionStore sessions, ServerConfig config) =>
            sessions.ListActive(TimeSpan.FromMinutes(config.Auth.SessionTimeoutMinutes)));
    }

    private static ServerInfoDto ServerInfo(ServerConfig config, Database database, JobStore jobs) => new(
        TapQueueVersion.Current, ServerClock.StartedAt, config.Auth.Mode, config.Server.Listen, config.Server.DataDir,
        database.SchemaVersion(), config.Jobs.HoldHours, config.Auth.SessionTimeoutMinutes, jobs.CountHeld());

    /// <summary>The request body is TapQueueClient.exe. Clients start installing it on their next heartbeat.</summary>
    private static async Task<IResult> PublishClientBuild(HttpContext http, string? version, ClientBuildStore builds, ILoggerFactory loggers)
    {
        version = Clean(version);
        if (version is null)
            return Results.BadRequest(new ErrorResponse("version is required, e.g. ?version=0.2.0+1a2b3c4."));
        ClientBuildRecord build;
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
        return Results.Ok(build.ToDto());
    }

    private static IResult CancelJob(long id, JobStore jobs, Spool spool, ILoggerFactory loggers)
    {
        if (jobs.Get(id) is not { } job)
            return Results.NotFound(new ErrorResponse($"No job {id}."));
        if (!jobs.TryTransition(id, JobStatus.Held, JobStatus.Canceled))
            return Results.Conflict(new ErrorResponse($"Job {id} is {job.Status}, not held, so it can't be canceled."));
        spool.Delete(id);
        loggers.CreateLogger("TapQueue.Server.Api.AdminApi").LogInformation("Admin canceled job {Id} ({Name}) of {User}", id, job.Name, job.Username ?? "nobody");
        return Results.NoContent();
    }

    private static async Task<IResult> CreatePrinter(CreatePrinterRequest request, PrinterStore store, PrinterRegistry printers, CancellationToken ct)
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
        await printers.ProbeAsync(printer, ct);
        return Results.Ok(printers.ToAdminDto(printer));
    }

    private static async Task<IResult> UpdatePrinter(string id, UpdatePrinterRequest request, PrinterStore store, PrinterRegistry printers, CancellationToken ct)
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
        await printers.ProbeAsync(printer, ct);
        return Results.Ok(printers.ToAdminDto(printer));
    }

    private static IResult DeletePrinter(string id, PrinterStore store, PrinterRegistry printers)
    {
        var stations = store.StationsUsing(id);
        if (stations.Count > 0)
            return Results.Conflict(new ErrorResponse(
                $"{(stations.Count == 1 ? "Station" : "Stations")} {string.Join(", ", stations)} {(stations.Count == 1 ? "releases" : "release")} to printer \"{id}\". " +
                "Move them with `tapqueue-admin stations move` or remove them first."));
        if (!store.Delete(id))
            return Results.NotFound(new ErrorResponse($"No printer \"{id}\"."));
        printers.Forget(id);
        return Results.NoContent();
    }

    private static IResult CreateQueue(CreateQueueRequest request, QueueStore queues)
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
        return Results.Ok(ToAdminDto(queue));
    }

    private static IResult UpdateQueue(string id, UpdateQueueRequest request, QueueStore queues)
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
        return Results.Ok(ToAdminDto(queue));
    }

    private static QueueAdminDto ToAdminDto(QueueRecord q) =>
        new(q.Id, q.Name, q.Description, q.Location, q.Color, q.Duplex, q.DefaultMedia, $"/ipp/{q.Id}");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult CreateBadge(CreateBadgeRequest request, UserStore users, BadgeStore badges, UnknownTaps unknownTaps)
    {
        var card = BadgeStore.Normalize(request.Card ?? "");
        if (card.Length == 0)
            return Results.BadRequest(new ErrorResponse("card is required"));
        var user = users.FindByUsername(request.Username ?? "");
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{request.Username}\"."));
        if (badges.FindByCard(card) is { } existing)
            return Results.Conflict(new ErrorResponse($"That card is already linked to {existing.Username} (badge {existing.Id})."));

        var badge = badges.Add(user.Id, card);
        unknownTaps.Remove(card);
        return Results.Ok(badge.ToDto());
    }

    private static IResult CreateStation(CreateStationRequest request, StationStore stations, PrinterRegistry printers)
    {
        var id = request.Id?.Trim();
        if (!Ids.IsValid(id))
            return Results.BadRequest(new ErrorResponse($"Station id must be {Ids.Rule}."));
        var printer = printers.Find(request.PrinterId ?? "");
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\". See `tapqueue-admin printers`."));
        if (stations.Get(id!) is not null)
            return Results.Conflict(new ErrorResponse($"Station \"{id}\" already exists."));

        var token = Tokens.New();
        var station = stations.Create(id!, printer.Id, Tokens.Hash(token));
        return Results.Ok(new StationTokenResponse(station.ToDto(), token));
    }

    private static IResult UpdateStation(string id, UpdateStationRequest request, StationStore stations, PrinterRegistry printers)
    {
        if (stations.Get(id) is null)
            return Results.NotFound(new ErrorResponse($"No station \"{id}\"."));
        if (printers.Find(request.PrinterId ?? "") is not { } printer)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\". See `tapqueue-admin printers`."));
        return Results.Ok(stations.SetPrinter(id, printer.Id)!.ToDto());
    }

    private static IResult ResetStationToken(string id, StationStore stations)
    {
        var station = stations.Get(id);
        if (station is null)
            return Results.NotFound(new ErrorResponse($"No station \"{id}\"."));
        var token = Tokens.New();
        stations.SetTokenHash(station.Id, Tokens.Hash(token));
        return Results.Ok(new StationTokenResponse(station.ToDto(), token));
    }

    private static IResult CreateUser(CreateUserRequest request, UserStore users)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrEmpty(username))
            return Results.BadRequest(new ErrorResponse("username is required"));
        if (users.FindByUsername(username) is not null)
            return Results.Conflict(new ErrorResponse($"User \"{username}\" already exists."));

        var token = Tokens.New();
        var user = users.Create(username, string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim(), Tokens.Hash(token));
        return Results.Ok(new UserTokenResponse(user.ToDto(), token));
    }

    private static IResult UpdateUser(string username, UpdateUserRequest request, UserStore users, ILoggerFactory loggers)
    {
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        if (Clean(request.DisplayName) is { } displayName)
            user = users.SetDisplayName(user.Id, displayName)!;
        if (request.Disabled is { } disabled && disabled != user.Disabled)
        {
            user = users.SetDisabled(user.Id, disabled)!;
            loggers.CreateLogger("TapQueue.Server.Api.AdminApi").LogInformation(
                disabled ? "Admin disabled {User}; they're signed out and can't print or release" : "Admin re-enabled {User}", user.Username);
        }
        return Results.Ok(user.ToAdminDto());
    }

    /// <summary>Cancels the user's held jobs, then deletes them with their cards. Job history keeps their name.</summary>
    private static IResult DeleteUser(string username, UserStore users, JobStore jobs, Spool spool, ILoggerFactory loggers)
    {
        if (users.FindByUsername(username) is not { } user)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        var canceled = 0;
        foreach (var job in jobs.ListForUser(user.Id, heldOnly: true))
        {
            if (!jobs.TryTransition(job.Id, JobStatus.Held, JobStatus.Canceled)) continue;
            spool.Delete(job.Id);
            canceled++;
        }
        users.Delete(user.Id);
        loggers.CreateLogger("TapQueue.Server.Api.AdminApi").LogInformation(
            "Admin deleted user {User} ({Canceled} held jobs canceled)", user.Username, canceled);
        return Results.NoContent();
    }

    private static IResult ResetToken(string username, UserStore users)
    {
        var user = users.FindByUsername(username);
        if (user is null)
            return Results.NotFound(new ErrorResponse($"No user \"{username}\"."));
        var token = Tokens.New();
        users.SetTokenHash(user.Id, Tokens.Hash(token));
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
        return Results.Ok(await release.ReleaseAsync(user, printer, request.JobIds, ct));
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
