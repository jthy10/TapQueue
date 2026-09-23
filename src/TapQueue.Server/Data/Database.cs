using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Data;

/// <summary>Opens connections to the SQLite database and creates the schema on first run.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Migrate()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY,
                username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                display_name  TEXT NOT NULL,
                token_hash    TEXT,
                created_at    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id            INTEGER PRIMARY KEY,
                token_hash    TEXT NOT NULL UNIQUE,
                user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                windows_user  TEXT,
                hostname      TEXT,
                remote_ip     TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_remote_ip ON sessions(remote_ip);

            CREATE TABLE IF NOT EXISTS jobs (
                id                  INTEGER PRIMARY KEY,
                user_id             INTEGER REFERENCES users(id) ON DELETE SET NULL,
                owner_hint          TEXT,
                queue_id            TEXT NOT NULL,
                name                TEXT NOT NULL,
                document_format     TEXT NOT NULL,
                copies              INTEGER NOT NULL DEFAULT 1,
                job_attributes      BLOB,
                size_bytes          INTEGER NOT NULL DEFAULT 0,
                status              TEXT NOT NULL,
                source_ip           TEXT NOT NULL,
                submitted_at        TEXT NOT NULL,
                expires_at          TEXT NOT NULL,
                released_at         TEXT,
                released_printer_id TEXT,
                error               TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_user_status ON jobs(user_id, status);
            """;
        cmd.ExecuteNonQuery();
    }
}
