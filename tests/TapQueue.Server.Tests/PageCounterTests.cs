using System.Text;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Tests;

public sealed class PageCounterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-pages-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Made with Ghostscript: a three-page PDF, the same with object streams, and rendered to PWG raster and URF.
    [Theory]
    [InlineData("three-pages.pdf")]
    [InlineData("three-pages-objstm.pdf")]
    [InlineData("three-pages.pwg")]
    [InlineData("three-pages.urf")]
    public void CountsFixtures(string name) =>
        Assert.Equal(3, PageCounter.Count(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void LaterRevisionsOfAPdfWin()
    {
        // An incremental update rewrites the page tree root with one page fewer.
        var pdf = """
            %PDF-1.4
            1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
            2 0 obj << /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R >> endobj
            4 0 obj << /Type /Page /Parent 2 0 R >> endobj
            trailer << /Root 1 0 R >>
            %%EOF
            2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
            trailer << /Root 1 0 R /Prev 9 >>
            %%EOF
            """;

        Assert.Equal(1, PageCounter.Count(Write("a.pdf", Encoding.Latin1.GetBytes(pdf))));
    }

    [Fact]
    public void CountsPageObjectsWhenTheCatalogIsMissing()
    {
        var pdf = """
            %PDF-1.4
            3 0 obj << /Type /Page >> endobj
            4 0 obj << /Type/Page >> endobj
            5 0 obj << /Type /Pages /Count 7 >> endobj
            """;

        Assert.Equal(2, PageCounter.Count(Write("b.pdf", Encoding.Latin1.GetBytes(pdf))));
    }

    [Fact]
    public void JpegIsOnePage() =>
        Assert.Equal(1, PageCounter.Count(Write("c.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10])));

    [Fact]
    public void UnknownAndTruncatedDocumentsAreNotCounted()
    {
        Assert.Null(PageCounter.Count(Write("d.bin", Encoding.ASCII.GetBytes("\x1b%-12345X@PJL"))));
        Assert.Null(PageCounter.Count(Write("e.bin", [])));

        var pwg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pwg"));
        Assert.Null(PageCounter.Count(Write("f.pwg", pwg[..(pwg.Length / 3)])));
    }

    [Theory]
    [InlineData(10, null, 10)]
    [InlineData(10, new[] { 1, 3 }, 3)]
    [InlineData(10, new[] { 2, 2, 5, 20 }, 7)]
    [InlineData(10, new[] { 1, 5, 3, 7 }, 7)]
    [InlineData(4, new[] { 6, 9 }, 0)]
    public void PageRangesSelectPages(int pages, int[]? bounds, int expected)
    {
        var ranges = bounds?.Chunk(2).Select(b => new IppRange(b[0], b[1])).ToList();
        Assert.Equal(expected, PageCounter.ApplyPageRanges(pages, ranges));
    }

    [Fact]
    public void PageRangesDontCostMemoryOrTimeForHugeCounts() =>
        Assert.Equal(int.MaxValue - 1, PageCounter.ApplyPageRanges(int.MaxValue, [new IppRange(2, int.MaxValue), new IppRange(5, 9)]));

    [Fact]
    public void ImplausiblePageCountsAreCapped()
    {
        var pdf = """
            %PDF-1.4
            1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
            2 0 obj << /Type /Pages /Kids [3 0 R] /Count 2000000000 >> endobj
            trailer << /Root 1 0 R >>
            """;

        Assert.Equal(PageCounter.MaxPages, PageCounter.Count(Write("huge.pdf", Encoding.Latin1.GetBytes(pdf))));
    }

    [Fact]
    public void ObjectStreamsThatInflateTooFarAreSkipped()
    {
        // A few hundred KB that inflate past the limit; the page objects outside it still count.
        var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var written = 0L; written <= PageCounter.MaxInflatedBytes; written += zeros.Length)
                deflate.Write(zeros);
        }
        var pdf = new MemoryStream();
        pdf.Write(Encoding.Latin1.GetBytes("%PDF-1.5\n3 0 obj << /Type /Page >> endobj\n7 0 obj << /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode >>\nstream\n"));
        compressed.WriteTo(pdf);
        pdf.Write(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

        Assert.Equal(1, PageCounter.Count(Write("bomb.pdf", pdf.ToArray())));
    }

    [Fact]
    public void PdfsThatMakeThePatternsBacktrackAreNotCounted()
    {
        // Every "n 0 obj" with no endobj after it would scan the rest of the file: quadratic without a timeout.
        var pdf = "%PDF-1.4\n" + string.Concat(Enumerable.Repeat("1 0 obj ", 2_000_000));
        Assert.Null(PageCounter.Count(Write("slow.pdf", Encoding.Latin1.GetBytes(pdf))));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
