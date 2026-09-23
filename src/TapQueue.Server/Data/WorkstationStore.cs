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
    DateTimeOffset LastSeenAt)
{
    public bool Online => DateTimeOffset.UtcNow - LastSeenAt < TimeSpan.FromSeconds(WorkstationStatus.OfflineAfterSeconds);
}

/// <summary>
/// PCs running the TapQueue service, which checks in about once a minute whether or not anyone is
/// signed in. That's how the console knows which client each PC runs and whether an update failed,
/// and how a PC gets told to update now. PCs are known by their Windows computer name.
/// </summary>
public sealed class WorkstationStore(Database database)
{
    private const string Columns = "hostname, last_ip, version, binary_sha256, update_error, pending_command, first_seen_at, last_seen_at";

    public List<WorkstationRecord> List() =>
        database.Query($"SELECT {Columns} FROM workstations ORDER BY hostname", Map);

    public WorkstationRecord? Get(string hostname) =>
        database.QueryOne($"SELECT {Columns} FROM workstations WHERE hostname = $h", Map, ("$h", hostname));

    /// <summary>Records a check-in and hands back the pending command (clearing it, so it runs once).</summary>
    public string? CheckIn(string hostname, string ip, ClientSetupRequest report)
    {
        var now = DateTimeOffset.UtcNow;
        var command = database.Scalar("SELECT pending_command FROM workstations WHERE hostname = $h", ("$h", hostname)) as string;
        database.Execute("""
            INSERT INTO workstations (hostname, last_ip, version, binary_sha256, update_error, first_seen_at, last_seen_at)
            VALUES ($h, $ip, $v, $sha, $err, $now, $now)
            ON CONFLICT (hostname) DO UPDATE SET hostname = $h, last_ip = $ip, version = $v, binary_sha256 = $sha,
                update_error = $err, pending_command = NULL, last_seen_at = $now
            """, ("$h", hostname), ("$ip", ip), ("$v", report.Version), ("$sha", report.Sha256?.ToLowerInvariant()),
            ("$err", report.UpdateError), ("$now", now));
        return command;
    }

    /// <summary>Queues a command for the PC's next check-in, replacing any it hasn't picked up.</summary>
    public WorkstationRecord? SetCommand(string hostname, string? command) =>
        database.QueryOne($"UPDATE workstations SET pending_command = $c WHERE hostname = $h RETURNING {Columns}",
            Map, ("$c", command), ("$h", hostname));

    /// <summary>Forgets a PC, e.g. one that was retired. It comes back if its TapQueue service checks in again.</summary>
    public bool Delete(string hostname) =>
        database.Execute("DELETE FROM workstations WHERE hostname = $h", ("$h", hostname)) == 1;

    private static WorkstationRecord Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5),
        r.GetTime(6), r.GetTime(7));
}
