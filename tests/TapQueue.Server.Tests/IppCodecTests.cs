using TapQueue.Server.Config;
using TapQueue.Server.Ipp;

namespace TapQueue.Server.Tests;

public class IppCodecTests
{
    [Fact]
    public async Task RoundTripsEveryValueKind()
    {
        var message = IppMessage.CreateRequest(IppOperation.PrintJob, 42, "ipp://printer/ipp/print");
        message.Group(IppTag.OperationAttributes)
            .Add("requesting-user-name", IppValue.Name("jake"))
            .Add("document-format", IppValue.MimeType("application/pdf"));
        message.Group(IppTag.JobAttributes)
            .Add("copies", IppValue.Integer(3))
            .Add("sides", IppValue.Keyword("one-sided"))
            .Add("print-quality", IppValue.Enum(4))
            .Add("some-flag", IppValue.Boolean(true))
            .Add("copies-supported", IppValue.Range(1, 999))
            .Add("printer-resolution", IppValue.Resolution(600, 600))
            .Add("media-col", IppValue.Collection(new IppCollection()
                .Add("media-size", IppValue.Collection(new IppCollection()
                    .Add("x-dimension", IppValue.Integer(21590))
                    .Add("y-dimension", IppValue.Integer(27940))))
                .Add("media-type", IppValue.Keyword("stationery"))))
            .Add("multi", IppValue.Keyword("a"), IppValue.Keyword("b"));

        var bytes = IppWriter.Encode(message);
        var decoded = await IppReader.ReadAsync(new MemoryStream(bytes));

        Assert.Equal(IppOperation.PrintJob, decoded.Code);
        Assert.Equal(42, decoded.RequestId);
        Assert.Equal("jake", decoded.OperationString("requesting-user-name"));
        Assert.Equal("application/pdf", decoded.OperationString("document-format"));
        Assert.Equal(3, decoded.Find(IppTag.JobAttributes, "copies")!.First!.Value.AsInt());
        Assert.Equal(4, decoded.Find(IppTag.JobAttributes, "print-quality")!.First!.Value.AsInt());
        Assert.True(decoded.Find(IppTag.JobAttributes, "some-flag")!.First!.Value.AsBool());
        Assert.Equal(new IppRange(1, 999), decoded.Find(IppTag.JobAttributes, "copies-supported")!.First!.Value.Value);
        Assert.Equal(new IppResolution(600, 600, 3), decoded.Find(IppTag.JobAttributes, "printer-resolution")!.First!.Value.Value);
        Assert.Equal(["a", "b"], decoded.Find(IppTag.JobAttributes, "multi")!.Values.Select(v => v.AsString()));

        var mediaCol = (IppCollection)decoded.Find(IppTag.JobAttributes, "media-col")!.First!.Value.Value!;
        var size = (IppCollection)mediaCol.Single(m => m.Name == "media-size").First!.Value.Value!;
        Assert.Equal(21590, size.Single(m => m.Name == "x-dimension").First!.Value.AsInt());
        Assert.Equal("stationery", mediaCol.Single(m => m.Name == "media-type").First!.Value.AsString());

        // Re-encoding what we decoded gives identical bytes.
        Assert.Equal(bytes, IppWriter.Encode(decoded));
    }

    [Fact]
    public async Task LeavesDocumentDataUnread()
    {
        var header = IppWriter.Encode(IppMessage.CreateRequest(IppOperation.PrintJob, 1, "ipp://x/ipp/print"));
        var document = "%PDF-1.7 fake"u8.ToArray();
        var stream = new MemoryStream([.. header, .. document]);

        await IppReader.ReadAsync(stream);

        var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal(document, rest.ToArray());
    }

    [Fact]
    public async Task RejectsTruncatedMessages()
    {
        var bytes = IppWriter.Encode(IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, 1, "ipp://x/ipp/print"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => IppReader.ReadAsync(new MemoryStream(bytes[..^5])));
    }

    [Fact]
    public void QueueAdvertisesItsNameAndIppEverywhereBasics()
    {
        var queue = new QueueConfig { Id = "secure", Name = "TapQueue Secure Print" };
        var attrs = QueueAttributes.Build(queue, "ipp://server:8631/ipp/secure", "http://server:8631", 10, 0, requested: null);

        Assert.Equal("TapQueue Secure Print", attrs.Find("printer-name")!.First!.Value.AsString());
        string[] required =
        [
            "printer-uri-supported", "uri-security-supported", "uri-authentication-supported", "printer-state",
            "printer-state-reasons", "ipp-versions-supported", "operations-supported", "charset-configured",
            "charset-supported", "natural-language-configured", "generated-natural-language-supported",
            "document-format-default", "document-format-supported", "printer-is-accepting-jobs", "queued-job-count",
            "pdl-override-supported", "printer-up-time", "compression-supported", "media-col-database",
            "media-col-default", "media-col-ready", "media-default", "media-ready", "media-supported",
            "printer-resolution-supported", "pwg-raster-document-type-supported", "urf-supported", "printer-uuid",
        ];
        Assert.All(required, name => Assert.NotNull(attrs.Find(name)));
        Assert.Equal(["one-sided"], attrs.Find("sides-supported")!.Values.Select(v => v.AsString()));
    }

    [Fact]
    public void RequestedAttributesFilterTheResponse()
    {
        var queue = new QueueConfig { Id = "secure", Name = "Q" };
        var attrs = QueueAttributes.Build(queue, "ipp://s/ipp/secure", "http://s", 1, 0, ["printer-name", "printer-state"]);
        Assert.Equal(["printer-name", "printer-state"], attrs.Attributes.Select(a => a.Name).Order());
    }

    [Fact]
    public async Task JobTemplateKeepsPrintOptionsButDropsOurOwnJobFields()
    {
        var group = new IppGroup(IppTag.JobAttributes)
            .Add("copies", IppValue.Integer(2))
            .Add("page-ranges", IppValue.Range(1, 3))
            .Add("job-name", IppValue.Name("ignored"))
            .Add("ipp-attribute-fidelity", IppValue.Boolean(true));

        var decoded = await JobTemplate.DecodeAsync(JobTemplate.Encode(group), default);

        Assert.Equal(["copies", "page-ranges"], decoded.Attributes.Select(a => a.Name));
        Assert.Equal(new IppRange(1, 3), decoded.Find("page-ranges")!.First!.Value.Value);
    }

    [Theory]
    [InlineData("ipp://192.0.2.10/ipp/print", "http://192.0.2.10:631/ipp/print")]
    [InlineData("ipps://192.0.2.10/ipp/print", "https://192.0.2.10:631/ipp/print")]
    [InlineData("ipp://printer:8631/ipp/print", "http://printer:8631/ipp/print")]
    [InlineData("http://printer/ipp/print", "http://printer/ipp/print")]
    public void MapsIppSchemesToHttp(string ipp, string http) =>
        Assert.Equal(http, IppClient.ToHttpUri(ipp).ToString());
}
