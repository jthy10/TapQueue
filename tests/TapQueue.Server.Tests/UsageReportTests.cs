using TapQueue.Server.Data;
using TapQueue.Server.Reports;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests;

/// <summary>Pages added up by person, group, printer, queue, day and month.</summary>
public sealed class UsageReportTests : IDisposable
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly Database _db;
    private readonly JobStore _jobs;
    private readonly UserStore _users;
    private readonly UsageReport _report;

    public UsageReportTests()
    {
        _db = new Database(Path.Combine(_dir, "test.db"));
        _db.Migrate();
        _jobs = new JobStore(_db);
        _users = new UserStore(_db);
        _report = new UsageReport(_db);
        new PrinterStore(_db).Create(new PrinterRecord("office", "Office printer", "", "ipp://192.0.2.9/ipp/print", false));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <param name="at">When it was released, UTC.</param>
    private void Released(long? userId, string at, int? pages, int copies = 1, string printer = "office", string queue = "secure")
    {
        var job = _jobs.Create(new NewJob(userId, null, queue, "doc", "application/pdf", copies, "192.0.2.1", DateTimeOffset.UtcNow.AddHours(1), null));
        _jobs.MarkReceived(job.Id, 10, pages);
        _jobs.MarkReleased(job.Id, printer);
        _db.Execute("UPDATE jobs SET released_at = $at WHERE id = $id", ("$at", DateTimeOffset.Parse(at + "Z")), ("$id", job.Id));
    }

    private UsageReportDto Run(string by, string from = "2026-10-01", string to = "2026-10-31", TimeZoneInfo? zone = null) =>
        _report.Run(DateOnly.Parse(from), DateOnly.Parse(to), by, zone ?? TimeZoneInfo.Utc);

    private static string[] Lines(UsageReportDto report) => report.Rows.Select(r => $"{r.Key}|{r.Name}|{r.Jobs}|{r.Pages}|{r.UncountedJobs}").ToArray();

    [Fact]
    public void PeopleAreListedMostPagesFirstCountingCopiesAndOnePageForJobsThatCouldntBeCounted()
    {
        var alice = _users.Create("alice", "Alice A", null).Id;
        var bob = _users.Create("bob", "Bob B", null).Id;
        Released(alice, "2026-10-05T10:00:00", pages: 3, copies: 2);
        Released(alice, "2026-10-06T10:00:00", pages: null, copies: 4);
        Released(bob, "2026-10-07T10:00:00", pages: 20);
        Released(null, "2026-10-08T10:00:00", pages: 1);

        var report = Run(ReportGrouping.User);

        Assert.Equal(["bob|Bob B|1|20|0", "alice|Alice A|2|10|1", "|(nobody)|1|1|0"], Lines(report));
        Assert.Equal((4, 31L, 1), (report.Jobs, report.Pages, report.UncountedJobs));
    }

    [Fact]
    public void OnlyReleasedJobsInThePeriodCount()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        Released(alice, "2026-09-30T23:59:59", pages: 100);
        Released(alice, "2026-10-01T00:00:00", pages: 1);
        Released(alice, "2026-10-31T23:59:59", pages: 2);
        Released(alice, "2026-11-01T00:00:00", pages: 100);
        var held = _jobs.Create(new NewJob(alice, null, "secure", "held", "application/pdf", 1, "192.0.2.1", DateTimeOffset.UtcNow.AddHours(1), null));
        _jobs.MarkReceived(held.Id, 10, 100);

        Assert.Equal(["alice|Alice|2|3|0"], Lines(Run(ReportGrouping.User)));
    }

    [Fact]
    public void DaysStartAtMidnightOnTheServersClock()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        // 03:30 UTC on the 2nd is 23:30 on the 1st in New York.
        Released(alice, "2026-10-02T03:30:00", pages: 5);
        Released(alice, "2026-10-02T04:30:00", pages: 7);

        var report = Run(ReportGrouping.Day, "2026-10-01", "2026-10-03", NewYork);

        Assert.Equal(["2026-10-01|2026-10-01|1|5|0", "2026-10-02|2026-10-02|1|7|0", "2026-10-03|2026-10-03|0|0|0"], Lines(report));
        Assert.Equal("America/New_York", report.TimeZone);
    }

    [Fact]
    public void MonthsCoverThePeriodEvenWhenItStartsMidMonth()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        Released(alice, "2026-08-10T10:00:00", pages: 100);
        Released(alice, "2026-08-20T10:00:00", pages: 4);
        Released(alice, "2026-10-02T10:00:00", pages: 6);

        Assert.Equal(["2026-08|2026-08|1|4|0", "2026-09|2026-09|0|0|0", "2026-10|2026-10|1|6|0"],
            Lines(Run(ReportGrouping.Month, "2026-08-15", "2026-10-15")));
    }

    [Fact]
    public void SomeoneInTwoGroupsCountsInBothAndPeopleInNoneAreTogether()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        var bob = _users.Create("bob", "Bob", null).Id;
        var groups = new GroupStore(_db);
        groups.Create("staff", "Staff", "");
        groups.Create("it", "IT", "");
        groups.SetMembers("staff", [(alice, null)]);
        groups.SetMembers("it", [(alice, null)]);
        Released(alice, "2026-10-05T10:00:00", pages: 3);
        Released(bob, "2026-10-05T10:00:00", pages: 9);

        var report = Run(ReportGrouping.Group);

        Assert.Equal(["|(no group)|1|9|0", "it|IT|1|3|0", "staff|Staff|1|3|0"], Lines(report));
        Assert.Equal(12, report.Pages);
    }

    [Fact]
    public void PrintersAndQueuesGoByNameAndRemovedOnesAreStillCounted()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        new QueueStore(_db).Create(new QueueRecord("secure", "Secure Print", "", "", false, false, "iso_a4_210x297mm"));
        Released(alice, "2026-10-05T10:00:00", pages: 3);
        Released(alice, "2026-10-05T10:00:00", pages: 8, printer: "lobby", queue: "old");

        Assert.Equal(["lobby|(removed printer)|1|8|0", "office|Office printer|1|3|0"], Lines(Run(ReportGrouping.Printer)));
        Assert.Equal(["old|(removed queue)|1|8|0", "secure|Secure Print|1|3|0"], Lines(Run(ReportGrouping.Queue)));
    }

    [Fact]
    public void ADeletedUsersPagesStayUnderTheirUsername()
    {
        var alice = _users.Create("alice", "Alice", null).Id;
        Released(alice, "2026-10-05T10:00:00", pages: 3);
        _users.Delete(alice);

        Assert.Equal(["alice|(deleted user)|1|3|0"], Lines(Run(ReportGrouping.User)));
    }

    [Fact]
    public void TheCsvHasATotalAndDoesntLetANameBecomeAFormula()
    {
        var eve = _users.Create("eve", "=HYPERLINK(\"http://example.com\")", null).Id;
        Released(eve, "2026-10-05T10:00:00", pages: 3);

        var csv = UsageReport.ToCsv(Run(ReportGrouping.User));

        Assert.Equal(
            "User,Name,Jobs,Pages,Jobs counted as 1 page\r\n" +
            "eve,\"'=HYPERLINK(\"\"http://example.com\"\")\",1,3,0\r\n" +
            "Total,2026-10-01 to 2026-10-31,1,3,0\r\n", csv);
    }
}
