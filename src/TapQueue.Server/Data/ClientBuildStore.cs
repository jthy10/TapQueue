using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record ClientBuildRecord(long Id, string Version, string Sha256, long SizeBytes, DateTimeOffset PublishedAt)
{
    public ClientBuildDto ToDto() => new(Version, Sha256, SizeBytes, PublishedAt, $"/api/v1/client/builds/{Sha256}");
}

/// <summary>
/// Windows client builds published with `tapqueue-admin client publish`. The newest one is what
/// every client should be running; clients running anything else download it and install it.
/// The files live in data_dir/client-builds, named by their SHA-256.
/// </summary>
public sealed class ClientBuildStore
{
    private const string Columns = "id, version, sha256, size_bytes, published_at";

    private readonly Database _database;
    private readonly string _directory;

    public ClientBuildStore(Database database, string dataDir)
    {
        _database = database;
        _directory = Path.Combine(dataDir, "client-builds");
        Directory.CreateDirectory(_directory);
    }

    public ClientBuildRecord? Latest() =>
        _database.QueryOne($"SELECT {Columns} FROM client_builds ORDER BY id DESC LIMIT 1", Map);

    /// <summary>The most recent publish of the build with this hash.</summary>
    public ClientBuildRecord? Find(string sha256) =>
        _database.QueryOne($"SELECT {Columns} FROM client_builds WHERE sha256 = $sha ORDER BY id DESC LIMIT 1", Map,
            ("$sha", sha256.ToLowerInvariant()));

    public List<ClientBuildRecord> List() =>
        _database.Query($"SELECT {Columns} FROM client_builds ORDER BY id DESC", Map);

    /// <summary>Stores an uploaded TapQueueClient.exe and makes it the build clients should run.</summary>
    public async Task<ClientBuildRecord> PublishAsync(string version, Stream exe, CancellationToken ct)
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
                throw new InvalidDataException("The uploaded client build is empty.");
            File.Move(temp, PathFor(sha256), overwrite: true);

            return _database.QueryOne($"""
                INSERT INTO client_builds (version, sha256, size_bytes, published_at)
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

    private string PathFor(string sha256) => Path.Combine(_directory, $"{sha256}.exe");

    private static ClientBuildRecord Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetTime(4));
}
