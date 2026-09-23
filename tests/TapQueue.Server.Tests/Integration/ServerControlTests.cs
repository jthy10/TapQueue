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
}
