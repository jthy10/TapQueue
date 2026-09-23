using TapQueue.Server.Data;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>Endpoints used by release stations. Authenticated with the station's own token.</summary>
public static class StationApi
{
    public static void MapStationApi(this IEndpointRouteBuilder app)
    {
        var station = app.MapGroup("/api/v1/station").AddEndpointFilter(RequireStation);

        station.MapGet("/", (HttpContext http, PrinterRegistry printers) =>
        {
            var current = CurrentStation(http);
            return printers.Find(current.PrinterId) is { } printer
                ? Results.Ok(new StationInfoResponse(current.Id, printers.ToDto(printer)))
                : Results.Conflict(new ErrorResponse($"Station \"{current.Id}\" is assigned to printer \"{current.PrinterId}\", which doesn't exist."));
        });
        station.MapPost("/tap", Tap);
    }

    private static async Task<IResult> Tap(StationTapRequest request, HttpContext http, BadgeStore badges, UserStore users,
        PrinterRegistry printers, ReleaseService release, UnknownTaps unknownTaps, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("TapQueue.Server.Api.StationApi");
        var station = CurrentStation(http);
        var card = BadgeStore.Normalize(request.Card ?? "");
        if (card.Length == 0)
            return Results.BadRequest(new ErrorResponse("card is required"));

        var printer = printers.Find(station.PrinterId);
        if (printer is null)
            return Results.Conflict(new ErrorResponse($"Station \"{station.Id}\" is assigned to printer \"{station.PrinterId}\", which doesn't exist."));

        var badge = badges.Use(card);
        var user = badge is null ? null : users.FindById(badge.UserId);
        if (user is null)
        {
            unknownTaps.Add(card, station.Id);
            logger.LogInformation("Unknown badge {Hint} tapped at station {Station}", BadgeStore.Hint(card), station.Id);
            return Results.Ok(new StationTapResponse(TapOutcome.UnknownBadge, "Badge not recognized. Ask an admin to link it to your account.", null, []));
        }

        // Not tied to the request: the station giving up on a slow printer shouldn't leave a job half-sent.
        var result = await release.ReleaseAsync(user, printer, jobIds: null, CancellationToken.None);
        var sent = result.Results.Count(r => r.Success);
        var failed = result.Results.Count - sent;
        logger.LogInformation("{User} tapped at station {Station}: {Sent} released, {Failed} failed", user.Username, station.Id, sent, failed);

        var (outcome, message) = (sent, failed) switch
        {
            (0, 0) => (TapOutcome.NoJobs, $"Hi {user.DisplayName}, you have nothing waiting to print."),
            (_, 0) => (TapOutcome.Released, $"Hi {user.DisplayName}, printing {Plural(sent, "job")}."),
            (0, _) => (TapOutcome.Failed, $"Couldn't print: {result.Results.First(r => !r.Success).Error}"),
            _ => (TapOutcome.Failed, $"Printing {Plural(sent, "job")}; {failed} couldn't be sent and are still held."),
        };
        return Results.Ok(new StationTapResponse(outcome, message, user.ToDto(), result.Results));
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static StationRecord CurrentStation(HttpContext http) => (StationRecord)http.Items[nameof(StationRecord)]!;

    private static async ValueTask<object?> RequireStation(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = ClientApi.BearerToken(http);
        var station = token is null ? null : http.RequestServices.GetRequiredService<StationStore>().Touch(Tokens.Hash(token), http.ClientIp());
        if (station is null)
            return Results.Json(new ErrorResponse("Unknown station token."), statusCode: StatusCodes.Status401Unauthorized);

        http.Items[nameof(StationRecord)] = station;
        return await next(context);
    }
}
