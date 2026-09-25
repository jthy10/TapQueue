using Microsoft.Data.Sqlite;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <summary>What an event is about, for filtering the activity log.</summary>
public static class EventCategory
{
    /// <summary>An admin changed something: users, cards, printers, queues, stations, builds.</summary>
    public const string Admin = "admin";
    /// <summary>A job arrived, was released, canceled or expired.</summary>
    public const string Job = "job";
    /// <summary>A card was tapped at a station.</summary>
    public const string Tap = "tap";
    /// <summary>A client signed in, or was turned away.</summary>
    public const string SignIn = "signin";
    /// <summary>A client program crashed and sent a crash report.</summary>
    public const string Crash = "crash";
    /// <summary>A printer had a problem (unreachable, paper jam, toner low) or recovered.</summary>
    public const string Printer = "printer";
    /// <summary>The Active Directory sync added, changed or disabled users and groups, or failed.</summary>
    public const string Directory = "directory";
}

/// <summary>
/// The activity log: who did what, in plain sentences. Subjects look like "user:alice" or
/// "printer:m404n" so the console can link to them and show one thing's history.
/// </summary>
public sealed class EventLog(Database database)
{
    /// <summary>Admin changes made with admin.token or in dev mode's open console, where nobody signed in.</summary>
    public const string AdminActor = "admin";

    private static readonly AsyncLocal<string?> Acting = new();

    /// <summary>Who admin changes in this request are by: the signed-in admin, or <see cref="AdminActor"/>.</summary>
    public static string CurrentAdmin => Acting.Value ?? AdminActor;

    /// <summary>Records admin changes under <paramref name="actor"/> until disposed. Set for each admin API request.</summary>
    public static IDisposable ActingAs(string actor)
    {
        var previous = Acting.Value;
        Acting.Value = actor;
        return new Restore(() => Acting.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>Things TapQueue noticed by itself, like a printer going offline.</summary>
    public const string System = "system";

    public static string User(string username) => $"user:{username}";
    public static string Printer(string id) => $"printer:{id}";
    public static string Queue(string id) => $"queue:{id}";
    public static string Station(string id) => $"station:{id}";
    public static string Job(long id) => $"job:{id}";
    public static string Group(string id) => $"group:{id}";

    public void Record(string category, string actor, string? subject, string message) =>
        database.Execute("""
            INSERT INTO events (at, category, actor, subject, message) VALUES ($at, $c, $a, $s, $m)
            """, ("$at", DateTimeOffset.UtcNow), ("$c", category), ("$a", actor), ("$s", subject), ("$m", message));

    public void Admin(string? subject, string message) => Record(EventCategory.Admin, CurrentAdmin, subject, message);

    /// <summary>Newest first. <paramref name="before"/> is an event id, for paging back.</summary>
    public List<EventDto> List(string? category = null, string? subject = null, long? before = null, int limit = 100) =>
        database.Query("""
            SELECT id, at, category, actor, subject, message FROM events
            WHERE ($c IS NULL OR category = $c)
              AND ($s IS NULL OR subject = $s COLLATE NOCASE)
              AND ($before IS NULL OR id < $before)
            ORDER BY id DESC LIMIT $limit
            """, Map, ("$c", category), ("$s", subject), ("$before", before), ("$limit", Math.Clamp(limit, 1, 500)));

    public int DeleteOlderThan(DateTimeOffset cutoff) =>
        database.Execute("DELETE FROM events WHERE at < $cutoff", ("$cutoff", cutoff));

    private static EventDto Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetTime(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetString(5));
}
