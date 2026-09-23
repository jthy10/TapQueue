using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

public static class JobStatus
{
    /// <summary>Created over IPP but the document hasn't fully arrived yet.</summary>
    public const string Receiving = "receiving";
    /// <summary>Waiting for its owner to release it at a printer.</summary>
    public const string Held = "held";
    public const string Releasing = "releasing";
    public const string Released = "released";
    public const string Canceled = "canceled";
    public const string Expired = "expired";
}

public sealed record JobRecord(
    long Id,
    long? UserId,
    string? Username,
    string? OwnerHint,
    string QueueId,
    string Name,
    string DocumentFormat,
    int Copies,
    long SizeBytes,
    string Status,
    string SourceIp,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedPrinterId,
    string? Error,
    string? FormerOwner,
    int? Pages)
{
    /// <summary>What the job counts against a quota: its pages times copies, and 1 page if it couldn't be counted.</summary>
    public int ChargedPages => (Pages ?? 1) * Copies;

    public JobDto ToDto() => new(
        Id, Name, QueueId, Status, SizeBytes, DocumentFormat, Copies, Username, OwnerHint,
        SubmittedAt, ExpiresAt, ReleasedAt, ReleasedPrinterId, Error, FormerOwner, Pages);
}

/// <param name="JobAttributes">The encoded IPP job-attributes group from the client (copies, media, page-ranges…).</param>
public sealed record NewJob(
    long? UserId,
    string? OwnerHint,
    string QueueId,
    string Name,
    string DocumentFormat,
    int Copies,
    string SourceIp,
    DateTimeOffset ExpiresAt,
    byte[]? JobAttributes);

public sealed class JobStore(Database database)
{
    private const string SelectColumns = """
        SELECT j.id, j.user_id, u.username, j.owner_hint, j.queue_id, j.name, j.document_format, j.copies,
               j.size_bytes, j.status, j.source_ip, j.submitted_at, j.expires_at, j.released_at,
               j.released_printer_id, j.error, j.former_owner, j.pages
        FROM jobs j LEFT JOIN users u ON u.id = j.user_id
        """;

    public JobRecord Create(NewJob job)
    {
        var id = (long)database.Scalar("""
            INSERT INTO jobs (user_id, owner_hint, queue_id, name, document_format, copies, job_attributes, status, source_ip, submitted_at, expires_at)
            VALUES ($user, $hint, $queue, $name, $format, $copies, $attrs, $status, $ip, $now, $expires)
            RETURNING id
            """,
            ("$user", job.UserId), ("$hint", job.OwnerHint), ("$queue", job.QueueId), ("$name", job.Name),
            ("$format", job.DocumentFormat), ("$copies", job.Copies), ("$attrs", job.JobAttributes),
            ("$status", JobStatus.Receiving), ("$ip", job.SourceIp), ("$now", DateTimeOffset.UtcNow), ("$expires", job.ExpiresAt))!;
        return Get(id)!;
    }

    public JobRecord? Get(long id) =>
        database.QueryOne(SelectColumns + " WHERE j.id = $id", Map, ("$id", id));

    public List<JobRecord> ListForUser(long userId, bool heldOnly) =>
        database.Query(SelectColumns + " WHERE j.user_id = $u" + (heldOnly ? " AND j.status = $held" : "") + " ORDER BY j.id DESC LIMIT 200",
            Map, ("$u", userId), ("$held", JobStatus.Held));

    public List<JobRecord> List(string? status, int limit = 200) =>
        database.Query(SelectColumns + (status is null ? "" : " WHERE j.status = $s") + " ORDER BY j.id DESC LIMIT $limit",
            Map, ("$s", status), ("$limit", limit));

    public List<JobRecord> ListFromIp(string sourceIp, int limit) =>
        database.Query(SelectColumns + " WHERE j.source_ip = $ip ORDER BY j.id DESC LIMIT $limit", Map, ("$ip", sourceIp), ("$limit", limit));

    public int CountHeld() =>
        Convert.ToInt32(database.Scalar("SELECT COUNT(*) FROM jobs WHERE status = $held", ("$held", JobStatus.Held)));

    /// <summary>Moves a job between states only if it is currently in <paramref name="from"/>. Returns false if it wasn't.</summary>
    public bool TryTransition(long id, string from, string to, string? error = null) =>
        database.Execute("UPDATE jobs SET status = $to, error = $err WHERE id = $id AND status = $from",
            ("$to", to), ("$err", error), ("$id", id), ("$from", from)) == 1;

    /// <param name="pages">Pages per copy, or null if they couldn't be counted.</param>
    public void MarkReceived(long id, long sizeBytes, int? pages) =>
        database.Execute("UPDATE jobs SET status = $held, size_bytes = $size, pages = $pages WHERE id = $id AND status = $receiving",
            ("$held", JobStatus.Held), ("$size", sizeBytes), ("$pages", pages), ("$id", id), ("$receiving", JobStatus.Receiving));

    public byte[]? GetJobAttributes(long id) =>
        database.Scalar("SELECT job_attributes FROM jobs WHERE id = $id", ("$id", id)) as byte[];

    public void SetDocumentFormat(long id, string format) =>
        database.Execute("UPDATE jobs SET document_format = $f WHERE id = $id", ("$f", format), ("$id", id));

    public void MarkReleased(long id, string printerId) =>
        database.Execute("""
            UPDATE jobs SET status = $released, released_at = $now, released_printer_id = $p, error = NULL
            WHERE id = $id
            """, ("$released", JobStatus.Released), ("$now", DateTimeOffset.UtcNow), ("$p", printerId), ("$id", id));

    /// <summary>Jobs whose spool files should be deleted: held past their expiry, or stuck receiving.</summary>
    public List<JobRecord> ListStale(DateTimeOffset now, TimeSpan receivingTimeout) =>
        database.Query(SelectColumns + """
             WHERE (j.status = $held AND j.expires_at < $now)
                OR (j.status = $receiving AND j.submitted_at < $receivingCutoff)
            """, Map,
            ("$held", JobStatus.Held), ("$receiving", JobStatus.Receiving), ("$now", now), ("$receivingCutoff", now - receivingTimeout));

    private static JobRecord Map(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        UserId: r.GetInt64OrNull(1),
        Username: r.GetStringOrNull(2),
        OwnerHint: r.GetStringOrNull(3),
        QueueId: r.GetString(4),
        Name: r.GetString(5),
        DocumentFormat: r.GetString(6),
        Copies: r.GetInt32(7),
        SizeBytes: r.GetInt64(8),
        Status: r.GetString(9),
        SourceIp: r.GetString(10),
        SubmittedAt: r.GetTime(11),
        ExpiresAt: r.GetTime(12),
        ReleasedAt: r.GetTimeOrNull(13),
        ReleasedPrinterId: r.GetStringOrNull(14),
        Error: r.GetStringOrNull(15),
        FormerOwner: r.GetStringOrNull(16),
        Pages: r.IsDBNull(17) ? null : r.GetInt32(17));
}
