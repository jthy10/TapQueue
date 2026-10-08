using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record WorkstationRecord(
    string Hostname,
    string LastIp,
    string? Version,
    string? BinarySha256,
    string? UpdateError,
    string? PendingCommand,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string Platform,
    string? KeyHash = null)
{
    public bool Online => DateTimeOffset.UtcNow - LastSeenAt < TimeSpan.FromSeconds(WorkstationStatus.OfflineAfterSeconds);
}

/// <summary>
/// PCs running the TapQueue service, which checks in about once a minute whether or not anyone is
/// signed in. That's how the console knows which client each PC runs and whether an update failed,
/// and how a PC gets told to update now. PCs are known by their computer name (hostname on Linux).
/// </summary>
public sealed class WorkstationStore(Database database)
{
    private const string Columns = "hostname, last_ip, version, binary_sha256, update_error, pending_command, first_seen_at, last_seen_at, platform, key_hash";

    public List<WorkstationRecord> List() =>
        database.Query($"SELECT {Columns} FROM workstations ORDER BY hostname", Map);

    public WorkstationRecord? Get(string hostname) =>
        database.QueryOne($"SELECT {Columns} FROM workstations WHERE hostname = $h", Map, ("$h", hostname));

    /// <summary>Records a check-in and hands back the pending command (clearing it, so it runs once).</summary>
    /// <param name="platform">The report's platform, already checked.</param>
    public string? CheckIn(string hostname, string ip, string platform, ClientSetupRequest report)
    {
        var now = DateTimeOffset.UtcNow;
        var command = database.Scalar("SELECT pending_command FROM workstations WHERE hostname = $h", ("$h", hostname)) as string;
        database.Execute("""
            INSERT INTO workstations (hostname, last_ip, version, binary_sha256, update_error, first_seen_at, last_seen_at, platform)
            VALUES ($h, $ip, $v, $sha, $err, $now, $now, $p)
            ON CONFLICT (hostname) DO UPDATE SET hostname = $h, last_ip = $ip, version = $v, binary_sha256 = $sha,
                update_error = $err, pending_command = NULL, last_seen_at = $now, platform = $p
            """, ("$h", hostname), ("$ip", ip), ("$v", report.Version), ("$sha", report.Sha256?.ToLowerInvariant()),
            ("$err", report.UpdateError), ("$now", now), ("$p", platform));
        return command;
    }

    /// <summary>The PC whose print key (<see cref="Tokens.PrintKey"/>) hashes to <paramref name="printKeyHash"/>, if any.</summary>
    public WorkstationRecord? FindByPrintKey(string printKeyHash) =>
        database.QueryOne($"SELECT {Columns} FROM workstations WHERE print_key_hash = $k", Map, ("$k", printKeyHash));

    /// <summary>
    /// Gives a PC that has no key yet <paramref name="key"/> (stored hashed, with its print key). False
    /// if it has one already (another check-in got there first), or isn't known.
    /// </summary>
    public bool SetKey(string hostname, string key) =>
        database.Execute("UPDATE workstations SET key_hash = $k, print_key_hash = $p WHERE hostname = $h AND key_hash IS NULL",
            ("$k", Tokens.Hash(key)), ("$p", Tokens.Hash(Tokens.PrintKey(key))), ("$h", hostname)) == 1;

    /// <summary>Queues a command for the PC's next check-in, replacing any it hasn't picked up.</summary>
    public WorkstationRecord? SetCommand(string hostname, string? command) =>
        database.QueryOne($"UPDATE workstations SET pending_command = $c WHERE hostname = $h RETURNING {Columns}",
            Map, ("$c", command), ("$h", hostname));

    /// <summary>
    /// Forgets a PC, e.g. one that was retired, along with its key and which sessions it vouched for.
    /// It comes back, with a new key, if its TapQueue service checks in again.
    /// </summary>
    public bool Delete(string hostname)
    {
        database.Execute("UPDATE sessions SET workstation = NULL, pc_user = NULL WHERE workstation = $h", ("$h", hostname));
        return database.Execute("DELETE FROM workstations WHERE hostname = $h", ("$h", hostname)) == 1;
    }

    private static WorkstationRecord Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5),
        r.GetTime(6), r.GetTime(7), r.GetString(8), r.GetStringOrNull(9));
}
