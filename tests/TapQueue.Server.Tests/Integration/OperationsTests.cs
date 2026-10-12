using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Hosting;
using TapQueue.Server.Admins;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// The things an office sets for itself: how long a tap waits, how long history is kept, the time
/// zone, sign-in lockout and reverse proxies; and what jobs remember about colour and sides.
/// </summary>
public sealed class OperationsTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");

    private static async Task<ServerInfoDto> Patch(TestServer server, UpdateServerSettingsRequest request) =>
        await TestServer.ReadAsync<ServerInfoDto>(await server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings", request, TapQueueJson.Options));

    private static async Task<HttpClient> Station(TestServer server)
    {
        var created = await TestServer.ReadAsync<StationTokenResponse>(await server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        return server.NewClient(created.Token);
    }

    private static async Task<StationTapResponse> Tap(HttpClient station, string card) =>
        await TestServer.ReadAsync<StationTapResponse>(await station.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest(card), TapQueueJson.Options));

    [Fact]
    public async Task ATapDoesntWaitForASlowPrinterAndTheNextPersonIsServedMeanwhile()
    {
        await using var server = await TestServer.StartAsync();
        using var station = await Station(server);
        using var alice = await server.SignInAsync("alice", windowsUser: "alice");
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("bob", null), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "1111"), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("bob", "2222"), TapQueueJson.Options);
        await server.PrintAsync("Big report", Pdf);
        await Patch(server, new(TapWaitSeconds: 1));
        server.Printer.PrintJobDelay = TimeSpan.FromSeconds(4);

        var started = DateTimeOffset.UtcNow;
        var tap = await Tap(station, "1111");
        var next = await Tap(station, "2222");
        var answered = DateTimeOffset.UtcNow - started;

        Assert.Equal(TapOutcome.Releasing, tap.Outcome);
        Assert.Contains("on their way", tap.Message);
        Assert.Equal(TapOutcome.NoJobs, next.Outcome);
        Assert.True(answered < TimeSpan.FromSeconds(3.5), $"Both taps took {answered.TotalSeconds:0.0} s; the printer takes 4 s a job.");

        // The release carried on by itself.
        for (var i = 0; i < 100 && server.Printer.Jobs.IsEmpty; i++)
            await Task.Delay(100);
        Assert.Equal("Big report", Assert.Single(server.Printer.Jobs).Name);
        for (var i = 0; i < 50 && (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs?status=released", TapQueueJson.Options))!.Count == 0; i++)
            await Task.Delay(100);
        var events = await server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=job", TapQueueJson.Options);
        Assert.Contains(events!, e => e.Message.Contains("released to Office printer at station lobby"));
    }

    [Fact]
    public async Task HowLongABusyPrinterIsRetriedIsASetting()
    {
        await using var server = await TestServer.StartAsync();
        using var alice = await server.SignInAsync("alice");
        await server.PrintAsync("Report", Pdf);
        await Patch(server, new(PrinterBusySeconds: 0));
        server.Printer.BusyCount = 1;

        var release = await TestServer.ReadAsync<ReleaseResponse>(await alice.PostAsJsonAsync("/api/v1/me/release",
            new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        // With no retrying, a busy printer is a failed release, and the job is held again.
        Assert.False(Assert.Single(release.Results).Success);
        Assert.Single((await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs?status=held", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task TheNewSettingsHaveDefaultsAndCanBeChangedCheckedAndReset()
    {
        await using var server = await TestServer.StartAsync();

        var before = (await server.Admin.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options))!;
        Assert.Equal((5, 120, 300, 0, 90, 5, 15), (before.TapWaitSeconds, before.PrinterBusySeconds, before.PrinterSendSeconds,
            before.JobHistoryDays, before.ActivityDays, before.SignInAttempts, before.SignInLockoutMinutes));
        Assert.Equal(TimeZoneInfo.Local.Id, before.TimeZone);

        var changed = await Patch(server, new(TapWaitSeconds: 0, JobHistoryDays: 365, ActivityDays: 0, TimeZone: "America/New_York", SignInAttempts: 10));
        Assert.Equal((0, 365, 0, "America/New_York", 10), (changed.TapWaitSeconds, changed.JobHistoryDays, changed.ActivityDays, changed.TimeZone, changed.SignInAttempts));
        Assert.Contains(ServerSettings.TimeZoneKey, changed.ChangedSettings!);
        var backups = (await server.Admin.GetFromJsonAsync<BackupsDto>("/api/v1/admin/server/backups", TapQueueJson.Options))!;
        Assert.Equal("America/New_York", backups.TimeZone);
        // 02:00 in New York, whatever the server's own zone.
        Assert.Equal(2, TimeZoneInfo.ConvertTime(backups.NextRunAt!.Value, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Hour);

        foreach (var bad in new UpdateServerSettingsRequest[]
                 {
                     new(JobHistoryDays: 7), new(TapWaitSeconds: 121), new(PrinterSendSeconds: 5), new(SignInAttempts: 1),
                     new(SignInLockoutMinutes: 0), new(TimeZone: "Mars/Olympus_Mons"), new(ActivityDays: -1),
                 })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings", bad, TapQueueJson.Options)).StatusCode);

        var reset = await Patch(server, new(Reset: [ServerSettings.TimeZoneKey, ServerSettings.JobHistoryDaysKey, ServerSettings.TapWaitKey]));
        Assert.Equal((5, 0, TimeZoneInfo.Local.Id), (reset.TapWaitSeconds, reset.JobHistoryDays, reset.TimeZone));

        var log = await server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);
        Assert.Contains(log!, e => e.Message.Contains("time zone America/New_York") && e.Message.Contains("job history kept for 365 days"));
    }

    [Fact]
    public async Task PageLimitPeriodsStartAtMidnightInTheSetTimeZone()
    {
        await using var server = await TestServer.StartAsync();
        await Patch(server, new(TimeZone: "Pacific/Auckland"));
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

        var (start, end) = QuotaPolicy.Period(QuotaPeriod.Day, DateTimeOffset.UtcNow, server.Service<ServerSettings>().TimeZone);

        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(start, auckland).TimeOfDay);
        Assert.Equal(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, auckland).Date, TimeZoneInfo.ConvertTime(start, auckland).Date);
        Assert.True(start <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < end);

        using var alice = await server.SignInAsync("alice");
        await server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(10, QuotaPeriod.Day), TapQueueJson.Options);
        var quota = (await server.Admin.GetFromJsonAsync<UserQuotaDto>("/api/v1/admin/users/alice/quota", TapQueueJson.Options))!;
        Assert.Equal(start, quota.Applies[0].PeriodStart);
    }

    [Fact]
    public async Task JobsRememberColourAndSidesAndReportsAddThemUp()
    {
        await using var server = await TestServer.StartAsync();
        await server.Admin.PatchAsJsonAsync($"/api/v1/admin/queues/{TestServer.QueueId}", new UpdateQueueRequest(Color: true, Duplex: true), TapQueueJson.Options);
        using var alice = await server.SignInAsync("alice");
        await server.PrintAsync("Brochure", Pdf, copies: 2, jobAttributes: a => a
            .Add("print-color-mode", IppValue.Keyword("color")).Add("sides", IppValue.Keyword("two-sided-long-edge")));
        await server.PrintAsync("Memo", Pdf, jobAttributes: a => a
            .Add("print-color-mode", IppValue.Keyword("monochrome")).Add("sides", IppValue.Keyword("one-sided")));

        var held = (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs?status=held", TapQueueJson.Options))!;
        Assert.Equal((true, true), (held.Single(j => j.Name == "Brochure").Color, held.Single(j => j.Name == "Brochure").Duplex));
        Assert.Equal((false, false), (held.Single(j => j.Name == "Memo").Color, held.Single(j => j.Name == "Memo").Duplex));

        await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options);
        var report = (await server.Admin.GetFromJsonAsync<UsageReportDto>("/api/v1/admin/reports/usage?by=user", TapQueueJson.Options))!;

        // Neither test PDF's pages can be counted, so each is 1 page a copy.
        Assert.Equal((3, 2, 1, 2), (report.Pages, report.ColorPages, report.MonoPages, report.DuplexPages));
        var csv = await server.Admin.GetStringAsync("/api/v1/admin/reports/usage?by=user&format=csv");
        Assert.Contains("Colour pages,Black and white pages,Two-sided pages", csv);
    }

    [Fact]
    public async Task FinishedJobsAndActivityAreDeletedAfterTheDaysTheyreKeptFor()
    {
        await using var server = await TestServer.StartAsync();
        using var alice = await server.SignInAsync("alice");
        await server.PrintAsync("Old report", Pdf);
        await server.PrintAsync("Recent report", Pdf);
        await server.PrintAsync("Still waiting", Pdf);
        var all = (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!;
        long Id(string name) => all.Single(j => j.Name == name).Id;
        await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId, [Id("Old report"), Id("Recent report")]), TapQueueJson.Options);
        var database = server.Service<Database>();
        database.Execute("UPDATE jobs SET released_at = $at WHERE id = $id", ("$at", DateTimeOffset.UtcNow.AddDays(-40)), ("$id", Id("Old report")));
        database.Execute("UPDATE events SET at = $at", ("$at", DateTimeOffset.UtcNow.AddDays(-40)));
        var cleanup = server.Service<IEnumerable<IHostedService>>().OfType<JobCleanupService>().Single();

        // By default job history is kept; activity goes after 90 days.
        cleanup.RunOnce(DateTimeOffset.UtcNow);
        Assert.Equal(3, (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!.Count);
        Assert.NotEmpty((await server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=job", TapQueueJson.Options))!);

        await Patch(server, new(JobHistoryDays: 32, ActivityDays: 30));
        cleanup.RunOnce(DateTimeOffset.UtcNow);

        var left = (await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!;
        Assert.Equal(["Recent report", "Still waiting"], left.Select(j => j.Name).Order());
        Assert.Empty((await server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=job", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task BehindATrustedProxyEachCallerIsThrottledByTheirOwnAddress()
    {
        await using var server = await TestServer.StartAsync(configure: config => config.Server.TrustedProxies = ["127.0.0.1"]);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("ada", null), TapQueueJson.Options);
        await server.Admin.PutAsJsonAsync("/api/v1/admin/users/ada/password", new SetPasswordRequest("a long console password"), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("ada", null, AdminRole.Admin), TapQueueJson.Options);

        async Task<HttpResponseMessage> SignIn(string from, string password)
        {
            using var browser = server.NewClient();
            browser.DefaultRequestHeaders.Add(AdminAccess.ConsoleHeader, "1");
            browser.DefaultRequestHeaders.Add("X-Forwarded-For", from);
            return await browser.PostAsJsonAsync("/api/v1/admin-auth/sign-in", new AdminSignInRequest("ada", password), TapQueueJson.Options);
        }

        for (var i = 0; i < 5; i++)
            await SignIn("198.51.100.66", "guess " + i);

        // Whoever was guessing is locked out; Ada, at her own address, isn't.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignIn("198.51.100.66", "a long console password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignIn("192.0.2.10", "a long console password")).StatusCode);
        var log = await server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=signin", TapQueueJson.Options);
        Assert.Contains(log!, e => e.Message.Contains("signed in to the admin console from 192.0.2.10"));
    }

    [Fact]
    public async Task WithoutATrustedProxyTheForwardedHeaderIsIgnored()
    {
        await using var server = await TestServer.StartAsync();
        using var station = await Station(server);
        station.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        await station.GetAsync("/api/v1/station/");

        var stations = await server.Admin.GetFromJsonAsync<List<StationDto>>("/api/v1/admin/stations", TapQueueJson.Options);
        Assert.Equal("127.0.0.1", stations!.Single().LastIp);
    }
}
