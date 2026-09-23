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

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
