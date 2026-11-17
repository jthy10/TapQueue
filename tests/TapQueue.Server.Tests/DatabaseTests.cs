using TapQueue.Server.Data;

namespace TapQueue.Server.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;

    [Fact]
    public void MigratesANewDatabaseToTheLatestVersionAndIsIdempotent()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        new UserStore(db).Create("alice", "Alice", null);
        db.Migrate();

        Assert.Equal(Database.LatestSchemaVersion, db.SchemaVersion());
        Assert.NotNull(new UserStore(db).FindByUsername("alice"));
    }

    [Fact]
    public void EveryMigrationSinceTheFloorIsReallyUndoneAndCanBeAppliedAgain()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        // Something in everything the newer schemas added, and in what they keep in settings.
        var alice = new UserStore(db).Create("alice", "Alice", null);
        new Accounts(db).Save(new AccountingSettings(TapQueue.Shared.Api.AccountingMode.Enforce, InitialBalance: 500));
        new Accounts(db).Adjust(alice.Id, 2500, "admin", "top-up");
        new PortalSessionStore(db).Start("hash", alice.Id, "192.0.2.1", "browser");
        new PrinterStore(db).Create(new PrinterRecord("office", "Office", "", "ipp://192.0.2.9/ipp/print", false, PriceMono: 40));
        new QueueStore(db).Create(new QueueRecord("direct", "Direct", "", "", false, true, QueueRecord.Letter,
            TapQueue.Shared.Api.SidesPolicy.ForceTwoSided, 50, "office"));
        db.Execute("""INSERT INTO settings (key, value) VALUES ('directory', '{"clientSignIn":"workstation","host":"dc01"}'), ('sso', '{}'), ('userPortal', 'off')""");

        Assert.Equal(Database.LatestSchemaVersion, db.DowngradeTo(Database.FloorSchema));

        Assert.Equal(Database.FloorSchema, db.SchemaVersion());
        string[] Columns(string table) => db.Query($"SELECT name FROM pragma_table_info('{table}')", r => r.GetString(0)).ToArray();
        Assert.DoesNotContain("balance", Columns("users"));
        Assert.DoesNotContain("price_mono", Columns("printers"));
        Assert.DoesNotContain("cost", Columns("jobs"));
        Assert.DoesNotContain("direct_printer_id", Columns("queues"));
        Assert.DoesNotContain("release_signature", Columns("client_builds"));
        Assert.Empty(db.Query("SELECT name FROM sqlite_master WHERE name IN ('balance_transactions', 'portal_sessions')", r => r.GetString(0)));
        // An older server would take "workstation" for the default it doesn't know it isn't.
        Assert.Contains("\"clientSignIn\":\"pc\"", (string)db.Scalar("SELECT value FROM settings WHERE key = 'directory'")!);
        Assert.Contains("dc01", (string)db.Scalar("SELECT value FROM settings WHERE key = 'directory'")!);
        Assert.Empty(db.Query("SELECT key FROM settings WHERE key IN ('sso', 'userPortal', 'accounting', 'initialBalance')", r => r.GetString(0)));

        // What was there before all that is untouched, and upgrading again works.
        db.Migrate();
        Assert.Equal(Database.LatestSchemaVersion, db.SchemaVersion());
        Assert.Equal(0, new UserStore(db).FindByUsername("alice")!.Balance);
        Assert.Equal("Office", new PrinterStore(db).Get("office")!.Name);
        Assert.Null(new QueueStore(db).Get("direct")!.DirectPrinterId);
    }

    [Fact]
    public void RefusesADatabaseFromANewerServer()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        db.Execute($"PRAGMA user_version = {Database.LatestSchemaVersion + 1}");

        Assert.Throws<InvalidOperationException>(db.Migrate);
    }

    [Fact]
    public void JobsARestartInterruptedMidReleaseAreHeldAgain()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        var jobs = new JobStore(db);
        var job = jobs.Create(new NewJob(null, null, "secure", "doc", "application/pdf", 1, "192.0.2.1", DateTimeOffset.UtcNow.AddHours(1), null));
        jobs.MarkReceived(job.Id, 10, 1);
        jobs.TryTransition(job.Id, JobStatus.Held, JobStatus.Releasing);

        Assert.Equal(1, jobs.RecoverInterruptedReleases());
        Assert.Equal(JobStatus.Held, jobs.Get(job.Id)!.Status);
        Assert.Equal(0, jobs.RecoverInterruptedReleases());
    }

    public void Dispose() => TestFiles.DeleteDirectory(_dir);
}
