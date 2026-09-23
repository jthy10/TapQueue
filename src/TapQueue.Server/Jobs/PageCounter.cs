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
/// </summary>
public static partial class PageCounter
{
    /// <summary>Bigger PDFs than this aren't read into memory to be counted.</summary>
    private const long MaxPdfBytes = 512L * 1024 * 1024;

    public static int? Count(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            Span<byte> magic = stackalloc byte[8];
            var read = file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            magic = magic[..read];
            file.Position = 0;

            if (magic.StartsWith("%PDF-"u8))
                return file.Length > MaxPdfBytes ? null : CountPdf(File.ReadAllBytes(path));
            if (magic.StartsWith("RaS2"u8))
                return CountPwgRaster(file);
            if (magic.StartsWith("UNIRAST\0"u8))
                return CountUrf(file);
            if (magic.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
                return 1;
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>How many of <paramref name="pages"/> the page-ranges attribute (1-based, inclusive) selects.</summary>
    public static int ApplyPageRanges(int pages, IReadOnlyCollection<IppRange>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
            return pages;
        var selected = new bool[pages];
        foreach (var range in ranges)
            for (var page = Math.Max(range.Lower, 1); page <= Math.Min(range.Upper, pages); page++)
                selected[page - 1] = true;
        return selected.Count(s => s);
    }

    // ---- PDF ----

    // A PDF's pages hang off its catalog: trailer /Root -> catalog /Pages -> page tree root /Count.
    // Objects may sit in the file as "n 0 obj ... endobj" or packed into compressed object streams,
    // and later definitions (incremental updates) replace earlier ones. When that chain can't be
    // followed, count the page objects instead.

    [GeneratedRegex(@"(\d+)\s+\d+\s+obj\b(.*?)endobj", RegexOptions.Singleline)]
    private static partial Regex PdfObject();

    [GeneratedRegex(@"/Root\s+(\d+)\s+\d+\s+R")]
    private static partial Regex PdfRoot();

    [GeneratedRegex(@"/Pages\s+(\d+)\s+\d+\s+R")]
    private static partial Regex PdfPagesRef();

    [GeneratedRegex(@"/Count\s+(\d+)(?!\s+\d+\s+R)")]
    private static partial Regex PdfCount();

    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial Regex PdfPageType();

    [GeneratedRegex(@"/Type\s*/ObjStm\b")]
    private static partial Regex PdfObjStmType();

    [GeneratedRegex(@"/(N|First)\s+(\d+)")]
    private static partial Regex PdfObjStmHeader();

    [GeneratedRegex(@"stream\r?\n")]
    private static partial Regex PdfStreamStart();

    internal static int? CountPdf(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var objects = new Dictionary<int, string>();
        foreach (Match m in PdfObject().Matches(text))
        {
            var number = int.Parse(m.Groups[1].ValueSpan);
            var body = m.Groups[2].Value;
            var dictionary = DictionaryPart(body);
            objects[number] = dictionary;
            if (PdfObjStmType().IsMatch(dictionary))
                foreach (var (packed, packedBody) in UnpackObjectStream(dictionary, body))
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

    private static IEnumerable<(int Number, string Body)> UnpackObjectStream(string dictionary, string body)
    {
        if (!dictionary.Contains("/FlateDecode"))
            yield break;
        int? n = null, first = null;
        foreach (Match m in PdfObjStmHeader().Matches(dictionary))
        {
            if (m.Groups[1].Value == "N") n = int.Parse(m.Groups[2].ValueSpan);
            else first = int.Parse(m.Groups[2].ValueSpan);
        }
        var start = PdfStreamStart().Match(body);
        var end = body.LastIndexOf("endstream", StringComparison.Ordinal);
        if (n is null || first is null || !start.Success || end < start.Index)
            yield break;

        string content;
        try
        {
            var compressed = Encoding.Latin1.GetBytes(body[(start.Index + start.Length)..end]);
            using var inflate = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            content = Encoding.Latin1.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            yield break; // encrypted, or another filter first
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
                yield return (entries[i].Number, content[from..to]);
        }
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
