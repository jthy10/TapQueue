using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Data;

/// <summary>A queue is a printer users see in Windows. Jobs sent to it are held until released.</summary>
/// <param name="Name">The printer name shown in the Windows print dialog.</param>
/// <param name="DefaultMedia">PWG media name, e.g. na_letter_8.5x11in or iso_a4_210x297mm.</param>
public sealed record QueueRecord(string Id, string Name, string Description, string Location, bool Color, bool Duplex, string DefaultMedia)
{
    public const string DefaultDescription = "Tap your badge at any TapQueue printer to release your print.";
    public const string Letter = "na_letter_8.5x11in";
}

public sealed class QueueStore(Database database)
{
    private const string Columns = "id, name, description, location, color, duplex, default_media";

    public QueueRecord? Get(string id) =>
        database.QueryOne($"SELECT {Columns} FROM queues WHERE id = $id", Map, ("$id", id));

    public List<QueueRecord> List() =>
        database.Query($"SELECT {Columns} FROM queues ORDER BY id", Map);

    public QueueRecord Create(QueueRecord queue) =>
        database.QueryOne($"""
            INSERT INTO queues (id, name, description, location, color, duplex, default_media, created_at)
            VALUES ($id, $name, $description, $location, $color, $duplex, $media, $now)
            RETURNING {Columns}
            """, Map, Parameters(queue).Append(("$now", DateTimeOffset.UtcNow)).ToArray())!;

    public QueueRecord? Update(QueueRecord queue) =>
        database.QueryOne($"""
            UPDATE queues SET name = $name, description = $description, location = $location,
                              color = $color, duplex = $duplex, default_media = $media
            WHERE id = $id
            RETURNING {Columns}
            """, Map, Parameters(queue));

    public bool Delete(string id) =>
        database.Execute("DELETE FROM queues WHERE id = $id", ("$id", id)) == 1;

    private static (string, object?)[] Parameters(QueueRecord q) =>
        [("$id", q.Id), ("$name", q.Name), ("$description", q.Description), ("$location", q.Location),
         ("$color", q.Color), ("$duplex", q.Duplex), ("$media", q.DefaultMedia)];

    private static QueueRecord Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetString(6));
}
