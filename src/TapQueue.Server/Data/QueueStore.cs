using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Data;

/// <summary>A queue is a printer users see in Windows. Jobs sent to it are held until released.</summary>
/// <param name="Name">The printer name shown in the Windows print dialog.</param>
/// <param name="DefaultMedia">PWG media name, e.g. na_letter_8.5x11in or iso_a4_210x297mm.</param>
/// <param name="SidesPolicy">What it does about two-sided printing: one of <see cref="Shared.Api.SidesPolicy"/>.</param>
/// <param name="MaxPages">The most pages (times copies) one job may have; 0 for no limit.</param>
/// <param name="DirectPrinterId">The printer its jobs go straight to instead of being held; null for a hold queue.</param>
public sealed record QueueRecord(string Id, string Name, string Description, string Location, bool Color, bool Duplex, string DefaultMedia,
    string SidesPolicy = Shared.Api.SidesPolicy.Any, int MaxPages = 0, string? DirectPrinterId = null)
{
    public const string DefaultDescription = "Tap your badge at any TapQueue printer to release your print.";
    public const string Letter = "na_letter_8.5x11in";

    /// <summary>Its jobs print on both sides whatever they ask for (when it offers two-sided at all).</summary>
    public bool ForcesTwoSided => Duplex && SidesPolicy == Shared.Api.SidesPolicy.ForceTwoSided;

    /// <summary>A job that doesn't say prints on both sides.</summary>
    public bool DefaultsToTwoSided => Duplex && SidesPolicy != Shared.Api.SidesPolicy.Any;
}

public sealed class QueueStore(Database database)
{
    private const string Columns = "id, name, description, location, color, duplex, default_media, sides_policy, max_pages, direct_printer_id";

    public QueueRecord? Get(string id) =>
        database.QueryOne($"SELECT {Columns} FROM queues WHERE id = $id", Map, ("$id", id));

    public List<QueueRecord> List() =>
        database.Query($"SELECT {Columns} FROM queues ORDER BY id", Map);

    public QueueRecord Create(QueueRecord queue) =>
        database.QueryOne($"""
            INSERT INTO queues (id, name, description, location, color, duplex, default_media, sides_policy, max_pages, direct_printer_id, created_at)
            VALUES ($id, $name, $description, $location, $color, $duplex, $media, $sides, $maxPages, $direct, $now)
            RETURNING {Columns}
            """, Map, Parameters(queue).Append(("$now", DateTimeOffset.UtcNow)).ToArray())!;

    public QueueRecord? Update(QueueRecord queue) =>
        database.QueryOne($"""
            UPDATE queues SET name = $name, description = $description, location = $location,
                              color = $color, duplex = $duplex, default_media = $media,
                              sides_policy = $sides, max_pages = $maxPages, direct_printer_id = $direct
            WHERE id = $id
            RETURNING {Columns}
            """, Map, Parameters(queue));

    public bool Delete(string id) =>
        database.Execute("DELETE FROM queues WHERE id = $id", ("$id", id)) == 1;

    /// <summary>Queues that print straight to this printer, which have to be changed before it can be removed.</summary>
    public List<QueueRecord> PrintingDirectlyTo(string printerId) =>
        List().Where(q => string.Equals(q.DirectPrinterId, printerId, StringComparison.OrdinalIgnoreCase)).ToList();

    private static (string, object?)[] Parameters(QueueRecord q) =>
        [("$id", q.Id), ("$name", q.Name), ("$description", q.Description), ("$location", q.Location),
         ("$color", q.Color), ("$duplex", q.Duplex), ("$media", q.DefaultMedia),
         ("$sides", q.SidesPolicy), ("$maxPages", q.MaxPages), ("$direct", q.DirectPrinterId)];

    private static QueueRecord Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetString(6),
            r.GetString(7), r.GetInt32(8), r.GetStringOrNull(9));
}
