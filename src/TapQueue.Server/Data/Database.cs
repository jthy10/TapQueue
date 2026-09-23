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

    /// <summary>Runs a statement and returns the number of rows it changed.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var db = Open();
        using var cmd = Command(db, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Runs a query and returns the first column of the first row, or null if there are no rows.</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var db = Open();
        using var cmd = Command(db, sql, parameters);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>Runs a query (or an INSERT/UPDATE … RETURNING) and maps each row.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var db = Open();
        using var cmd = Command(db, sql, parameters);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    public T? QueryOne<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) where T : class =>
        Query(sql, map, parameters).FirstOrDefault();

    private static SqliteCommand Command(SqliteConnection db, string sql, (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset time => time.ToString("O"),
                _ => value,
            });
        }
        return cmd;
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

            CREATE TABLE IF NOT EXISTS badges (
                id            INTEGER PRIMARY KEY,
                card_hash     TEXT NOT NULL UNIQUE,
                card_hint     TEXT NOT NULL,
                user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                created_at    TEXT NOT NULL,
                last_used_at  TEXT
            );

            CREATE TABLE IF NOT EXISTS stations (
                id            TEXT PRIMARY KEY COLLATE NOCASE,
                printer_id    TEXT NOT NULL,
                token_hash    TEXT NOT NULL UNIQUE,
                created_at    TEXT NOT NULL,
                last_seen_at  TEXT,
                last_ip       TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
