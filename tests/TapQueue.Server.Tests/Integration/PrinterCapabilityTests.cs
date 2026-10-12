using System.Net.Http.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>A queue offers PCs what the printers behind it can do, and a release checks the job against the printer.</summary>
public sealed class PrinterCapabilityTests : IAsyncLifetime
{
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        (await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/queues/{TestServer.QueueId}", new UpdateQueueRequest(Color: true, Duplex: true), TapQueueJson.Options))
            .EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task ProbeAsync()
    {
        var printers = _server.Service<PrinterRegistry>();
        await printers.ProbeAsync(printers.Find(TestServer.PrinterId)!, CancellationToken.None);
    }

    private async Task<IppMessage> QueueAttributesAsync()
    {
        using var ipp = new IppClient(tlsSkipVerify: false, TimeSpan.FromSeconds(30));
        return await ipp.SendAsync(_server.QueueUri, IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), _server.QueueUri));
    }

    private static string[] Keywords(IppMessage response, string name) =>
        response.Find(IppTag.PrinterAttributes, name)!.Values.Select(v => v.AsString()!).ToArray();

    private async Task<JobDto> OnlyJob(HttpClient user) =>
        Assert.Single((await user.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs?all=true", TapQueueJson.Options))!);

    [Fact]
    public async Task AQueueOffersWhatItAlwaysDidUntilAPrinterSaysWhatItCanDo()
    {
        await ProbeAsync();

        var queue = await QueueAttributesAsync();

        Assert.Equal(["na_letter_8.5x11in", "na_legal_8.5x14in", "na_executive_7.25x10.5in", "iso_a4_210x297mm"], Keywords(queue, "media-supported"));
        Assert.Contains("two-sided-long-edge", Keywords(queue, "sides-supported"));
        Assert.Contains("color", Keywords(queue, "print-color-mode-supported"));
    }

    [Fact]
    public async Task AQueueOffersThePrintersPaperSizesAndOnlyWhatSomePrinterCanDo()
    {
        _server.Printer.Media = ["iso_a4_210x297mm", "iso_a3_297x420mm", "custom_min_76x127mm", "custom_max_297x432mm", "something-odd"];
        _server.Printer.Color = false;
        _server.Printer.Sides = ["one-sided"];
        _server.Printer.Formats = ["image/pwg-raster", "image/urf", "application/octet-stream"];
        await ProbeAsync();

        var queue = await QueueAttributesAsync();

        Assert.Equal(["iso_a4_210x297mm", "iso_a3_297x420mm"], Keywords(queue, "media-supported"));
        Assert.Equal("iso_a4_210x297mm", Keywords(queue, "media-default").Single());
        Assert.Equal(["one-sided"], Keywords(queue, "sides-supported"));
        Assert.Equal(["monochrome"], Keywords(queue, "print-color-mode-supported"));
        // No printer takes PDF, so PCs aren't told to send it.
        Assert.Equal(["image/pwg-raster", "image/urf", "application/octet-stream"], Keywords(queue, "document-format-supported"));
        Assert.Equal("image/pwg-raster", Keywords(queue, "document-format-default").Single());
    }

    [Fact]
    public async Task AJobIsNotReleasedToAPrinterWithoutItsPaperSize()
    {
        _server.Printer.Media = ["iso_a4_210x297mm", "na_letter_8.5x11in"];
        await ProbeAsync();
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("poster", ThreePages, jobAttributes: job => job.Add("media", IppValue.Keyword("iso_a3_297x420mm")));

        var released = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        var result = Assert.Single(released.Results);
        Assert.False(result.Success);
        Assert.Equal("Office printer doesn't take A3 paper. Release this job at a printer that does.", result.Error);
        Assert.Empty(_server.Printer.Jobs);
        Assert.Equal(JobStatus.Held, (await OnlyJob(alice)).Status);
    }

    [Fact]
    public async Task APaperSizeInsideThePrintersCustomRangeIsReleased()
    {
        _server.Printer.Media = ["iso_a4_210x297mm", "custom_min_76x127mm", "custom_max_297x432mm"];
        await ProbeAsync();
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("letter", ThreePages, jobAttributes: job => job.Add("media-col", IppValue.Collection(new IppCollection()
            .Add("media-size", IppValue.Collection(new IppCollection().Add("x-dimension", IppValue.Integer(21590)).Add("y-dimension", IppValue.Integer(27940)))))));

        var released = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        Assert.True(Assert.Single(released.Results).Success);
    }

    [Fact]
    public async Task AColourTwoSidedJobAtAPrinterThatCanDoNeitherPrintsAndIsRecordedAsItCameOut()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("slides", ThreePages, jobAttributes: job => job
            .Add("print-color-mode", IppValue.Keyword("color")).Add("sides", IppValue.Keyword("two-sided-long-edge")));
        Assert.True((await OnlyJob(alice)).Color);
        _server.Printer.Color = false;
        _server.Printer.Sides = ["one-sided"];
        await ProbeAsync();

        var released = await TestServer.ReadAsync<ReleaseResponse>(
            await alice.PostAsJsonAsync("/api/v1/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        var result = Assert.Single(released.Results);
        Assert.True(result.Success);
        Assert.Equal("Office printer can't print in colour or on both sides, so this prints in black and white on one side.", result.Note);
        var job = await OnlyJob(alice);
        Assert.False(job.Color);
        Assert.False(job.Duplex);
    }

    [Theory]
    [InlineData("iso_a4_210x297mm", 21000, 29700)]
    [InlineData("na_letter_8.5x11in", 21590, 27940)]
    [InlineData("na_executive_7.25x10.5in", 18415, 26670)]
    [InlineData("custom_max_297x432mm", 29700, 43200)]
    public void PwgMediaNamesCarryTheirSize(string name, int x, int y) =>
        Assert.Equal(new MediaSize(name, x, y), MediaSize.Parse(name));

    [Theory]
    [InlineData("a4")]
    [InlineData("iso_a4_210x297")]
    [InlineData("iso_a4_x297mm")]
    [InlineData("roll_max_0x0mm")]
    public void OtherNamesAreNotSizes(string name) => Assert.Null(MediaSize.Parse(name));
}
