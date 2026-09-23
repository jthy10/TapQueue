using System.Net.Http.Json;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Admin changes, prints, releases, taps and sign-ins end up in the activity log.</summary>
public sealed class ActivityLogTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");

    private static async Task<List<EventDto>> Events(TestServer server, string query = "") =>
        (await server.Admin.GetFromJsonAsync<List<EventDto>>($"/api/v1/admin/events{query}", TapQueueJson.Options))!;

    [Fact]
    public async Task APrintAndATapAreRecordedAgainstTheUser()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Pdf);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);
        var station = await TestServer.ReadAsync<StationTokenResponse>(await server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        using var stationClient = server.NewClient(station.Token);
        await stationClient.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest("CARD-1"), TapQueueJson.Options);

        var alice = await Events(server, "?subject=user:alice");

        Assert.Equal(
            ["admin", "signin", "job", "admin", "job"],
            alice.Select(e => e.Category).Reverse());
        Assert.Contains("released to Office printer at station lobby", alice[0].Message);
        Assert.Equal("station:lobby", alice[0].Actor);
    }

    [Fact]
    public async Task AdminChangesAreRecordedAndFilterable()
    {
        await using var server = await TestServer.StartAsync();
        await server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}", new UpdatePrinterRequest(Name: "Front desk"), TapQueueJson.Options);

        var admin = await Events(server, "?category=admin");
        var printer = await Events(server, $"?subject=printer:{TestServer.PrinterId}");

        Assert.All(admin, e => Assert.Equal("admin", e.Actor));
        Assert.Contains(admin, e => e.Message.StartsWith("Added queue"));
        Assert.Equal(["Changed printer Front desk (office).", "Added printer Office printer (office) at " + server.Printer.Uri + "."],
            printer.Select(e => e.Message));
    }

    [Fact]
    public async Task UnknownTapsAreRecordedAgainstTheStation()
    {
        await using var server = await TestServer.StartAsync();
        var station = await TestServer.ReadAsync<StationTokenResponse>(await server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        using var stationClient = server.NewClient(station.Token);

        await stationClient.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest("12345678"), TapQueueJson.Options);
        var taps = await Events(server, "?category=tap");

        Assert.Equal("Unknown card …5678 tapped at station lobby.", taps.Single().Message);
    }

    [Fact]
    public async Task PagesBackWithBefore()
    {
        await using var server = await TestServer.StartAsync();
        var all = await Events(server);

        var older = await Events(server, $"?before={all[0].Id}&limit=1");

        Assert.Equal(all[1].Id, older.Single().Id);
    }
}
