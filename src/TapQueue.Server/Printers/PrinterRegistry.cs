using System.Collections.Concurrent;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Printers;

/// <param name="Health">Null when the printer didn't answer.</param>
/// <param name="HealthSince">When the health level last changed.</param>
public sealed record PrinterStatus(
    bool Online,
    string? MakeAndModel,
    string? StateMessage,
    IReadOnlyList<string> DocumentFormats,
    DateTimeOffset CheckedAt,
    PrinterHealthReport? Health = null,
    DateTimeOffset? HealthSince = null)
{
    public string Level => Health?.Level ?? PrinterHealthLevel.Offline;

    public bool CanPrint => Online && Health?.CanPrint != false;

    /// <summary>What's wrong in a few words, for messages: "paper jam", "unreachable".</summary>
    public string Problem => !Online ? "unreachable"
        : Health is { Problems.Count: > 0 } h ? string.Join(", ", h.Problems).ToLowerInvariant()
        : "not ready";
}

/// <summary>The physical printers, plus the last status we got from each one.</summary>
public sealed class PrinterRegistry(PrinterStore store, EventLog events, ILogger<PrinterRegistry> logger)
{
    private readonly ConcurrentDictionary<string, PrinterStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public List<PrinterRecord> All => store.List();

    public PrinterRecord? Find(string id) => store.Get(id);

    public PrinterStatus? StatusOf(string id) => _status.GetValueOrDefault(id);

    /// <summary>Drops what we knew about a printer that was removed or pointed somewhere else.</summary>
    public void Forget(string id) => _status.TryRemove(id, out _);

    public PrinterDto ToDto(PrinterRecord printer)
    {
        var status = StatusOf(printer.Id);
        return new PrinterDto(printer.Id, printer.Name, printer.Location, status?.Online ?? false,
            status?.MakeAndModel, status?.StateMessage);
    }

    public PrinterAdminDto ToAdminDto(PrinterRecord printer)
    {
        var status = StatusOf(printer.Id);
        var health = status is null ? null : new PrinterHealthDto(status.Level, status.Online ? status.Health?.State : null,
            status.CanPrint, status.Online ? status.Health?.Problems ?? [] : [status.StateMessage ?? "Unreachable"],
            status.Health?.Supplies ?? [], status.HealthSince);
        return new(ToDto(printer), printer.Uri, printer.TlsSkipVerify, status?.CheckedAt, health);
    }

    public async Task<PrinterStatus> ProbeAsync(PrinterRecord printer, CancellationToken ct)
    {
        using var client = new IppClient(printer.TlsSkipVerify, TimeSpan.FromSeconds(15));
        var request = IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), printer.Uri);
        request.Group(IppTag.OperationAttributes).Add("requested-attributes",
            IppValue.Keyword("printer-make-and-model"),
            IppValue.Keyword("printer-state-message"),
            IppValue.Keyword("document-format-supported"));
        request.Group(IppTag.OperationAttributes).Find("requested-attributes")!.Values.AddRange(PrinterHealth.Attributes.Select(IppValue.Keyword));

        var previous = StatusOf(printer.Id);
        PrinterStatus status;
        try
        {
            var response = await client.SendAsync(printer.Uri, request, ct: ct);
            if (!IppStatus.IsSuccess(response.Code))
                throw new InvalidOperationException($"Printer answered with IPP status 0x{response.Code:X4}.");
            var formats = response.Find(IppTag.PrinterAttributes, "document-format-supported")?.Values
                .Select(v => v.AsString()).OfType<string>().ToList() ?? [];
            var health = PrinterHealth.Assess(response);
            var message = response.Find(IppTag.PrinterAttributes, "printer-state-message")?.First?.AsString();
            if (string.IsNullOrWhiteSpace(message))
                message = health.Problems.Count > 0 ? string.Join(", ", health.Problems) : "ready";
            status = new PrinterStatus(
                Online: true,
                MakeAndModel: response.Find(IppTag.PrinterAttributes, "printer-make-and-model")?.First?.AsString(),
                StateMessage: message,
                DocumentFormats: formats,
                CheckedAt: DateTimeOffset.UtcNow,
                Health: health);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            status = new PrinterStatus(false, previous?.MakeAndModel, ex.Message, previous?.DocumentFormats ?? [], DateTimeOffset.UtcNow);
        }

        // Probes can overlap (the monitor, a release, "Check now"), so each change is reported once.
        lock (_status)
        {
            previous = StatusOf(printer.Id);
            status = status with { HealthSince = previous?.Level == status.Level ? previous.HealthSince : status.CheckedAt };
            _status[printer.Id] = status;
            Report(printer, previous, status);
        }
        return status;
    }

    /// <summary>Logs and records in the activity log when a printer's problems change.</summary>
    private void Report(PrinterRecord printer, PrinterStatus? previous, PrinterStatus status)
    {
        var problems = Problems(status);
        if (previous is not null && problems == Problems(previous))
            return;
        // Nothing to say about a printer that's fine the first time we see it.
        if (previous is null && status.Level == PrinterHealthLevel.Ok)
        {
            logger.LogInformation("Printer {Id} is online: {Model} ({Uri})", printer.Id, status.MakeAndModel, printer.Uri);
            return;
        }

        string message;
        if (!status.Online)
        {
            message = $"{printer.Name} is unreachable: {status.StateMessage}";
            logger.LogWarning("Printer {Id} is unreachable at {Uri}: {Message}", printer.Id, printer.Uri, status.StateMessage);
        }
        else if (status.Level == PrinterHealthLevel.Ok)
        {
            message = $"{printer.Name} is ready again.";
            logger.LogInformation("Printer {Id} is ready", printer.Id);
        }
        else
        {
            message = $"{printer.Name}: {problems}{(status.CanPrint ? "" : ". Jobs are kept on hold until it's fixed")}.";
            logger.LogWarning("Printer {Id} ({State}): {Problems}", printer.Id, status.Health?.State, problems);
        }
        events.Record(EventCategory.Printer, EventLog.System, EventLog.Printer(printer.Id), message);
    }

    private static string Problems(PrinterStatus status) =>
        !status.Online ? "unreachable" : string.Join(", ", status.Health?.Problems ?? []);
}

/// <summary>Checks every printer at startup and then every 30 seconds.</summary>
public sealed class PrinterMonitor(PrinterRegistry registry) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await Task.WhenAll(registry.All.Select(p => registry.ProbeAsync(p, stoppingToken)));
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
