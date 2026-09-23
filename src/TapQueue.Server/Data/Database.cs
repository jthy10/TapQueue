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

    /// <summary>Runs several statements as one transaction: all of them happen, or none do.</summary>
    public int ExecuteAtomically(string sql, params (string Name, object? Value)[] parameters)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = Command(db, sql, parameters);
        cmd.Transaction = transaction;
        var changed = cmd.ExecuteNonQuery();
        transaction.Commit();
        return changed;
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

    /// <summary>
    /// Schema changes, applied in order. The database's user_version records how many have run.
    /// Never edit one that has shipped; add a new one instead.
    /// </summary>
    private static readonly string[] Migrations =
    [
        // 1: users, sessions, jobs, badges, stations (v0.1). IF NOT EXISTS because databases from
        // before migrations were numbered already have these tables.
        """
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
        """,

        // 2: printers and queues move out of server.toml. stations.printer_id isn't a foreign key
        // (SQLite can't add one to an existing table); PrinterStore.Delete checks it instead.
        """
            CREATE TABLE printers (
                id               TEXT PRIMARY KEY COLLATE NOCASE,
                name             TEXT NOT NULL,
                location         TEXT NOT NULL DEFAULT '',
                uri              TEXT NOT NULL,
                tls_skip_verify  INTEGER NOT NULL DEFAULT 0,
                created_at       TEXT NOT NULL
            );

            CREATE TABLE queues (
                id             TEXT PRIMARY KEY COLLATE NOCASE,
                name           TEXT NOT NULL,
                description    TEXT NOT NULL,
                location       TEXT NOT NULL DEFAULT '',
                color          INTEGER NOT NULL DEFAULT 0,
                duplex         INTEGER NOT NULL DEFAULT 0,
                default_media  TEXT NOT NULL,
                created_at     TEXT NOT NULL
            );
        """,

        // 3: Windows client builds published by the admin, and which client version each session runs.
        """
            CREATE TABLE client_builds (
                id            INTEGER PRIMARY KEY,
                version       TEXT NOT NULL,
                sha256        TEXT NOT NULL,
                size_bytes    INTEGER NOT NULL,
                published_at  TEXT NOT NULL
            );

            ALTER TABLE sessions ADD COLUMN client_version TEXT;
        """,

        // 4: disabling users, keeping the owner's name on jobs after the user is deleted, and card labels.
        """
            ALTER TABLE users ADD COLUMN disabled_at TEXT;
            ALTER TABLE jobs ADD COLUMN former_owner TEXT;
            ALTER TABLE badges ADD COLUMN label TEXT NOT NULL DEFAULT '';
        """,

        // 5: the activity log (EventLog).
        """
            CREATE TABLE events (
                id        INTEGER PRIMARY KEY,
                at        TEXT NOT NULL,
                category  TEXT NOT NULL,
                actor     TEXT NOT NULL,
                subject   TEXT,
                message   TEXT NOT NULL
            );
            CREATE INDEX ix_events_subject ON events(subject);
        """,

        // 6: groups, and which queues and printers each may use (AccessPolicy). source and external_id
        // let a directory sync (AD, Entra) own some users and groups later; "local" ones are managed here.
        """
            CREATE TABLE groups (
                id            TEXT PRIMARY KEY COLLATE NOCASE,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                all_queues    INTEGER NOT NULL DEFAULT 1,
                all_printers  INTEGER NOT NULL DEFAULT 1,
                source        TEXT NOT NULL DEFAULT 'local',
                external_id   TEXT,
                created_at    TEXT NOT NULL
            );

            CREATE TABLE group_members (
                group_id  TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                user_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                PRIMARY KEY (group_id, user_id)
            );
            CREATE INDEX ix_group_members_user ON group_members(user_id);

            CREATE TABLE group_queues (
                group_id  TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                queue_id  TEXT NOT NULL REFERENCES queues(id) ON DELETE CASCADE,
                PRIMARY KEY (group_id, queue_id)
            );

            CREATE TABLE group_printers (
                group_id    TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
                printer_id  TEXT NOT NULL REFERENCES printers(id) ON DELETE CASCADE,
                PRIMARY KEY (group_id, printer_id)
            );

            ALTER TABLE users ADD COLUMN source TEXT NOT NULL DEFAULT 'local';
            ALTER TABLE users ADD COLUMN external_id TEXT;
        """,

        // 7: stations report in and take settings and commands from the server; station builds.
        // Setting columns left NULL mean "use station.toml".
        """
            ALTER TABLE stations ADD COLUMN name TEXT NOT NULL DEFAULT '';
            ALTER TABLE stations ADD COLUMN location TEXT NOT NULL DEFAULT '';
            ALTER TABLE stations ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE stations ADD COLUMN maintenance_message TEXT NOT NULL DEFAULT '';
            ALTER TABLE stations ADD COLUMN reader TEXT;
            ALTER TABLE stations ADD COLUMN device TEXT;
            ALTER TABLE stations ADD COLUMN repeat_seconds INTEGER;
            ALTER TABLE stations ADD COLUMN min_card_length INTEGER;
            ALTER TABLE stations ADD COLUMN feedback TEXT;
            ALTER TABLE stations ADD COLUMN settings_version INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE stations ADD COLUMN pending_command TEXT;
            ALTER TABLE stations ADD COLUMN version TEXT;
            ALTER TABLE stations ADD COLUMN binary_sha256 TEXT;
            ALTER TABLE stations ADD COLUMN started_at TEXT;
            ALTER TABLE stations ADD COLUMN reader_status TEXT;
            ALTER TABLE stations ADD COLUMN last_heartbeat_at TEXT;

            CREATE TABLE station_builds (
                id            INTEGER PRIMARY KEY,
                version       TEXT NOT NULL,
                sha256        TEXT NOT NULL,
                size_bytes    INTEGER NOT NULL,
                published_at  TEXT NOT NULL
            );
        """,
    ];

    public void Migrate()
    {
        using var db = Open();
        using (var wal = db.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL";
            wal.ExecuteNonQuery();
        }

        var version = SchemaVersion(db);
        if (version > Migrations.Length)
            throw new InvalidOperationException(
                $"The database is at schema version {version}, but this tapqueue-server only knows {Migrations.Length}. Is it older than the one that last ran?");

        for (var i = version; i < Migrations.Length; i++)
        {
            using var transaction = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public int SchemaVersion()
    {
        using var db = Open();
        return SchemaVersion(db);
    }

    private static int SchemaVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static int LatestSchemaVersion => Migrations.Length;
}
