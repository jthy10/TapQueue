using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Data;

/// <param name="Uri">The printer's own IPP endpoint, e.g. ipp://192.0.2.10/ipp/print.</param>
/// <param name="TlsSkipVerify">Printers ship with self-signed certificates, so ipps:// usually needs this.</param>
public sealed record PrinterRecord(string Id, string Name, string Location, string Uri, bool TlsSkipVerify)
{
    public static bool IsValidUri(string? uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "ipp" or "ipps" or "http" or "https";
}

/// <summary>The physical printers that held jobs can be released to.</summary>
public sealed class PrinterStore(Database database)
{
    private const string Columns = "id, name, location, uri, tls_skip_verify";

    public PrinterRecord? Get(string id) =>
        database.QueryOne($"SELECT {Columns} FROM printers WHERE id = $id", Map, ("$id", id));

    public List<PrinterRecord> List() =>
        database.Query($"SELECT {Columns} FROM printers ORDER BY id", Map);

    public PrinterRecord Create(PrinterRecord printer) =>
        database.QueryOne($"""
            INSERT INTO printers (id, name, location, uri, tls_skip_verify, created_at)
            VALUES ($id, $name, $location, $uri, $tls, $now)
            RETURNING {Columns}
            """, Map, Parameters(printer).Append(("$now", DateTimeOffset.UtcNow)).ToArray())!;

    public PrinterRecord? Update(PrinterRecord printer) =>
        database.QueryOne($"""
            UPDATE printers SET name = $name, location = $location, uri = $uri, tls_skip_verify = $tls
            WHERE id = $id
            RETURNING {Columns}
            """, Map, Parameters(printer));

    /// <summary>Stations that release to this printer, which have to be moved or removed before it can be deleted.</summary>
    public List<string> StationsUsing(string id) =>
        database.Query("SELECT id FROM stations WHERE printer_id = $id COLLATE NOCASE ORDER BY id", r => r.GetString(0), ("$id", id));

    public bool Delete(string id) =>
        database.Execute("DELETE FROM printers WHERE id = $id", ("$id", id)) == 1;

    private static (string, object?)[] Parameters(PrinterRecord p) =>
        [("$id", p.Id), ("$name", p.Name), ("$location", p.Location), ("$uri", p.Uri), ("$tls", p.TlsSkipVerify)];

    private static PrinterRecord Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4));
}
