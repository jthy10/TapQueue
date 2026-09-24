using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TapQueue.Server.Ipp;

namespace TapQueue.Server.Tests.Integration;

/// <summary>A network printer that speaks just enough IPP for the server to probe it and send it jobs.</summary>
public sealed class FakePrinter : IAsyncDisposable
{
    public sealed record ReceivedJob(IppMessage Request, byte[] Document)
    {
        public string? Name => Request.OperationString("job-name");
        public string? User => Request.OperationString("requesting-user-name");
    }

    private readonly WebApplication _app;

    public ConcurrentQueue<ReceivedJob> Jobs { get; } = new();

    /// <summary>What Print-Job answers with; anything but OK means the printer refused the job.</summary>
    public short PrintJobStatus { get; set; } = IppStatus.Ok;

    /// <summary>Answer this many Print-Jobs with server-error-busy before accepting one.</summary>
    public int BusyCount { get; set; }

    /// <summary>What Get-Printer-Attributes reports: printer-state, printer-state-reasons, printer-is-accepting-jobs.</summary>
    public int State { get; set; } = IppPrinterState.Idle;
    public string[] Reasons { get; set; } = ["none"];
    public bool AcceptingJobs { get; set; } = true;

    /// <summary>The black toner's level in percent (its low level is 10).</summary>
    public int TonerLevel { get; set; } = 80;

    public string Uri { get; }

    private FakePrinter(WebApplication app)
    {
        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Uri = address.Replace("http://", "ipp://") + "/ipp/print";
    }

    public static async Task<FakePrinter> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakePrinter? printer = null;
        app.MapPost("/ipp/print", (HttpContext http) => printer!.HandleAsync(http));
        await app.StartAsync();
        printer = new FakePrinter(app);
        return printer;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body);
        body.Position = 0;
        var request = await IppReader.ReadAsync(body);

        IppMessage response;
        if (request.Code == IppOperation.GetPrinterAttributes)
        {
            response = IppMessage.CreateResponse(request, IppStatus.Ok);
            response.Group(IppTag.PrinterAttributes)
                .Add("printer-make-and-model", IppValue.Text("Fake LaserJet"))
                .Add("printer-state", IppValue.Enum(State))
                .Add("printer-state-reasons", Reasons.Select(IppValue.Keyword))
                .Add("printer-is-accepting-jobs", IppValue.Boolean(AcceptingJobs))
                .Add("marker-names", IppValue.Name("black cartridge"))
                .Add("marker-colors", IppValue.Name("#000000"))
                .Add("marker-types", IppValue.Keyword("toner-cartridge"))
                .Add("marker-levels", IppValue.Integer(TonerLevel))
                .Add("marker-low-levels", IppValue.Integer(10))
                .Add("marker-high-levels", IppValue.Integer(100))
                .Add("document-format-supported", IppValue.MimeType("application/pdf"), IppValue.MimeType("application/octet-stream"));
        }
        else if (request.Code == IppOperation.PrintJob && BusyCount > 0)
        {
            BusyCount--;
            response = IppMessage.CreateResponse(request, IppStatus.ServerErrorBusy);
        }
        else if (request.Code == IppOperation.PrintJob)
        {
            response = IppMessage.CreateResponse(request, PrintJobStatus, PrintJobStatus == IppStatus.Ok ? null : "Out of paper");
            if (PrintJobStatus == IppStatus.Ok)
                Jobs.Enqueue(new ReceivedJob(request, body.ToArray()[(int)body.Position..]));
        }
        else
        {
            response = IppMessage.CreateResponse(request, IppStatus.ServerErrorOperationNotSupported);
        }

        http.Response.ContentType = "application/ipp";
        await http.Response.Body.WriteAsync(IppWriter.Encode(response));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
