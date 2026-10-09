using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Printers and queues are managed through the admin API and take effect without a restart.</summary>
public sealed class AdminPrinterAndQueueTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<HttpResponseMessage> Patch<T>(string path, T body) =>
        _server.Admin.PatchAsJsonAsync(path, body, TapQueueJson.Options);

    [Fact]
    public async Task AddedPrinterIsProbedRightAway()
    {
        var printers = await _server.Admin.GetFromJsonAsync<List<PrinterAdminDto>>("/api/v1/admin/printers", TapQueueJson.Options);

        var office = Assert.Single(printers ?? []);
        Assert.True(office.Printer.Online);
        Assert.Equal("Fake LaserJet", office.Printer.MakeAndModel);
        Assert.Equal(_server.Printer.Uri, office.Uri);
    }

    [Fact]
    public async Task PrinterCanBeRenamedAndRepointed()
    {
        var updated = await TestServer.ReadAsync<PrinterAdminDto>(await Patch($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(Uri: "ipp://127.0.0.1:1/ipp/print", Name: "Front desk")));

        Assert.Equal("Front desk", updated.Printer.Name);
        Assert.False(updated.Printer.Online);
        using var alice = await _server.SignInAsync("alice");
        var seen = Assert.Single(await alice.GetFromJsonAsync<List<PrinterDto>>("/api/v1/printers", TapQueueJson.Options) ?? []);
        Assert.Equal("Front desk", seen.Name);
    }

    [Theory]
    [InlineData("bad id", "ipp://192.0.2.1/ipp/print")]
    [InlineData("ok", "lpd://192.0.2.1/q")]
    [InlineData(TestServer.PrinterId, "ipp://192.0.2.1/ipp/print")]
    public async Task RejectsBadOrDuplicatePrinters(string id, string uri)
    {
        var response = await _server.Admin.PostAsJsonAsync("/api/v1/admin/printers", new CreatePrinterRequest(id, uri), TapQueueJson.Options);

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task PrinterInUseByAStationCantBeRemovedUntilTheStationMoves()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options);
        await TestServer.ReadAsync<PrinterAdminDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/printers",
            new CreatePrinterRequest("spare", "ipp://127.0.0.1:1/ipp/print"), TapQueueJson.Options));

        var refused = await _server.Admin.DeleteAsync($"/api/v1/admin/printers/{TestServer.PrinterId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("lobby", await refused.Content.ReadAsStringAsync());

        var moved = await TestServer.ReadAsync<StationDto>(await Patch("/api/v1/admin/stations/lobby", new UpdateStationRequest("spare")));
        Assert.Equal("spare", moved.PrinterId);
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync($"/api/v1/admin/printers/{TestServer.PrinterId}")).StatusCode);
    }

    [Fact]
    public async Task RenamedQueueIsWhatPrintClientsSee()
    {
        await TestServer.ReadAsync<QueueAdminDto>(await Patch($"/api/v1/admin/queues/{TestServer.QueueId}",
            new UpdateQueueRequest(Name: "Follow-Me Print", Duplex: true)));

        var request = IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, 1, _server.QueueUri);
        using var ipp = new IppClient(false, TimeSpan.FromSeconds(10));
        var attributes = await ipp.SendAsync(_server.QueueUri, request);

        Assert.Equal("Follow-Me Print", attributes.Find(IppTag.PrinterAttributes, "printer-name")?.First?.AsString());
        Assert.Contains("two-sided-long-edge", attributes.Find(IppTag.PrinterAttributes, "sides-supported")!.Values.Select(v => v.AsString()));
    }

    [Fact]
    public async Task RemovedQueueStopsAcceptingJobs()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync($"/api/v1/admin/queues/{TestServer.QueueId}")).StatusCode);

        await Assert.ThrowsAsync<HttpRequestException>(() => _server.PrintAsync("Too late", [1, 2, 3]));
        Assert.Empty(await _server.Admin.GetFromJsonAsync<List<QueueAdminDto>>("/api/v1/admin/queues", TapQueueJson.Options) ?? []);
    }

    [Fact]
    public async Task EachQueueNeedsItsOwnName()
    {
        // PCs know their printers by name: two queues with one name can't both be added to a PC.
        var same = await _server.Admin.PostAsJsonAsync("/api/v1/admin/queues",
            new CreateQueueRequest("second", TestServer.QueueName.ToUpperInvariant()), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Contains(TestServer.QueueId, await same.Content.ReadAsStringAsync());

        await TestServer.ReadAsync<QueueAdminDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/queues",
            new CreateQueueRequest("second", "Color"), TapQueueJson.Options));
        var renamed = await Patch("/api/v1/admin/queues/second", new UpdateQueueRequest(Name: $" {TestServer.QueueName} "));
        Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);

        // A queue keeps its own name (in any case), and other changes to it still go through.
        var kept = await TestServer.ReadAsync<QueueAdminDto>(await Patch("/api/v1/admin/queues/second", new UpdateQueueRequest(Name: "COLOR", Duplex: true)));
        Assert.Equal("COLOR", kept.Name);
        Assert.Equal(2, (await _server.Admin.GetFromJsonAsync<List<QueueAdminDto>>("/api/v1/admin/queues", TapQueueJson.Options))!.Count);
    }

    [Theory]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\tbed")]
    public async Task QueueNamesAPcCouldntUseAreRefused(string name)
    {
        var response = await _server.Admin.PostAsJsonAsync("/api/v1/admin/queues", new CreateQueueRequest("second", name), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tooLong = await Patch($"/api/v1/admin/queues/{TestServer.QueueId}", new UpdateQueueRequest(Name: new string('x', 128)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task QueueNeedsAName()
    {
        var response = await _server.Admin.PostAsJsonAsync("/api/v1/admin/queues", new CreateQueueRequest("second", " "), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
