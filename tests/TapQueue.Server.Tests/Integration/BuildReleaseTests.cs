using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TapQueue.Server.Admins;
using TapQueue.Server.Updates;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>"Check for updates" for client and station builds: found on GitHub, checked and published.</summary>
public sealed class BuildReleaseTests : IAsyncLifetime
{
    private static readonly byte[] WindowsClient = Encoding.ASCII.GetBytes("MZ pretend TapQueueClient.exe");
    private static readonly byte[] LinuxClient = Encoding.ASCII.GetBytes("\x7fELF pretend tapqueue-client");
    private static readonly byte[] Station = Encoding.ASCII.GetBytes("\x7fELF pretend tapqueue-station");

    /// <summary>A GitHub with one client release and one station release on it.</summary>
    private sealed class FakeFeed : IBuildFeed
    {
        public ServerReleaseDto? Client { get; set; } = new("0.9.7", "https://example.test/client-v0.9.7", DateTimeOffset.UtcNow);
        public ServerReleaseDto? StationRelease { get; set; } = new("0.12.1", "https://example.test/server-v0.12.1", DateTimeOffset.UtcNow);
        public string? AskedForServer { get; private set; }
        public Exception? Failure { get; set; }
        public int Checks { get; private set; }
        public Dictionary<string, byte[]> Files { get; } = [];

        public FakeFeed()
        {
            Files["client-v0.9.7/TapQueue_client_0.9.7_win-x64.zip"] = Zip(("TapQueueClient.exe", WindowsClient), ("version.txt", "0.9.7+be5698f\n"u8.ToArray()));
            Files["client-v0.9.7/TapQueue_client_0.9.7_linux-x64.tar.gz"] = TarGz(("TapQueue_client_0.9.7_linux-x64/tapqueue-client", LinuxClient),
                ("TapQueue_client_0.9.7_linux-x64/version.txt", "0.9.7+be5698f\n"u8.ToArray()));
            Files["server-v0.12.1/TapQueue_station_0.12.1_linux-x64.tar.gz"] = TarGz(("TapQueue_station_0.12.1_linux-x64/install-station.sh", "#!/bin/sh"u8.ToArray()),
                ("TapQueue_station_0.12.1_linux-x64/tapqueue-station", Station));
            Sum("client-v0.9.7");
            Sum("server-v0.12.1");
        }

        /// <summary>Writes the tag's SHA256SUMS from the files as they are now.</summary>
        public void Sum(string tag) =>
            Files[$"{tag}/SHA256SUMS"] = Encoding.ASCII.GetBytes(string.Concat(Files.Where(f => f.Key.StartsWith(tag + "/") && !f.Key.EndsWith("SHA256SUMS"))
                .Select(f => $"{Convert.ToHexStringLower(SHA256.HashData(f.Value))}  {f.Key[(tag.Length + 1)..]}\n")));

        public Task<ServerReleaseDto?> LatestClientReleaseAsync(CancellationToken ct)
        {
            Checks++;
            return Failure is null ? Task.FromResult(Client) : Task.FromException<ServerReleaseDto?>(Failure);
        }

        public Task<ServerReleaseDto?> LatestStationReleaseAsync(string serverVersion, CancellationToken ct)
        {
            AskedForServer = serverVersion;
            return Task.FromResult(StationRelease);
        }

        public Task<string?> CommitAsync(string tag, CancellationToken ct) => Task.FromResult<string?>("8173198aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        public Task DownloadAsync(string tag, string asset, string destination, CancellationToken ct) =>
            Files.TryGetValue($"{tag}/{asset}", out var bytes)
                ? File.WriteAllBytesAsync(destination, bytes, ct)
                : Task.FromException(new HttpRequestException($"GitHub answered 404 Not Found for {asset}."));

        private static byte[] Zip(params (string Name, byte[] Content)[] files)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, content) in files)
                {
                    using var entry = zip.CreateEntry(name).Open();
                    entry.Write(content);
                }
            return stream.ToArray();
        }

        private static byte[] TarGz(params (string Name, byte[] Content)[] files)
        {
            var stream = new MemoryStream();
            using (var tar = new TarWriter(new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true)))
                foreach (var (name, content) in files)
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
            return stream.ToArray();
        }
    }

    private readonly FakeFeed _feed = new();
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.StartAsync(configureServices: services => services.AddSingleton<IBuildFeed>(_feed));

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<BuildReleaseDto> Check(string what) =>
        (await _server.Admin.GetFromJsonAsync<BuildReleaseDto>($"/api/v1/admin/{what}-builds/release?refresh=true", TapQueueJson.Options))!;

    private Task<HttpResponseMessage> Publish(string what, string version) =>
        _server.Admin.PostAsJsonAsync($"/api/v1/admin/{what}-builds/release", new PublishReleaseRequest(version), TapQueueJson.Options);

    private async Task<List<ClientBuildDto>> ClientBuilds() =>
        (await _server.Admin.GetFromJsonAsync<List<ClientBuildDto>>("/api/v1/admin/client-builds", TapQueueJson.Options))!;

    [Fact]
    public async Task TheNewestClientReleaseIsFoundAndPublishedForBothPlatforms()
    {
        var found = await Check("client");
        Assert.Equal("0.9.7", found.Latest!.Version);
        Assert.True(found.UpdateAvailable);
        Assert.Empty(found.Published);

        var after = await TestServer.ReadAsync<BuildReleaseDto>(await Publish("client", "0.9.7"));

        Assert.False(after.UpdateAvailable);
        Assert.Equal(["0.9.7+be5698f", "0.9.7+be5698f"], after.Published);
        var builds = await ClientBuilds();
        Assert.Equal([ClientPlatform.Linux, ClientPlatform.Windows], builds.Select(b => b.Platform).Order());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(WindowsClient)), builds.Single(b => b.Platform == ClientPlatform.Windows).Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(LinuxClient)), builds.Single(b => b.Platform == ClientPlatform.Linux).Sha256);

        // A PC is handed it like any other published build.
        using var alice = await _server.SignInAsync("alice");
        var offered = (await TestServer.ReadAsync<ClientHeartbeatResponse>(await alice.PostAsync("/api/v1/client/heartbeat", null))).ClientBuild;
        Assert.Equal("0.9.7+be5698f", offered!.Version);
        Assert.Equal(WindowsClient, await alice.GetByteArrayAsync(offered.DownloadPath));

        // Publishing what's already handed out changes nothing.
        await TestServer.ReadAsync<BuildReleaseDto>(await Publish("client", "0.9.7"));
        Assert.Equal(2, (await ClientBuilds()).Count);
    }

    [Fact]
    public async Task AnOlderBuildOnOnePlatformStillCountsAsAnUpdate()
    {
        await _server.PublishClientAsync("0.9.7+be5698f", LinuxClient, ClientPlatform.Linux);
        await _server.PublishClientAsync("0.9.6+685bd8e", [.. WindowsClient, 1]);

        Assert.True((await Check("client")).UpdateAvailable);
        var after = await TestServer.ReadAsync<BuildReleaseDto>(await Publish("client", "0.9.7"));

        Assert.False(after.UpdateAvailable);
        // Linux already had that exact program, so only Windows got a new build.
        Assert.Equal(3, (await ClientBuilds()).Count);
    }

    [Fact]
    public async Task ANewerBuildThanTheNewestReleaseIsntCalledOutOfDate()
    {
        foreach (var platform in ClientPlatform.All)
            await _server.PublishClientAsync("0.10.0+dev", WindowsClient, platform);

        Assert.False((await Check("client")).UpdateAvailable);
    }

    [Fact]
    public async Task AFileThatDoesntMatchTheReleasesChecksumsIsntPublished()
    {
        await Check("client");
        _feed.Files["client-v0.9.7/TapQueue_client_0.9.7_linux-x64.tar.gz"][20] ^= 0xff;

        var response = await Publish("client", "0.9.7");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("doesn't match the release's SHA256SUMS", (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error);
        // Not even the Windows one, which was fine: PCs stay on one version.
        Assert.Empty(await ClientBuilds());
        Assert.Empty(Directory.GetDirectories(_server.Service<Config.ServerConfig>().Server.DataDir, "release-*"));
    }

    [Theory]
    [InlineData("0.9.6")]
    [InlineData("../../evil")]
    [InlineData("")]
    public async Task OnlyTheReleaseTheLastCheckFoundCanBePublished(string version)
    {
        await Check("client");

        Assert.Equal(HttpStatusCode.Conflict, (await Publish("client", version)).StatusCode);
        Assert.Empty(await ClientBuilds());
    }

    [Fact]
    public async Task NothingCanBePublishedBeforeACheck()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Publish("client", "0.9.7")).StatusCode);
    }

    [Fact]
    public async Task AFailedCheckSaysWhyAndIsTriedAgainAtOnce()
    {
        _feed.Failure = new HttpRequestException("GitHub answered 403 rate limit exceeded.");
        var failed = await Check("client");
        Assert.Contains("403", failed.CheckError);
        Assert.False(failed.UpdateAvailable);

        _feed.Failure = null;
        Assert.Null((await Check("client")).CheckError);
        // A check that worked is remembered for a minute.
        await Check("client");
        Assert.Equal(2, _feed.Checks);
    }

    [Fact]
    public async Task TheStationBuildThatGoesWithThisServerIsPublished()
    {
        var found = await Check("station");
        Assert.Equal(ReleaseVersion.Of(TapQueueVersion.Current), _feed.AskedForServer);
        Assert.True(found.UpdateAvailable);

        var after = await TestServer.ReadAsync<BuildReleaseDto>(await Publish("station", "0.12.1"));

        // The station archive has no version.txt, so the label is the release and its commit, as the program prints it.
        Assert.Equal(["0.12.1+8173198"], after.Published);
        Assert.False(after.UpdateAvailable);
        var build = Assert.Single((await _server.Admin.GetFromJsonAsync<List<StationBuildDto>>("/api/v1/admin/station-builds", TapQueueJson.Options))!);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Station)), build.Sha256);
    }

    [Fact]
    public void ClientAndStationReleasesArePickedByTheirOwnTagsAndFiles()
    {
        static string Release(string tag, params string[] files) =>
            $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"html_url":"https://example.test/{{tag}}","assets":[{{string.Join(",", files.Select(f => $$"""{"name":"{{f}}"}"""))}}]}""";
        var releases = JsonDocument.Parse($"[{string.Join(",",
            Release("server-v0.13.0", "TapQueue_server_0.13.0_linux-x64.tar.gz", "TapQueue_station_0.13.0_linux-x64.tar.gz", "SHA256SUMS"),
            Release("server-v0.12.1", "TapQueue_server_0.12.1_linux-x64.tar.gz", "TapQueue_station_0.12.1_linux-x64.tar.gz", "SHA256SUMS"),
            Release("client-v0.9.8", "TapQueue_client_0.9.8_win-x64.zip", "SHA256SUMS"),
            Release("client-v0.9.7", "TapQueue_client_0.9.7_win-x64.zip", "TapQueue_client_0.9.7_linux-x64.tar.gz", "SHA256SUMS"))}]").RootElement;

        // 0.9.8 has no Linux archive yet: not offered.
        var client = GitHubReleaseFeed.PickLatest(releases, "client-v",
            v => [GitHubReleaseFeed.ClientArchive(v, ClientPlatform.Windows), GitHubReleaseFeed.ClientArchive(v, ClientPlatform.Linux), "SHA256SUMS"]);
        Assert.Equal("0.9.7", client!.Version);

        // A server on 0.12.1 doesn't hand its stations 0.13.0.
        var station = GitHubReleaseFeed.PickLatest(releases, "server-v", v => [GitHubReleaseFeed.StationArchive(v), "SHA256SUMS"], v => v <= new Version(0, 12, 1));
        Assert.Equal("0.12.1", station!.Version);
    }

    [Theory]
    [InlineData("GET", "/client-builds/release", AdminRole.Viewer)]
    [InlineData("POST", "/client-builds/release", AdminRole.Admin)]
    [InlineData("GET", "/station-builds/release", AdminRole.Viewer)]
    [InlineData("POST", "/station-builds/release", AdminRole.Admin)]
    public void PublishingFromGitHubNeedsWhatPublishingByUploadDoes(string method, string path, string role)
    {
        Assert.Equal(new AdminRequirement(AdminArea.Updates, role), AdminAccess.Requirement(method, path));
    }
}
