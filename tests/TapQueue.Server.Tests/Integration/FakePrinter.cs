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

    /// <summary>How long the printer takes to accept each Print-Job.</summary>
    public TimeSpan PrintJobDelay { get; set; }

    /// <summary>Answer this many Print-Jobs with server-error-busy before accepting one.</summary>
    public int BusyCount { get; set; }

    /// <summary>What Get-Printer-Attributes reports: printer-state, printer-state-reasons, printer-is-accepting-jobs.</summary>
    public int State { get; set; } = IppPrinterState.Idle;
    public string[] Reasons { get; set; } = ["none"];
    public bool AcceptingJobs { get; set; } = true;

    /// <summary>The black toner's level in percent (its low level is 10).</summary>
    public int TonerLevel { get; set; } = 80;

    /// <summary>What the printer says it can do; null leaves the attribute out, as a printer that doesn't say.</summary>
    public string[]? Media { get; set; }
    public bool? Color { get; set; }
    public string[]? Sides { get; set; }
    public string[] Formats { get; set; } = ["application/pdf", "application/octet-stream"];

    /// <summary>The state jobs are in once accepted: by default they've printed by the time anyone asks.</summary>
    public int NewJobState { get; set; } = IppJobState.Completed;

    /// <summary>What a job that isn't finished reports as job-impressions-completed; null leaves it out, as some printers do.</summary>
    public int? ImpressionsCompleted { get; set; }

    /// <summary>The printer's own job numbers and their job-state. Cancel-Job moves an unfinished one to canceled.</summary>
    public ConcurrentDictionary<int, int> JobStates { get; } = new();

    private int _lastJobId = 100;

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
                .Add("document-format-supported", Formats.Select(IppValue.MimeType));
            if (Media is not null)
                response.Group(IppTag.PrinterAttributes).Add("media-supported", Media.Select(IppValue.Keyword));
            if (Color is { } color)
                response.Group(IppTag.PrinterAttributes).Add("color-supported", IppValue.Boolean(color));
            if (Sides is not null)
                response.Group(IppTag.PrinterAttributes).Add("sides-supported", Sides.Select(IppValue.Keyword));
        }
        else if (request.Code == IppOperation.PrintJob && BusyCount > 0)
        {
            BusyCount--;
            response = IppMessage.CreateResponse(request, IppStatus.ServerErrorBusy);
        }
        else if (request.Code == IppOperation.PrintJob)
        {
            await Task.Delay(PrintJobDelay);
            response = IppMessage.CreateResponse(request, PrintJobStatus, PrintJobStatus == IppStatus.Ok ? null : "Out of paper");
            if (PrintJobStatus == IppStatus.Ok)
            {
                Jobs.Enqueue(new ReceivedJob(request, body.ToArray()[(int)body.Position..]));
                var id = Interlocked.Increment(ref _lastJobId);
                JobStates[id] = NewJobState;
                response.Group(IppTag.JobAttributes).Add("job-id", IppValue.Integer(id)).Add("job-state", IppValue.Enum(NewJobState));
            }
        }
        else if (request.Code is IppOperation.GetJobAttributes or IppOperation.CancelJob)
        {
            var id = request.OperationInt("job-id") ?? 0;
            if (!JobStates.TryGetValue(id, out var state))
                response = IppMessage.CreateResponse(request, IppStatus.ClientErrorNotFound);
            else if (request.Code == IppOperation.CancelJob)
            {
                var finished = state >= IppJobState.Canceled;
                if (!finished)
                    JobStates[id] = IppJobState.Canceled;
                response = IppMessage.CreateResponse(request, finished ? IppStatus.ClientErrorNotPossible : IppStatus.Ok);
            }
            else
            {
                response = IppMessage.CreateResponse(request, IppStatus.Ok);
                var job = response.Group(IppTag.JobAttributes).Add("job-state", IppValue.Enum(state));
                if (ImpressionsCompleted is { } printed)
                    job.Add("job-impressions-completed", IppValue.Integer(printed));
            }
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
