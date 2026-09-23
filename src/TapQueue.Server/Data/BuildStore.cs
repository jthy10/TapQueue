using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="DownloadPath">Where the program that runs this build downloads it from.</param>
public sealed record BuildRecord(long Id, string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt, string DownloadPath)
{
    public ClientBuildDto ToDto() => new(Version, Sha256, SizeBytes, PublishedAt, DownloadPath);

    public StationBuildDto ToStationDto() => new(Version, Sha256, SizeBytes, PublishedAt, DownloadPath);
}

/// <summary>
/// Windows client builds published with `tapqueue-admin clients publish`. The newest one is what every
/// client should be running; clients running anything else download it and install it.
/// </summary>
public sealed class ClientBuildStore(Database database, string dataDir)
    : BuildStore(database, dataDir, "client_builds", "client-builds", ".exe", "/api/v1/client/builds/");

/// <summary>Release station builds (the tapqueue-station program). Stations install the newest one the same way.</summary>
public sealed class StationBuildStore(Database database, string dataDir)
    : BuildStore(database, dataDir, "station_builds", "station-builds", "", "/api/v1/station/builds/");

/// <summary>
/// Published builds of a program that updates itself from the server. The files live in
/// data_dir/&lt;folder&gt;, named by their SHA-256.
/// </summary>
public abstract class BuildStore
{
    private const string Columns = "id, version, sha256, size_bytes, published_at";

    private readonly Database _database;
    private readonly string _directory;
    private readonly string _table;
    private readonly string _extension;
    private readonly string _downloadPrefix;

    protected BuildStore(Database database, string dataDir, string table, string folder, string extension, string downloadPrefix)
    {
        _database = database;
        _directory = Path.Combine(dataDir, folder);
        _table = table;
        _extension = extension;
        _downloadPrefix = downloadPrefix;
        Directory.CreateDirectory(_directory);
    }

    public BuildRecord? Latest() =>
        _database.QueryOne($"SELECT {Columns} FROM {_table} ORDER BY id DESC LIMIT 1", Map);

    /// <summary>The most recent publish of the build with this hash.</summary>
    public BuildRecord? Find(string sha256) =>
        _database.QueryOne($"SELECT {Columns} FROM {_table} WHERE sha256 = $sha ORDER BY id DESC LIMIT 1", Map,
            ("$sha", sha256.ToLowerInvariant()));

    public List<BuildRecord> List() =>
        _database.Query($"SELECT {Columns} FROM {_table} ORDER BY id DESC", Map);

    /// <summary>Stores an uploaded program and makes it the build everything should run.</summary>
    public async Task<BuildRecord> PublishAsync(string version, Stream exe, CancellationToken ct)
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

            return _database.QueryOne($"""
                INSERT INTO {_table} (version, sha256, size_bytes, published_at)
                VALUES ($version, $sha, $size, $now)
                RETURNING {Columns}
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
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetTime(4), _downloadPrefix + r.GetString(2));
}
