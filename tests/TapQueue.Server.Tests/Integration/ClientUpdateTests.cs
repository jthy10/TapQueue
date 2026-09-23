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

    private async Task<ClientBuildDto> PublishAsync(string version, byte[] exe) =>
        await TestServer.ReadAsync<ClientBuildDto>(await _server.Admin.PostAsync(
            $"/api/v1/admin/client-builds?version={Uri.EscapeDataString(version)}", new ByteArrayContent(exe)));

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
    public async Task DownloadNeedsASession()
    {
        var published = await PublishAsync("0.3.0", Exe);
        using var anonymous = _server.NewClient();

        using var response = await anonymous.GetAsync(published.DownloadPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
}
