using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>End to end: an admin publishes a Windows client build and signed-in clients are told to install it.</summary>
public sealed class ClientUpdateTests : IAsyncLifetime
{
    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ pretend TapQueueClient.exe");
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<ClientBuildDto> PublishAsync(string version, byte[] exe, string? platform = null) =>
        await TestServer.ReadAsync<ClientBuildDto>(await _server.Admin.PostAsync(
            $"/api/v1/admin/client-builds?version={Uri.EscapeDataString(version)}" + (platform is null ? "" : $"&platform={platform}"),
            new ByteArrayContent(exe)));

    private async Task<ClientSetupResponse> CheckInAsync(string computer, string? platform) =>
        await TestServer.ReadAsync<ClientSetupResponse>(await _server.NewClient().PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest(computer, "0.5.0+test", "aaaa", null, platform), TapQueueJson.Options));

    private static async Task<ClientHeartbeatResponse> HeartbeatAsync(HttpClient client) =>
        await TestServer.ReadAsync<ClientHeartbeatResponse>(await client.PostAsync("/api/v1/client/heartbeat", null));

    [Fact]
    public async Task HeartbeatHasNoBuildUntilOneIsPublished()
    {
        using var alice = await _server.SignInAsync("alice");

        Assert.Null((await HeartbeatAsync(alice)).ClientBuild);
    }

    [Fact]
    public async Task PublishedBuildIsOfferedAndDownloadable()
    {
        using var alice = await _server.SignInAsync("alice");

        var published = await PublishAsync("0.3.0+abc1234", Exe);
        var offered = (await HeartbeatAsync(alice)).ClientBuild;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Exe)), published.Sha256);
        Assert.Equal(published, offered);
        Assert.Equal(Exe, await alice.GetByteArrayAsync(offered!.DownloadPath));
    }

    [Fact]
    public async Task NewestPublishedBuildWins()
    {
        using var alice = await _server.SignInAsync("alice");

        await PublishAsync("0.3.0", Exe);
        var newer = await PublishAsync("0.3.1", [.. Exe, (byte)'!']);

        Assert.Equal(newer.Sha256, (await HeartbeatAsync(alice)).ClientBuild?.Sha256);
    }

    [Fact]
    public async Task ServiceGetsQueuesAndBuildWithoutSigningIn()
    {
        var published = await PublishAsync("0.3.0", Exe);
        using var service = _server.NewClient();

        var setup = await service.GetFromJsonAsync<ClientSetupResponse>("/api/v1/client/setup", TapQueueJson.Options);

        Assert.Equal(TestServer.QueueId, Assert.Single(setup!.Queues).Id);
        Assert.Equal(published, setup.ClientBuild);
        Assert.Equal(Exe, await service.GetByteArrayAsync(published.DownloadPath + "?computer=TEST-PC"));
    }

    [Fact]
    public async Task UnknownBuildIsNotFound()
    {
        using var service = _server.NewClient();

        using var response = await service.GetAsync($"/api/v1/client/builds/{new string('0', 64)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublishingNeedsTheAdminToken()
    {
        using var alice = await _server.SignInAsync("alice");

        using var response = await alice.PostAsync("/api/v1/admin/client-builds?version=0.3.0", new ByteArrayContent(Exe));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminSeesWhichVersionEachClientRuns()
    {
        using var alice = await _server.SignInAsync("alice");

        var clients = await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options);

        var client = Assert.Single(clients!);
        Assert.Equal(("alice", "TEST-PC", "test"), (client.Username, client.Hostname, client.ClientVersion));
    }

    [Fact]
    public async Task EachPlatformGetsItsOwnBuild()
    {
        var windows = await PublishAsync("0.5.0", Exe);
        var linux = await PublishAsync("0.5.0", "\u007fELF pretend tapqueue-client"u8.ToArray(), ClientPlatform.Linux);

        Assert.Equal((ClientPlatform.Windows, ClientPlatform.Linux), (windows.Platform, linux.Platform));
        Assert.Equal(windows, (await CheckInAsync("WIN-PC", null)).ClientBuild); // services before 0.5 send no platform
        Assert.Equal(windows, (await CheckInAsync("WIN-PC", ClientPlatform.Windows)).ClientBuild);
        Assert.Equal(linux, (await CheckInAsync("linux-pc", ClientPlatform.Linux)).ClientBuild);

        var pcs = await _server.Admin.GetFromJsonAsync<List<WorkstationDto>>("/api/v1/admin/workstations", TapQueueJson.Options);
        Assert.Equal([("linux-pc", ClientPlatform.Linux, false), ("WIN-PC", ClientPlatform.Windows, false)],
            pcs!.Select(p => (p.Hostname, p.Platform, p.UpToDate)));
    }

    [Fact]
    public async Task PublishingForOnePlatformLeavesTheOtherAlone()
    {
        await PublishAsync("0.5.0", "\u007fELF pretend tapqueue-client"u8.ToArray(), ClientPlatform.Linux);

        Assert.Null((await CheckInAsync("WIN-PC", ClientPlatform.Windows)).ClientBuild);
    }

    [Fact]
    public async Task UnknownPlatformsAreRejected()
    {
        using var publish = await _server.Admin.PostAsync("/api/v1/admin/client-builds?version=0.5.0&platform=amiga", new ByteArrayContent(Exe));
        using var checkIn = await _server.NewClient().PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest("PC", "0.5.0", "aaaa", null, "amiga"), TapQueueJson.Options);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (publish.StatusCode, checkIn.StatusCode));
    }
}
