using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// What a queue insists on (both sides of the paper, black and white, no more than so many pages a
/// job), and queues that print straight to a printer instead of holding their jobs.
/// </summary>
public sealed class QueueRulesTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        // A printer that can do colour and both sides, so the queue's own rules are what decide.
        _server.Printer.Color = true;
        _server.Printer.Sides = ["one-sided", "two-sided-long-edge", "two-sided-short-edge"];
        var printers = _server.Service<Printers.PrinterRegistry>();
        await printers.ProbeAsync(printers.Find(TestServer.PrinterId)!, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<QueueAdminDto> Queue(UpdateQueueRequest request) =>
        await TestServer.ReadAsync<QueueAdminDto>(await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/queues/{TestServer.QueueId}", request, TapQueueJson.Options));

    private async Task<IppMessage> QueueAttributes()
    {
        using var ipp = new IppClient(false, TimeSpan.FromSeconds(10));
        return await ipp.SendAsync(_server.QueueUri, IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), _server.QueueUri));
    }

    private static List<string?> Keywords(IppMessage message, string name) =>
        message.Find(IppTag.PrinterAttributes, name)!.Values.Select(v => v.AsString()).ToList();

    private static Task<HttpResponseMessage> Release(HttpClient user) =>
        user.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options);

    private static async Task<List<JobDto>> Jobs(HttpClient user) =>
        (await user.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!;

    private string? Sent(string attribute) =>
        Assert.Single(_server.Printer.Jobs).Request.Find(IppTag.JobAttributes, attribute)?.First?.AsString();

    [Fact]
    public async Task AQueueThatOnlyPrintsTwoSidedOffersNothingElseAndOverridesAJobThatAsks()
    {
        using var alice = await _server.SignInAsync("alice");
        var queue = await Queue(new UpdateQueueRequest(Duplex: true, SidesPolicy: SidesPolicy.ForceTwoSided));
        Assert.Equal(SidesPolicy.ForceTwoSided, queue.SidesPolicy);

        var offered = await QueueAttributes();
        Assert.Equal(["two-sided-long-edge", "two-sided-short-edge"], Keywords(offered, "sides-supported"));
        Assert.Equal(["two-sided-long-edge"], Keywords(offered, "sides-default"));

        // Something that ignores what the queue offers and asks for one side still prints on two.
        await _server.PrintAsync("report", ThreePages, jobAttributes: a => a.Add("sides", IppValue.Keyword("one-sided")));
        Assert.True(Assert.Single(await Jobs(alice)).Duplex);
        (await Release(alice)).EnsureSuccessStatusCode();
        Assert.Equal("two-sided-long-edge", Sent("sides"));
    }

    [Fact]
    public async Task AQueueCanPreferTwoSidedAndStillLetPeopleChoose()
    {
        using var alice = await _server.SignInAsync("alice");
        await Queue(new UpdateQueueRequest(Duplex: true, SidesPolicy: SidesPolicy.DefaultTwoSided));

        var offered = await QueueAttributes();
        Assert.Contains("one-sided", Keywords(offered, "sides-supported"));
        Assert.Equal(["two-sided-long-edge"], Keywords(offered, "sides-default"));

        await _server.PrintAsync("unsaid", ThreePages);
        await _server.PrintAsync("one side please", ThreePages, jobAttributes: a => a.Add("sides", IppValue.Keyword("one-sided")));
        var jobs = await Jobs(alice);
        Assert.True(jobs.Single(j => j.Name == "unsaid").Duplex);
        Assert.False(jobs.Single(j => j.Name == "one side please").Duplex);
    }

    [Fact]
    public async Task AQueueWithoutColourPrintsBlackAndWhiteWhateverTheJobAsks()
    {
        using var alice = await _server.SignInAsync("alice");
        Assert.Equal(["monochrome"], Keywords(await QueueAttributes(), "print-color-mode-supported"));

        await _server.PrintAsync("brochure", ThreePages, jobAttributes: a => a.Add("print-color-mode", IppValue.Keyword("color")));
        Assert.False(Assert.Single(await Jobs(alice)).Color);
        (await Release(alice)).EnsureSuccessStatusCode();
        Assert.Equal("monochrome", Sent("print-color-mode"));
    }

    [Fact]
    public async Task AQueueThatOffersColourLeavesTheJobAsItWas()
    {
        using var alice = await _server.SignInAsync("alice");
        await Queue(new UpdateQueueRequest(Color: true, Duplex: true));

        await _server.PrintAsync("brochure", ThreePages, jobAttributes: a => a.Add("print-color-mode", IppValue.Keyword("color")));
        Assert.True(Assert.Single(await Jobs(alice)).Color);
        (await Release(alice)).EnsureSuccessStatusCode();
        Assert.Equal("color", Sent("print-color-mode"));
        Assert.Null(Sent("sides"));
    }

    [Fact]
    public async Task AJobLongerThanTheQueueTakesIsRefusedAndThePersonIsTold()
    {
        using var alice = await _server.SignInAsync("alice");
        Assert.Equal(5, (await Queue(new UpdateQueueRequest(MaxPages: 5))).MaxPages);

        var tooLong = await _server.PrintAsync("thesis", ThreePages, copies: 2);
        Assert.Equal(IppStatus.ClientErrorNotPossible, tooLong.Code);
        Assert.Contains("has 6 pages", tooLong.OperationString("status-message"));
        Assert.Equal(JobStatus.Canceled, Assert.Single(await Jobs(alice)).Status);
        var notice = Assert.Single((await alice.GetFromJsonAsync<List<UserNoticeDto>>("/api/v1/me/notices", TapQueueJson.Options))!);
        Assert.Contains("takes at most 5 in one job", notice.Message);

        var fits = await _server.PrintAsync("memo", ThreePages);
        Assert.Equal(IppStatus.Ok, fits.Code);
        Assert.Contains(await Jobs(alice), j => j.Name == "memo" && j.Status == JobStatus.Held);
    }

    [Fact]
    public async Task ADirectQueuePrintsStraightAwayAndStillCountsAndCharges()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/accounting", new UpdateAccountingRequest(AccountingMode.Track, PriceMono: 50), TapQueueJson.Options);
        var queue = await Queue(new UpdateQueueRequest(DirectPrinterId: TestServer.PrinterId));
        Assert.Equal(TestServer.PrinterId, queue.DirectPrinterId);

        await _server.PrintAsync("memo", ThreePages);

        // Nobody released it: it went by itself, as alice.
        for (var i = 0; i < 100 && _server.Printer.Jobs.IsEmpty; i++)
            await Task.Delay(50);
        Assert.Equal("alice", Assert.Single(_server.Printer.Jobs).User);
        JobDto job = null!;
        for (var i = 0; i < 100 && (job = Assert.Single(await Jobs(alice))).Status != JobStatus.Released; i++)
            await Task.Delay(50);
        Assert.Equal(JobStatus.Released, job.Status);
        Assert.Equal(TestServer.PrinterId, job.ReleasedPrinterId);
        Assert.Equal(150, job.Cost);

        // The printer can't be removed from under it, and it can be made a hold queue again.
        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin.DeleteAsync($"/api/v1/admin/printers/{TestServer.PrinterId}")).StatusCode);
        Assert.Null((await Queue(new UpdateQueueRequest(DirectPrinterId: ""))).DirectPrinterId);
        await _server.PrintAsync("held again", ThreePages);
        await Task.Delay(300);
        Assert.Contains(await Jobs(alice), j => j.Name == "held again" && j.Status == JobStatus.Held);
    }

    [Fact]
    public async Task ADirectJobThatCantPrintStaysHeldAndThePersonIsTold()
    {
        using var alice = await _server.SignInAsync("alice");
        await Queue(new UpdateQueueRequest(DirectPrinterId: TestServer.PrinterId));
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/quota", new QuotaDto(0, QuotaPeriod.Month), TapQueueJson.Options)).EnsureSuccessStatusCode();

        await _server.PrintAsync("memo", ThreePages);

        List<UserNoticeDto> notices = [];
        for (var i = 0; i < 100 && notices.Count == 0; i++)
        {
            await Task.Delay(50);
            notices = (await alice.GetFromJsonAsync<List<UserNoticeDto>>("/api/v1/me/notices", TapQueueJson.Options))!;
        }
        Assert.Contains("didn't print at Office printer", Assert.Single(notices).Message);
        Assert.Equal(JobStatus.Held, Assert.Single(await Jobs(alice)).Status);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task RulesThatMakeNoSenseAreRefused()
    {
        Task<HttpResponseMessage> Patch(UpdateQueueRequest request) =>
            _server.Admin.PatchAsJsonAsync($"/api/v1/admin/queues/{TestServer.QueueId}", request, TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(SidesPolicy: "sometimes"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(MaxPages: -1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(DirectPrinterId: "nowhere"))).StatusCode);
        var made = await _server.Admin.PostAsJsonAsync("/api/v1/admin/queues",
            new CreateQueueRequest("lab", "Lab printer", Duplex: true, SidesPolicy: SidesPolicy.ForceTwoSided, MaxPages: 50, DirectPrinterId: "OFFICE"), TapQueueJson.Options);
        var lab = await TestServer.ReadAsync<QueueAdminDto>(made);
        Assert.Equal((SidesPolicy.ForceTwoSided, 50, TestServer.PrinterId), (lab.SidesPolicy, lab.MaxPages, lab.DirectPrinterId));
    }
}
