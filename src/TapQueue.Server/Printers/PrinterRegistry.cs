using System.Collections.Concurrent;
using TapQueue.Server.Config;
using TapQueue.Server.Ipp;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Printers;

public sealed record PrinterStatus(
    bool Online,
    string? MakeAndModel,
    string? StateMessage,
    IReadOnlyList<string> DocumentFormats,
    DateTimeOffset CheckedAt);

/// <summary>The physical printers from config, plus the last status we got from each one.</summary>
public sealed class PrinterRegistry(ServerConfig config, ILogger<PrinterRegistry> logger)
{
    private readonly ConcurrentDictionary<string, PrinterStatus> _status = new();

    public IReadOnlyList<PrinterConfig> All => config.Printers;

    public PrinterConfig? Find(string id) =>
        config.Printers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public PrinterStatus? StatusOf(string id) => _status.GetValueOrDefault(id);

    public PrinterDto ToDto(PrinterConfig printer)
    {
        var status = StatusOf(printer.Id);
        return new PrinterDto(printer.Id, DisplayName(printer), printer.Location, status?.Online ?? false,
            status?.MakeAndModel, status?.StateMessage);
    }

    public static string DisplayName(PrinterConfig printer) => string.IsNullOrWhiteSpace(printer.Name) ? printer.Id : printer.Name;

    public async Task<PrinterStatus> ProbeAsync(PrinterConfig printer, CancellationToken ct)
    {
        using var client = new IppClient(printer.TlsSkipVerify, TimeSpan.FromSeconds(15));
        var request = IppMessage.CreateRequest(IppOperation.GetPrinterAttributes, IppClient.NextRequestId(), printer.Uri);
        request.Group(IppTag.OperationAttributes).Add("requested-attributes",
            IppValue.Keyword("printer-make-and-model"),
            IppValue.Keyword("printer-state"),
            IppValue.Keyword("printer-state-message"),
            IppValue.Keyword("printer-state-reasons"),
            IppValue.Keyword("document-format-supported"));

        PrinterStatus status;
        try
        {
            var response = await client.SendAsync(printer.Uri, request, ct: ct);
            var formats = response.Find(IppTag.PrinterAttributes, "document-format-supported")?.Values
                .Select(v => v.AsString()).OfType<string>().ToList() ?? [];
            var reasons = response.Find(IppTag.PrinterAttributes, "printer-state-reasons")?.Values
                .Select(v => v.AsString()).OfType<string>().Where(r => r != "none").ToList() ?? [];
            var message = response.Find(IppTag.PrinterAttributes, "printer-state-message")?.First?.AsString();
            if (string.IsNullOrWhiteSpace(message))
                message = reasons.Count > 0 ? string.Join(", ", reasons) : "ready";
            status = new PrinterStatus(
                Online: IppStatus.IsSuccess(response.Code),
                MakeAndModel: response.Find(IppTag.PrinterAttributes, "printer-make-and-model")?.First?.AsString(),
                StateMessage: message,
                DocumentFormats: formats,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            status = new PrinterStatus(false, StatusOf(printer.Id)?.MakeAndModel, ex.Message, StatusOf(printer.Id)?.DocumentFormats ?? [], DateTimeOffset.UtcNow);
        }

        var previous = StatusOf(printer.Id);
        _status[printer.Id] = status;
        if (previous is null || previous.Online != status.Online)
        {
            if (status.Online)
                logger.LogInformation("Printer {Id} is online: {Model} ({Uri})", printer.Id, status.MakeAndModel, printer.Uri);
            else
                logger.LogWarning("Printer {Id} is unreachable at {Uri}: {Message}", printer.Id, printer.Uri, status.StateMessage);
        }
        return status;
    }
}

/// <summary>Checks every printer at startup and then every couple of minutes.</summary>
public sealed class PrinterMonitor(PrinterRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            await Task.WhenAll(registry.All.Select(p => registry.ProbeAsync(p, stoppingToken)));
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
