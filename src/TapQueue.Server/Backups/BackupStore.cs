using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Backups;

/// <summary>What backup.json inside a backup says about it.</summary>
public sealed record BackupManifest(string ServerVersion, int SchemaVersion, DateTimeOffset CreatedAt);

/// <summary>
/// Backups of everything that can't be made again: the database, the server's TLS certificate and key
/// (PCs and stations trust that one) and the Active Directory CA. Each is one .tar.gz in
/// data_dir/backups, named for when and why it was made. Held documents and published client and
/// station builds are left out: jobs are gone in a day anyway, and builds can be published again.
/// </summary>
public sealed partial class BackupStore
{
    public const string DatabaseEntry = "tapqueue.db";
    public const string ManifestEntry = "backup.json";

    /// <summary>Folders in data_dir that go into a backup whole. Only plain files directly in them.</summary>
    public static readonly string[] Folders = ["tls", "directory-ca"];

    /// <summary>Backups made before a schema upgrade, kept besides the scheduled ones.</summary>
    private const int UpgradeBackupsKept = 3;

    private readonly string _dataDir;
    private readonly string _databasePath;
    private readonly Lock _lock = new();

    public BackupStore(ServerConfig config) : this(config.Server.DataDir)
    {
    }

    public BackupStore(string dataDir)
    {
        _dataDir = dataDir;
        _databasePath = Path.Combine(dataDir, "tapqueue.db");
        Directory = Path.Combine(dataDir, "backups");
    }

    public string Directory { get; }

    [GeneratedRegex(@"^tapqueue-(\d{8}T\d{6}Z)-(daily|manual|upgrade)\.tar\.gz$")]
    private static partial Regex NamePattern();

    /// <summary>Newest first.</summary>
    public List<BackupDto> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        return new DirectoryInfo(Directory).EnumerateFiles("tapqueue-*.tar.gz")
            .Select(file => Parse(file.Name) is { } parsed ? new BackupDto(file.Name, parsed.Kind, parsed.CreatedAt, file.Length) : null)
            .OfType<BackupDto>()
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    /// <summary>The file for a backup by name, or null if there's none. Names that aren't a backup's never reach the disk.</summary>
    public string? FileFor(string name)
    {
        if (Parse(name) is null)
            return null;
        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (FileFor(name) is not { } path)
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Makes a backup now. The database is copied with VACUUM INTO, which gives a consistent copy
    /// while the server keeps printing.
    /// </summary>
    public BackupDto Create(string kind, DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            var createdAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
            createdAt = new DateTimeOffset(createdAt.Ticks - createdAt.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            // Names are to the second; two in one second would otherwise be one file.
            string name;
            while (File.Exists(Path.Combine(Directory, name = NameFor(createdAt, kind))))
                createdAt = createdAt.AddSeconds(1);

            CreatePrivateDirectory(Directory);
            var snapshot = Path.Combine(Directory, $"snapshot-{Guid.NewGuid():N}.tmp");
            var archive = Path.Combine(Directory, $"archive-{Guid.NewGuid():N}.tmp");
            try
            {
                var schemaVersion = Snapshot(snapshot);
                using (var file = new FileStream(archive, PrivateFile(FileMode.CreateNew)))
                using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
                {
                    var manifest = new BackupManifest(TapQueueVersion.Current, schemaVersion, createdAt);
                    WriteEntry(tar, ManifestEntry, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, TapQueueJson.Options)));
                    using (var database = File.OpenRead(snapshot))
                        WriteEntry(tar, DatabaseEntry, database);
                    foreach (var folder in Folders)
                    {
                        var path = Path.Combine(_dataDir, folder);
                        if (!System.IO.Directory.Exists(path))
                            continue;
                        foreach (var item in System.IO.Directory.EnumerateFiles(path).Order())
                        {
                            using var content = File.OpenRead(item);
                            WriteEntry(tar, $"{folder}/{Path.GetFileName(item)}", content);
                        }
                    }
                }
                File.Move(archive, Path.Combine(Directory, name));
                return new BackupDto(name, kind, createdAt, new FileInfo(Path.Combine(Directory, name)).Length);
            }
            finally
            {
                File.Delete(snapshot);
                File.Delete(archive);
            }
        }
    }

    /// <summary>
    /// Deletes scheduled backups beyond the newest <paramref name="keepDaily"/> and all but the last few
    /// made before upgrades. Ones an admin made by hand stay until they delete them.
    /// </summary>
    public int Prune(int keepDaily)
    {
        var all = List();
        var old = all.Where(b => b.Kind == BackupKind.Daily).Skip(Math.Max(keepDaily, 1))
            .Concat(all.Where(b => b.Kind == BackupKind.Upgrade).Skip(UpgradeBackupsKept))
            .ToList();
        foreach (var backup in old)
            Delete(backup.Name);
        return old.Count;
    }

    /// <summary>
    /// Before a newer server changes the database's schema, keeps a copy the older server can still
    /// read. Null when there's nothing to upgrade (a new database, or one already current).
    /// </summary>
    public BackupDto? CreateBeforeUpgrade()
    {
        if (!File.Exists(_databasePath))
            return null;
        var version = new Database(_databasePath).SchemaVersion();
        if (version == 0 || version >= Database.LatestSchemaVersion)
            return null;
        var backup = Create(BackupKind.Upgrade);
        Prune(int.MaxValue);
        return backup;
    }

    private int Snapshot(string to)
    {
        // Not pooled, so nothing keeps the snapshot's source connection (or a lock) around afterwards.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using (var vacuum = db.CreateCommand())
        {
            vacuum.CommandText = $"VACUUM INTO '{to.Replace("'", "''")}'";
            vacuum.ExecuteNonQuery();
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(to, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void WriteEntry(TarWriter tar, string name, Stream content) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = content,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    private static string NameFor(DateTimeOffset at, string kind) =>
        $"tapqueue-{at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{kind}.tar.gz";

    private static (string Kind, DateTimeOffset CreatedAt)? Parse(string name) =>
        NamePattern().Match(name) is { Success: true } match
        && DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? (match.Groups[2].Value, at)
            : null;

    /// <summary>Backups hold password hashes and the server's private key: only the server's user may read them.</summary>
    internal static void CreatePrivateDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static FileStreamOptions PrivateFile(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
