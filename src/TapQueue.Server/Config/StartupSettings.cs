using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TapQueue.Server.Data;

namespace TapQueue.Server.Config;

/// <summary>
/// The server.toml settings that are read once, when the server starts, and can be changed from the
/// console all the same: a value saved there is kept in the database and laid over server.toml's at
/// the next start. Only values that differ from the file's are kept, so a setting nobody changed
/// in the console still follows the file.
/// <para>
/// server.listen, server.data_dir and admin.token aren't among them: the database is in data_dir, and
/// tapqueue-admin and the install scripts find the server on its own machine by the other two.
/// </para>
/// </summary>
public sealed class StartupSettings(Database database, ServerConfig file, ServerConfig running)
{
    public const string DiscoveryPort = "discoveryPort";
    public const string TrustedProxies = "trustedProxies";
    public const string AuthMode = "authMode";
    public const string TlsListen = "tlsListen";
    public const string TlsCertFile = "tlsCertFile";
    public const string TlsKeyFile = "tlsKeyFile";
    public const string TlsNames = "tlsNames";
    public const string TlsRequire = "tlsRequire";
    public const string BackupCopyTo = "backupCopyTo";
    public const string BackupPassphrase = "backupPassphrase";

    /// <summary>What they're called in the API (and, after <see cref="Prefix"/>, in the settings table).</summary>
    public static readonly string[] Keys =
    [
        DiscoveryPort, TrustedProxies, AuthMode, TlsListen, TlsCertFile, TlsKeyFile, TlsNames, TlsRequire, BackupCopyTo, BackupPassphrase,
    ];

    private const string Prefix = "startup.";

    /// <summary>The flag that deletes them all, for when one keeps the server from starting.</summary>
    public const string ForgetFlag = "--forget-console-settings";

    /// <summary>server.toml as it is on disk.</summary>
    public ServerConfig File => file;

    /// <summary>What the server started with: server.toml with what was saved here then.</summary>
    public ServerConfig Running => running;

    /// <summary>What it would start with now.</summary>
    public ServerConfig Next() => Overlay(file, Saved());

    /// <summary>The settings whose saved value differs from server.toml's.</summary>
    public List<string> SetHere() => Keys.Where(Saved().ContainsKey).ToList();

    /// <summary>True when a restart would change something: a value saved since the server started.</summary>
    public bool RestartNeeded()
    {
        var next = Next();
        return Keys.Any(key => Text(next, key) != Text(running, key));
    }

    /// <summary>
    /// Keeps <paramref name="next"/>'s values for the next start: each one that differs from
    /// server.toml's is saved, and the rest go back to following the file.
    /// </summary>
    public void Save(ServerConfig next)
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        foreach (var key in Keys)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$k", Prefix + key);
            if (Text(next, key) == Text(file, key))
            {
                command.CommandText = "DELETE FROM settings WHERE key = $k";
            }
            else
            {
                command.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v";
                command.Parameters.AddWithValue("$v", Text(next, key));
            }
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private Dictionary<string, string> Saved() => Read(database.Open());

    /// <summary>
    /// <paramref name="file"/> with what the console saved in its data_dir laid over it: what the
    /// server starts with. The database isn't created or upgraded for this.
    /// </summary>
    public static ServerConfig Effective(ServerConfig file) => Overlay(file, Read(file.Server.DataDir));

    /// <summary>The saved settings in a data directory, by key; none if there's no database there yet.</summary>
    public static Dictionary<string, string> Read(string dataDir) =>
        OpenExisting(dataDir, SqliteOpenMode.ReadOnly) is { } db ? Read(db) : [];

    /// <summary>Deletes every saved setting, so the server starts with server.toml alone. Returns the ones it deleted.</summary>
    public static List<string> Forget(string dataDir)
    {
        if (OpenExisting(dataDir, SqliteOpenMode.ReadWrite) is not { } db)
            return [];
        var saved = Read(db, dispose: false).Keys.ToList();
        using (db)
        {
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key LIKE $p";
            command.Parameters.AddWithValue("$p", Prefix + "%");
            if (saved.Count > 0)
                command.ExecuteNonQuery();
        }
        return saved;
    }

    public static ServerConfig Overlay(ServerConfig file, IReadOnlyDictionary<string, string> saved)
    {
        var config = Copy(file);
        foreach (var key in Keys)
            if (saved.TryGetValue(key, out var text))
                Apply(config, key, text);
        return config;
    }

    public static ServerConfig Copy(ServerConfig config) =>
        JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(config))!;

    /// <summary>A setting's value as it's kept in the database: lists as JSON, the rest as plain text.</summary>
    public static string Text(ServerConfig config, string key) => key switch
    {
        DiscoveryPort => config.Server.DiscoveryPort.ToString(CultureInfo.InvariantCulture),
        TrustedProxies => JsonSerializer.Serialize(config.Server.TrustedProxies),
        AuthMode => config.Auth.Mode,
        TlsListen => config.Tls.Listen,
        TlsCertFile => config.Tls.CertFile,
        TlsKeyFile => config.Tls.KeyFile,
        TlsNames => JsonSerializer.Serialize(config.Tls.Names),
        TlsRequire => config.Tls.Require ? "true" : "false",
        BackupCopyTo => config.Backup.CopyTo,
        BackupPassphrase => config.Backup.Passphrase,
        _ => throw new ArgumentException($"Unknown setting \"{key}\".", nameof(key)),
    };

    /// <summary>Sets one from <see cref="Text"/>'s form. A value that can't be read (a hand-edited database) leaves server.toml's.</summary>
    private static void Apply(ServerConfig config, string key, string text)
    {
        switch (key)
        {
            case DiscoveryPort:
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    config.Server.DiscoveryPort = port;
                break;
            case TrustedProxies:
                if (List(text) is { } proxies)
                    config.Server.TrustedProxies = proxies;
                break;
            case AuthMode: config.Auth.Mode = text; break;
            case TlsListen: config.Tls.Listen = text; break;
            case TlsCertFile: config.Tls.CertFile = text; break;
            case TlsKeyFile: config.Tls.KeyFile = text; break;
            case TlsNames:
                if (List(text) is { } names)
                    config.Tls.Names = names;
                break;
            case TlsRequire: config.Tls.Require = text == "true"; break;
            case BackupCopyTo: config.Backup.CopyTo = text; break;
            case BackupPassphrase: config.Backup.Passphrase = text; break;
        }
    }

    private static List<string>? List(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SqliteConnection? OpenExisting(string dataDir, SqliteOpenMode mode)
    {
        var path = Path.Combine(dataDir, "tapqueue.db");
        if (!System.IO.File.Exists(path))
            return null;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode }.ToString());
        db.Open();
        return db;
    }

    private static Dictionary<string, string> Read(SqliteConnection db, bool dispose = true)
    {
        try
        {
            using var exists = db.CreateCommand();
            // A database from before the console had settings (schema 8) has no such table yet.
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'settings'";
            if (exists.ExecuteScalar() is null)
                return [];
            using var command = db.CreateCommand();
            command.CommandText = "SELECT key, value FROM settings WHERE key LIKE $p";
            command.Parameters.AddWithValue("$p", Prefix + "%");
            using var reader = command.ExecuteReader();
            var saved = new Dictionary<string, string>();
            while (reader.Read())
                saved[reader.GetString(0)[Prefix.Length..]] = reader.GetString(1);
            return saved;
        }
        finally
        {
            if (dispose)
                db.Dispose();
        }
    }
}
