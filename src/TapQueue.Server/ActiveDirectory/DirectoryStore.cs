using System.Text.Json;
using Microsoft.Data.Sqlite;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// How to reach AD and what to do with it. Kept in the database (settings key "directory") so it's
/// set from the console; the password never leaves the server.
/// </summary>
/// <param name="Host">A domain controller, or the domain's name. Its certificate must be issued for this name.</param>
/// <param name="BindDn">The read-only account to bind as: a DN, or user@domain.</param>
/// <param name="CaCertificate">PEM of the CA that issued the DCs' certificates; empty to use the OS's trusted CAs.</param>
/// <param name="BadgeAttribute">The user attribute holding their card number (like employeeNumber); empty to leave cards to TapQueue.</param>
/// <param name="SyncTime">When the daily sync runs, HH:mm in the server's time zone.</param>
/// <param name="MaxDisablePercent">A sync that would disable more than this share of AD users stops instead.</param>
public sealed record DirectoryConfig(
    bool Enabled = false,
    string Host = "",
    int Port = 636,
    string BindDn = "",
    string Password = "",
    string CaCertificate = "",
    string BadgeAttribute = "",
    string SyncTime = "01:00",
    int MaxDisablePercent = 20)
{
    public DirectoryConfigDto ToDto() =>
        new(Enabled, Host, Port, BindDn, Password.Length > 0, CaCertificate, BadgeAttribute, SyncTime, MaxDisablePercent);

    public static bool IsValidTime(string? time) =>
        TimeOnly.TryParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    public TimeOnly DailyAt => TimeOnly.ParseExact(SyncTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <param name="Kind">One of <see cref="DirectoryScopeKind"/>.</param>
/// <param name="Guid">The object's objectGUID, so the scope survives renames and moves in AD.</param>
/// <param name="Dn">Where it was when added (or last seen), for showing.</param>
public sealed record DirectoryScopeItem(long Id, string Kind, string Guid, string Dn, string Name, DateTimeOffset AddedAt)
{
    public DirectoryScopeDto ToDto() => new(Id, Kind, Guid, Dn, Name, AddedAt);
}

/// <summary>The directory settings, the sync's scope, and the history of syncs.</summary>
public sealed class DirectoryStore(Database database)
{
    private const string ConfigKey = "directory";
    private const string RunColumns = "id, started_at, finished_at, trigger, dry_run, outcome, summary, error";

    public DirectoryConfig Config() =>
        database.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", ConfigKey)) is string json
            ? JsonSerializer.Deserialize<DirectoryConfig>(json, TapQueueJson.Options) ?? new DirectoryConfig()
            : new DirectoryConfig();

    public void SaveConfig(DirectoryConfig config) =>
        database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
            ("$k", ConfigKey), ("$v", JsonSerializer.Serialize(config, TapQueueJson.Options)));

    public List<DirectoryScopeItem> Scope() =>
        database.Query("SELECT id, kind, guid, dn, name, added_at FROM directory_scope ORDER BY kind, name COLLATE NOCASE", MapScope);

    public DirectoryScopeItem? AddScope(string kind, string guid, string dn, string name) =>
        database.QueryOne("""
            INSERT INTO directory_scope (kind, guid, dn, name, added_at) VALUES ($kind, $guid, $dn, $name, $now)
            ON CONFLICT (guid) DO NOTHING
            RETURNING id, kind, guid, dn, name, added_at
            """, MapScope, ("$kind", kind), ("$guid", guid), ("$dn", dn), ("$name", name), ("$now", DateTimeOffset.UtcNow));

    public DirectoryScopeItem? GetScope(long id) =>
        database.QueryOne("SELECT id, kind, guid, dn, name, added_at FROM directory_scope WHERE id = $id", MapScope, ("$id", id));

    public bool RemoveScope(long id) => database.Execute("DELETE FROM directory_scope WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Keeps the shown name and place up to date when an OU, group or user moves or is renamed in AD.</summary>
    public void UpdateScope(long id, string dn, string name) =>
        database.Execute("UPDATE directory_scope SET dn = $dn, name = $name WHERE id = $id", ("$dn", dn), ("$name", name), ("$id", id));

    public long StartRun(string trigger, bool dryRun) =>
        (long)database.Scalar("""
            INSERT INTO directory_runs (started_at, trigger, dry_run, outcome) VALUES ($now, $t, $d, $o) RETURNING id
            """, ("$now", DateTimeOffset.UtcNow), ("$t", trigger), ("$d", dryRun), ("$o", DirectoryRunOutcome.Running))!;

    public void FinishRun(long id, string outcome, string summary, string? error) =>
        database.Execute("UPDATE directory_runs SET finished_at = $now, outcome = $o, summary = $s, error = $e WHERE id = $id",
            ("$now", DateTimeOffset.UtcNow), ("$o", outcome), ("$s", summary), ("$e", error), ("$id", id));

    public List<DirectoryRunDto> Runs(int limit = 20) =>
        database.Query($"SELECT {RunColumns} FROM directory_runs ORDER BY id DESC LIMIT $limit", MapRun, ("$limit", limit));

    /// <summary>The last sync that changed things (not a preview) and finished, for catching up after downtime.</summary>
    public DirectoryRunDto? LastCompletedSync() =>
        database.QueryOne($"SELECT {RunColumns} FROM directory_runs WHERE NOT dry_run AND outcome = $o ORDER BY id DESC LIMIT 1", MapRun,
            ("$o", DirectoryRunOutcome.Succeeded));

    /// <summary>Runs left "running" by a server that stopped mid-sync.</summary>
    public void AbandonUnfinishedRuns() =>
        database.Execute("UPDATE directory_runs SET outcome = $o, error = 'The server stopped during the sync.' WHERE outcome = $running",
            ("$o", DirectoryRunOutcome.Failed), ("$running", DirectoryRunOutcome.Running));

    public int DeleteRunsOlderThan(DateTimeOffset cutoff) =>
        database.Execute("DELETE FROM directory_runs WHERE started_at < $cutoff", ("$cutoff", cutoff));

    private static DirectoryScopeItem MapScope(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetTime(5));

    private static DirectoryRunDto MapRun(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetTime(1), r.GetTimeOrNull(2), r.GetString(3), r.GetBoolean(4), r.GetString(5), r.GetString(6), r.GetStringOrNull(7));
}
