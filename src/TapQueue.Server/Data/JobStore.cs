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
    string? Error)
{
    public JobDto ToDto() => new(
        Id, Name, QueueId, Status, SizeBytes, DocumentFormat, Copies, Username, OwnerHint,
        SubmittedAt, ExpiresAt, ReleasedAt, ReleasedPrinterId, Error);
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
               j.released_printer_id, j.error
        FROM jobs j LEFT JOIN users u ON u.id = j.user_id
        """;

    public JobRecord Create(NewJob job)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (user_id, owner_hint, queue_id, name, document_format, copies, job_attributes, status, source_ip, submitted_at, expires_at)
            VALUES ($user, $hint, $queue, $name, $format, $copies, $attrs, $status, $ip, $now, $expires)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$user", (object?)job.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hint", (object?)job.OwnerHint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$queue", job.QueueId);
        cmd.Parameters.AddWithValue("$name", job.Name);
        cmd.Parameters.AddWithValue("$format", job.DocumentFormat);
        cmd.Parameters.AddWithValue("$copies", job.Copies);
        cmd.Parameters.AddWithValue("$attrs", (object?)job.JobAttributes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", JobStatus.Receiving);
        cmd.Parameters.AddWithValue("$ip", job.SourceIp);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$expires", job.ExpiresAt.ToString("O"));
        var id = (long)cmd.ExecuteScalar()!;
        return Get(id)!;
    }

    public JobRecord? Get(long id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE j.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public List<JobRecord> ListForUser(long userId, bool heldOnly)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE j.user_id = $u" +
            (heldOnly ? " AND j.status = 'held'" : "") + " ORDER BY j.id DESC LIMIT 200";
        cmd.Parameters.AddWithValue("$u", userId);
        return ReadAll(cmd);
    }

    public List<JobRecord> List(string? status, int limit = 200)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + (status is null ? "" : " WHERE j.status = $s") + " ORDER BY j.id DESC LIMIT $limit";
        if (status is not null)
            cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public List<JobRecord> ListFromIp(string sourceIp, int limit)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE j.source_ip = $ip ORDER BY j.id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$ip", sourceIp);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public int CountHeld()
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE status = 'held'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Moves a job between states only if it is currently in <paramref name="from"/>. Returns false if it wasn't.</summary>
    public bool TryTransition(long id, string from, string to, string? error = null)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status = $to, error = $err WHERE id = $id AND status = $from";
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$from", from);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void MarkReceived(long id, long sizeBytes)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status = 'held', size_bytes = $size WHERE id = $id AND status = 'receiving'";
        cmd.Parameters.AddWithValue("$size", sizeBytes);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetJobAttributes(long id)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT job_attributes FROM jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as byte[];
    }

    public void SetDocumentFormat(long id, string format)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET document_format = $f WHERE id = $id";
        cmd.Parameters.AddWithValue("$f", format);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkReleased(long id, string printerId)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE jobs SET status = 'released', released_at = $now, released_printer_id = $p, error = NULL
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$p", printerId);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Jobs whose spool files should be deleted: held past their expiry, or stuck receiving.</summary>
    public List<JobRecord> ListStale(DateTimeOffset now, TimeSpan receivingTimeout)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = SelectColumns + """
             WHERE (j.status = 'held' AND j.expires_at < $now)
                OR (j.status = 'receiving' AND j.submitted_at < $receivingCutoff)
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$receivingCutoff", (now - receivingTimeout).ToString("O"));
        return ReadAll(cmd);
    }

    private static List<JobRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (r.Read())
        {
            jobs.Add(new JobRecord(
                Id: r.GetInt64(0),
                UserId: r.IsDBNull(1) ? null : r.GetInt64(1),
                Username: r.IsDBNull(2) ? null : r.GetString(2),
                OwnerHint: r.IsDBNull(3) ? null : r.GetString(3),
                QueueId: r.GetString(4),
                Name: r.GetString(5),
                DocumentFormat: r.GetString(6),
                Copies: r.GetInt32(7),
                SizeBytes: r.GetInt64(8),
                Status: r.GetString(9),
                SourceIp: r.GetString(10),
                SubmittedAt: DateTimeOffset.Parse(r.GetString(11)),
                ExpiresAt: DateTimeOffset.Parse(r.GetString(12)),
                ReleasedAt: r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13)),
                ReleasedPrinterId: r.IsDBNull(14) ? null : r.GetString(14),
                Error: r.IsDBNull(15) ? null : r.GetString(15)));
        }
        return jobs;
    }
}
