using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>PCs checking in from the TapQueue service, updating them now, and signing people out of them.</summary>
public sealed class WorkstationTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<ClientSetupResponse> CheckIn(string computer = "TEST-PC", string? sha = "aaaa", string? error = null)
    {
        using var service = _server.NewClient();
        return await TestServer.ReadAsync<ClientSetupResponse>(await service.PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest(computer, "0.3.0+test", sha, error), TapQueueJson.Options));
    }

    private async Task<List<WorkstationDto>> Workstations() =>
        (await _server.Admin.GetFromJsonAsync<List<WorkstationDto>>("/api/v1/admin/workstations", TapQueueJson.Options))!;

    [Fact]
    public async Task APcShowsUpWhenItsServiceChecksInWithWhoIsSignedIn()
    {
        using var alice = await _server.SignInAsync("alice"); // from TEST-PC

        var setup = await CheckIn(error: "Access denied");
        Assert.Equal(TestServer.QueueId, Assert.Single(setup.Queues).Id);

        var pc = Assert.Single(await Workstations());
        Assert.Equal("TEST-PC", pc.Hostname);
        Assert.Equal("0.3.0+test", pc.Version);
        Assert.True(pc.Online);
        Assert.True(pc.UpToDate); // nothing published
        Assert.Equal("Access denied", pc.UpdateError);
        Assert.Equal("alice", Assert.Single(pc.Sessions).Username);
    }

    [Fact]
    public async Task UpdateNowIsHandedToThePcOnce()
    {
        await CheckIn();
        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin.PostAsync("/api/v1/admin/workstations/TEST-PC/update", null)).StatusCode);

        await _server.Admin.PostAsync("/api/v1/admin/client-builds?version=0.3.0%2Bnew", new ByteArrayContent("MZ new client"u8.ToArray()));
        Assert.False(Assert.Single(await Workstations()).UpToDate);
        Assert.Equal(HttpStatusCode.Accepted, (await _server.Admin.PostAsync("/api/v1/admin/workstations/test-pc/update", null)).StatusCode);

        Assert.Equal(WorkstationCommand.Update, (await CheckIn()).Command);
        Assert.Null((await CheckIn()).Command);
    }

    [Fact]
    public async Task ASignedOutClientIsToldSoAndItsJobsNoLongerGoToThatUser()
    {
        using var alice = await _server.SignInAsync("alice");
        var session = Assert.Single((await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options))!);

        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync($"/api/v1/admin/clients/{session.Id}")).StatusCode);

        var heartbeat = await alice.PostAsync("/api/v1/client/heartbeat", null);
        Assert.Equal(HttpStatusCode.Forbidden, heartbeat.StatusCode);
        Assert.Contains("signed you out", await heartbeat.Content.ReadAsStringAsync());
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options))!);

        await _server.PrintAsync("after.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice");
        var job = Assert.Single((await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);
        Assert.Null(job.Owner);

        Assert.Equal(HttpStatusCode.NotFound, (await _server.Admin.DeleteAsync($"/api/v1/admin/clients/{session.Id}")).StatusCode);
    }
}
