using TapQueue.Server.Data;

namespace TapQueue.Server.Tests;

public sealed class PrinterAndQueueStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;
    private readonly PrinterStore _printers;
    private readonly QueueStore _queues;
    private readonly StationStore _stations;

    public PrinterAndQueueStoreTests()
    {
        var db = new Database(Path.Combine(_dir, "test.db"));
        db.Migrate();
        _printers = new PrinterStore(db);
        _queues = new QueueStore(db);
        _stations = new StationStore(db);
    }

    [Fact]
    public void PrintersCanBeAddedChangedAndRemoved()
    {
        _printers.Create(new PrinterRecord("office", "Office", "", "ipp://192.0.2.10/ipp/print", false));

        var moved = _printers.Update(new PrinterRecord("OFFICE", "Office", "2nd floor", "ipps://192.0.2.11/ipp/print", true));

        Assert.Equal(new PrinterRecord("office", "Office", "2nd floor", "ipps://192.0.2.11/ipp/print", true), moved);
        Assert.Equal(moved, Assert.Single(_printers.List()));
        Assert.Null(_printers.Update(new PrinterRecord("nope", "", "", "ipp://x", false)));
        Assert.True(_printers.Delete("office"));
        Assert.Empty(_printers.List());
    }

    [Fact]
    public void KnowsWhichStationsUseAPrinter()
    {
        _printers.Create(new PrinterRecord("office", "Office", "", "ipp://192.0.2.10/ipp/print", false));
        _stations.Create("lobby", "office", "hash");

        Assert.Equal(["lobby"], _printers.StationsUsing("Office"));
    }

    [Fact]
    public void QueuesCanBeAddedChangedAndRemoved()
    {
        var queue = _queues.Create(new QueueRecord("secure", "Secure Print", QueueRecord.DefaultDescription, "", false, false, QueueRecord.Letter));

        var renamed = _queues.Update(queue with { Name = "Follow-Me", Duplex = true });

        Assert.Equal("Follow-Me", _queues.Get("SECURE")?.Name);
        Assert.True(renamed?.Duplex);
        Assert.True(_queues.Delete("secure"));
        Assert.Null(_queues.Get("secure"));
    }

    [Theory]
    [InlineData("ipp://10.0.0.180/ipp/print", true)]
    [InlineData("ipps://printer.local:443/ipp/print", true)]
    [InlineData("lpd://10.0.0.180/queue", false)]
    [InlineData("10.0.0.180", false)]
    public void ChecksPrinterUris(string uri, bool valid) => Assert.Equal(valid, PrinterRecord.IsValidUri(uri));

    [Theory]
    [InlineData("office-2", true)]
    [InlineData("a_b", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("slash/y", false)]
    public void ChecksIds(string id, bool valid) => Assert.Equal(valid, Ids.IsValid(id));

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
