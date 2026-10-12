using Microsoft.Extensions.Logging.Abstractions;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

public sealed class ClientUpdaterTests
{
    /// <summary>Fails the test if the updater tries to download anything.</summary>
    private sealed class NoDownloads : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task ABuildForAnotherPlatformIsNeverInstalled()
    {
        // What a server older than 0.5 sends: no platform, which reads as Windows.
        var windowsBuild = System.Text.Json.JsonSerializer.Deserialize<ClientBuildDto>(
            """{"version":"0.4.0+71a5d74","sha256":"fb5f7043601efc45aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":1,"publishedAt":"2026-09-24T00:00:00Z","downloadPath":"/api/v1/client/builds/x"}""",
            TapQueueJson.Options)!;
        Assert.Equal(ClientPlatform.Windows, windowsBuild.Platform);
        Assert.Equal(ClientPlatform.Linux, ClientPlatform.Current); // the tests run on Linux

        var handler = new NoDownloads();
        var updater = new ClientUpdater(new HttpClient(handler) { BaseAddress = new Uri("http://server/") }, NullLogger.Instance, new HttpClient(handler));

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(windowsBuild, CancellationToken.None));
        Assert.Contains("Windows build", updater.LastError);
        Assert.Equal(0, handler.Requests);
    }

    /// <summary>Where releases are published: answers with one release's list of programs, and counts the asks.</summary>
    private sealed class Releases(string? programs, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            return programs is null
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("No route to host"))
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(programs) });
        }
    }

    private static readonly string Sha = new('a', 64);

    private static ClientBuildDto Offered(string version = "0.18.0+1a2b3c4") =>
        new(version, Sha, 1, DateTimeOffset.UtcNow, "/api/v1/client/builds/x", ClientPlatform.Linux);

    private static (ClientUpdater Updater, NoDownloads Server) Updater(Releases releases)
    {
        var server = new NoDownloads();
        return (new ClientUpdater(new HttpClient(server) { BaseAddress = new Uri("http://server/") }, NullLogger.Instance, new HttpClient(releases)), server);
    }

    [Fact]
    public async Task ABuildThatIsNotInItsReleaseIsNeverDownloaded()
    {
        var releases = new Releases($"{new string('b', 64)}  tapqueue-client\n{Sha}  TapQueueClient.exe\n");
        var (updater, server) = Updater(releases);

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None));

        Assert.Equal("The server's build 0.18.0+1a2b3c4 isn't the tapqueue-client of TapQueue release 0.18.0, so it wasn't installed.", updater.LastError);
        Assert.Equal(["https://github.com/jthy10/TapQueue/releases/download/client-v0.18.0/SHA256SUMS.programs"], releases.Asked);
        Assert.Equal(0, server.Requests);
        // It's not asked about again: the answer won't change.
        await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None);
        Assert.Single(releases.Asked);
    }

    [Fact]
    public async Task ABuildThatIsInItsReleaseIsDownloadedFromTheServer()
    {
        var (updater, server) = Updater(new Releases($"{Sha}  tapqueue-client\n"));

        // The stand-in server has no such file, so the install fails there, after the check let it through.
        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task WithoutAWayToTheReleasesNothingIsInstalledAndItIsTriedAgainLater()
    {
        var releases = new Releases(null);
        var (updater, server) = Updater(releases);

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None));
        Assert.Contains("Couldn't reach github.com", updater.LastError);
        Assert.Equal(0, server.Requests);

        // Not every minute; but "update now" asks again at once.
        await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None);
        Assert.Single(releases.Asked);
        updater.RetryFailed();
        await updater.InstallIfDifferentAsync(Offered(), CancellationToken.None);
        Assert.Equal(2, releases.Asked.Count);
    }

    [Theory]
    [InlineData("0.18.0/../../other")]
    [InlineData("latest")]
    [InlineData("")]
    public async Task ABuildNotNamedAfterAReleaseIsRefusedWithoutAskingAnyone(string version)
    {
        var releases = new Releases($"{Sha}  tapqueue-client\n");
        var (updater, server) = Updater(releases);

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(Offered(version), CancellationToken.None));
        Assert.Contains("isn't a TapQueue release", updater.LastError);
        Assert.Empty(releases.Asked);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task AReleaseWithoutAListOfProgramsCannotBeInstalledFrom()
    {
        var (updater, server) = Updater(new Releases("Not Found", System.Net.HttpStatusCode.NotFound));

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(Offered("0.16.0"), CancellationToken.None));
        Assert.Contains("doesn't list its programs", updater.LastError);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task ASiteThatBuildsItsOwnChecksItsOwnReleases()
    {
        var releases = new Releases($"{Sha}  tapqueue-client\n");
        var result = await ReleaseCheck.VerifyAsync(new HttpClient(releases), "https://builds.example.test/tapqueue/", ReleaseCheck.StationTag,
            "0.18.0", "tapqueue-client", Sha.ToUpperInvariant(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(["https://builds.example.test/tapqueue/server-v0.18.0/SHA256SUMS.programs"], releases.Asked);
    }
}
