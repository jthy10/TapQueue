using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>Endpoints used by tapqueue-admin. Authenticated with admin.token from server.toml.</summary>
public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(RequireAdmin);

        admin.MapGet("/users", (UserStore users) => users.List().Select(u => u.ToDto()));
        admin.MapPost("/users", CreateUser);
        admin.MapPost("/users/{username}/token", ResetToken);
        admin.MapGet("/jobs", (JobStore jobs, string? status) => jobs.List(status).Select(j => j.ToDto()));
        admin.MapGet("/printers", async (PrinterRegistry printers, bool? refresh, CancellationToken ct) =>
        {
            if (refresh == true)
                await Task.WhenAll(printers.All.Select(p => printers.ProbeAsync(p, ct)));
            return printers.All.Select(printers.ToDto);
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
        admin.MapDelete("/badges/{id:long}", (long id, BadgeStore badges) =>
            badges.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse($"No badge {id}.")));
        admin.MapGet("/badges/unknown", (UnknownTaps taps) => taps.Recent());

        admin.MapGet("/stations", (StationStore stations) => stations.List().Select(s => s.ToDto()));
        admin.MapPost("/stations", CreateStation);
        admin.MapPost("/stations/{id}/token", ResetStationToken);
        admin.MapDelete("/stations/{id}", (string id, StationStore stations) =>
            stations.Delete(id) ? Results.NoContent() : Results.NotFound(new ErrorResponse($"No station \"{id}\".")));
    }

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
        if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return Results.BadRequest(new ErrorResponse("Station id must be letters, digits, '-' or '_'."));
        var printer = printers.Find(request.PrinterId ?? "");
        if (printer is null)
            return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\". It must be a [[printers]] id from server.toml."));
        if (stations.Get(id) is not null)
            return Results.Conflict(new ErrorResponse($"Station \"{id}\" already exists."));

        var token = Tokens.New();
        var station = stations.Create(id, printer.Id, Tokens.Hash(token));
        return Results.Ok(new StationTokenResponse(station.ToDto(), token));
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
        var expected = http.RequestServices.GetRequiredService<ServerConfig>().Admin.Token;
        var token = ClientApi.BearerToken(http);
        if (token is null || !Tokens.FixedTimeEquals(token, expected))
            return Results.Json(new ErrorResponse("Admin token required."), statusCode: StatusCodes.Status401Unauthorized);
        return await next(context);
    }
}
