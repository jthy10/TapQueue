using TapQueue.Server.Data;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/stations and /station-builds: release stations, their settings, commands and builds.</summary>
public static class AdminStationsApi
{
    private static readonly string[] Resettable = ["reader", "device", "repeatSeconds", "minCardLength"];

    public static void MapStationsApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/stations", (StationStore stations) => stations.List().Select(s => s.ToDto()));
        admin.MapGet("/stations/{id}", (string id, StationStore stations) =>
            stations.Get(id) is { } station ? Results.Ok(station.ToDto()) : NotFound(id));
        admin.MapPost("/stations", Create);
        admin.MapPatch("/stations/{id}", Update);
        admin.MapPost("/stations/{id}/token", ResetToken);
        admin.MapPost("/stations/{id}/restart", Restart);
        admin.MapDelete("/stations/{id}", Delete);

        admin.MapGet("/station-builds", (StationBuildStore builds) => builds.List().Select(b => b.ToStationDto()));
        admin.MapPost("/station-builds", PublishBuild);
    }

    private static IResult NotFound(string id) => Results.NotFound(new ErrorResponse($"No station \"{id}\"."));

    private static IResult Create(CreateStationRequest request, StationStore stations, PrinterRegistry printers, EventLog events)
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
        events.Admin(EventLog.Station(station.Id), $"Added station {station.Id}, releasing to {printer.Name}.");
        return Results.Ok(new StationTokenResponse(station.ToDto(), token));
    }

    /// <summary>Settings reach the station on its next heartbeat. A reader change makes it restart itself.</summary>
    private static IResult Update(string id, UpdateStationRequest request, StationStore stations, PrinterRegistry printers, EventLog events)
    {
        if (stations.Get(id) is not { } station)
            return NotFound(id);
        var s = station.Settings;
        var changes = new List<string>();

        if (request.PrinterId is not null)
        {
            if (printers.Find(request.PrinterId) is not { } printer)
                return Results.NotFound(new ErrorResponse($"Unknown printer \"{request.PrinterId}\". See `tapqueue-admin printers`."));
            if (!printer.Id.Equals(s.PrinterId, StringComparison.OrdinalIgnoreCase))
                changes.Add($"now releases to {printer.Name}");
            s = s with { PrinterId = printer.Id };
        }
        if (request.Reader is not null)
        {
            if (request.Reader is not ("keyboard" or "pcprox"))
                return Results.BadRequest(new ErrorResponse("reader must be \"keyboard\" or \"pcprox\"."));
            s = s with { Reader = request.Reader };
        }
        if (request.Device is not null) s = s with { Device = request.Device.Trim() };
        if (request.RepeatSeconds is { } repeat)
        {
            if (repeat is < 0 or > 3600) return Results.BadRequest(new ErrorResponse("repeatSeconds must be 0 to 3600."));
            s = s with { RepeatSeconds = repeat };
        }
        if (request.MinCardLength is { } min)
        {
            if (min is < 1 or > 64) return Results.BadRequest(new ErrorResponse("minCardLength must be 1 to 64."));
            s = s with { MinCardLength = min };
        }
        if (request.Feedback is not null)
        {
            if (request.Feedback is not StationSettings.NoFeedback)
                return Results.BadRequest(new ErrorResponse("feedback must be \"none\"; stations have no feedback devices yet."));
            s = s with { Feedback = request.Feedback };
        }
        if (request.MaintenanceMessage is not null) s = s with { MaintenanceMessage = request.MaintenanceMessage.Trim() };
        if (request.Enabled is { } enabled && enabled != s.Enabled)
        {
            changes.Add(enabled ? "back in service" : $"out of service{(s.MaintenanceMessage.Length > 0 ? $" (\"{s.MaintenanceMessage}\")" : "")}");
            s = s with { Enabled = enabled };
        }
        foreach (var name in request.Reset ?? [])
        {
            if (!Resettable.Contains(name))
                return Results.BadRequest(new ErrorResponse($"Can't reset \"{name}\"; only {string.Join(", ", Resettable)}."));
            s = name switch
            {
                "reader" => s with { Reader = null },
                "device" => s with { Device = null },
                "repeatSeconds" => s with { RepeatSeconds = null },
                _ => s with { MinCardLength = null },
            };
        }

        if (s != station.Settings)
        {
            station = stations.SetSettings(station.Id, s)!;
            if (changes.Count == 0) changes.Add("settings changed");
        }
        if (request.Name is not null || request.Location is not null)
        {
            station = stations.SetDetails(station.Id, request.Name?.Trim() ?? station.Name, request.Location?.Trim() ?? station.Location)!;
            changes.Add("details changed");
        }
        if (changes.Count > 0)
            events.Admin(EventLog.Station(station.Id), $"Station {station.Id}: {string.Join("; ", changes)}.");
        return Results.Ok(station.ToDto());
    }

    private static IResult ResetToken(string id, StationStore stations, EventLog events)
    {
        if (stations.Get(id) is not { } station)
            return NotFound(id);
        var token = Tokens.New();
        stations.SetTokenHash(station.Id, Tokens.Hash(token));
        events.Admin(EventLog.Station(station.Id), $"Made a new token for station {station.Id}; the old one stopped working.");
        return Results.Ok(new StationTokenResponse(station.ToDto(), token));
    }

    private static IResult Restart(string id, StationStore stations, EventLog events)
    {
        if (stations.Get(id) is not { } station)
            return NotFound(id);
        stations.SetCommand(station.Id, StationCommand.Restart);
        events.Admin(EventLog.Station(station.Id), $"Asked station {station.Id} to restart.");
        return Results.Ok(stations.Get(station.Id)!.ToDto());
    }

    private static IResult Delete(string id, StationStore stations, EventLog events)
    {
        if (!stations.Delete(id))
            return NotFound(id);
        events.Admin(EventLog.Station(id), $"Removed station {id}.");
        return Results.NoContent();
    }

    /// <summary>The request body is the tapqueue-station program. Stations install it on their next heartbeat.</summary>
    private static async Task<IResult> PublishBuild(HttpContext http, string? version, StationBuildStore builds, EventLog events)
    {
        version = AdminApi.Clean(version);
        if (version is null)
            return Results.BadRequest(new ErrorResponse("version is required, e.g. ?version=0.3.0+1a2b3c4."));
        BuildRecord build;
        try
        {
            build = await builds.PublishAsync(version, http.Request.Body, http.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        events.Admin(null, $"Published station build {build.Version}; stations install it on their next heartbeat.");
        return Results.Ok(build.ToStationDto());
    }
}
