using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using TapQueue.Server.Ipp;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Counts the pages in a spooled document, for quotas. Knows PDF, PWG raster, Apple raster (URF)
/// and JPEG, recognised by their first bytes rather than the document-format the client claimed.
/// Returns null for anything else, or a file it can't make sense of (an encrypted PDF, say).
/// Documents come from anyone who can reach the server, so the work is bounded: patterns give up
/// after <see cref="PatternTimeoutMs"/> ms, compressed streams stop at <see cref="MaxInflatedBytes"/>, and
/// a count over <see cref="MaxPages"/> is taken as <see cref="MaxPages"/>.
/// </summary>
public static partial class PageCounter
{
    /// <summary>Bigger PDFs than this aren't read into memory to be counted.</summary>
    private const long MaxPdfBytes = 512L * 1024 * 1024;

    /// <summary>More pages than anyone prints in one job; keeps pages times copies well inside an int.</summary>
    public const int MaxPages = 100_000;

    /// <summary>The most a PDF's compressed object streams may unpack to, all together.</summary>
    internal const long MaxInflatedBytes = 64L * 1024 * 1024;

    private const int PatternTimeoutMs = 5_000;

    private static readonly Lock PdfLock = new();

    public static int? Count(string path) => CountUnbounded(path) is { } pages ? Math.Min(pages, MaxPages) : null;

    private static int? CountUnbounded(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            Span<byte> magic = stackalloc byte[8];
            var read = file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            magic = magic[..read];
            file.Position = 0;

            if (magic.StartsWith("%PDF-"u8))
            {
                if (file.Length > MaxPdfBytes)
                    return null;
                // A PDF is read into memory whole (and again as text) to be counted. One at a time, so
                // several big ones arriving together can't use up the server's memory between them.
                lock (PdfLock)
                    return CountPdf(File.ReadAllBytes(path));
            }
            if (magic.StartsWith("RaS2"u8))
                return CountPwgRaster(file);
            if (magic.StartsWith("UNIRAST\0"u8))
                return CountUrf(file);
            if (magic.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
                return 1;
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or OverflowException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>How many of <paramref name="pages"/> the page-ranges attribute (1-based, inclusive) selects.</summary>
    public static int ApplyPageRanges(int pages, IReadOnlyCollection<IppRange>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
            return pages;
        // Overlapping ranges count each page once. Merged rather than marked page by page, since both
        // the page count and the ranges come from the client.
        long selected = 0;
        long next = 1; // the first page not counted yet
        foreach (var range in ranges.OrderBy(r => r.Lower))
        {
            var from = Math.Max(Math.Max(range.Lower, 1L), next);
            long to = Math.Min(range.Upper, pages);
            if (to < from)
                continue;
            selected += to - from + 1;
            next = to + 1;
        }
        return (int)selected;
    }

    // ---- PDF ----

    // A PDF's pages hang off its catalog: trailer /Root -> catalog /Pages -> page tree root /Count.
    // Objects may sit in the file as "n 0 obj ... endobj" or packed into compressed object streams,
    // and later definitions (incremental updates) replace earlier ones. When that chain can't be
    // followed, count the page objects instead.

    [GeneratedRegex(@"(\d+)\s+\d+\s+obj\b(.*?)endobj", RegexOptions.Singleline, PatternTimeoutMs)]
    private static partial Regex PdfObject();

    [GeneratedRegex(@"/Root\s+(\d+)\s+\d+\s+R", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfRoot();

    [GeneratedRegex(@"/Pages\s+(\d+)\s+\d+\s+R", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfPagesRef();

    [GeneratedRegex(@"/Count\s+(\d+)(?!\s+\d+\s+R)", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfCount();

    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfPageType();

    [GeneratedRegex(@"/Type\s*/ObjStm\b", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfObjStmType();

    [GeneratedRegex(@"/(N|First)\s+(\d+)", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfObjStmHeader();

    [GeneratedRegex(@"stream\r?\n", RegexOptions.None, PatternTimeoutMs)]
    private static partial Regex PdfStreamStart();

    internal static int? CountPdf(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var objects = new Dictionary<int, string>();
        var inflateBudget = MaxInflatedBytes;
        foreach (Match m in PdfObject().Matches(text))
        {
            var number = int.Parse(m.Groups[1].ValueSpan);
            var body = m.Groups[2].Value;
            var dictionary = DictionaryPart(body);
            objects[number] = dictionary;
            if (PdfObjStmType().IsMatch(dictionary))
                foreach (var (packed, packedBody) in UnpackObjectStream(dictionary, body, ref inflateBudget))
                    objects[packed] = packedBody;
        }

        var roots = PdfRoot().Matches(text);
        if (roots.Count > 0
            && objects.TryGetValue(int.Parse(roots[^1].Groups[1].ValueSpan), out var catalog)
            && PdfPagesRef().Match(catalog) is { Success: true } pagesRef
            && objects.TryGetValue(int.Parse(pagesRef.Groups[1].ValueSpan), out var pageTree)
            && PdfCount().Match(pageTree) is { Success: true } count)
            return int.Parse(count.Groups[1].ValueSpan);

        var pages = objects.Values.Count(o => PdfPageType().IsMatch(o));
        return pages > 0 ? pages : null;
    }

    private static string DictionaryPart(string body)
    {
        var stream = PdfStreamStart().Match(body);
        return stream.Success ? body[..stream.Index] : body;
    }

    private static List<(int Number, string Body)> UnpackObjectStream(string dictionary, string body, ref long budget)
    {
        var objects = new List<(int Number, string Body)>();
        if (!dictionary.Contains("/FlateDecode"))
            return objects;
        int? n = null, first = null;
        foreach (Match m in PdfObjStmHeader().Matches(dictionary))
        {
            if (m.Groups[1].Value == "N") n = int.Parse(m.Groups[2].ValueSpan);
            else first = int.Parse(m.Groups[2].ValueSpan);
        }
        var start = PdfStreamStart().Match(body);
        var end = body.LastIndexOf("endstream", StringComparison.Ordinal);
        if (n is null || first is null || !start.Success || end < start.Index || budget <= 0)
            return objects;

        string content;
        try
        {
            var compressed = Encoding.Latin1.GetBytes(body[(start.Index + start.Length)..end]);
            using var inflate = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            // A few KB can inflate to gigabytes; stop at what's left of the budget.
            var buffer = new byte[81920];
            int read;
            while ((read = inflate.Read(buffer)) > 0)
            {
                if (output.Length + read > budget)
                {
                    budget = 0;
                    return objects;
                }
                output.Write(buffer, 0, read);
            }
            budget -= output.Length;
            content = Encoding.Latin1.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (InvalidDataException)
        {
            return objects; // encrypted, or another filter first
        }

        // The stream starts with N pairs of "object-number offset", offsets counted from First.
        var header = content[..Math.Min(first.Value, content.Length)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<(int Number, int Offset)>();
        for (var i = 0; i + 1 < header.Length && entries.Count < n; i += 2)
            if (int.TryParse(header[i], out var number) && int.TryParse(header[i + 1], out var offset))
                entries.Add((number, first.Value + offset));
        for (var i = 0; i < entries.Count; i++)
        {
            var from = Math.Min(entries[i].Offset, content.Length);
            var to = i + 1 < entries.Count ? Math.Min(entries[i + 1].Offset, content.Length) : content.Length;
            if (to > from)
                objects.Add((entries[i].Number, content[from..to]));
        }
        return objects;
    }

    // ---- PWG raster (PWG 5102.4) ----

    private const int PwgHeaderBytes = 1796;

    /// <summary>
    /// Each page is a 1796-byte header and then compressed lines, which have to be walked to find
    /// where the next page starts.
    /// </summary>
    internal static int? CountPwgRaster(Stream input)
    {
        using var stream = new BufferedStream(input, 65536);
        stream.Position = 4;
        var length = stream.Length;
        var header = new byte[PwgHeaderBytes];
        var pages = 0;
        while (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length)
        {
            var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(376));
            var bitsPerPixel = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(388));
            var bytesPerLine = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(392));
            var bytesPerPixel = Math.Max(1, (int)(bitsPerPixel + 7) / 8);
            if (height == 0 || bytesPerLine == 0 || bytesPerLine > 1 << 20)
                return null;
            if (!SkipRasterLines(stream, length, height, bytesPerLine, bytesPerPixel))
                return null;
            pages++;
        }
        return pages > 0 ? pages : null;
    }

    /// <summary>
    /// Lines come in groups: a repeat count, then runs until the line is full. A run byte of 0-127
    /// repeats the next pixel 1-128 times, 129-255 is followed by 2-128 literal pixels, and 128
    /// fills the rest of the line with white.
    /// </summary>
    private static bool SkipRasterLines(Stream stream, long length, uint height, uint bytesPerLine, int bytesPerPixel)
    {
        var pixelsPerLine = bytesPerLine / (uint)bytesPerPixel;
        uint lines = 0;
        while (lines < height)
        {
            var repeat = stream.ReadByte();
            if (repeat < 0) return false;
            lines += (uint)repeat + 1;

            uint pixels = 0;
            while (pixels < pixelsPerLine)
            {
                var run = stream.ReadByte();
                if (run < 0) return false;
                if (run == 128)
                    break;
                if (run < 128)
                {
                    if (!Skip(stream, length, bytesPerPixel)) return false;
                    pixels += (uint)run + 1;
                }
                else
                {
                    var count = 257 - run;
                    if (!Skip(stream, length, count * bytesPerPixel)) return false;
                    pixels += (uint)count;
                }
            }
        }
        return true;
    }

    private static bool Skip(Stream stream, long length, int bytes)
    {
        var target = stream.Position + bytes;
        if (target > length) return false;
        stream.Position = target;
        return true;
    }

    // ---- Apple raster (URF) ----

    /// <summary>
    /// "UNIRAST\0" and the page count, which a writer that streams leaves at 0. Then each page is a
    /// 32-byte header and lines compressed the same way as PWG raster.
    /// </summary>
    internal static int? CountUrf(Stream input)
    {
        using var stream = new BufferedStream(input, 65536);
        var fileHeader = new byte[12];
        if (stream.ReadAtLeast(fileHeader, fileHeader.Length, throwOnEndOfStream: false) < fileHeader.Length)
            return null;
        var declared = BinaryPrimitives.ReadUInt32BigEndian(fileHeader.AsSpan(8));
        if (declared is > 0 and < 100_000)
            return (int)declared;

        var length = stream.Length;
        var header = new byte[32];
        var pages = 0;
        while (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length)
        {
            var bitsPerPixel = header[0];
            var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
            var width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            var height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
            if (width == 0 || height == 0 || width > 1 << 20)
                return null;
            if (!SkipRasterLines(stream, length, height, (width * bitsPerPixel + 7) / 8, bytesPerPixel))
                return null;
            pages++;
        }
        return pages > 0 ? pages : null;
    }
}
