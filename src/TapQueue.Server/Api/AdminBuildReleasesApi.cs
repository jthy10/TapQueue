using TapQueue.Server.Updates;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// /api/v1/admin/client-builds/release and /station-builds/release: the newest builds on GitHub, and
/// publishing them without downloading and uploading the programs by hand.
/// </summary>
public static class AdminBuildReleasesApi
{
    public static void MapBuildReleasesApi(this RouteGroupBuilder admin)
    {
        // ?refresh=true asks GitHub (at most once a minute); without it, what the last check found.
        admin.MapGet("/client-builds/release", async (BuildUpdater updater, bool? refresh, CancellationToken ct) =>
            refresh == true ? await updater.CheckClientsAsync(ct) : updater.ClientStatus());
        admin.MapPost("/client-builds/release", (PublishReleaseRequest request, BuildUpdater updater, CancellationToken ct) =>
            Publish(() => updater.PublishClientsAsync(request.Version ?? "", ct)));
        admin.MapGet("/station-builds/release", async (BuildUpdater updater, bool? refresh, CancellationToken ct) =>
            refresh == true ? await updater.CheckStationsAsync(ct) : updater.StationStatus());
        admin.MapPost("/station-builds/release", (PublishReleaseRequest request, BuildUpdater updater, CancellationToken ct) =>
            Publish(() => updater.PublishStationAsync(request.Version ?? "", ct)));
    }

    private static async Task<IResult> Publish(Func<Task<BuildReleaseDto>> publish)
    {
        try
        {
            return Results.Ok(await publish());
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }
}
