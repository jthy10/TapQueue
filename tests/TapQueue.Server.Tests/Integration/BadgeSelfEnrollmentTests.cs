using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// People linking their own badge: someone signed in says which printer they're at, and the next card
/// nobody owns that's tapped at a station there becomes theirs.
/// </summary>
public sealed class BadgeSelfEnrollmentTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private HttpClient _station = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var created = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/stations",
            new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        _station = _server.NewClient(created.Token);
        // A station counts as there once it has reported in.
        (await _station.PostAsJsonAsync("/api/v1/station/heartbeat", new StationHeartbeatRequest("0.19.0+test", DateTimeOffset.UtcNow, "pcprox", "ok", null, 1), TapQueueJson.Options))
            .EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _station.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<StationTapResponse> Tap(string card) =>
        await TestServer.ReadAsync<StationTapResponse>(await _station.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest(card), TapQueueJson.Options));

    private static Task<HttpResponseMessage> Start(HttpClient tray, string printer = TestServer.PrinterId) =>
        tray.PostAsJsonAsync("/api/v1/me/badge-enrollment", new StartBadgeEnrollmentRequest(printer), TapQueueJson.Options);

    private static async Task<BadgeEnrollmentDto> Status(HttpClient tray) =>
        (await tray.GetFromJsonAsync<BadgeEnrollmentDto>("/api/v1/me/badge-enrollment", TapQueueJson.Options))!;

    [Fact]
    public async Task TheNextUnknownCardTappedThereBecomesTheirsAndThenPrints()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice");
        Assert.Equal(TapOutcome.UnknownBadge, (await Tap("04A1B2C3")).Outcome);
        Assert.Equal(BadgeEnrollmentState.None, (await Status(alice)).State);

        var waiting = await TestServer.ReadAsync<BadgeEnrollmentDto>(await Start(alice));
        Assert.Equal(BadgeEnrollmentState.Waiting, waiting.State);
        Assert.InRange(waiting.SecondsLeft, 1, 60);

        var tap = await Tap("04a1b2c3 ");
        Assert.Equal(TapOutcome.Enrolled, tap.Outcome);
        Assert.Equal("alice", tap.User!.Username);

        var linked = await Status(alice);
        Assert.Equal(BadgeEnrollmentState.Linked, linked.State);
        Assert.Equal("…B2C3", linked.CardHint);
        var mine = Assert.Single((await alice.GetFromJsonAsync<List<MyBadgeDto>>("/api/v1/me/badges", TapQueueJson.Options))!);
        Assert.True(mine.CanRemove);
        // It's off the list of cards nobody owns, and it's in the activity log.
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<UnknownTapDto>>("/api/v1/admin/badges/unknown", TapQueueJson.Options))!);
        var taps = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=tap", TapQueueJson.Options);
        Assert.Contains(taps!, e => e.Message.Contains("alice linked card …B2C3 to their own account"));

        // The same card now releases her jobs.
        Assert.Equal(TapOutcome.Released, (await Tap("04A1B2C3")).Outcome);
    }

    [Fact]
    public async Task OnlyOnePersonWaitsAtAStationAndACardSomeoneOwnsIsNotTaken()
    {
        using var alice = await _server.SignInAsync("alice");
        using var bob = await _server.SignInAsync("bob");
        await TestServer.ReadAsync<BadgeDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("bob", "BOB-CARD"), TapQueueJson.Options));

        Assert.Equal(HttpStatusCode.OK, (await Start(alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Start(bob)).StatusCode);

        // Bob taps his own card while alice waits: it stays his, and she's still waiting.
        Assert.Equal(TapOutcome.NoJobs, (await Tap("BOB-CARD")).Outcome);
        Assert.Equal(BadgeEnrollmentState.Waiting, (await Status(alice)).State);
        Assert.Empty((await alice.GetFromJsonAsync<List<MyBadgeDto>>("/api/v1/me/badges", TapQueueJson.Options))!);

        // She gives up, and now bob can link a second card.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/me/badge-enrollment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Start(bob)).StatusCode);
        Assert.Equal("bob", (await Tap("SPARE-1")).User!.Username);
    }

    [Fact]
    public async Task ItNeedsAReaderThereAndCanBeTurnedOff()
    {
        using var alice = await _server.SignInAsync("alice");
        await TestServer.ReadAsync<PrinterAdminDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/printers",
            new CreatePrinterRequest("basement", _server.Printer.Uri, "Basement printer"), TapQueueJson.Options));

        Assert.Equal(HttpStatusCode.Conflict, (await Start(alice, "basement")).StatusCode);   // no station there
        Assert.Equal(HttpStatusCode.NotFound, (await Start(alice, "nowhere")).StatusCode);

        var info = await TestServer.ReadAsync<ServerInfoDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(Options: new Dictionary<string, string> { ["badgeSelfEnroll"] = "off" }), TapQueueJson.Options));
        Assert.Equal("off", Assert.Single(info.Options!, o => o.Name == "badge-self-enroll").Value);
        Assert.Equal(HttpStatusCode.Forbidden, (await Start(alice)).StatusCode);
        Assert.Equal(TapOutcome.UnknownBadge, (await Tap("04A1B2C3")).Outcome);

        var bogus = await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(Options: new Dictionary<string, string> { ["badgeSelfEnroll"] = "maybe" }), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        var back = await TestServer.ReadAsync<ServerInfoDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(Reset: ["badgeSelfEnroll"]), TapQueueJson.Options));
        Assert.False(Assert.Single(back.Options!, o => o.Key == "badgeSelfEnroll").Changed);
    }

    [Fact]
    public async Task PeopleRemoveTheirOwnCardsButNotAnyoneElses()
    {
        using var alice = await _server.SignInAsync("alice");
        using var bob = await _server.SignInAsync("bob");
        var card = await TestServer.ReadAsync<BadgeDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "LOST-CARD"), TapQueueJson.Options));

        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/me/badges/{card.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/me/badges/{card.Id}")).StatusCode);
        Assert.Equal(TapOutcome.UnknownBadge, (await Tap("LOST-CARD")).Outcome);
    }
}
