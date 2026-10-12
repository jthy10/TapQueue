using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared;

namespace TapQueue.Server.Backups;

/// <summary>What a restore did, for telling the person who ran it.</summary>
/// <param name="ReplacedDir">Where what was there before went, or null if there was nothing.</param>
/// <param name="LostJobs">Jobs that were waiting when the backup was made; their documents weren't in it.</param>
/// <param name="ForgottenBuilds">Published client and station builds whose files aren't on this server.</param>
public sealed record RestoreResult(BackupManifest Manifest, string? ReplacedDir, int LostJobs, int ForgottenBuilds);

/// <summary>
/// `tapqueue-server --restore file`: puts a backup's database, TLS certificate and Active Directory CA
/// back in data_dir, with the server stopped. What was there is moved aside, not deleted.
/// </summary>
public static class BackupRestore
{
    /// <summary>The most a backup's database may unpack to: a damaged or hostile archive can't fill the disk.</summary>
    private const long MaxEntryBytes = 64L * 1024 * 1024 * 1024;

    /// <exception cref="InvalidDataException">The backup can't be restored here; the message says why. Nothing was changed.</exception>
    public static RestoreResult Run(ServerConfig config, string archivePath, DateTimeOffset? now = null)
    {
        if (!File.Exists(archivePath))
            throw new InvalidDataException($"There's no file {archivePath}.");
        var dataDir = config.Server.DataDir;
        EnsureServerIsStopped(config);

        Directory.CreateDirectory(dataDir);
        var staging = Path.Combine(dataDir, $"restore-{Guid.NewGuid():N}.tmp");
        BackupStore.CreatePrivateDirectory(staging);
        try
        {
            var manifest = Extract(archivePath, staging);
            Check(Path.Combine(staging, BackupStore.DatabaseEntry), manifest);

            var replaced = MoveCurrentAside(dataDir, (now ?? DateTimeOffset.UtcNow).ToUniversalTime());
            File.Move(Path.Combine(staging, BackupStore.DatabaseEntry), Path.Combine(dataDir, "tapqueue.db"));
            foreach (var folder in BackupStore.Folders)
                if (Directory.Exists(Path.Combine(staging, folder)))
                    Directory.Move(Path.Combine(staging, folder), Path.Combine(dataDir, folder));

            var (lostJobs, forgottenBuilds) = Reconcile(dataDir);
            return new RestoreResult(manifest, replaced, lostJobs, forgottenBuilds);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>A running server has the database open and would carry on writing to the one moved aside.</summary>
    private static void EnsureServerIsStopped(ServerConfig config)
    {
        var endpoint = ServerApp.ParseEndpoint(config.Server.Listen);
        if (endpoint.Port == 0)
            return;
        try
        {
            using var probe = new TcpListener(endpoint);
            probe.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidDataException(
                $"Something is listening on {config.Server.Listen}: stop the server first (sudo systemctl stop tapqueue-server).");
        }
        catch (SocketException)
        {
            // Not an address of this machine, or a port this user can't open: nothing here is using it.
        }
    }

    private static BackupManifest Extract(string archivePath, string staging)
    {
        BackupManifest? manifest = null;
        var hasDatabase = false;
        try
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                    continue;
                if (entry.Length > MaxEntryBytes)
                    throw new InvalidDataException($"{entry.Name} in the backup is too big to be real.");
                if (entry.Name == BackupStore.ManifestEntry)
                {
                    manifest = JsonSerializer.Deserialize<BackupManifest>(entry.DataStream, TapQueueJson.Options);
                    continue;
                }
                // Only the files a backup holds, by name: nothing in the archive chooses where it lands.
                var parts = entry.Name.Split('/');
                string target;
                if (entry.Name == BackupStore.DatabaseEntry)
                {
                    target = Path.Combine(staging, BackupStore.DatabaseEntry);
                    hasDatabase = true;
                }
                else if (parts.Length == 2 && BackupStore.Folders.Contains(parts[0]) && IsPlainFileName(parts[1]))
                {
                    BackupStore.CreatePrivateDirectory(Path.Combine(staging, parts[0]));
                    target = Path.Combine(staging, parts[0], parts[1]);
                }
                else
                {
                    continue;
                }
                using var output = new FileStream(target, BackupStore.PrivateFile(FileMode.Create));
                entry.DataStream.CopyTo(output);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or JsonException)
        {
            throw new InvalidDataException($"{archivePath} isn't a TapQueue backup: {ex.Message}");
        }
        if (manifest is null || !hasDatabase)
            throw new InvalidDataException($"{archivePath} isn't a TapQueue backup: it has no {(hasDatabase ? BackupStore.ManifestEntry : BackupStore.DatabaseEntry)}.");
        return manifest;
    }

    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && !name.Any(c => c is '/' or '\\' or '\0');

    private static void Check(string databasePath, BackupManifest manifest)
    {
        using var db = OpenUnpooled(databasePath, SqliteOpenMode.ReadOnly);
        string integrity;
        int version;
        try
        {
            integrity = Convert.ToString(Scalar(db, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) ?? "";
            version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException($"The database in the backup can't be read: {ex.Message}");
        }
        if (integrity != "ok")
            throw new InvalidDataException($"The database in the backup is damaged: {integrity}");
        if (version > Database.LatestSchemaVersion)
            throw new InvalidDataException(
                $"The backup is from TapQueue server {manifest.ServerVersion} (database schema {version}), newer than this one " +
                $"({TapQueueVersion.Current}, schema {Database.LatestSchemaVersion}). Install that version or a newer one, then restore.");
    }

    /// <summary>Moves the database and the folders a backup replaces into data_dir/replaced-&lt;time&gt;.</summary>
    private static string? MoveCurrentAside(string dataDir, DateTimeOffset now)
    {
        var current = new[] { "tapqueue.db", "tapqueue.db-wal", "tapqueue.db-shm" }.Concat(BackupStore.Folders)
            .Select(name => Path.Combine(dataDir, name))
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();
        if (current.Count == 0)
            return null;

        var stamped = Path.Combine(dataDir, $"replaced-{now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}");
        var aside = stamped;
        for (var n = 2; Directory.Exists(aside); n++)
            aside = $"{stamped}-{n}";
        BackupStore.CreatePrivateDirectory(aside);
        foreach (var path in current)
            Directory.Move(path, Path.Combine(aside, Path.GetFileName(path)));
        return aside;
    }

    /// <summary>
    /// The database remembers files that weren't in the backup. Jobs that were waiting have no
    /// document any more, so they're canceled. A published build whose file is gone would have every
    /// PC or station trying to download it for ever; if the newest one's file is missing, that
    /// program's builds are all forgotten (rather than handing out an older one) until one is published again.
    /// </summary>
    private static (int LostJobs, int ForgottenBuilds) Reconcile(string dataDir)
    {
        using var db = OpenUnpooled(Path.Combine(dataDir, "tapqueue.db"), SqliteOpenMode.ReadWrite);
        var lostJobs = 0;
        if (HasTable(db, "jobs"))
        {
            // The spool isn't touched by a restore, so a job from the same server may still have its document.
            foreach (var (id, size) in Jobs(db, $"SELECT id, size_bytes FROM jobs WHERE status IN ('{JobStatus.Receiving}', '{JobStatus.Held}', '{JobStatus.Releasing}')"))
            {
                var document = new FileInfo(Path.Combine(dataDir, "spool", $"{id}.job"));
                if (document.Exists && document.Length == size)
                    continue;
                Execute(db, $"UPDATE jobs SET status = '{JobStatus.Canceled}', error = 'Its document wasn''t in the backup this server was restored from.' WHERE id = {id}");
                lostJobs++;
            }
        }

        var forgotten = 0;
        forgotten += ForgetMissingBuilds(db, "station_builds", null, Path.Combine(dataDir, "station-builds"), "");
        if (HasTable(db, "client_builds"))
        {
            var hasPlatform = Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('client_builds') WHERE name = 'platform'"), CultureInfo.InvariantCulture) > 0;
            var platforms = hasPlatform ? Strings(db, "SELECT DISTINCT platform FROM client_builds") : [null];
            foreach (var platform in platforms)
                forgotten += ForgetMissingBuilds(db, "client_builds", platform, Path.Combine(dataDir, "client-builds"), ".exe");
        }
        return (lostJobs, forgotten);
    }

    private static int ForgetMissingBuilds(SqliteConnection db, string table, string? platform, string directory, string extension)
    {
        if (!HasTable(db, table))
            return 0;
        var where = platform is null ? "1 = 1" : $"platform = '{platform.Replace("'", "''")}'";
        var builds = new List<(long Id, string Sha256)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, sha256 FROM {table} WHERE {where} ORDER BY id DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                builds.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        if (builds.Count == 0)
            return 0;
        bool Present((long Id, string Sha256) build) => File.Exists(Path.Combine(directory, build.Sha256 + extension));
        var gone = Present(builds[0]) ? builds.Where(b => !Present(b)).ToList() : builds;
        foreach (var build in gone)
            Execute(db, $"DELETE FROM {table} WHERE id = {build.Id}");
        return gone.Count;
    }

    private static SqliteConnection OpenUnpooled(string path, SqliteOpenMode mode)
    {
        // Pooled connections stay open after Dispose, and this file is about to be moved.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static bool HasTable(SqliteConnection db, string table) =>
        Convert.ToInt32(Scalar(db, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'"), CultureInfo.InvariantCulture) > 0;

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<(long Id, long SizeBytes)> Jobs(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var jobs = new List<(long, long)>();
        while (reader.Read())
            jobs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return jobs;
    }

    private static List<string?> Strings(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string?>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }
}
