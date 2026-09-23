using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Stations report in with heartbeats and take settings, commands and builds from the server.</summary>
public sealed class StationControlTests : IAsyncLifetime
{
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

    private async Task<StationHeartbeatResponse> Heartbeat(int settingsVersion = 1, string? sha = null) =>
        await TestServer.ReadAsync<StationHeartbeatResponse>(await _station.PostAsJsonAsync("/api/v1/station/heartbeat",
            new StationHeartbeatRequest("0.3.0+test", DateTimeOffset.UtcNow.AddMinutes(-5), "pcprox", "ok", sha, settingsVersion), TapQueueJson.Options));

    private async Task<StationDto> Update(UpdateStationRequest request) =>
        await TestServer.ReadAsync<StationDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/stations/lobby", request, TapQueueJson.Options));

    private async Task<StationDto> Admin() =>
        (await _server.Admin.GetFromJsonAsync<StationDto>("/api/v1/admin/stations/lobby", TapQueueJson.Options))!;

    [Fact]
    public async Task AHeartbeatMakesTheStationOnlineAndReturnsItsSettings()
    {
        Assert.False((await Admin()).Online);

        var beat = await Heartbeat();
        var station = await Admin();

        Assert.True(station.Online);
        Assert.Equal("0.3.0+test", station.Version);
        Assert.Equal("ok", station.ReaderStatus);
        Assert.Equal(TestServer.PrinterId, beat.Settings.PrinterId);
        Assert.Null(beat.Settings.Reader); // station.toml decides
        Assert.Null(beat.Command);
    }

    [Fact]
    public async Task ChangedSettingsGetANewVersionAndCanBeHandedBack()
    {
        var before = (await Heartbeat()).Settings.Version;

        var changed = await Update(new UpdateStationRequest(Reader: "keyboard", Device: "/dev/input/event3", RepeatSeconds: 8, Name: "Front desk"));
        var reset = await Update(new UpdateStationRequest(Reset: ["reader", "device"]));

        Assert.True(changed.Settings!.Version > before);
        Assert.Equal(("keyboard", "/dev/input/event3", 8), (changed.Settings.Reader, changed.Settings.Device, changed.Settings.RepeatSeconds));
        Assert.Equal("Front desk", changed.Name);
        Assert.Null(reset.Settings!.Reader);
        Assert.Null(reset.Settings.Device);
        Assert.Equal(8, reset.Settings.RepeatSeconds);
    }

    [Fact]
    public async Task BadSettingsAreRejected()
    {
        var response = await _server.Admin.PatchAsJsonAsync("/api/v1/admin/stations/lobby", new UpdateStationRequest(Reader: "laser"), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARestartIsDeliveredOnce()
    {
        await _server.Admin.PostAsync("/api/v1/admin/stations/lobby/restart", null);

        Assert.Equal(StationCommand.Restart, (await Admin()).PendingCommand);
        Assert.Equal(StationCommand.Restart, (await Heartbeat()).Command);
        Assert.Null((await Heartbeat()).Command);
    }

    [Fact]
    public async Task ADisabledStationReleasesNothing()
    {
        using var client = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);
        await Update(new UpdateStationRequest(Enabled: false, MaintenanceMessage: "Printer being serviced, use the one upstairs."));

        var tap = await TestServer.ReadAsync<StationTapResponse>(await _station.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest("CARD-1"), TapQueueJson.Options));

        Assert.Equal(TapOutcome.StationDisabled, tap.Outcome);
        Assert.Equal("Printer being serviced, use the one upstairs.", tap.Message);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task StationsAreOfferedThePublishedBuildAndCanDownloadIt()
    {
        var program = Encoding.ASCII.GetBytes("\u007fELF pretend station build");
        var published = await TestServer.ReadAsync<StationBuildDto>(await _server.Admin.PostAsync(
            "/api/v1/admin/station-builds?version=0.3.1", new ByteArrayContent(program)));

        var beat = await Heartbeat();
        var download = await _station.GetByteArrayAsync(beat.Build!.DownloadPath);
        var anonymous = await _server.NewClient().GetAsync(beat.Build.DownloadPath);

        Assert.Equal(published.Sha256, beat.Build.Sha256);
        Assert.Equal(program, download);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
