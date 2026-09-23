using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Server settings changed while it runs, its live log and restarting it.</summary>
public sealed class ServerControlTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpResponseMessage> PatchSettings(UpdateServerSettingsRequest request) =>
        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings", request, TapQueueJson.Options);

    [Fact]
    public async Task SettingsDefaultToServerTomlAndCanBeChangedAndReset()
    {
        var before = (await _server.Admin.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options))!;
        Assert.Equal(24, before.HoldHours);
        Assert.Empty(before.ChangedSettings!);

        var changed = await TestServer.ReadAsync<ServerInfoDto>(await PatchSettings(new(HoldHours: 48, SessionTimeoutMinutes: 15)));
        Assert.Equal(48, changed.HoldHours);
        Assert.Equal(15, changed.SessionTimeoutMinutes);
        Assert.Equal(["holdHours", "sessionTimeoutMinutes"], changed.ChangedSettings!);

        var reset = await TestServer.ReadAsync<ServerInfoDto>(await PatchSettings(new(Reset: ["holdHours"])));
        Assert.Equal(24, reset.HoldHours);
        Assert.Equal(["sessionTimeoutMinutes"], reset.ChangedSettings!);
    }

    [Fact]
    public async Task NewJobsAreHeldForTheChangedTime()
    {
        await TestServer.ReadAsync<ServerInfoDto>(await PatchSettings(new(HoldHours: 2)));
        using var alice = await _server.SignInAsync("alice");

        await _server.PrintAsync("report.pdf", "%PDF-1.4 test"u8.ToArray());

        var job = Assert.Single((await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options))!);
        Assert.InRange(job.ExpiresAt - job.SubmittedAt, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 1)]
    public async Task OutOfRangeSettingsAreRefused(int? holdHours, int? sessionTimeout)
    {
        var response = await PatchSettings(new(holdHours, sessionTimeout));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheLogShowsWhatJustHappened()
    {
        using var alice = await _server.SignInAsync("alice");

        var lines = (await _server.Admin.GetFromJsonAsync<List<LogLineDto>>("/api/v1/admin/server/log", TapQueueJson.Options))!;
        var signIn = Assert.Single(lines, l => l.Message.StartsWith("alice signed in"));
        Assert.Equal("info", signIn.Level);
        Assert.Equal("ClientApi", signIn.Category);

        var after = (await _server.Admin.GetFromJsonAsync<List<LogLineDto>>($"/api/v1/admin/server/log?after={signIn.Id}", TapQueueJson.Options))!;
        Assert.DoesNotContain(after, l => l.Id <= signIn.Id);
    }

    [Fact]
    public async Task TheLogStreamSendsNewLinesAsTheyHappen()
    {
        using var stream = await _server.Admin.GetStreamAsync("/api/v1/admin/server/log/stream");
        using var reader = new StreamReader(stream);
        using var bob = await _server.SignInAsync("bob");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null && !line.Contains("bob signed in"))
        {
        }
        Assert.NotNull(line);
        Assert.StartsWith("data: ", line);
    }

    [Fact]
    public async Task RestartIsRefusedWhenNothingWouldStartTheServerAgain()
    {
        var info = (await _server.Admin.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options))!;
        Assert.False(info.CanRestart); // tests don't run under systemd

        var response = await _server.Admin.PostAsync("/api/v1/admin/server/restart", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
