using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Data;

/// <summary>Column readers for the types we store. Times are ISO 8601 text.</summary>
internal static class SqliteReaderExtensions
{
    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    public static DateTimeOffset GetTime(this SqliteDataReader r, int i) => DateTimeOffset.Parse(r.GetString(i));

    public static DateTimeOffset? GetTimeOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetTime(i);
}
