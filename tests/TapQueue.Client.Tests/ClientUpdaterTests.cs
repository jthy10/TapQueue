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
        var updater = new ClientUpdater(new HttpClient(handler) { BaseAddress = new Uri("http://server/") }, NullLogger.Instance);

        Assert.Equal(UpdateOutcome.Failed, await updater.InstallIfDifferentAsync(windowsBuild, CancellationToken.None));
        Assert.Contains("Windows build", updater.LastError);
        Assert.Equal(0, handler.Requests);
    }
}
