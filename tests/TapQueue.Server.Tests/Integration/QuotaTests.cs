using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Page limits on users and groups, checked job by job at release.</summary>
public sealed class QuotaTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private static readonly byte[] Uncountable = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task SetUserQuota(string username, int pages, string period = QuotaPeriod.Month) =>
        (await _server.Admin.PutAsJsonAsync($"/api/v1/admin/users/{username}/quota", new QuotaDto(pages, period), TapQueueJson.Options))
            .EnsureSuccessStatusCode();

    private async Task CreateGroup(string id, int? pages = null, string period = QuotaPeriod.Month)
    {
        (await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest(id, id), TapQueueJson.Options)).EnsureSuccessStatusCode();
        if (pages is not null)
            (await _server.Admin.PutAsJsonAsync($"/api/v1/admin/groups/{id}/quota", new QuotaDto(pages.Value, period), TapQueueJson.Options))
                .EnsureSuccessStatusCode();
    }

    private async Task Join(string group, string username) =>
        (await _server.Admin.PutAsync($"/api/v1/admin/groups/{group}/members/{username}", null)).EnsureSuccessStatusCode();

    private async Task SetOverrun(string overrun) =>
        (await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings", new UpdateServerSettingsRequest(QuotaOverrun: overrun), TapQueueJson.Options))
            .EnsureSuccessStatusCode();

    private static async Task<List<ReleaseResult>> Release(HttpClient user) =>
        (await TestServer.ReadAsync<ReleaseResponse>(
            await user.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options))).Results.ToList();

    private async Task<UserQuotaDto> Quota(string username) =>
        (await _server.Admin.GetFromJsonAsync<UserQuotaDto>($"/api/v1/admin/users/{username}/quota", TapQueueJson.Options))!;

    [Fact]
    public async Task UsersWithoutLimitsPrintEverything()
    {
        using var alice = await _server.SignInAsync("alice");
        await CreateGroup("everyone");
        await Join("everyone", "alice");
        for (var i = 0; i < 3; i++)
            await _server.PrintAsync($"job {i}", ThreePages);

        Assert.All(await Release(alice), r => Assert.True(r.Success));
        Assert.Empty((await Quota("alice")).Applies);
    }

    [Fact]
    public async Task ByDefaultAJobThatStartsUnderTheLimitPrintsInFull()
    {
        using var alice = await _server.SignInAsync("alice");
        await SetUserQuota("alice", 5);
        await _server.PrintAsync("first", ThreePages);
        await _server.PrintAsync("second", ThreePages);
        await _server.PrintAsync("third", ThreePages);

        var results = await Release(alice);

        Assert.Equal([true, true, false], results.Select(r => r.Success));
        Assert.Contains("used all 5 pages", results[2].Error);
        Assert.Equal(["first", "second"], _server.Printer.Jobs.Select(j => j.Name));
        var usage = Assert.Single((await Quota("alice")).Applies);
        Assert.Equal((6, 0, "user"), (usage.Used, usage.Remaining, usage.Source));
    }

    [Fact]
    public async Task WhenOverrunIsDeniedOnlyJobsThatFitPrint()
    {
        using var alice = await _server.SignInAsync("alice");
        await SetOverrun(QuotaOverrun.Deny);
        await SetUserQuota("alice", 5);
        await _server.PrintAsync("first", ThreePages);
        await _server.PrintAsync("second", ThreePages);
        await _server.PrintAsync("small", Uncountable);

        var results = await Release(alice);

        Assert.Equal([true, false, true], results.Select(r => r.Success));
        Assert.Contains("would go over your page limit: you have 2 of 5 left this month", results[1].Error);
        var held = await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options);
        Assert.Equal("second", Assert.Single(held!).Name);
    }

    [Fact]
    public async Task CopiesCount()
    {
        using var alice = await _server.SignInAsync("alice");
        await SetOverrun(QuotaOverrun.Deny);
        await SetUserQuota("alice", 5);
        await _server.PrintAsync("two copies", ThreePages, copies: 2);

        Assert.False(Assert.Single(await Release(alice)).Success);
    }

    [Fact]
    public async Task TheMostGenerousGroupLimitCountsAndGroupsWithoutOneDontLiftIt()
    {
        using var alice = await _server.SignInAsync("alice");
        await CreateGroup("everyone");
        await CreateGroup("students", pages: 2, QuotaPeriod.Day);
        await CreateGroup("library", pages: 4, QuotaPeriod.Week);
        foreach (var group in new[] { "everyone", "students", "library" })
            await Join(group, "alice");
        await _server.PrintAsync("first", ThreePages);
        await _server.PrintAsync("second", ThreePages);

        var results = await Release(alice);

        // The weekly 4 still had room after the first job; the daily 2 didn't.
        Assert.Equal([true, true], results.Select(r => r.Success));
        var applies = (await Quota("alice")).Applies;
        Assert.Equal(["group:library", "group:students"], applies.Select(a => a.Source));
        Assert.All(applies, a => Assert.Equal(6, a.Used));
    }

    [Fact]
    public async Task AUsersOwnLimitOverridesTheirGroups()
    {
        using var alice = await _server.SignInAsync("alice");
        await CreateGroup("staff", pages: 1000);
        await Join("staff", "alice");
        await SetUserQuota("alice", 0);
        await _server.PrintAsync("first", ThreePages);

        Assert.False(Assert.Single(await Release(alice)).Success);

        (await _server.Admin.DeleteAsync("/api/v1/admin/users/alice/quota")).EnsureSuccessStatusCode();
        Assert.True(Assert.Single(await Release(alice)).Success);
    }

    [Fact]
    public async Task LimitsAndTheOverrunSettingAreValidated()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);

        var badPeriod = await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(10, "year"), TapQueueJson.Options);
        var badPages = await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(-1, QuotaPeriod.Day), TapQueueJson.Options);
        var badOverrun = await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(QuotaOverrun: "sometimes"), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, badPeriod.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPages.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badOverrun.StatusCode);
        var server = await _server.Admin.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options);
        Assert.Equal(QuotaOverrun.Allow, server!.QuotaOverrun);
    }

    [Theory]
    [InlineData("2026-09-23T15:00:00Z", QuotaPeriod.Day, "2026-09-23", "2026-09-24")]
    [InlineData("2026-09-23T15:00:00Z", QuotaPeriod.Week, "2026-09-21", "2026-09-28")] // a Wednesday
    [InlineData("2026-09-27T23:59:00Z", QuotaPeriod.Week, "2026-09-21", "2026-09-28")] // Sunday
    [InlineData("2026-09-28T00:00:00Z", QuotaPeriod.Week, "2026-09-28", "2026-10-05")] // Monday
    [InlineData("2026-12-31T23:00:00Z", QuotaPeriod.Month, "2026-12-01", "2027-01-01")]
    public void PeriodsStartAtMidnightOnMondayAndOnTheFirst(string now, string period, string start, string end)
    {
        var (from, to) = QuotaPolicy.Period(period, DateTimeOffset.Parse(now), TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(start + "T00:00:00Z"), from);
        Assert.Equal(DateTimeOffset.Parse(end + "T00:00:00Z"), to);
    }
}
