using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>People deleting their own jobs: held ones, and ones already at the printer, with the pages given back.</summary>
public sealed class JobCancelTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<int> Used(string username)
    {
        var quota = await _server.Admin.GetFromJsonAsync<UserQuotaDto>($"/api/v1/admin/users/{username}/quota", TapQueueJson.Options);
        return Assert.Single(quota!.Applies).Used;
    }

    /// <summary>Signs in alice with a 20-page limit, prints a three-page job twice over and releases it.</summary>
    private async Task<(HttpClient Alice, JobDto Job)> ReleaseOneAsync()
    {
        var alice = await _server.SignInAsync("alice");
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(20, QuotaPeriod.Month), TapQueueJson.Options)).EnsureSuccessStatusCode();
        await _server.PrintAsync("report", ThreePages, copies: 2);
        (await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(6, await Used("alice"));
        return (alice, await OnlyJob(alice));
    }

    private static async Task<JobDto> OnlyJob(HttpClient user) =>
        Assert.Single((await user.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!);

    [Fact]
    public async Task AHeldJobIsDeletedAndNothingIsGivenBack()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("report", ThreePages);
        var job = await OnlyJob(alice);

        var result = await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}"));

        Assert.Equal(0, result.RefundedPages);
        Assert.Contains("Deleted \"report\"", result.Message);
        Assert.Equal(JobStatus.Canceled, (await OnlyJob(alice)).Status);
    }

    [Fact]
    public async Task AJobThePrinterHasNotStartedIsStoppedAndGivenBackInFull()
    {
        _server.Printer.NewJobState = IppJobState.Pending;
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;

        var result = await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}"));

        Assert.Equal(6, result.RefundedPages);
        Assert.Contains("before it printed", result.Message);
        Assert.Contains("6 pages no longer count", result.Message);
        Assert.Equal(IppJobState.Canceled, Assert.Single(_server.Printer.JobStates).Value);
        Assert.Equal(JobStatus.Canceled, (await OnlyJob(alice)).Status);
        Assert.Equal(0, await Used("alice"));
    }

    [Fact]
    public async Task AJobStoppedPartWayGivesBackThePagesThatDidNotPrint()
    {
        _server.Printer.NewJobState = IppJobState.Processing;
        _server.Printer.ImpressionsCompleted = 2;
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;

        var result = await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}"));

        Assert.Equal(4, result.RefundedPages);
        Assert.Contains("after 2 pages", result.Message);
        var after = await OnlyJob(alice);
        Assert.Equal((JobStatus.Released, 4), (after.Status, after.RefundedPages));
        Assert.Equal(2, await Used("alice"));

        // Once is all: it can't be stopped, or given back, a second time.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}")).StatusCode);
        Assert.Equal(2, await Used("alice"));
    }

    [Fact]
    public async Task AJobThatHadStartedOnAPrinterThatDoesNotCountGivesNothingBack()
    {
        _server.Printer.NewJobState = IppJobState.Processing;
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;

        var result = await TestServer.ReadAsync<CancelJobResponse>(await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}"));

        Assert.Equal(0, result.RefundedPages);
        Assert.Contains("still count", result.Message);
        Assert.Equal(IppJobState.Canceled, Assert.Single(_server.Printer.JobStates).Value);
        Assert.Equal(6, await Used("alice"));
    }

    [Fact]
    public async Task AJobThatHasPrintedCannotBeDeleted()
    {
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;

        var response = await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already printed", (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error);
        Assert.Equal(6, await Used("alice"));
    }

    [Fact]
    public async Task NobodyDeletesSomeoneElsesJob()
    {
        _server.Printer.NewJobState = IppJobState.Pending;
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;
        using var bob = await _server.SignInAsync("bob");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/me/jobs/{job.Id}")).StatusCode);
        Assert.Equal(6, await Used("alice"));
    }

    [Fact]
    public async Task ThePagesGivenBackLeaveTheUsageReport()
    {
        _server.Printer.NewJobState = IppJobState.Processing;
        _server.Printer.ImpressionsCompleted = 1;
        var (alice, job) = await ReleaseOneAsync();
        using var _ = alice;
        (await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}")).EnsureSuccessStatusCode();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var report = _server.Service<Reports.UsageReport>().Run(today.AddDays(-1), today.AddDays(1), ReportGrouping.User, TimeZoneInfo.Local);

        Assert.Equal(1, report.Pages);
    }

    [Fact]
    public async Task TheQueuesAPersonMayPrintToAreListed()
    {
        using var alice = await _server.SignInAsync("alice");

        var queues = await alice.GetFromJsonAsync<List<QueueDto>>("/api/v1/queues", TapQueueJson.Options);

        Assert.Equal(TestServer.QueueName, Assert.Single(queues!).Name);
    }
}
