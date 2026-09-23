using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>End to end: print to the hold queue over IPP, then release from the tray client or the admin CLI.</summary>
public sealed class PrintAndReleaseTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task PrintedJobIsHeldForTheSignedInUser()
    {
        using var alice = await _server.SignInAsync("alice");

        var response = await _server.PrintAsync("Quarterly report", Pdf, requestingUser: "CORP\\alice");

        Assert.Equal(IppStatus.Ok, response.Code);
        // Windows should consider the job done as soon as it's been delivered.
        Assert.Equal(IppJobState.Completed, response.Find(IppTag.JobAttributes, "job-state")?.First?.AsInt());
        var job = Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
        Assert.Equal("Quarterly report", job.Name);
        Assert.Equal("held", job.Status);
        Assert.Equal("alice", job.Owner);
        Assert.Equal(Pdf.Length, job.SizeBytes);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task HeldJobsKnowTheirPageCount()
    {
        using var alice = await _server.SignInAsync("alice");
        var threePages = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));

        await _server.PrintAsync("Three pages", threePages);
        await _server.PrintAsync("Not really a PDF", Pdf);

        var jobs = await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? [];
        Assert.Equal(3, jobs.Single(j => j.Name == "Three pages").Pages);
        Assert.Null(jobs.Single(j => j.Name == "Not really a PDF").Pages);
    }

    [Fact]
    public async Task ReleaseSendsTheDocumentAndPrintOptionsToThePrinter()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf, copies: 3);

        var result = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        Assert.True(Assert.Single(result.Results).Success);
        var printed = Assert.Single(_server.Printer.Jobs);
        Assert.Equal(Pdf, printed.Document);
        Assert.Equal("Report", printed.Name);
        Assert.Equal("alice", printed.User);
        Assert.Equal(3, printed.Request.Find(IppTag.JobAttributes, "copies")?.First?.AsInt());
        Assert.Empty(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
    }

    [Fact]
    public async Task JobsAreReleasedOldestFirst()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("first", Pdf);
        await _server.PrintAsync("second", Pdf);

        await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options);

        Assert.Equal(["first", "second"], _server.Printer.Jobs.Select(j => j.Name));
    }

    [Fact]
    public async Task UsersOnlySeeAndReleaseTheirOwnJobs()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("alice's", Pdf);
        var aliceJob = Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
        using var bob = await _server.SignInAsync("bob");

        Assert.Empty(await bob.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
        var result = await TestServer.ReadAsync<ReleaseResponse>(
            await bob.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId, [aliceJob.Id]), TapQueueJson.Options));
        Assert.False(Assert.Single(result.Results).Success);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/me/jobs/{aliceJob.Id}")).StatusCode);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task FailedReleaseLeavesTheJobHeldWithTheError()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf);
        _server.Printer.PrintJobStatus = IppStatus.ServerErrorInternal;

        var result = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        Assert.False(Assert.Single(result.Results).Success);
        var job = Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
        Assert.Equal("held", job.Status);
        Assert.Contains("Out of paper", job.Error);

        _server.Printer.PrintJobStatus = IppStatus.Ok;
        result = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));
        Assert.True(Assert.Single(result.Results).Success);
    }

    [Fact]
    public async Task ReleaseWaitsOutABusyPrinter()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf);
        _server.Printer.BusyCount = 1;

        var result = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        Assert.True(Assert.Single(result.Results).Success);
        Assert.Single(_server.Printer.Jobs);
    }

    [Fact]
    public async Task CanceledJobIsGoneAndCantBeReleased()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Oops", Pdf);
        var job = Assert.Single(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/me/jobs/{job.Id}")).StatusCode);

        Assert.Empty(await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options) ?? []);
        await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task JobWithNoSignedInClientHasNoOwner()
    {
        await _server.PrintAsync("Anonymous", Pdf, requestingUser: "mallory");

        var job = Assert.Single(await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options) ?? []);
        Assert.Null(job.Owner);
        Assert.Equal("mallory", job.ClaimedUser);
    }

    [Fact]
    public async Task AdminCanReleaseOnAUsersBehalf()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("Report", Pdf);

        var result = await TestServer.ReadAsync<ReleaseResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/release",
            new AdminReleaseRequest("alice", TestServer.PrinterId), TapQueueJson.Options));

        Assert.True(Assert.Single(result.Results).Success);
        Assert.Single(_server.Printer.Jobs);
    }

    [Fact]
    public async Task ApisRejectMissingOrWrongTokens()
    {
        using var anonymous = _server.NewClient();
        using var wrong = _server.NewClient("not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/me/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest("1234"))).StatusCode);
        var signIn = await anonymous.PostAsJsonAsync("/api/v1/client/session", new ClientSessionRequest("nobody", "guess", null, null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }
}
