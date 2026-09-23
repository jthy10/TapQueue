using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public sealed record StationRecord(string Id, string PrinterId, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? LastIp)
{
    public StationDto ToDto() => new(Id, PrinterId, CreatedAt, LastSeenAt, LastIp);
}

/// <summary>
/// A release station is a badge reader next to a printer. Each one has its own token and
/// releases jobs to the printer it's assigned to here, so a station can't pick another printer.
/// </summary>
public sealed class StationStore(Database database)
{
    private const string Columns = "id, printer_id, created_at, last_seen_at, last_ip";

    public StationRecord? Get(string id) =>
        database.QueryOne($"SELECT {Columns} FROM stations WHERE id = $id", Map, ("$id", id));

    public List<StationRecord> List() =>
        database.Query($"SELECT {Columns} FROM stations ORDER BY id", Map);

    public StationRecord Create(string id, string printerId, string tokenHash) =>
        database.QueryOne($"""
            INSERT INTO stations (id, printer_id, token_hash, created_at)
            VALUES ($id, $p, $t, $now)
            RETURNING {Columns}
            """, Map, ("$id", id), ("$p", printerId), ("$t", tokenHash), ("$now", DateTimeOffset.UtcNow))!;

    public StationRecord? SetPrinter(string id, string printerId) =>
        database.QueryOne($"UPDATE stations SET printer_id = $p WHERE id = $id RETURNING {Columns}", Map, ("$p", printerId), ("$id", id));

    public void SetTokenHash(string id, string tokenHash) =>
        database.Execute("UPDATE stations SET token_hash = $t WHERE id = $id", ("$t", tokenHash), ("$id", id));

    public bool Delete(string id) =>
        database.Execute("DELETE FROM stations WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Looks up a station by token and marks it as seen from <paramref name="remoteIp"/>.</summary>
    public StationRecord? Touch(string tokenHash, string remoteIp) =>
        database.QueryOne($"""
            UPDATE stations SET last_seen_at = $now, last_ip = $ip WHERE token_hash = $t
            RETURNING {Columns}
            """, Map, ("$now", DateTimeOffset.UtcNow), ("$ip", remoteIp), ("$t", tokenHash));

    private static StationRecord Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetTime(2), r.GetTimeOrNull(3), r.GetStringOrNull(4));
}
