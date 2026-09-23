using System.Net.Http.Json;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>End to end: a badge tap at a release station prints the user's held jobs.</summary>
public sealed class StationTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;
    private HttpClient _station = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var created = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        _station = _server.NewClient(created.Token);
    }

    public async Task DisposeAsync()
    {
        _station.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<StationTapResponse> TapAsync(string card) =>
        await TestServer.ReadAsync<StationTapResponse>(
            await _station.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest(card), TapQueueJson.Options));

    [Fact]
    public async Task StationLearnsItsPrinterWhenItChecksIn()
    {
        var info = await _station.GetFromJsonAsync<StationInfoResponse>("/api/v1/station/", TapQueueJson.Options);

        Assert.Equal("lobby", info?.StationId);
        Assert.Equal(TestServer.PrinterId, info?.Printer.Id);
    }

    [Fact]
    public async Task TapReleasesTheBadgeOwnersJobs()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "04A1B2C3"), TapQueueJson.Options);
        await _server.PrintAsync("Report", Pdf);

        var tap = await TapAsync("04a1b2c3\n");

        Assert.Equal(TapOutcome.Released, tap.Outcome);
        Assert.Equal("alice", tap.User?.Username);
        Assert.Equal("Report", Assert.Single(_server.Printer.Jobs).Name);
    }

    [Fact]
    public async Task TapWithNothingHeldSaysSo()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "1234"), TapQueueJson.Options);

        Assert.Equal(TapOutcome.NoJobs, (await TapAsync("1234")).Outcome);
    }

    [Fact]
    public async Task UnknownBadgeCanBeEnrolledFromTheLastTap()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf);

        var first = await TapAsync("99887766");
        Assert.Equal(TapOutcome.UnknownBadge, first.Outcome);
        var unknown = Assert.Single(await _server.Admin.GetFromJsonAsync<List<UnknownTapDto>>("/api/v1/admin/badges/unknown", TapQueueJson.Options) ?? []);
        Assert.Equal("lobby", unknown.StationId);

        await TestServer.ReadAsync<BadgeDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges",
            new CreateBadgeRequest("alice", unknown.Card), TapQueueJson.Options));
        Assert.Equal(TapOutcome.Released, (await TapAsync("99887766")).Outcome);
        Assert.Empty(await _server.Admin.GetFromJsonAsync<List<UnknownTapDto>>("/api/v1/admin/badges/unknown", TapQueueJson.Options) ?? []);
    }

    [Fact]
    public async Task FailedTapKeepsJobsHeld()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "1234"), TapQueueJson.Options);
        await _server.PrintAsync("Report", Pdf);
        _server.Printer.PrintJobStatus = Ipp.IppStatus.ClientErrorNotPossible;

        var tap = await TapAsync("1234");

        Assert.Equal(TapOutcome.Failed, tap.Outcome);
        Assert.Equal("held", Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []).Status);
    }
}
