using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using TapQueue.Server.Backups;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests;

/// <summary>Backup files and restoring them with the server stopped.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DataDir(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    // Port 0: there's no server to find running.
    private static ServerConfig Config(string dataDir) => new() { Server = { Listen = "127.0.0.1:0", DataDir = dataDir } };

    private static Database NewDatabase(string dataDir)
    {
        var db = new Database(Path.Combine(dataDir, "tapqueue.db"));
        db.Migrate();
        return db;
    }

    private static List<string> Entries(string archive)
    {
        using var tar = new TarReader(new GZipStream(File.OpenRead(archive), CompressionMode.Decompress));
        var names = new List<string>();
        while (tar.GetNextEntry() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    [Fact]
    public void ABackupHoldsTheDatabaseTheCertificateAndTheDirectoryCaButNoDocuments()
    {
        var data = DataDir("a");
        NewDatabase(data);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "tls")).FullName, "server.key"), "key");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "directory-ca")).FullName, "ca.pem"), "ca");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "spool")).FullName, "1.job"), "document");
        var backups = new BackupStore(data);

        var backup = backups.Create(BackupKind.Manual);

        Assert.Equal(["backup.json", "tapqueue.db", "tls/server.key", "directory-ca/ca.pem"], Entries(backups.FileFor(backup.Name)!));
        Assert.Equal(backup, Assert.Single(backups.List()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backups.FileFor(backup.Name)!));
    }

    [Fact]
    public void WithAPassphraseBackupsAreEncryptedAndRestoringNeedsIt()
    {
        const string passphrase = "correct horse battery staple";
        var old = DataDir("old");
        var db = NewDatabase(old);
        var alice = new UserStore(db).Create("alice", "Alice", null);
        new BadgeStore(db, new BadgeKey(Path.Combine(old, BadgeKey.Folder, "badge.key"))).Add(alice.Id, "04A1B2C3");
        var backups = new BackupStore(old, passphrase);

        var backup = backups.Create(BackupKind.Manual);
        var archive = backups.FileFor(backup.Name)!;

        Assert.True(backup.Encrypted);
        Assert.EndsWith(".tar.gz.enc", backup.Name);
        Assert.True(BackupCipher.IsEncrypted(archive));
        // Not a gzip any more, and nothing in it reads as the database it holds.
        Assert.DoesNotContain("SQLite format 3", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(archive)));

        var fresh = DataDir("fresh");
        Assert.Contains("is encrypted", Assert.Throws<InvalidDataException>(() => BackupRestore.Run(Config(fresh), archive)).Message);
        Assert.Contains("passphrase is wrong",
            Assert.Throws<InvalidDataException>(() => BackupRestore.Run(Config(fresh), archive, passphrase: "something else entirely")).Message);
        Assert.False(File.Exists(Path.Combine(fresh, "tapqueue.db")));

        var config = Config(fresh);
        config.Backup.Passphrase = passphrase;
        BackupRestore.Run(config, archive);

        // The key the cards were hashed with came back with them.
        var restored = new BadgeStore(new Database(Path.Combine(fresh, "tapqueue.db")), new BadgeKey(config));
        Assert.False(restored.KeyMismatch);
        Assert.Equal(alice.Id, restored.Use("04a1b2c3")?.UserId);
    }

    [Fact]
    public void BackupsAreCopiedToASecondFolderWhichFollowsWhatIsKept()
    {
        var data = DataDir("data");
        var elsewhere = Path.Combine(_dir, "nas", "tapqueue");
        NewDatabase(data);
        var backups = new BackupStore(data, copyTo: elsewhere);

        var first = backups.Create(BackupKind.Daily, DateTimeOffset.UtcNow.AddDays(-2));
        var second = backups.Create(BackupKind.Daily, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.True(first.Copied);
        Assert.Null(backups.CopyError);
        Assert.Equal(File.ReadAllBytes(backups.FileFor(second.Name)!), File.ReadAllBytes(Path.Combine(elsewhere, second.Name)));
        Assert.DoesNotContain(Directory.GetFiles(elsewhere), f => f.EndsWith(".tmp"));

        backups.Prune(keepDaily: 1);

        Assert.Equal([second.Name], Directory.GetFiles(elsewhere).Select(Path.GetFileName));
    }

    [Fact]
    public void ABackupIsStillMadeWhenTheSecondFolderIsAwayAndFollowsLater()
    {
        var data = DataDir("data");
        NewDatabase(data);
        // A file where the folder should be: nothing can be written "in" it.
        var elsewhere = Path.Combine(_dir, "share");
        File.WriteAllText(elsewhere, "not a folder");
        var backups = new BackupStore(data, copyTo: elsewhere);

        var backup = backups.Create(BackupKind.Manual);

        Assert.NotNull(backups.FileFor(backup.Name));
        Assert.False(backup.Copied);
        Assert.NotNull(backups.CopyError);

        File.Delete(elsewhere);
        Assert.True(backups.CopyOut());
        Assert.True(Assert.Single(backups.List()).Copied);
        Assert.Null(backups.CopyError);
    }

    [Fact]
    public void RestoringBringsBackWhatWasThereAndKeepsWhatItReplaced()
    {
        var old = DataDir("old");
        new UserStore(NewDatabase(old)).Create("alice", "Alice", null);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(old, "tls")).FullName, "server.key"), "old key");
        var backups = new BackupStore(old);
        var archive = backups.FileFor(backups.Create(BackupKind.Manual).Name)!;

        var fresh = DataDir("fresh");
        new UserStore(NewDatabase(fresh)).Create("bob", "Bob", null);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(fresh, "tls")).FullName, "server.key"), "new key");
        SqliteConnection.ClearAllPools();

        var result = BackupRestore.Run(Config(fresh), archive);

        var users = new UserStore(NewDatabase(fresh));
        Assert.NotNull(users.FindByUsername("alice"));
        Assert.Null(users.FindByUsername("bob"));
        Assert.Equal("old key", File.ReadAllText(Path.Combine(fresh, "tls", "server.key")));
        Assert.Equal("new key", File.ReadAllText(Path.Combine(result.ReplacedDir!, "tls", "server.key")));
        Assert.True(File.Exists(Path.Combine(result.ReplacedDir!, "tapqueue.db")));
        Assert.Equal(Database.LatestSchemaVersion, result.Manifest.SchemaVersion);
    }

    [Fact]
    public void JobsWhoseDocumentsArentThereAreCanceledAndBuildsWithoutFilesForgotten()
    {
        var old = DataDir("old");
        var db = NewDatabase(old);
        var jobs = new JobStore(db);
        var kept = jobs.Create(new NewJob(null, null, "secure", "kept", "application/pdf", 1, "192.0.2.1", DateTimeOffset.UtcNow.AddHours(1), null));
        jobs.MarkReceived(kept.Id, 8, 1);
        var lost = jobs.Create(new NewJob(null, null, "secure", "lost", "application/pdf", 1, "192.0.2.1", DateTimeOffset.UtcNow.AddHours(1), null));
        jobs.MarkReceived(lost.Id, 8, 1);
        new StationBuildStore(db, old).PublishAsync("1.0", new MemoryStream([1, 2, 3]), default).GetAwaiter().GetResult();
        var backups = new BackupStore(old);
        var archive = backups.FileFor(backups.Create(BackupKind.Manual).Name)!;

        var fresh = DataDir("fresh");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(fresh, "spool")).FullName, $"{kept.Id}.job"), "document");
        var result = BackupRestore.Run(Config(fresh), archive);

        Assert.Equal(1, result.LostJobs);
        Assert.Equal(1, result.ForgottenBuilds);
        Assert.Null(result.ReplacedDir);
        var restored = NewDatabase(fresh);
        Assert.Equal(JobStatus.Held, new JobStore(restored).Get(kept.Id)!.Status);
        Assert.Equal(JobStatus.Canceled, new JobStore(restored).Get(lost.Id)!.Status);
        Assert.Null(new StationBuildStore(restored, fresh).Latest());
    }

    [Fact]
    public void ABackupFromANewerServerIsRefusedAndNothingChanges()
    {
        var newer = DataDir("newer");
        NewDatabase(newer).Execute($"PRAGMA user_version = {Database.LatestSchemaVersion + 1}");
        var backups = new BackupStore(newer);
        var archive = backups.FileFor(backups.Create(BackupKind.Manual).Name)!;

        var here = DataDir("here");
        new UserStore(NewDatabase(here)).Create("bob", "Bob", null);
        SqliteConnection.ClearAllPools();

        var error = Assert.Throws<InvalidDataException>(() => BackupRestore.Run(Config(here), archive));

        Assert.Contains("newer than this one", error.Message);
        Assert.NotNull(new UserStore(NewDatabase(here)).FindByUsername("bob"));
        Assert.Empty(Directory.GetDirectories(here));
    }

    [Fact]
    public void AnArchiveCantWriteOutsideTheDataDirectory()
    {
        var source = DataDir("source");
        NewDatabase(source);
        var backups = new BackupStore(source);
        var good = backups.FileFor(backups.Create(BackupKind.Manual).Name)!;
        // The same backup with entries added that try to climb out or land somewhere else.
        var bad = Path.Combine(_dir, "bad.tar.gz");
        using (var reader = new TarReader(new GZipStream(File.OpenRead(good), CompressionMode.Decompress)))
        using (var writer = new TarWriter(new GZipStream(File.Create(bad), CompressionLevel.Fastest)))
        {
            while (reader.GetNextEntry(copyData: true) is { } entry)
                writer.WriteEntry(entry);
            foreach (var name in new[] { "../escaped", "tls/../../escaped", "spool/1.job", "/tmp/tapqueue-escaped", "tls/sub/key" })
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream([1]) });
        }

        var target = DataDir("target");
        BackupRestore.Run(Config(target), bad);

        Assert.Equal(["tapqueue.db"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped")));
    }

    [Fact]
    public void AFileThatIsntABackupIsRefused()
    {
        var file = Path.Combine(_dir, "notes.tar.gz");
        File.WriteAllText(file, "not a backup");

        var error = Assert.Throws<InvalidDataException>(() => BackupRestore.Run(Config(DataDir("target")), file));

        Assert.Contains("isn't a TapQueue backup", error.Message);
    }

    [Fact]
    public void ARunningServerIsntRestoredOver()
    {
        var source = DataDir("source");
        NewDatabase(source);
        var backups = new BackupStore(source);
        var archive = backups.FileFor(backups.Create(BackupKind.Manual).Name)!;
        using var running = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        running.Start();
        var config = new ServerConfig { Server = { Listen = running.LocalEndpoint.ToString()!, DataDir = DataDir("target") } };

        var error = Assert.Throws<InvalidDataException>(() => BackupRestore.Run(config, archive));

        Assert.Contains("stop the server first", error.Message);
    }

    [Fact]
    public void OnlyTheNewestDailyBackupsAreKeptAndManualOnesAreLeftAlone()
    {
        var data = DataDir("a");
        NewDatabase(data);
        var backups = new BackupStore(data);
        var start = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
        var manual = backups.Create(BackupKind.Manual, start.AddDays(-30));
        var daily = Enumerable.Range(0, 5).Select(day => backups.Create(BackupKind.Daily, start.AddDays(day))).ToList();

        Assert.Equal(3, backups.Prune(keepDaily: 2));

        Assert.Equal([daily[4].Name, daily[3].Name, manual.Name], backups.List().Select(b => b.Name));
    }

    [Fact]
    public void TwoBackupsInOneSecondAreTwoFiles()
    {
        var data = DataDir("a");
        NewDatabase(data);
        var backups = new BackupStore(data);
        var now = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

        backups.Create(BackupKind.Manual, now);
        backups.Create(BackupKind.Manual, now);

        Assert.Equal(2, backups.List().Count);
    }

    [Theory]
    [InlineData("../tapqueue.db")]
    [InlineData("tapqueue-20261001T020000Z-daily.tar.gz/../../tapqueue.db")]
    [InlineData("snapshot-1.tmp")]
    public void OnlyBackupsCanBeAskedForByName(string name)
    {
        var data = DataDir("a");
        NewDatabase(data);
        var backups = new BackupStore(data);
        backups.Create(BackupKind.Manual);

        Assert.Null(backups.FileFor(name));
        Assert.False(backups.Delete(name));
    }

    [Fact]
    public void ADatabaseAboutToBeUpgradedIsBackedUpFirst()
    {
        var data = DataDir("a");
        var db = NewDatabase(data);
        var backups = new BackupStore(data);
        Assert.Null(backups.CreateBeforeUpgrade());

        // What a database from the release before looks like to this one.
        db.Execute($"PRAGMA user_version = {Database.LatestSchemaVersion - 1}");
        var backup = backups.CreateBeforeUpgrade();

        Assert.Equal(BackupKind.Upgrade, backup!.Kind);
        Assert.Null(new BackupStore(DataDir("empty")).CreateBeforeUpgrade());
    }
}
