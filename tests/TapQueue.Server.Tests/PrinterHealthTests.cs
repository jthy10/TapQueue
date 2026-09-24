using TapQueue.Server.Ipp;
using TapQueue.Server.Printers;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests;

public class PrinterHealthTests
{
    private static IppMessage Response(int state, params string[] reasons)
    {
        var response = new IppMessage { Code = IppStatus.Ok };
        response.Group(IppTag.PrinterAttributes)
            .Add("printer-state", IppValue.Enum(state))
            .Add("printer-state-reasons", reasons.Length == 0 ? [IppValue.Keyword("none")] : reasons.Select(IppValue.Keyword));
        return response;
    }

    private static IppMessage WithToner(IppMessage response, int level, int low = 10, int high = 100)
    {
        response.Group(IppTag.PrinterAttributes)
            .Add("marker-names", IppValue.Name("black cartridge"))
            .Add("marker-types", IppValue.Keyword("toner-cartridge"))
            .Add("marker-colors", IppValue.Name("#000000"))
            .Add("marker-levels", IppValue.Integer(level))
            .Add("marker-low-levels", IppValue.Integer(low))
            .Add("marker-high-levels", IppValue.Integer(high));
        return response;
    }

    [Fact]
    public void IdlePrinterWithNoReasonsIsOk()
    {
        var report = PrinterHealth.Assess(Response(IppPrinterState.Idle));
        Assert.Equal(PrinterHealthLevel.Ok, report.Level);
        Assert.Equal("idle", report.State);
        Assert.True(report.CanPrint);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void ErrorReasonsAreErrorsButDontStopReleasesUnlessThePrinterStopped()
    {
        var jammed = PrinterHealth.Assess(Response(IppPrinterState.Idle, "media-jam-error"));
        Assert.Equal(PrinterHealthLevel.Error, jammed.Level);
        Assert.Equal(["Paper jam"], jammed.Problems);
        Assert.True(jammed.CanPrint);

        var stopped = PrinterHealth.Assess(Response(IppPrinterState.Stopped, "door-open"));
        Assert.Equal(PrinterHealthLevel.Error, stopped.Level);
        Assert.Equal("stopped", stopped.State);
        Assert.False(stopped.CanPrint);
        Assert.Equal(["Door open"], stopped.Problems);
    }

    [Fact]
    public void StoppedWithoutAReasonStillSaysSo()
    {
        var report = PrinterHealth.Assess(Response(IppPrinterState.Stopped));
        Assert.Equal(["Stopped"], report.Problems);
        Assert.False(report.CanPrint);
    }

    [Fact]
    public void NotAcceptingJobsCantPrint()
    {
        var response = Response(IppPrinterState.Idle);
        response.Group(IppTag.PrinterAttributes).Add("printer-is-accepting-jobs", IppValue.Boolean(false));
        var report = PrinterHealth.Assess(response);
        Assert.False(report.CanPrint);
        Assert.Equal(PrinterHealthLevel.Error, report.Level);
        Assert.Equal(["Not accepting jobs"], report.Problems);
    }

    [Fact]
    public void WarningsReportsAndUnknownReasons()
    {
        var report = PrinterHealth.Assess(Response(IppPrinterState.Processing,
            "media-low-warning", "cover-open-report", "something-odd", "media-empty"));
        Assert.Equal(PrinterHealthLevel.Error, report.Level);
        Assert.Equal("printing", report.State);
        // Errors first, then warnings; -report reasons are left out.
        Assert.Equal(["Out of paper", "Paper low", "Something odd"], report.Problems);
    }

    [Fact]
    public void SupplyLevelsAreReportedAndLowOnesWarn()
    {
        var healthy = PrinterHealth.Assess(WithToner(Response(IppPrinterState.Idle), level: 60));
        Assert.Equal(PrinterHealthLevel.Ok, healthy.Level);
        var toner = Assert.Single(healthy.Supplies);
        Assert.Equal(new PrinterSupplyDto("Black cartridge", "toner-cartridge", "#000000", 60, false), toner);

        // The printer's own toner-low reason is replaced by the supply's line with its level.
        var low = PrinterHealth.Assess(WithToner(Response(IppPrinterState.Idle, "toner-low-warning"), level: 5));
        Assert.Equal(PrinterHealthLevel.Warning, low.Level);
        Assert.Equal(["Black cartridge low (5%)"], low.Problems);
        Assert.True(low.CanPrint);
    }

    [Fact]
    public void SupplyLevelsAreScaledAndUnknownLevelsAreNull()
    {
        Assert.Equal(50, PrinterHealth.Assess(WithToner(Response(IppPrinterState.Idle), level: 1, low: 0, high: 2)).Supplies[0].Level);

        var unknown = PrinterHealth.Assess(WithToner(Response(IppPrinterState.Idle, "toner-low"), level: -3));
        Assert.Null(unknown.Supplies[0].Level);
        Assert.False(unknown.Supplies[0].Low);
        // With no level to show, the printer's own reason stays.
        Assert.Equal(["Toner low"], unknown.Problems);
    }
}
