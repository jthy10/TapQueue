using System.Globalization;
using TapQueue.Server.Data;
using TapQueue.Server.Users;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Reports;

/// <summary>
/// Pages printed over a period, added up by person, group, printer, queue, day or month. Counts
/// what quotas count: released jobs, as pages times copies, and 1 page for a job whose pages
/// couldn't be counted. Days and months are the server's (its time zone), like quota periods.
/// </summary>
public sealed class UsageReport(Database database)
{
    /// <summary>The longest period one report covers. By day that's a row a day.</summary>
    public const int MaxDays = 3660;

    private sealed record Job(DateTimeOffset ReleasedAt, long? UserId, string? FormerOwner, string? PrinterId, string QueueId, long Pages, bool Uncounted);

    /// <param name="from">First day, in <paramref name="zone"/>.</param>
    /// <param name="to">Last day, included.</param>
    /// <param name="by">One of <see cref="ReportGrouping"/>.</param>
    public UsageReportDto Run(DateOnly from, DateOnly to, string by, TimeZoneInfo zone)
    {
        var jobs = database.Query("""
            SELECT released_at, user_id, former_owner, released_printer_id, queue_id, COALESCE(pages, 1) * copies, pages IS NULL
            FROM jobs WHERE status = $released AND released_at >= $from AND released_at < $to
            """, r => new Job(r.GetTime(0), r.GetInt64OrNull(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetString(4), r.GetInt64(5), r.GetBoolean(6)),
            ("$released", JobStatus.Released), ("$from", StartOf(from, zone)), ("$to", StartOf(to.AddDays(1), zone)));

        var rows = by switch
        {
            ReportGrouping.User => ByUser(jobs),
            ReportGrouping.Group => ByGroup(jobs),
            ReportGrouping.Printer => ByName(jobs, j => j.PrinterId, "SELECT id, name FROM printers", "(removed printer)"),
            ReportGrouping.Queue => ByName(jobs, j => j.QueueId, "SELECT id, name FROM queues", "(removed queue)"),
            ReportGrouping.Day => ByTime(jobs, zone, from, to, d => d, d => d.AddDays(1), "yyyy-MM-dd"),
            ReportGrouping.Month => ByTime(jobs, zone, from, to, d => new DateOnly(d.Year, d.Month, 1), d => d.AddMonths(1), "yyyy-MM"),
            _ => throw new ArgumentException($"Unknown grouping \"{by}\".", nameof(by)),
        };
        return new UsageReportDto(Format(from), Format(to), by, zone.Id, jobs.Count, jobs.Sum(j => j.Pages), jobs.Count(j => j.Uncounted), rows);
    }

    /// <summary>The report as a spreadsheet would want it, with a total line.</summary>
    public static string ToCsv(UsageReportDto report)
    {
        var heading = report.By switch
        {
            ReportGrouping.User => "User",
            ReportGrouping.Group => "Group",
            ReportGrouping.Printer => "Printer",
            ReportGrouping.Queue => "Queue",
            ReportGrouping.Day => "Day",
            _ => "Month",
        };
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string[]> { new[] { heading, "Name", "Jobs", "Pages", "Jobs counted as 1 page" } };
        lines.AddRange(report.Rows.Select(r => new[] { r.Key, r.Name, N(r.Jobs), N(r.Pages), N(r.UncountedJobs) }));
        // A group report adds people in two groups twice, so its rows don't add up to this.
        lines.Add(["Total", $"{report.From} to {report.To}", N(report.Jobs), N(report.Pages), N(report.UncountedJobs)]);
        return Csv.Write(lines, guardFormulas: true);
    }

    private List<UsageRowDto> ByUser(List<Job> jobs)
    {
        var users = database.Query("SELECT id, username, display_name FROM users", r => (Id: r.GetInt64(0), Username: r.GetString(1), Name: r.GetString(2)))
            .ToDictionary(u => u.Id);
        return Rows(jobs.GroupBy(j => j.UserId is { } id && users.TryGetValue(id, out var user)
            ? (Key: user.Username, Name: user.Name)
            // A deleted user's jobs keep their username; ones nobody owned have neither.
            : j.FormerOwner is { } former ? (Key: former, Name: "(deleted user)") : (Key: "", Name: "(nobody)")));
    }

    /// <summary>Each job counts for every group its owner is in now; people in no group are one row.</summary>
    private List<UsageRowDto> ByGroup(List<Job> jobs)
    {
        var names = database.Query("SELECT id, name FROM groups", r => (Id: r.GetString(0), Name: r.GetString(1)))
            .ToDictionary(g => g.Id, g => g.Name, StringComparer.OrdinalIgnoreCase);
        var groupsOf = database.Query("SELECT user_id, group_id FROM group_members", r => (UserId: r.GetInt64(0), GroupId: r.GetString(1)))
            .ToLookup(m => m.UserId, m => m.GroupId);
        return Rows(jobs
            .SelectMany(j => j.UserId is { } id && groupsOf[id].Any()
                ? groupsOf[id].Select(g => (Key: (Key: g, Name: names.GetValueOrDefault(g, g)), Job: j))
                : [(Key: (Key: "", Name: "(no group)"), Job: j)])
            .GroupBy(x => x.Key, x => x.Job));
    }

    private List<UsageRowDto> ByName(List<Job> jobs, Func<Job, string?> id, string namesSql, string removed)
    {
        var names = database.Query(namesSql, r => (Id: r.GetString(0), Name: r.GetString(1)))
            .ToDictionary(x => x.Id, x => x.Name, StringComparer.OrdinalIgnoreCase);
        return Rows(jobs.GroupBy(j => id(j) is { } key ? (Key: key, Name: names.GetValueOrDefault(key, removed)) : (Key: "", Name: "(unknown)")));
    }

    /// <summary>A row for every day or month in the period, in order, printed on or not.</summary>
    private static List<UsageRowDto> ByTime(List<Job> jobs, TimeZoneInfo zone, DateOnly from, DateOnly to,
        Func<DateOnly, DateOnly> bucket, Func<DateOnly, DateOnly> next, string format)
    {
        var printed = jobs.GroupBy(j => bucket(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(j.ReleasedAt, zone).DateTime)))
            .ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<UsageRowDto>();
        for (var at = bucket(from); at <= to; at = next(at))
        {
            var key = at.ToString(format, CultureInfo.InvariantCulture);
            var inIt = printed.GetValueOrDefault(at, []);
            rows.Add(new UsageRowDto(key, key, inIt.Count, inIt.Sum(j => j.Pages), inIt.Count(j => j.Uncounted)));
        }
        return rows;
    }

    /// <summary>Most pages first.</summary>
    private static List<UsageRowDto> Rows(IEnumerable<IGrouping<(string Key, string Name), Job>> groups) =>
        groups.Select(g => new UsageRowDto(g.Key.Key, g.Key.Name, g.Count(), g.Sum(j => j.Pages), g.Count(j => j.Uncounted)))
            .OrderByDescending(r => r.Pages).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // A midnight the clocks skip starts an hour later.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
