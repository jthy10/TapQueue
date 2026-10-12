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

/// <summary>What became of a released job at the printer, for printers that give their jobs a number.</summary>
public static class PrintState
{
    /// <summary>At the printer and not finished; the server keeps the document and keeps asking.</summary>
    public const string Printing = "printing";
    /// <summary>The printer said it finished the job.</summary>
    public const string Printed = "printed";
    /// <summary>It was stopped at the printer part way (or the printer doesn't say how far it got).</summary>
    public const string Stopped = "stopped";
    /// <summary>The printer stopped answering for the job, or forgot it, before saying how it ended.</summary>
    public const string Unconfirmed = "unconfirmed";
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
    int? Pages,
    string? Workstation = null,
    int? PrinterJobId = null,
    int RefundedPages = 0,
    bool? Color = null,
    bool? Duplex = null,
    string? PrintState = null,
    int? PrintedPages = null)
{
    /// <summary>What the job counts against a quota: its pages times copies, and 1 page if it couldn't be counted.</summary>
    public int ChargedPages => (Pages ?? 1) * Copies;

    public JobDto ToDto() => new(
        Id, Name, QueueId, Status, SizeBytes, DocumentFormat, Copies, Username, OwnerHint,
        SubmittedAt, ExpiresAt, ReleasedAt, ReleasedPrinterId, Error, FormerOwner, Pages, RefundedPages, Color, Duplex,
        Status == JobStatus.Released ? PrintState : null, PrintedPages);
}

/// <param name="JobAttributes">The encoded IPP job-attributes group from the client (copies, media, page-ranges…).</param>
/// <param name="Workstation">The PC whose keyed printer it came through, or null for a keyless job.</param>
public sealed record NewJob(
    long? UserId,
    string? OwnerHint,
    string QueueId,
    string Name,
    string DocumentFormat,
    int Copies,
    string SourceIp,
    DateTimeOffset ExpiresAt,
    byte[]? JobAttributes,
    string? Workstation = null);

public sealed class JobStore(Database database)
{
    private const string SelectColumns = """
        SELECT j.id, j.user_id, u.username, j.owner_hint, j.queue_id, j.name, j.document_format, j.copies,
               j.size_bytes, j.status, j.source_ip, j.submitted_at, j.expires_at, j.released_at,
               j.released_printer_id, j.error, j.former_owner, j.pages, j.workstation, j.printer_job_id, j.refunded_pages,
               j.color, j.duplex, j.print_state, j.printed_pages
        FROM jobs j LEFT JOIN users u ON u.id = j.user_id
        """;

    public JobRecord Create(NewJob job)
    {
        var id = (long)database.Scalar("""
            INSERT INTO jobs (user_id, owner_hint, queue_id, name, document_format, copies, job_attributes, status, source_ip, submitted_at, expires_at, workstation)
            VALUES ($user, $hint, $queue, $name, $format, $copies, $attrs, $status, $ip, $now, $expires, $ws)
            RETURNING id
            """,
            ("$user", job.UserId), ("$hint", job.OwnerHint), ("$queue", job.QueueId), ("$name", job.Name),
            ("$format", job.DocumentFormat), ("$copies", job.Copies), ("$attrs", job.JobAttributes),
            ("$status", JobStatus.Receiving), ("$ip", job.SourceIp), ("$now", DateTimeOffset.UtcNow), ("$expires", job.ExpiresAt),
            ("$ws", job.Workstation))!;
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

    /// <summary>Jobs sent through <paramref name="workstation"/>'s keyed printers, or with null, keyless jobs from <paramref name="sourceIp"/>.</summary>
    public List<JobRecord> ListFrom(string? workstation, string sourceIp, int limit) => workstation is null
        ? database.Query(SelectColumns + " WHERE j.source_ip = $ip AND j.workstation IS NULL ORDER BY j.id DESC LIMIT $limit", Map, ("$ip", sourceIp), ("$limit", limit))
        : database.Query(SelectColumns + " WHERE j.workstation = $w ORDER BY j.id DESC LIMIT $limit", Map, ("$w", workstation), ("$limit", limit));

    public int CountHeld() =>
        Convert.ToInt32(database.Scalar("SELECT COUNT(*) FROM jobs WHERE status = $held", ("$held", JobStatus.Held)));

    /// <summary>The size of the documents a user has waiting: held, or on their way to a printer.</summary>
    public long HeldBytes(long userId) =>
        Convert.ToInt64(database.Scalar("SELECT COALESCE(SUM(size_bytes), 0) FROM jobs WHERE user_id = $u AND status IN ($held, $releasing)",
            ("$u", userId), ("$held", JobStatus.Held), ("$releasing", JobStatus.Releasing)));

    /// <summary>Moves a job between states only if it is currently in <paramref name="from"/>. Returns false if it wasn't.</summary>
    public bool TryTransition(long id, string from, string to, string? error = null) =>
        database.Execute("UPDATE jobs SET status = $to, error = $err WHERE id = $id AND status = $from",
            ("$to", to), ("$err", error), ("$id", id), ("$from", from)) == 1;

    /// <summary>
    /// Puts jobs that were being sent to a printer when the server stopped back on hold, so their owner
    /// can release them again (the printer may or may not have got them). Run once at startup, when no
    /// release can be under way. Returns how many.
    /// </summary>
    public int RecoverInterruptedReleases() =>
        database.Execute("UPDATE jobs SET status = $held, error = $err WHERE status = $releasing",
            ("$held", JobStatus.Held), ("$err", "The server restarted while this job was being sent to the printer."),
            ("$releasing", JobStatus.Releasing));

    /// <param name="pages">Pages per copy, or null if they couldn't be counted.</param>
    /// <param name="color">Whether it's to print in colour; null if the job didn't say.</param>
    /// <param name="duplex">Whether it's to print on both sides of the paper.</param>
    public void MarkReceived(long id, long sizeBytes, int? pages, bool? color = null, bool? duplex = null) =>
        database.Execute("UPDATE jobs SET status = $held, size_bytes = $size, pages = $pages, color = $color, duplex = $duplex WHERE id = $id AND status = $receiving",
            ("$held", JobStatus.Held), ("$size", sizeBytes), ("$pages", pages), ("$color", color), ("$duplex", duplex),
            ("$id", id), ("$receiving", JobStatus.Receiving));

    /// <summary>
    /// Deletes the history of jobs that finished (released, canceled or expired) before
    /// <paramref name="cutoff"/>: their names, owners and where they came from. Their documents went
    /// when they finished. Returns how many.
    /// </summary>
    public int DeleteFinishedBefore(DateTimeOffset cutoff) =>
        database.Execute("""
            DELETE FROM jobs WHERE status IN ($released, $canceled, $expired) AND COALESCE(released_at, expires_at) < $cutoff
            """, ("$released", JobStatus.Released), ("$canceled", JobStatus.Canceled), ("$expired", JobStatus.Expired), ("$cutoff", cutoff));

    public byte[]? GetJobAttributes(long id) =>
        database.Scalar("SELECT job_attributes FROM jobs WHERE id = $id", ("$id", id)) as byte[];

    public void SetDocumentFormat(long id, string format) =>
        database.Execute("UPDATE jobs SET document_format = $f WHERE id = $id", ("$f", format), ("$id", id));

    /// <param name="printerJobId">
    /// The printer's number for the job, if it gave one. With one the job is followed at the printer
    /// (<see cref="PrintState.Printing"/>) until the printer says how it ended.
    /// </param>
    public void MarkReleased(long id, string printerId, int? printerJobId = null) =>
        database.Execute("""
            UPDATE jobs SET status = $released, released_at = $now, released_printer_id = $p, printer_job_id = $pj, print_state = $ps, error = NULL
            WHERE id = $id
            """, ("$released", JobStatus.Released), ("$now", DateTimeOffset.UtcNow), ("$p", printerId), ("$pj", printerJobId),
            ("$ps", printerJobId is null ? null : PrintState.Printing), ("$id", id));

    /// <summary>Released jobs the printer hasn't finished as far as the server knows, oldest first.</summary>
    public List<JobRecord> ListPrinting() =>
        database.Query(SelectColumns + " WHERE j.print_state = $printing AND j.status = $released ORDER BY j.id", Map,
            ("$printing", PrintState.Printing), ("$released", JobStatus.Released));

    /// <summary>Records how a job that was printing ended. False if it wasn't being followed any more.</summary>
    /// <param name="printedPages">What the printer said it printed, if it said.</param>
    public bool FinishPrinting(long id, string state, int? printedPages) =>
        database.Execute("UPDATE jobs SET print_state = $s, printed_pages = $n WHERE id = $id AND print_state = $printing",
            ("$s", state), ("$n", printedPages), ("$id", id), ("$printing", PrintState.Printing)) == 1;

    /// <summary>
    /// The printer gave up on a job it had accepted: it's held again as if it had never been released,
    /// so it counts against no page limit and its owner can release it somewhere else.
    /// </summary>
    public bool ReturnToHold(long id, string error, DateTimeOffset expiresAt) =>
        database.Execute("""
            UPDATE jobs SET status = $held, error = $err, expires_at = $expires, released_at = NULL, released_printer_id = NULL,
                            printer_job_id = NULL, print_state = NULL, printed_pages = NULL, refunded_pages = 0
            WHERE id = $id AND status = $released AND print_state = $printing
            """, ("$held", JobStatus.Held), ("$err", error), ("$expires", expiresAt), ("$id", id),
            ("$released", JobStatus.Released), ("$printing", PrintState.Printing)) == 1;

    /// <summary>
    /// A released job was stopped at the printer part way: <paramref name="pages"/> of what it was
    /// charged never printed and no longer count. Only once per job; false if it already has a refund.
    /// </summary>
    public bool Refund(long id, int pages) =>
        database.Execute("UPDATE jobs SET refunded_pages = $n WHERE id = $id AND status = $released AND refunded_pages = 0",
            ("$n", pages), ("$id", id), ("$released", JobStatus.Released)) == 1;

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
        Pages: r.IsDBNull(17) ? null : r.GetInt32(17),
        Workstation: r.GetStringOrNull(18),
        PrinterJobId: r.IsDBNull(19) ? null : r.GetInt32(19),
        RefundedPages: r.GetInt32(20),
        Color: r.IsDBNull(21) ? null : r.GetBoolean(21),
        Duplex: r.IsDBNull(22) ? null : r.GetBoolean(22),
        PrintState: r.GetStringOrNull(23),
        PrintedPages: r.IsDBNull(24) ? null : r.GetInt32(24));
}
