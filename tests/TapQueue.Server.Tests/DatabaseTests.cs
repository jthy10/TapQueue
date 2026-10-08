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

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
