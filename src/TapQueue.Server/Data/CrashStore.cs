using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <summary>
/// Crash reports from client programs, for an admin to look at (console: Workstations; CLI:
/// `tapqueue-admin crashes`). Only the newest <see cref="Keep"/> are kept, so a PC stuck crashing
/// at every start can't fill the disk.
/// </summary>
public sealed class CrashStore(Database database)
{
    public const int Keep = 500;
    public const int MaxDetailsLength = 32_000;

    private const string Columns = "id, computer, ip, program, platform, version, occurred_at, received_at, message, details";

    public CrashReportDto Add(CrashReportRequest report, string platform, string ip)
    {
        var crash = database.QueryOne($"""
            INSERT INTO crash_reports (computer, ip, program, platform, version, occurred_at, received_at, message, details)
            VALUES ($computer, $ip, $program, $platform, $version, $occurred, $now, $message, $details)
            RETURNING {Columns}
            """, Map,
            ("$computer", Cut(report.Computer.Trim(), 255)), ("$ip", ip), ("$program", Cut(report.Program.Trim(), 32)),
            ("$platform", platform), ("$version", report.Version is null ? null : Cut(report.Version.Trim(), 64)),
            ("$occurred", report.OccurredAt), ("$now", DateTimeOffset.UtcNow),
            ("$message", Cut(report.Message.Trim(), 1000)), ("$details", report.Details is null ? null : Cut(report.Details, MaxDetailsLength)))!;
        database.Execute($"DELETE FROM crash_reports WHERE id <= (SELECT id FROM crash_reports ORDER BY id DESC LIMIT 1 OFFSET {Keep})");
        return crash;
    }

    /// <summary>Newest first, optionally only one PC's.</summary>
    public List<CrashReportDto> List(string? computer, int limit) => computer is null
        ? database.Query($"SELECT {Columns} FROM crash_reports ORDER BY id DESC LIMIT $limit", Map, ("$limit", limit))
        : database.Query($"SELECT {Columns} FROM crash_reports WHERE computer = $c ORDER BY id DESC LIMIT $limit", Map,
            ("$c", computer), ("$limit", limit));

    public bool Delete(long id) => database.Execute("DELETE FROM crash_reports WHERE id = $id", ("$id", id)) == 1;

    public int Clear(string? computer) => computer is null
        ? database.Execute("DELETE FROM crash_reports")
        : database.Execute("DELETE FROM crash_reports WHERE computer = $c", ("$c", computer));

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static CrashReportDto Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetStringOrNull(5),
        r.GetTime(6), r.GetTime(7), r.GetString(8), r.GetStringOrNull(9));
}
