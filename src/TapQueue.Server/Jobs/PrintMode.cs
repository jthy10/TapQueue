using System.Buffers.Binary;
using TapQueue.Server.Ipp;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Whether a job was sent to print in colour and on both sides of the paper, for reports. It's what
/// the job asked for: the print dialog's choices (print-color-mode and sides), or failing that what
/// a raster document says about itself. A PDF sent with colour left on "auto" to a colour queue could
/// come out either way, so its colour is unknown (null) rather than guessed.
/// </summary>
public static class PrintMode
{
    private const int PwgHeaderBytes = 1796;

    /// <param name="attributes">The job's IPP job attributes, if it sent any.</param>
    /// <param name="queueColor">Whether the queue it was sent to offers colour at all.</param>
    public static (bool? Color, bool Duplex) Of(IppGroup? attributes, string documentPath, bool queueColor)
    {
        var asked = attributes?.Find("print-color-mode")?.First?.AsString() ?? attributes?.Find("output-mode")?.First?.AsString();
        var sides = attributes?.Find("sides")?.First?.AsString();
        bool? color = asked switch
        {
            "color" => true,
            "monochrome" or "bi-level" or "process-monochrome" or "process-bi-level" or "auto-monochrome" => false,
            _ => null,
        };
        bool? duplex = sides is null ? null : sides.StartsWith("two-sided", StringComparison.Ordinal);
        if (color is null || duplex is null)
        {
            var raster = Raster(documentPath);
            color ??= raster?.Color ?? (queueColor ? null : false);
            // Nothing said: the queue's default is one side.
            duplex ??= raster?.Duplex ?? false;
        }
        return (color, duplex.Value);
    }

    /// <summary>What the first page of a PWG or Apple raster document says; null for anything else.</summary>
    internal static (bool Color, bool Duplex)? Raster(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new byte[12 + PwgHeaderBytes];
            var read = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            if (read >= 4 + PwgHeaderBytes && start.AsSpan().StartsWith("RaS2"u8))
            {
                var page = start.AsSpan(4);
                var duplex = BinaryPrimitives.ReadUInt32BigEndian(page[272..]) != 0;
                var bitsPerPixel = BinaryPrimitives.ReadUInt32BigEndian(page[388..]);
                var colors = BinaryPrimitives.ReadUInt32BigEndian(page[420..]);
                return (colors > 1 || colors == 0 && bitsPerPixel >= 24, duplex);
            }
            // After "UNIRAST\0" and the page count, a page header: bits per pixel, colour space, duplex (1 is one side).
            if (read >= 12 + 3 && start.AsSpan().StartsWith("UNIRAST\0"u8))
                return (start[12] >= 24, start[14] >= 2);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
