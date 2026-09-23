using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Disabling a user stops them signing in, printing and releasing; deleting one keeps their job history.</summary>
public sealed class UserLifecycleTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");

    private static Task<HttpResponseMessage> Patch(TestServer server, string username, UpdateUserRequest body) =>
        server.Admin.PatchAsJsonAsync($"/api/v1/admin/users/{username}", body, TapQueueJson.Options);

    private static async Task<List<JobDto>> Jobs(TestServer server) =>
        (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!;

    [Fact]
    public async Task DisabledUserCantSignInReleaseOrTap()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Pdf);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);
        var station = await TestServer.ReadAsync<StationTokenResponse>(await server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        using var stationClient = server.NewClient(station.Token);

        var disabled = await TestServer.ReadAsync<UserAdminDto>(await Patch(server, "alice", new UpdateUserRequest(Disabled: true)));
        var heartbeat = await client.PostAsync("/api/v1/client/heartbeat", null);
        var tap = await TestServer.ReadAsync<StationTapResponse>(await stationClient.PostAsJsonAsync(
            "/api/v1/station/tap", new StationTapRequest("CARD-1"), TapQueueJson.Options));

        Assert.NotNull(disabled.DisabledAt);
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode); // signed out everywhere
        Assert.Equal(TapOutcome.Disabled, tap.Outcome);
        Assert.Empty(server.Printer.Jobs);
        Assert.Equal("held", (await Jobs(server)).Single().Status); // kept for when they're re-enabled
    }

    [Fact]
    public async Task ReEnabledUserReleasesTheirHeldJobs()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Pdf);
        await Patch(server, "alice", new UpdateUserRequest(Disabled: true));

        var whileDisabled = await TestServer.ReadAsync<ReleaseResponse>(await server.Admin.PostAsJsonAsync("/api/v1/admin/release",
            new AdminReleaseRequest("alice", TestServer.PrinterId), TapQueueJson.Options));
        var enabled = await TestServer.ReadAsync<UserAdminDto>(await Patch(server, "alice", new UpdateUserRequest(Disabled: false)));
        var afterwards = await TestServer.ReadAsync<ReleaseResponse>(await server.Admin.PostAsJsonAsync("/api/v1/admin/release",
            new AdminReleaseRequest("alice", TestServer.PrinterId), TapQueueJson.Options));

        Assert.False(whileDisabled.Results.Single().Success);
        Assert.Null(enabled.DisabledAt);
        Assert.True(afterwards.Results.Single().Success);
    }

    [Fact]
    public async Task DisabledUserIsTurnedAwayAtSignInAndPrint()
    {
        // In dev mode the IPP username alone matches a job to a user, so this reaches the check.
        await using var server = await TestServer.StartAsync(authMode: "dev");
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("bob", null), TapQueueJson.Options);
        await Patch(server, "bob", new UpdateUserRequest(Disabled: true));

        var signIn = await server.NewClient().PostAsJsonAsync("/api/v1/client/session",
            new ClientSessionRequest("bob", null, "bob", "TEST-PC", "test"), TapQueueJson.Options);
        var response = await server.PrintAsync("secret.pdf", Pdf, requestingUser: "bob");

        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal(IppStatus.ClientErrorNotAuthorized, response.Code);
        Assert.Empty(await Jobs(server));
    }

    [Fact]
    public async Task RenamingAUserKeepsEverythingElse()
    {
        await using var server = await TestServer.StartAsync();
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);

        var renamed = await TestServer.ReadAsync<UserAdminDto>(await Patch(server, "alice", new UpdateUserRequest(DisplayName: "Alice Liddell")));

        Assert.Equal("Alice Liddell", renamed.DisplayName);
        Assert.Null(renamed.DisabledAt);
    }

    [Fact]
    public async Task DeletingAUserCancelsHeldJobsAndKeepsHistory()
    {
        await using var server = await TestServer.StartAsync();
        using var client = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Pdf);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);

        var deleted = await server.Admin.DeleteAsync("/api/v1/admin/users/alice");
        var job = (await Jobs(server)).Single();
        var badges = await server.Admin.GetFromJsonAsync<List<BadgeDto>>("/api/v1/admin/badges", TapQueueJson.Options);
        var recreated = await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("canceled", job.Status);
        Assert.Null(job.Owner);
        Assert.Equal("alice", job.FormerOwner);
        Assert.Empty(badges!);
        Assert.True(recreated.IsSuccessStatusCode);
    }
}
