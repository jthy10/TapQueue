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

        // 8: settings edited in the console (server.toml gives the defaults), sessions an admin
        // signed out, and PCs that run the TapQueue service.
        """
            CREATE TABLE settings (
                key    TEXT PRIMARY KEY,
                value  TEXT NOT NULL
            );

            ALTER TABLE sessions ADD COLUMN signed_out_at TEXT;

            CREATE TABLE workstations (
                hostname         TEXT PRIMARY KEY COLLATE NOCASE,
                last_ip          TEXT NOT NULL,
                version          TEXT,
                binary_sha256    TEXT,
                update_error     TEXT,
                pending_command  TEXT,
                first_seen_at    TEXT NOT NULL,
                last_seen_at     TEXT NOT NULL
            );
        """,

        // 9: quotas. pages is the job's page count (per copy, after page-ranges), NULL if it
        // couldn't be counted. A quota is a page limit per period (day, week or month) on a user or group.
        """
            ALTER TABLE jobs ADD COLUMN pages INTEGER;
            CREATE INDEX ix_jobs_user_released ON jobs(user_id, released_at);

            ALTER TABLE users ADD COLUMN quota_pages INTEGER;
            ALTER TABLE users ADD COLUMN quota_period TEXT;
            ALTER TABLE groups ADD COLUMN quota_pages INTEGER;
            ALTER TABLE groups ADD COLUMN quota_period TEXT;
        """,

        // 10: Linux clients. Client builds are per platform (ClientPlatform), and so are PCs; everything
        // before this was Windows.
        """
            ALTER TABLE client_builds ADD COLUMN platform TEXT NOT NULL DEFAULT 'win-x64';
            ALTER TABLE workstations ADD COLUMN platform TEXT NOT NULL DEFAULT 'win-x64';
        """,

        // 11: crash reports sent by clients (CrashStore).
        """
            CREATE TABLE crash_reports (
                id           INTEGER PRIMARY KEY,
                computer     TEXT NOT NULL COLLATE NOCASE,
                ip           TEXT NOT NULL,
                program      TEXT NOT NULL,
                platform     TEXT NOT NULL,
                version      TEXT,
                occurred_at  TEXT NOT NULL,
                received_at  TEXT NOT NULL,
                message      TEXT NOT NULL,
                details      TEXT
            );
            CREATE INDEX ix_crash_reports_computer ON crash_reports(computer, id);
        """,

        // 12: Active Directory sync (DirectorySync). Users and groups it owns have source 'ad' and their
        // objectGUID in external_id. disabled_by says who disabled a user ('admin' or 'directory'), so a
        // sync only re-enables users it disabled; directory_state says why AD did (disabled, expired,
        // missing). via is the nested AD group a member of an AD group came through. The scope is what to
        // pull users from: OUs, groups and single users, by objectGUID.
        """
            ALTER TABLE users ADD COLUMN disabled_by TEXT;
            ALTER TABLE users ADD COLUMN directory_state TEXT;
            UPDATE users SET disabled_by = 'admin' WHERE disabled_at IS NOT NULL;
            CREATE UNIQUE INDEX ix_users_external ON users(source, external_id) WHERE external_id IS NOT NULL;
            CREATE UNIQUE INDEX ix_groups_external ON groups(source, external_id) WHERE external_id IS NOT NULL;

            ALTER TABLE group_members ADD COLUMN via TEXT;
            ALTER TABLE badges ADD COLUMN source TEXT NOT NULL DEFAULT 'local';

            CREATE TABLE directory_scope (
                id        INTEGER PRIMARY KEY,
                kind      TEXT NOT NULL,
                guid      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                dn        TEXT NOT NULL,
                name      TEXT NOT NULL,
                added_at  TEXT NOT NULL
            );

            CREATE TABLE directory_runs (
                id           INTEGER PRIMARY KEY,
                started_at   TEXT NOT NULL,
                finished_at  TEXT,
                trigger      TEXT NOT NULL,
                dry_run      INTEGER NOT NULL,
                outcome      TEXT NOT NULL,
                summary      TEXT NOT NULL DEFAULT '',
                error        TEXT
            );
        """,

        // 13: Domain sign-in on the tray (ClientSignIn.Domain). A password sign-in leaves a remembered
        // login the tray signs in with until the person signs out; sessions say which login they came from
        // so an admin signing a PC out also forgets its login.
        """
            CREATE TABLE client_logins (
                id            INTEGER PRIMARY KEY,
                token_hash    TEXT NOT NULL UNIQUE,
                user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                windows_user  TEXT,
                hostname      TEXT,
                created_at    TEXT NOT NULL,
                last_used_at  TEXT NOT NULL
            );
            CREATE INDEX ix_client_logins_user ON client_logins(user_id);
            ALTER TABLE sessions ADD COLUMN login_id INTEGER REFERENCES client_logins(id) ON DELETE SET NULL;
        """,

        // 14: Admin sign-in (AdminAccess). A grant gives a user, or every member of a group, a role in one
        // area of the admin console ('*' for all of them). Local users sign in with password_hash; AD users
        // with their domain password. admin_sessions are the console's sign-in cookies, stored hashed.
        """
            ALTER TABLE users ADD COLUMN password_hash TEXT;

            CREATE TABLE admin_grants (
                id          INTEGER PRIMARY KEY,
                user_id     INTEGER REFERENCES users(id) ON DELETE CASCADE,
                group_id    TEXT REFERENCES groups(id) ON DELETE CASCADE,
                area        TEXT NOT NULL,
                role        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                CHECK ((user_id IS NULL) <> (group_id IS NULL))
            );
            CREATE UNIQUE INDEX ix_admin_grants_user ON admin_grants(user_id, area) WHERE user_id IS NOT NULL;
            CREATE UNIQUE INDEX ix_admin_grants_group ON admin_grants(group_id, area) WHERE group_id IS NOT NULL;

            CREATE TABLE admin_sessions (
                id            INTEGER PRIMARY KEY,
                token_hash    TEXT NOT NULL UNIQUE,
                user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                ip            TEXT NOT NULL,
                user_agent    TEXT,
                created_at    TEXT NOT NULL,
                last_seen_at  TEXT NOT NULL
            );
            CREATE INDEX ix_admin_sessions_user ON admin_sessions(user_id);
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
