using System.Net.Http.Json;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Released jobs are followed at the printer until it says how they ended.</summary>
public sealed class PrintTrackingTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;
    private HttpClient _alice = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _alice = await _server.SignInAsync("alice");
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(20, QuotaPeriod.Month), TapQueueJson.Options)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _alice.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<int> Used()
    {
        var quota = await _server.Admin.GetFromJsonAsync<UserQuotaDto>("/api/v1/admin/users/alice/quota", TapQueueJson.Options);
        return Assert.Single(quota!.Applies).Used;
    }

    private async Task<JobDto> OnlyJob() =>
        Assert.Single((await _alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!);

    private string[] Documents() => Directory.GetFiles(Path.Combine(_server.Service<ServerConfig>().Server.DataDir, "spool"));

    /// <summary>Prints three pages twice over and releases them to a printer that leaves the job in <paramref name="state"/>.</summary>
    private async Task<int> ReleaseAsync(int state)
    {
        _server.Printer.NewJobState = state;
        await _server.PrintAsync("report", ThreePages, copies: 2);
        (await _alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options)).EnsureSuccessStatusCode();
        return _server.Printer.JobStates.Keys.Max();
    }

    private Task CheckAsync() => _server.Service<PrintTracker>().CheckAsync(CancellationToken.None);

    [Fact]
    public async Task AJobIsPrintingUntilThePrinterSaysItFinished()
    {
        var atPrinter = await ReleaseAsync(IppJobState.Processing);

        await CheckAsync();
        var job = await OnlyJob();
        Assert.Equal(JobStatus.Released, job.Status);
        Assert.Equal(PrintState.Printing, job.PrintState);
        Assert.Single(Documents());

        _server.Printer.JobStates[atPrinter] = IppJobState.Completed;
        _server.Printer.ImpressionsCompleted = 6;
        await CheckAsync();

        job = await OnlyJob();
        Assert.Equal(PrintState.Printed, job.PrintState);
        Assert.Equal(6, job.PrintedPages);
        Assert.Empty(Documents());
        Assert.Equal(6, await Used());
    }

    [Fact]
    public async Task AJobThePrinterAbortsIsHeldAgainAndCountsForNothing()
    {
        var atPrinter = await ReleaseAsync(IppJobState.Processing);
        Assert.Equal(6, await Used());

        _server.Printer.JobStates[atPrinter] = IppJobState.Aborted;
        await CheckAsync();

        var job = await OnlyJob();
        Assert.Equal(JobStatus.Held, job.Status);
        Assert.Contains("gave up on this job", job.Error);
        Assert.Null(job.ReleasedAt);
        Assert.Equal(0, await Used());

        // The document is still there, so it can go to a printer again.
        _server.Printer.NewJobState = IppJobState.Completed;
        (await _alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(2, _server.Printer.Jobs.Count);
        Assert.Equal(PrintState.Printed, (await OnlyJob()).PrintState);
        Assert.Equal(6, await Used());
        var events = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=job", TapQueueJson.Options);
        Assert.Contains(events!, e => e.Message.Contains("gave up on alice's \"report\""));
    }

    [Fact]
    public async Task AJobStoppedAtThePrinterGivesBackWhatDidNotPrint()
    {
        var atPrinter = await ReleaseAsync(IppJobState.Processing);

        _server.Printer.JobStates[atPrinter] = IppJobState.Canceled;
        _server.Printer.ImpressionsCompleted = 2;
        await CheckAsync();

        var job = await OnlyJob();
        Assert.Equal(JobStatus.Released, job.Status);
        Assert.Equal(PrintState.Stopped, job.PrintState);
        Assert.Equal(4, job.RefundedPages);
        Assert.Equal(2, await Used());
        Assert.Empty(Documents());
    }

    [Fact]
    public async Task AJobStoppedAtThePrinterBeforeItPrintedIsCanceled()
    {
        var atPrinter = await ReleaseAsync(IppJobState.Pending);

        _server.Printer.JobStates[atPrinter] = IppJobState.Canceled;
        _server.Printer.ImpressionsCompleted = 0;
        await CheckAsync();

        Assert.Equal(JobStatus.Canceled, (await OnlyJob()).Status);
        Assert.Equal(0, await Used());
        Assert.Empty(Documents());
    }

    [Fact]
    public async Task AJobThePrinterForgotStaysReleased()
    {
        var atPrinter = await ReleaseAsync(IppJobState.Processing);

        _server.Printer.JobStates.TryRemove(atPrinter, out _);
        await CheckAsync();

        var job = await OnlyJob();
        Assert.Equal(JobStatus.Released, job.Status);
        Assert.Equal(PrintState.Unconfirmed, job.PrintState);
        Assert.Equal(6, await Used());
        Assert.Empty(Documents());
    }

    [Fact]
    public async Task AJobStoppedFromTheTrayIsNoLongerFollowed()
    {
        await ReleaseAsync(IppJobState.Processing);
        _server.Printer.ImpressionsCompleted = 2;

        var result = await TestServer.ReadAsync<CancelJobResponse>(await _alice.DeleteAsync($"/api/v1/me/jobs/{(await OnlyJob()).Id}"));
        await CheckAsync();

        Assert.Equal(4, result.RefundedPages);
        var job = await OnlyJob();
        Assert.Equal(PrintState.Stopped, job.PrintState);
        Assert.Equal(4, job.RefundedPages);
        Assert.Empty(Documents());
    }
}
