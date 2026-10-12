using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Api;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The screen next to a station says what each tap did, to anyone with its link.</summary>
public sealed class StationScreenTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;
    private HttpClient _station = null!;
    private HttpClient _anyone = null!;
    private string _screen = null!;

    public async Task InitializeAsync()
    {
        StationScreenApi.Wait = TimeSpan.FromSeconds(3);
        _server = await TestServer.StartAsync();
        var created = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        _station = _server.NewClient(created.Token);
        _anyone = _server.NewClient();
        var link = await TestServer.ReadAsync<StationScreenLinkResponse>(await _server.Admin.PostAsync("/api/v1/admin/stations/lobby/screen", null));
        Assert.StartsWith("/screen/lobby?key=", link.Path);
        _screen = "/api/v1" + link.Path;
    }

    public async Task DisposeAsync()
    {
        _station.Dispose();
        _anyone.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<StationTapResponse> TapAsync(string card) =>
        await TestServer.ReadAsync<StationTapResponse>(
            await _station.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest(card), TapQueueJson.Options));

    private async Task<StationScreenDto> ScreenAsync(long after = 0) =>
        (await _anyone.GetFromJsonAsync<StationScreenDto>($"{_screen}&after={after}", TapQueueJson.Options))!;

    [Fact]
    public async Task ThePageIsThereAndTheStateNeedsTheKey()
    {
        var page = await _anyone.GetAsync("/screen/lobby");
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Tap your badge to print", await page.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await _anyone.GetAsync("/api/v1/screen/lobby")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _anyone.GetAsync("/api/v1/screen/lobby?key=guess")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _anyone.GetAsync(_screen.Replace("lobby", "nowhere"))).StatusCode);
    }

    [Fact]
    public async Task BeforeAnyTapItShowsThePrinter()
    {
        var screen = await ScreenAsync();

        Assert.Equal("lobby", screen.Station);
        Assert.Equal("Office printer", screen.Printer);
        Assert.True(screen.PrinterReady);
        Assert.Equal(0, screen.Seq);
        Assert.Null(screen.Message);
    }

    [Fact]
    public async Task EachKindOfTapShowsSomethingDifferent()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "04A1B2C3"), TapQueueJson.Options);

        await TapAsync("99999999");
        var unknown = await ScreenAsync();
        Assert.Equal(TapOutcome.UnknownBadge, unknown.Outcome);
        Assert.Contains("Badge not recognized", unknown.Message);

        await TapAsync("04a1b2c3");
        var nothing = await ScreenAsync(unknown.Seq);
        Assert.Equal(TapOutcome.NoJobs, nothing.Outcome);
        Assert.Contains("nothing waiting", nothing.Message);
        Assert.True(nothing.Seq > unknown.Seq);

        await _server.PrintAsync("Report", Pdf);
        await TapAsync("04a1b2c3");
        var released = await ScreenAsync(nothing.Seq);
        Assert.Equal(TapOutcome.Released, released.Outcome);
        Assert.Contains("printing 1 job", released.Message);
        Assert.InRange(released.AgeMs!.Value, 0, 5000);
    }

    [Fact]
    public async Task ARefusedReleaseSaysWhy()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "04A1B2C3"), TapQueueJson.Options);
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(0, QuotaPeriod.Month), TapQueueJson.Options)).EnsureSuccessStatusCode();
        await _server.PrintAsync("Report", Pdf);

        await TapAsync("04a1b2c3");

        var screen = await ScreenAsync();
        Assert.Equal(TapOutcome.Failed, screen.Outcome);
        Assert.Contains("pages you get a month", screen.Message);
    }

    [Fact]
    public async Task ATapThatPrintedNothingIsAlsoToldToThePersonsTray()
    {
        using var alice = await _server.SignInAsync("alice");
        using var bob = await _server.SignInAsync("bob");
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "04A1B2C3"), TapQueueJson.Options);
        Assert.Empty((await alice.GetFromJsonAsync<List<UserNoticeDto>>("/api/v1/me/notices", TapQueueJson.Options))!);

        await TapAsync("04a1b2c3");
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(0, QuotaPeriod.Month), TapQueueJson.Options)).EnsureSuccessStatusCode();
        await _server.PrintAsync("Report", Pdf, requestingUser: "alice");
        await TapAsync("04a1b2c3");

        var notices = (await alice.GetFromJsonAsync<List<UserNoticeDto>>("/api/v1/me/notices", TapQueueJson.Options))!;
        Assert.Equal(2, notices.Count);
        Assert.Equal("You tapped at Office printer, but nothing was waiting to print.", notices[0].Message);
        Assert.StartsWith("Your tap at Office printer didn't print: You've used all 0 pages", notices[1].Message);
        Assert.Equal(notices[1], Assert.Single((await alice.GetFromJsonAsync<List<UserNoticeDto>>($"/api/v1/me/notices?after={notices[0].Id}", TapQueueJson.Options))!));
        // Nobody else's.
        Assert.Empty((await bob.GetFromJsonAsync<List<UserNoticeDto>>("/api/v1/me/notices", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task AWaitingScreenHearsOfATapAtOnceAndOfAPrinterProblemSoon()
    {
        var before = await ScreenAsync();
        var waiting = ScreenAsync(before.Seq);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        var started = DateTimeOffset.UtcNow;
        await TapAsync("99999999");
        var tapped = await waiting;
        Assert.Equal(TapOutcome.UnknownBadge, tapped.Outcome);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));

        // No tap: the request is answered after the wait, with how the printer is now.
        _server.Printer.State = IppPrinterState.Stopped;
        _server.Printer.Reasons = ["media-jam-error"];
        var printers = _server.Service<PrinterRegistry>();
        await printers.ProbeAsync(printers.Find(TestServer.PrinterId)!, CancellationToken.None);
        var jammed = await ScreenAsync(tapped.Seq);
        Assert.False(jammed.PrinterReady);
        Assert.Contains("jam", jammed.PrinterProblem);
        Assert.Equal(tapped.Seq, jammed.Seq);
    }

    [Fact]
    public async Task ANewLinkReplacesTheOldOneAndTurningItOffEndsIt()
    {
        var link = await TestServer.ReadAsync<StationScreenLinkResponse>(await _server.Admin.PostAsync("/api/v1/admin/stations/lobby/screen", null));
        Assert.Equal(HttpStatusCode.NotFound, (await _anyone.GetAsync(_screen)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _anyone.GetAsync("/api/v1" + link.Path + "&after=0")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync("/api/v1/admin/stations/lobby/screen")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _anyone.GetAsync("/api/v1" + link.Path)).StatusCode);
    }
}
