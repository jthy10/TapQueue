using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Clients sending crash reports, and an admin reading them.</summary>
public sealed class CrashReportTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpResponseMessage> ReportAsync(string computer, string message, string? platform = ClientPlatform.Windows, string? details = "at Foo()") =>
        await _server.NewClient().PostAsJsonAsync("/api/v1/client/crash",
            new CrashReportRequest(computer, CrashProgram.Service, platform, "0.5.0+test", DateTimeOffset.UtcNow, message, details),
            TapQueueJson.Options);

    private async Task<List<CrashReportDto>> CrashesAsync(string query = "") =>
        (await _server.Admin.GetFromJsonAsync<List<CrashReportDto>>("/api/v1/admin/crashes" + query, TapQueueJson.Options))!;

    [Fact]
    public async Task AReportedCrashIsKeptForTheAdminAndLogged()
    {
        (await ReportAsync("UP", "Boom")).EnsureSuccessStatusCode();

        var crash = Assert.Single(await CrashesAsync());
        Assert.Equal(("UP", CrashProgram.Service, ClientPlatform.Windows, "0.5.0+test", "Boom", "at Foo()"),
            (crash.Computer, crash.Program, crash.Platform, crash.Version, crash.Message, crash.Details));
        var events = await _server.Admin.GetStringAsync("/api/v1/admin/events?limit=5");
        Assert.Contains("TapQueue service on UP", events);
    }

    [Fact]
    public async Task CrashesCanBeListedPerPcAndCleared()
    {
        await ReportAsync("UP", "One");
        await ReportAsync("linux-pc", "Two", ClientPlatform.Linux);

        Assert.Equal("Two", Assert.Single(await CrashesAsync("?computer=LINUX-PC")).Message);

        (await _server.Admin.DeleteAsync("/api/v1/admin/crashes?computer=UP")).EnsureSuccessStatusCode();
        Assert.Equal(["linux-pc"], (await CrashesAsync()).Select(c => c.Computer));
    }

    [Fact]
    public async Task LongDetailsAreCutAndBadReportsRejected()
    {
        await ReportAsync("UP", "Big", details: new string('x', 100_000));

        Assert.Equal(32_000, Assert.Single(await CrashesAsync()).Details!.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync("UP", "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReportAsync("UP", "x", platform: "amiga")).StatusCode);
    }

    [Fact]
    public async Task ReadingCrashesNeedsTheAdminToken()
    {
        using var anonymous = _server.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/crashes")).StatusCode);
    }
}
