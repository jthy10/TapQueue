using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="DownloadPath">Where the program that runs this build downloads it from.</param>
/// <param name="Platform">For client builds, which <see cref="ClientPlatform"/> runs it; null for station builds.</param>
public sealed record BuildRecord(long Id, string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath, string? Platform)
{
    public ClientBuildDto ToDto() => new(Version, Sha256, SizeBytes, PublishedAt, DownloadPath, Platform ?? ClientPlatform.Windows);

    public StationBuildDto ToStationDto() => new(Version, Sha256, SizeBytes, PublishedAt, DownloadPath);
}

/// <summary>
/// Client builds published with `tapqueue-admin clients publish`, one line of them per platform. The
/// newest one for a platform is what every PC of that platform should be running; PCs running
/// anything else download it and install it.
/// </summary>
public sealed class ClientBuildStore(Database database, string dataDir)
    : BuildStore(database, dataDir, "client_builds", "client-builds", ".exe", "/api/v1/client/builds/", hasPlatform: true)
{
    public BuildRecord? Latest(string platform) => LatestFor(platform);

    /// <summary>The newest build for each platform that has one.</summary>
    public List<BuildRecord> LatestPerPlatform() =>
        ClientPlatform.All.Select(Latest).OfType<BuildRecord>().ToList();

    public Task<BuildRecord> PublishAsync(string platform, string version, Stream program, CancellationToken ct) =>
        PublishAsync(version, program, platform, ct);
}

/// <summary>Release station builds (the tapqueue-station program). Stations install the newest one the same way.</summary>
public sealed class StationBuildStore(Database database, string dataDir)
    : BuildStore(database, dataDir, "station_builds", "station-builds", "", "/api/v1/station/builds/", hasPlatform: false)
{
    public BuildRecord? Latest() => LatestFor(null);

    public Task<BuildRecord> PublishAsync(string version, Stream program, CancellationToken ct) =>
        PublishAsync(version, program, null, ct);
}

/// <summary>
/// Published builds of a program that updates itself from the server. The files live in
/// data_dir/&lt;folder&gt;, named by their SHA-256 (client builds end in .exe whatever their platform, from when
/// only Windows had a client).
/// </summary>
public abstract class BuildStore
{
    private readonly Database _database;
    private readonly string _directory;
    private readonly string _table;
    private readonly string _extension;
    private readonly string _downloadPrefix;
    private readonly bool _hasPlatform;
    private readonly string _columns;

    protected BuildStore(Database database, string dataDir, string table, string folder, string extension, string downloadPrefix, bool hasPlatform)
    {
        _database = database;
        _directory = Path.Combine(dataDir, folder);
        _table = table;
        _extension = extension;
        _downloadPrefix = downloadPrefix;
        _hasPlatform = hasPlatform;
        _columns = "id, version, sha256, size_bytes, published_at" + (hasPlatform ? ", platform" : "");
        Directory.CreateDirectory(_directory);
    }

    protected BuildRecord? LatestFor(string? platform) => platform is null
        ? _database.QueryOne($"SELECT {_columns} FROM {_table} ORDER BY id DESC LIMIT 1", Map)
        : _database.QueryOne($"SELECT {_columns} FROM {_table} WHERE platform = $platform ORDER BY id DESC LIMIT 1", Map,
            ("$platform", platform));

    /// <summary>The most recent publish of the build with this hash.</summary>
    public BuildRecord? Find(string sha256) =>
        _database.QueryOne($"SELECT {_columns} FROM {_table} WHERE sha256 = $sha ORDER BY id DESC LIMIT 1", Map,
            ("$sha", sha256.ToLowerInvariant()));

    public List<BuildRecord> List() =>
        _database.Query($"SELECT {_columns} FROM {_table} ORDER BY id DESC", Map);

    /// <summary>Stores an uploaded program and makes it the build everything (of that platform) should run.</summary>
    protected async Task<BuildRecord> PublishAsync(string version, Stream exe, string? platform, CancellationToken ct)
    {
        var temp = Path.Combine(_directory, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            string sha256;
            long size;
            await using (var file = File.Create(temp))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await exe.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
                size = file.Length;
            }
            if (size == 0)
                throw new InvalidDataException("The uploaded build is empty.");
            File.Move(temp, PathFor(sha256), overwrite: true);

            return _hasPlatform
                ? _database.QueryOne($"""
                    INSERT INTO {_table} (version, sha256, size_bytes, published_at, platform)
                    VALUES ($version, $sha, $size, $now, $platform)
                    RETURNING {_columns}
                    """, Map, ("$version", version), ("$sha", sha256), ("$size", size), ("$now", DateTimeOffset.UtcNow), ("$platform", platform))!
                : _database.QueryOne($"""
                    INSERT INTO {_table} (version, sha256, size_bytes, published_at)
                    VALUES ($version, $sha, $size, $now)
                    RETURNING {_columns}
                    """, Map, ("$version", version), ("$sha", sha256), ("$size", size), ("$now", DateTimeOffset.UtcNow))!;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The stored file for a published build, or null if nothing with that hash was published.</summary>
    public string? FileFor(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            return null;
        var path = PathFor(sha256.ToLowerInvariant());
        return File.Exists(path) ? path : null;
    }

    private string PathFor(string sha256) => Path.Combine(_directory, sha256 + _extension);

    private BuildRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetTime(4), _downloadPrefix + r.GetString(2),
            _hasPlatform ? r.GetString(5) : null);
}
