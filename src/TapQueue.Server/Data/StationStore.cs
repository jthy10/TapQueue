using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <summary>Settings the server holds for a station; null ones come from its station.toml.</summary>
public sealed record StationSettings(
    int Version,
    string PrinterId,
    string? Reader,
    string? Device,
    int? RepeatSeconds,
    int? MinCardLength,
    string Feedback,
    bool Enabled,
    string MaintenanceMessage)
{
    /// <summary>The only kind so far. Reserved for a screen or status light at the station later.</summary>
    public const string NoFeedback = "none";

    public StationSettingsDto ToDto() => new(Version, PrinterId, Reader, Device, RepeatSeconds, MinCardLength, Feedback, Enabled, MaintenanceMessage);
}

public sealed record StationRecord(
    string Id,
    string PrinterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string? LastIp,
    string Name,
    string Location,
    StationSettings Settings,
    string? PendingCommand,
    string? Version,
    string? BinarySha256,
    DateTimeOffset? StartedAt,
    string? ReaderStatus,
    DateTimeOffset? LastHeartbeatAt)
{
    public bool Online => LastHeartbeatAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(StationStatus.OfflineAfterSeconds);

    /// <summary>What to call it: its name if it has one, otherwise its id.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Id;

    public StationDto ToDto() => new(Id, PrinterId, CreatedAt, LastSeenAt, LastIp, Name, Location, Online, Version, StartedAt,
        ReaderStatus, LastHeartbeatAt, Settings.ToDto(), PendingCommand);
}

/// <summary>
/// A release station is a badge reader next to a printer. Each one has its own token and
/// releases jobs to the printer it's assigned to here, so a station can't pick another printer.
/// It sends a heartbeat every few seconds, which is how it picks up settings and commands.
/// </summary>
public sealed class StationStore(Database database)
{
    private const string Columns = """
        id, printer_id, created_at, last_seen_at, last_ip, name, location,
        settings_version, reader, device, repeat_seconds, min_card_length, COALESCE(feedback, 'none'), enabled, maintenance_message,
        pending_command, version, binary_sha256, started_at, reader_status, last_heartbeat_at
        """;

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
        database.QueryOne($"UPDATE stations SET printer_id = $p, settings_version = settings_version + 1 WHERE id = $id RETURNING {Columns}",
            Map, ("$p", printerId), ("$id", id));

    public StationRecord? SetDetails(string id, string name, string location) =>
        database.QueryOne($"UPDATE stations SET name = $n, location = $l WHERE id = $id RETURNING {Columns}",
            Map, ("$n", name), ("$l", location), ("$id", id));

    /// <summary>Saves settings (printer, reader, enabled…) and bumps their version so the station re-applies them.</summary>
    public StationRecord? SetSettings(string id, StationSettings s) =>
        database.QueryOne($"""
            UPDATE stations SET printer_id = $p, reader = $reader, device = $device, repeat_seconds = $repeat,
                min_card_length = $min, feedback = $feedback, enabled = $enabled, maintenance_message = $msg,
                settings_version = settings_version + 1
            WHERE id = $id RETURNING {Columns}
            """, Map, ("$p", s.PrinterId), ("$reader", s.Reader), ("$device", s.Device), ("$repeat", s.RepeatSeconds),
            ("$min", s.MinCardLength), ("$feedback", s.Feedback), ("$enabled", s.Enabled), ("$msg", s.MaintenanceMessage), ("$id", id));

    public void SetTokenHash(string id, string tokenHash) =>
        database.Execute("UPDATE stations SET token_hash = $t WHERE id = $id", ("$t", tokenHash), ("$id", id));

    /// <summary>Queues a command for the station's next heartbeat, replacing any it hasn't picked up.</summary>
    public void SetCommand(string id, string? command) =>
        database.Execute("UPDATE stations SET pending_command = $c WHERE id = $id", ("$c", command), ("$id", id));

    public bool Delete(string id) =>
        database.Execute("DELETE FROM stations WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Looks up a station by token and marks it as seen from <paramref name="remoteIp"/>.</summary>
    public StationRecord? Touch(string tokenHash, string remoteIp) =>
        database.QueryOne($"""
            UPDATE stations SET last_seen_at = $now, last_ip = $ip WHERE token_hash = $t
            RETURNING {Columns}
            """, Map, ("$now", DateTimeOffset.UtcNow), ("$ip", remoteIp), ("$t", tokenHash));

    /// <summary>Records a heartbeat and hands back the pending command (clearing it, so it runs once).</summary>
    public string? Heartbeat(string id, StationHeartbeatRequest beat)
    {
        var command = database.Scalar("SELECT pending_command FROM stations WHERE id = $id", ("$id", id)) as string;
        database.Execute("""
            UPDATE stations SET version = $v, started_at = $started, reader_status = $status, binary_sha256 = $sha,
                last_heartbeat_at = $now, pending_command = NULL
            WHERE id = $id
            """, ("$v", beat.Version), ("$started", beat.StartedAt), ("$status", beat.ReaderStatus), ("$sha", beat.Sha256?.ToLowerInvariant()),
            ("$now", DateTimeOffset.UtcNow), ("$id", id));
        return command;
    }

    private static StationRecord Map(SqliteDataReader r) => new(
        Id: r.GetString(0),
        PrinterId: r.GetString(1),
        CreatedAt: r.GetTime(2),
        LastSeenAt: r.GetTimeOrNull(3),
        LastIp: r.GetStringOrNull(4),
        Name: r.GetString(5),
        Location: r.GetString(6),
        Settings: new StationSettings(
            Version: r.GetInt32(7),
            PrinterId: r.GetString(1),
            Reader: r.GetStringOrNull(8),
            Device: r.GetStringOrNull(9),
            RepeatSeconds: r.IsDBNull(10) ? null : r.GetInt32(10),
            MinCardLength: r.IsDBNull(11) ? null : r.GetInt32(11),
            Feedback: r.GetString(12),
            Enabled: r.GetBoolean(13),
            MaintenanceMessage: r.GetString(14)),
        PendingCommand: r.GetStringOrNull(15),
        Version: r.GetStringOrNull(16),
        BinarySha256: r.GetStringOrNull(17),
        StartedAt: r.GetTimeOrNull(18),
        ReaderStatus: r.GetStringOrNull(19),
        LastHeartbeatAt: r.GetTimeOrNull(20));
}
