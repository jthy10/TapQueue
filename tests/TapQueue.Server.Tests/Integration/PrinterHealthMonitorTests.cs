using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Printer health in the admin API and activity log, and releases held back from a stopped printer.</summary>
public sealed class PrinterHealthMonitorTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<PrinterAdminDto> CheckPrinterAsync() =>
        Assert.Single(await _server.Admin.GetFromJsonAsync<List<PrinterAdminDto>>("/api/v1/admin/printers?refresh=true", TapQueueJson.Options) ?? []);

    private async Task<List<EventDto>> PrinterEventsAsync() =>
        await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=printer", TapQueueJson.Options) ?? [];

    [Fact]
    public async Task HealthySupplyLevelsAndProblemsAreReported()
    {
        var healthy = (await CheckPrinterAsync()).Health!;
        Assert.Equal(PrinterHealthLevel.Ok, healthy.Level);
        Assert.Equal("idle", healthy.State);
        Assert.True(healthy.CanPrint);
        Assert.Equal(80, Assert.Single(healthy.Supplies).Level);

        _server.Printer.TonerLevel = 4;
        _server.Printer.Reasons = ["media-empty-error"];
        var printer = await CheckPrinterAsync();
        Assert.Equal(PrinterHealthLevel.Error, printer.Health!.Level);
        Assert.Equal(["Out of paper", "Black cartridge low (4%)"], printer.Health.Problems);
        Assert.Equal("Out of paper, Black cartridge low (4%)", printer.Printer.StateMessage);

        _server.Printer.TonerLevel = 80;
        _server.Printer.Reasons = ["none"];
        await CheckPrinterAsync();
        Assert.Equal(["Office printer is ready again.", "Office printer: Out of paper, Black cartridge low (4%)."],
            (await PrinterEventsAsync()).Select(e => e.Message));
    }

    [Fact]
    public async Task JobsStayHeldWhileThePrinterIsStoppedAndReleaseOnceItsFixed()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf);
        _server.Printer.State = IppPrinterState.Stopped;
        _server.Printer.Reasons = ["media-jam-error"];
        Assert.False((await CheckPrinterAsync()).Health!.CanPrint);

        var refused = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));
        var result = Assert.Single(refused.Results);
        Assert.False(result.Success);
        Assert.Equal("Office printer can't print right now (paper jam). Your jobs are still held.", result.Error);
        Assert.Empty(_server.Printer.Jobs);
        Assert.Equal("held", Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []).Status);

        // Fixed since the last check: the release checks again rather than trusting what it last saw.
        _server.Printer.State = IppPrinterState.Idle;
        _server.Printer.Reasons = ["none"];
        var released = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));
        Assert.True(Assert.Single(released.Results).Success);
        Assert.Single(_server.Printer.Jobs);
    }

    [Fact]
    public async Task UnreachablePrinterIsOffline()
    {
        await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(Uri: "ipp://127.0.0.1:9/ipp/print"), TapQueueJson.Options);

        var printer = await CheckPrinterAsync();
        Assert.Equal(PrinterHealthLevel.Offline, printer.Health!.Level);
        Assert.False(printer.Health.CanPrint);
        Assert.Null(printer.Health.State);
        Assert.StartsWith("Office printer is unreachable", (await PrinterEventsAsync())[0].Message);
    }
}
