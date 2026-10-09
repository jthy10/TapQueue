using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TapQueue.Server.Updates;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Checking GitHub for a newer server release and asking the updater to install it, now or later.</summary>
public sealed class ServerUpdateTests : IAsyncLifetime
{
    private sealed class FakeFeed : IReleaseFeed
    {
        public ServerReleaseDto? Latest { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public Task<ServerReleaseDto?> LatestServerReleaseAsync(CancellationToken ct)
        {
            Calls++;
            return Failure is null ? Task.FromResult(Latest) : Task.FromException<ServerReleaseDto?>(Failure);
        }
    }

    private const string Newer = "99.0.0";

    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-update").FullName;
    private readonly FakeFeed _feed = new() { Latest = new(Newer, "https://example.test/releases/server-v99.0.0", null) };
    private bool _installed = true;
    private TestServer _server = null!;

    private string RequestFile => Path.Combine(_dir, "update-request");
    private string StatusFile => Path.Combine(_dir, "status");

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "update-server.sh"), "");
        File.WriteAllText(Path.Combine(_dir, "tapqueue-update.path"), "");
        var setup = new UpdaterSetup(RequestFile, StatusFile, Path.Combine(_dir, "update-server.sh"),
            Path.Combine(_dir, "tapqueue-update.path"), () => _installed);
        _server = await TestServer.StartAsync(configureServices: services =>
        {
            services.AddSingleton(setup);
            services.AddSingleton<IReleaseFeed>(_feed);
        });
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<ServerUpdateDto> Check() =>
        (await _server.Admin.GetFromJsonAsync<ServerUpdateDto>("/api/v1/admin/server/update?refresh=true", TapQueueJson.Options))!;

    private Task<HttpResponseMessage> Apply(string version, DateTimeOffset? at = null) =>
        _server.Admin.PostAsJsonAsync("/api/v1/admin/server/update", new ApplyServerUpdateRequest(version, at), TapQueueJson.Options);

    [Fact]
    public async Task NothingIsCheckedUntilAnAdminAsks()
    {
        var status = (await _server.Admin.GetFromJsonAsync<ServerUpdateDto>("/api/v1/admin/server/update", TapQueueJson.Options))!;

        Assert.Null(status.CheckedAt);
        Assert.Null(status.Latest);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(0, _feed.Calls);
    }

    [Fact]
    public async Task ACheckFindsANewerRelease()
    {
        var status = await Check();

        Assert.True(status.UpdateAvailable);
        Assert.Equal(Newer, status.Latest!.Version);
        Assert.Equal(ServerUpdater.Current, status.Current);
        Assert.True(status.CanApply);
        Assert.NotNull(status.CheckedAt);
    }

    [Fact]
    public async Task TheRunningReleaseIsUpToDate()
    {
        _feed.Latest = new(ServerUpdater.Current, "https://example.test", null);

        var status = await Check();

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task AFailedCheckSaysWhy()
    {
        _feed.Failure = new HttpRequestException("no route to host");

        var status = await Check();

        Assert.False(status.UpdateAvailable);
        Assert.Contains("no route to host", status.CheckError);
    }

    [Fact]
    public async Task UpdatingNowAsksTheUpdaterForThatVersion()
    {
        await Check();

        var status = await TestServer.ReadAsync<ServerUpdateDto>(await Apply(Newer));

        Assert.Equal(Newer + "\n", File.ReadAllText(RequestFile));
        Assert.Equal("requested", status.LastRun!.State);
        Assert.Equal(Newer, status.LastRun.Version);
        // A second request while the first is waiting is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(Newer)).StatusCode);
    }

    [Fact]
    public async Task OnlyTheNewestReleaseTheCheckFoundIsInstalled()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(Newer)).StatusCode); // not checked yet
        await Check();

        Assert.Equal(HttpStatusCode.Conflict, (await Apply("98.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Apply("0.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Apply("99.0.0; rm -rf /")).StatusCode);
        Assert.False(File.Exists(RequestFile));
    }

    [Fact]
    public async Task AServerThatCantUpgradeItselfSaysHowTo()
    {
        File.Delete(Path.Combine(_dir, "update-server.sh"));

        var status = await Check();
        var response = await Apply(Newer);

        Assert.False(status.CanApply);
        Assert.Contains("install.sh | sudo bash -s server", status.CannotApplyReason);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(RequestFile));
    }

    [Fact]
    public async Task AServerNotRunBySystemdCantUpgradeItself()
    {
        _installed = false;

        var status = await Check();

        Assert.False(status.CanApply);
        Assert.Contains("systemd", status.CannotApplyReason);
    }

    [Fact]
    public async Task AScheduledUpgradeStartsWhenItsTimeComes()
    {
        await Check();
        var at = DateTimeOffset.UtcNow.AddHours(3);

        var status = await TestServer.ReadAsync<ServerUpdateDto>(await Apply(Newer, at));

        Assert.Equal(Newer, status.Scheduled!.Version);
        Assert.Equal(at.ToUnixTimeSeconds(), status.Scheduled.At.ToUnixTimeSeconds());
        Assert.False(File.Exists(RequestFile));

        var updater = _server.Service<ServerUpdater>();
        updater.RunDue(at.AddMinutes(-1));
        Assert.False(File.Exists(RequestFile));
        updater.RunDue(at.AddSeconds(1));
        Assert.Equal(Newer + "\n", File.ReadAllText(RequestFile));
        Assert.Null(updater.Scheduled());
    }

    [Fact]
    public async Task UpgradesWaitForJobsBeingPrinted()
    {
        await Check();
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", "%PDF-1.4 test"u8.ToArray());
        _server.Service<TapQueue.Server.Data.Database>().Execute("UPDATE jobs SET status = 'releasing'");

        Assert.Equal(HttpStatusCode.Conflict, (await Apply(Newer)).StatusCode);

        var at = DateTimeOffset.UtcNow.AddHours(1);
        await TestServer.ReadAsync<ServerUpdateDto>(await Apply(Newer, at));
        var updater = _server.Service<ServerUpdater>();
        updater.RunDue(at.AddMinutes(1));
        Assert.False(File.Exists(RequestFile));
        Assert.NotNull(updater.Scheduled());
        // It doesn't wait forever on a job that's stuck.
        updater.RunDue(at.AddMinutes(16));
        Assert.True(File.Exists(RequestFile));
    }

    [Fact]
    public async Task AScheduledUpgradeCanBeCalledOff()
    {
        await Check();
        await TestServer.ReadAsync<ServerUpdateDto>(await Apply(Newer, DateTimeOffset.UtcNow.AddHours(3)));

        var status = await TestServer.ReadAsync<ServerUpdateDto>(await _server.Admin.DeleteAsync("/api/v1/admin/server/update"));

        Assert.Null(status.Scheduled);
        _server.Service<ServerUpdater>().RunDue(DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(File.Exists(RequestFile));
    }

    [Fact]
    public async Task SchedulesMoreThanAMonthAheadAreRefused()
    {
        await Check();

        Assert.Equal(HttpStatusCode.Conflict, (await Apply(Newer, DateTimeOffset.UtcNow.AddDays(40))).StatusCode);
    }

    [Fact]
    public async Task TheUpdatersOutcomeIsShown()
    {
        File.WriteAllText(StatusFile, $"state=failed\nversion={Newer}\nat=2026-10-08T02:00:00Z\nmessage=TapQueue_server_99.0.0_linux-x64.tar.gz doesn't match its SHA-256\n");

        var status = (await _server.Admin.GetFromJsonAsync<ServerUpdateDto>("/api/v1/admin/server/update", TapQueueJson.Options))!;

        Assert.Equal("failed", status.LastRun!.State);
        Assert.Equal(Newer, status.LastRun.Version);
        Assert.Contains("SHA-256", status.LastRun.Message);
    }

    [Fact]
    public void TheNewestPublishedServerReleaseWithItsFilesIsPicked()
    {
        static string Release(string tag, bool draft = false, bool prerelease = false, params string[] assets) =>
            JsonSerializer.Serialize(new
            {
                tag_name = tag, draft, prerelease, html_url = $"https://github.com/jthy10/TapQueue/releases/tag/{tag}",
                published_at = "2026-09-25T12:00:00Z", assets = assets.Select(a => new { name = a }),
            });
        static string[] Files(string v) => [$"TapQueue_server_{v}_linux-x64.tar.gz", "SHA256SUMS"];

        var json = $"[{string.Join(",",
            Release("client-v9.0.0", false, false, "SHA256SUMS"),
            Release("server-v0.10.0", true, false, Files("0.10.0")),
            Release("server-v0.9.0", false, true, Files("0.9.0")),
            Release("server-v0.8.1", false, false, "SHA256SUMS"),
            Release("server-v0.7.0", false, false, Files("0.7.0")),
            Release("server-v0.8.0", false, false, Files("0.8.0")),
            Release("server-v0.6.2", false, false, Files("0.6.2")))}]";

        var latest = GitHubReleaseFeed.PickLatest(JsonDocument.Parse(json).RootElement);

        Assert.Equal("0.8.0", latest!.Version);
        Assert.Equal("https://github.com/jthy10/TapQueue/releases/tag/server-v0.8.0", latest.Url);
    }

    [Theory]
    [InlineData("0.8.1", "0.8.0+abc1234", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.8.0", "0.8.0+abc1234", false)]
    [InlineData("0.7.9", "0.8.0", false)]
    [InlineData("1.0", "0.8.0", false)]
    public void VersionsCompareAsNumbers(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, ReleaseVersion.IsNewer(candidate, current));
    }
}
