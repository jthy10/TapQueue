using TapQueue.Server.Data;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// The screen at a station: a page any browser can show (a tablet on the wall, an old phone, a
/// monitor on the reader box) that says what each tap did and whether the printer is ready. It
/// needs nothing from the station program. The page is opened with a link that carries the
/// station's screen key, made by an admin; without the key there's nothing to see.
/// </summary>
public static class StationScreenApi
{
    /// <summary>How long a screen's request waits for the next tap before it's answered anyway (with the printer's state).</summary>
    public static TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(20);

    public static void MapStationScreenApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/screen/{stationId}", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // The key is in the page's address; it mustn't travel anywhere else as a referrer.
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Stream(typeof(StationScreenApi).Assembly.GetManifestResourceStream("TapQueue.Server.Assets.station-screen.html")!,
                "text/html; charset=utf-8");
        });
        app.MapGet("/api/v1/screen/{stationId}", State);
    }

    private static async Task<IResult> State(string stationId, string? key, long? after, HttpContext http, StationStore stations, StationScreens screens,
        PrinterRegistry printers)
    {
        // The same answer whether the station doesn't exist, has no screen or the key is wrong.
        if (string.IsNullOrEmpty(key) || stations.ScreenKeyHash(stationId) is not { } hash || !Tokens.FixedTimeEquals(hash, Tokens.Hash(key)))
            return Results.NotFound(new ErrorResponse("No such station screen. Ask an admin for a new screen link."));

        var message = await screens.WaitAsync(stationId, after ?? 0, Wait, http.RequestAborted);
        if (stations.Get(stationId) is not { } station)
            return Results.NotFound(new ErrorResponse("No such station screen. Ask an admin for a new screen link."));
        var printer = printers.Find(station.PrinterId);
        var status = printer is null ? null : printers.StatusOf(printer.Id);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new StationScreenDto(
            station.DisplayName,
            printer?.Name ?? station.PrinterId,
            PrinterReady: status?.CanPrint ?? true,
            PrinterProblem: status is { CanPrint: false } ? status.Problem : null,
            station.Settings.Enabled,
            station.Settings.Enabled || station.Settings.MaintenanceMessage.Length == 0 ? null : station.Settings.MaintenanceMessage,
            message?.Seq ?? 0,
            message?.Outcome,
            message?.Message,
            message is null ? null : (long)Math.Max(0, (DateTimeOffset.UtcNow - message.At).TotalMilliseconds)));
    }
}
