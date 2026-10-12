using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

/// <summary>What the "Printers and print queue" window lists and says.</summary>
public sealed class PrintQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly PrinterDto[] Printers = [new("office", "Office printer", "2nd floor", true, null, null)];

    private static JobDto Job(long id, string status, double hoursAgo = 1, int? pages = 3, int copies = 1, int refunded = 0, string? error = null) =>
        new(id, $"job {id}", "secure", status, 2048, "application/pdf", copies, "alice", null,
            Now.AddHours(-hoursAgo - 1), Now.AddHours(8), status == PrintQueue.Released ? Now.AddHours(-hoursAgo) : null,
            status == PrintQueue.Released ? "office" : null, error, null, pages, refunded);

    [Fact]
    public void WaitingJobsComeFirstThenWhatWasSentInTheLastDay()
    {
        var jobs = new[]
        {
            Job(1, PrintQueue.Released, hoursAgo: 30), Job(2, "canceled"), Job(3, PrintQueue.Released, hoursAgo: 5),
            Job(4, PrintQueue.Released, hoursAgo: 2), Job(5, "expired"), Job(6, PrintQueue.Held), Job(7, PrintQueue.Releasing, hoursAgo: 0),
        };

        Assert.Equal([7, 6, 4, 3], PrintQueue.Visible(jobs, Now).Select(j => j.Id));
    }

    [Fact]
    public void HeldJobsAndOnesNotYetStoppedCanBeDeleted()
    {
        Assert.True(PrintQueue.CanDelete(Job(1, PrintQueue.Held)));
        Assert.True(PrintQueue.CanDelete(Job(2, PrintQueue.Released)));
        Assert.False(PrintQueue.CanDelete(Job(3, PrintQueue.Released, refunded: 2)));
        Assert.False(PrintQueue.CanDelete(Job(4, PrintQueue.Releasing)));
    }

    [Fact]
    public void StatusNamesThePrinterAndWhatWasGivenBack()
    {
        Assert.StartsWith("Waiting until ", PrintQueue.Status(Job(1, PrintQueue.Held), Printers));
        Assert.Equal("Waiting (last try failed)", PrintQueue.Status(Job(1, PrintQueue.Held, error: "Out of paper"), Printers));
        Assert.StartsWith("Sent to Office printer at ", PrintQueue.Status(Job(2, PrintQueue.Released), Printers));
        Assert.Equal("Stopped at Office printer, 1 page given back", PrintQueue.Status(Job(3, PrintQueue.Released, refunded: 1), Printers));
        // A printer the person may no longer release at isn't in their list any more.
        Assert.StartsWith("Sent to office at ", PrintQueue.Status(Job(4, PrintQueue.Released), []));
        Assert.Equal("Printing at Office printer", PrintQueue.Status(Job(5, PrintQueue.Released) with { PrintState = "printing" }, Printers));
        Assert.Equal("Printed at Office printer", PrintQueue.Status(Job(5, PrintQueue.Released) with { PrintState = "printed" }, Printers));
    }

    [Fact]
    public void PagesShowCopiesAndUncountedJobs()
    {
        Assert.Equal("3", PrintQueue.Pages(Job(1, PrintQueue.Held)));
        Assert.Equal("3 × 2", PrintQueue.Pages(Job(1, PrintQueue.Held, copies: 2)));
        Assert.Equal("?", PrintQueue.Pages(Job(1, PrintQueue.Held, pages: null)));
    }

    [Fact]
    public void TheDeleteQuestionSaysWhatHappensToEachKind()
    {
        var waiting = PrintQueue.DeleteQuestion([Job(1, PrintQueue.Held)]);
        var both = PrintQueue.DeleteQuestion([Job(1, PrintQueue.Held), Job(2, PrintQueue.Held), Job(3, PrintQueue.Released)]);

        Assert.Equal("Delete 1 job that is waiting? It won't be printed.", waiting);
        Assert.StartsWith("Delete 2 jobs that are waiting? They won't be printed.\n\nAnd stop 1 job already sent to the printer?", both);
        Assert.Contains("given back to your page limit", both);
    }

    [Fact]
    public void QueuesSayWhetherThisPcHasThem()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Secure Print" };

        Assert.Equal("Installed on this PC", PrintQueue.QueueStatus(installed, "secure print"));
        Assert.StartsWith("Not installed", PrintQueue.QueueStatus(installed, "Color"));
        Assert.Equal("", PrintQueue.QueueStatus(null, "Color"));
    }

    [Fact]
    public void PrintersSayWhatsWrongWithThem()
    {
        Assert.Equal("Ready", PrintQueue.PrinterStatus(Printers[0]));
        Assert.Equal("Offline", PrintQueue.PrinterStatus(Printers[0] with { Online = false, StateMessage = "whatever" }));
        Assert.Equal("Toner low", PrintQueue.PrinterStatus(Printers[0] with { StateMessage = "Toner low" }));
    }
}
