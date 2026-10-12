using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Updates;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Going back to an older release: the builds PCs and stations get, then the server itself.</summary>
public sealed class ServerDowngradeTests : IAsyncLifetime
{
    private const string Older = "0.17.0";
    private static readonly byte[] WindowsClient = Encoding.ASCII.GetBytes("MZ the older TapQueueClient.exe");
    private static readonly byte[] LinuxClient = Encoding.ASCII.GetBytes("\x7fELF the older tapqueue-client");
    private static readonly byte[] Station = Encoding.ASCII.GetBytes("\x7fELF the older tapqueue-station");

    /// <summary>A GitHub whose older releases are whatever the test says, with the files of <see cref="Older"/>.</summary>
    private sealed class FakeGitHub : IReleaseFeed, IBuildFeed
    {
        public List<ServerReleaseDto> OlderReleases { get; } = [new(Older, $"https://example.test/server-v{Older}", null)];
        public Dictionary<string, byte[]> Files { get; } = [];

        public FakeGitHub()
        {
            Files[$"client-v{Older}/TapQueue_client_{Older}_win-x64.zip"] = Zip(("TapQueueClient.exe", WindowsClient), ("version.txt", Encoding.ASCII.GetBytes($"{Older}+aaaaaaa\n")));
            Files[$"client-v{Older}/TapQueue_client_{Older}_linux-x64.tar.gz"] = TarGz(("c/tapqueue-client", LinuxClient), ("c/version.txt", Encoding.ASCII.GetBytes($"{Older}+aaaaaaa\n")));
            Files[$"server-v{Older}/TapQueue_station_{Older}_linux-x64.tar.gz"] = TarGz(("s/tapqueue-station", Station));
            Sum($"client-v{Older}");
            Sum($"server-v{Older}");
        }

        public void Sum(string tag) =>
            Files[$"{tag}/SHA256SUMS"] = Encoding.ASCII.GetBytes(string.Concat(Files.Where(f => f.Key.StartsWith(tag + "/") && !f.Key.EndsWith("SHA256SUMS"))
                .Select(f => $"{Convert.ToHexStringLower(SHA256.HashData(f.Value))}  {f.Key[(tag.Length + 1)..]}\n")));

        public Task<ServerReleaseDto?> LatestServerReleaseAsync(CancellationToken ct) => Task.FromResult<ServerReleaseDto?>(null);
        public Task<IReadOnlyList<ServerReleaseDto>> OlderReleasesAsync(string current, CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerReleaseDto>>(OlderReleases);
        public Task<ServerReleaseDto?> LatestClientReleaseAsync(CancellationToken ct) => Task.FromResult<ServerReleaseDto?>(null);
        public Task<ServerReleaseDto?> LatestStationReleaseAsync(string serverVersion, CancellationToken ct) => Task.FromResult<ServerReleaseDto?>(null);
        public Task<string?> CommitAsync(string tag, CancellationToken ct) => Task.FromResult<string?>("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

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

    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-downgrade").FullName;
    private readonly FakeGitHub _github = new();
    private TestServer _server = null!;

    private string RequestFile => Path.Combine(_dir, "update-request");

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "update-server.sh"), "");
        File.WriteAllText(Path.Combine(_dir, "tapqueue-update.path"), "");
        var setup = new UpdaterSetup(RequestFile, Path.Combine(_dir, "status"), Path.Combine(_dir, "update-server.sh"),
            Path.Combine(_dir, "tapqueue-update.path"), () => true);
        _server = await TestServer.StartAsync(configureServices: services =>
        {
            services.AddSingleton(setup);
            services.AddSingleton<IReleaseFeed>(_github);
            services.AddSingleton<IBuildFeed>(_github);
        });
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<ServerDowngradeDto> Look() =>
        (await _server.Admin.GetFromJsonAsync<ServerDowngradeDto>("/api/v1/admin/server/downgrade?refresh=true", TapQueueJson.Options))!;

    private Task<HttpResponseMessage> GoBackTo(string version) =>
        _server.Admin.PostAsJsonAsync("/api/v1/admin/server/downgrade", new DowngradeRequest(version), TapQueueJson.Options);

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error;

    [Fact]
    public async Task NothingIsListedUntilSomeoneLooks()
    {
        var before = (await _server.Admin.GetFromJsonAsync<ServerDowngradeDto>("/api/v1/admin/server/downgrade", TapQueueJson.Options))!;
        Assert.Empty(before.Versions);
        Assert.Null(before.CheckedAt);
        Assert.Equal("0.17.0", before.Oldest);

        var found = await Look();
        Assert.Equal([Older], found.Versions.Select(v => v.Version));
        Assert.True(found.CanApply);
    }

    [Fact]
    public async Task GoingBackHandsOutTheOlderBuildsAndThenAsksForTheOlderServer()
    {
        await _server.PublishClientAsync("0.18.0+new", "MZ the newer client"u8.ToArray());
        await Look();

        var response = await GoBackTo(Older);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // PCs of both platforms and stations are offered the older release's programs.
        var clients = (await _server.Admin.GetFromJsonAsync<List<ClientBuildDto>>("/api/v1/admin/client-builds", TapQueueJson.Options))!;
        Assert.Equal($"{Older}+aaaaaaa", clients.First(b => b.Platform == ClientPlatform.Windows).Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(WindowsClient)), clients.First(b => b.Platform == ClientPlatform.Windows).Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(LinuxClient)), clients.First(b => b.Platform == ClientPlatform.Linux).Sha256);
        var station = (await _server.Admin.GetFromJsonAsync<List<StationBuildDto>>("/api/v1/admin/station-builds", TapQueueJson.Options))![0];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Station)), station.Sha256);
        // And the updater is asked for the older server, marked as a downgrade.
        Assert.Equal($"downgrade:{Older}\n", File.ReadAllText(RequestFile));
        var update = (await _server.Admin.GetFromJsonAsync<ServerUpdateDto>("/api/v1/admin/server/update", TapQueueJson.Options))!;
        Assert.Equal(("requested", Older), (update.LastRun!.State, update.LastRun.Version));
        // Not twice at once.
        Assert.Contains("already under way", await Error(await GoBackTo(Older)));
    }

    [Fact]
    public async Task OnlyAReleaseTheLastLookFoundAndNothingBeforeTheOldest()
    {
        Assert.Contains("isn't one of the releases", await Error(await GoBackTo(Older)));

        _github.OlderReleases.Add(new("0.16.0", "https://example.test/server-v0.16.0", null));
        await Look();
        Assert.Contains("0.17.0 is the oldest release", await Error(await GoBackTo("0.16.0")));
        Assert.Contains("0.17.0 is the oldest release", await Error(await GoBackTo("../../etc")));
        Assert.False(File.Exists(RequestFile));
    }

    [Fact]
    public async Task IfTheOlderBuildsCannotBeHadNothingChanges()
    {
        // A client archive that isn't what the release's SHA256SUMS says.
        _github.Files[$"client-v{Older}/TapQueue_client_{Older}_linux-x64.tar.gz"] = [1, 2, 3];
        await Look();

        var response = await GoBackTo(Older);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("doesn't match the release's SHA256SUMS", await Error(response));
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<ClientBuildDto>>("/api/v1/admin/client-builds", TapQueueJson.Options))!);
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<StationBuildDto>>("/api/v1/admin/station-builds", TapQueueJson.Options))!);
        Assert.False(File.Exists(RequestFile));
    }

    [Fact]
    public void OlderReleasesAreTheCompleteOnesBetweenTheOldestAndWhatIsRunning()
    {
        static string Release(string tag, params string[] files) =>
            $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":false,"html_url":"https://example.test/{{tag}}","assets":[{{string.Join(",", files.Select(f => $$"""{"name":"{{f}}"}"""))}}]}""";
        static string[] Server(string v) => [$"TapQueue_server_{v}_linux-x64.tar.gz", $"TapQueue_station_{v}_linux-x64.tar.gz", "SHA256SUMS"];
        static string[] Client(string v) => [$"TapQueue_client_{v}_win-x64.zip", $"TapQueue_client_{v}_linux-x64.tar.gz", "SHA256SUMS"];
        var releases = JsonDocument.Parse("[" + string.Join(",",
            Release("server-v0.16.0", Server("0.16.0")), Release("client-v0.16.0", Client("0.16.0")),   // before the oldest
            Release("server-v0.17.0", Server("0.17.0")), Release("client-v0.17.0", Client("0.17.0")),
            Release("server-v0.17.1", Server("0.17.1")),                                                // no clients to go with it
            Release("server-v0.18.0", Server("0.18.0")), Release("client-v0.18.0", Client("0.18.0")),
            Release("server-v0.19.0", Server("0.19.0")), Release("client-v0.19.0", Client("0.19.0")),   // what's running
            Release("server-v0.20.0", Server("0.20.0")), Release("client-v0.20.0", Client("0.20.0"))) + "]").RootElement;

        Assert.Equal(["0.18.0", "0.17.0"], GitHubReleaseFeed.PickOlder(releases, "0.19.0+abc1234").Select(r => r.Version));
        Assert.Empty(GitHubReleaseFeed.PickOlder(releases, "0.17.0"));
    }

    [Fact]
    public void TheDatabaseStepsBackOneMigrationAtATimeAndNeverPastTheOldest()
    {
        var path = Path.Combine(_dir, "steps.db");
        var database = new Database(path);
        database.Migrate();
        Assert.True(Database.EveryMigrationCanBeUndone, "Every migration after Database.FloorSchema needs an entry in Database.Downgrades.");
        // Two migrations a later version might add, and how each is undone.
        database.Execute($"ALTER TABLE jobs ADD COLUMN later_one TEXT; ALTER TABLE jobs ADD COLUMN later_two TEXT; PRAGMA user_version = {Database.FloorSchema + 2}");
        string[] undo = ["ALTER TABLE jobs DROP COLUMN later_one;", "ALTER TABLE jobs DROP COLUMN later_two;"];
        bool Has(string column) => Convert.ToInt32(database.Scalar("SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = $n", ("$n", column))) == 1;

        Assert.Equal(Database.FloorSchema + 2, database.DowngradeTo(Database.FloorSchema + 1, undo));
        Assert.True(Has("later_one"));
        Assert.False(Has("later_two"));
        Assert.Equal(Database.FloorSchema + 1, database.SchemaVersion());

        database.DowngradeTo(Database.FloorSchema, undo);
        Assert.False(Has("later_one"));
        Assert.Contains("0.17.0", Assert.Throws<InvalidOperationException>(() => database.DowngradeTo(Database.FloorSchema - 1, undo)).Message);
        Assert.Contains("newer, not older", Assert.Throws<InvalidOperationException>(() => database.DowngradeTo(Database.FloorSchema + 1, undo)).Message);
        // A migration nobody wrote the undoing of can't be stepped over.
        database.Execute($"PRAGMA user_version = {Database.FloorSchema + 3}");
        Assert.Contains("doesn't know how to undo", Assert.Throws<InvalidOperationException>(() => database.DowngradeTo(Database.FloorSchema, undo)).Message);
    }
}
