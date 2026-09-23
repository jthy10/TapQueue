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
    private const string SelectColumns = "SELECT id, printer_id, created_at, last_seen_at, last_ip FROM stations";

    public StationRecord? Get(string id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public List<StationRecord> List()
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " ORDER BY id";
        return ReadAll(cmd);
    }

    public StationRecord Create(string id, string printerId, string tokenHash)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO stations (id, printer_id, token_hash, created_at)
            VALUES ($id, $p, $t, $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", printerId);
        cmd.Parameters.AddWithValue("$t", tokenHash);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public void SetTokenHash(string id, string tokenHash)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE stations SET token_hash = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", tokenHash);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(string id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM stations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Looks up a station by token and marks it as seen from <paramref name="remoteIp"/>.</summary>
    public StationRecord? Touch(string tokenHash, string remoteIp)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE stations SET last_seen_at = $now, last_ip = $ip WHERE token_hash = $t
            RETURNING id, printer_id, created_at, last_seen_at, last_ip
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$ip", remoteIp);
        cmd.Parameters.AddWithValue("$t", tokenHash);
        return ReadAll(cmd).FirstOrDefault();
    }

    private static List<StationRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var stations = new List<StationRecord>();
        while (r.Read())
        {
            stations.Add(new StationRecord(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2)),
                r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4)));
        }
        return stations;
    }
}
