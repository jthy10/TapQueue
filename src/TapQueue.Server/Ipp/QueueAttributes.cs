using System.Security.Cryptography;
using System.Text;
using TapQueue.Server.Data;

namespace TapQueue.Server.Ipp;

/// <summary>
/// The printer attributes a TapQueue queue advertises. This is what Windows' built-in IPP class
/// driver reads to decide what the print dialog offers, so it follows the IPP Everywhere
/// (PWG 5100.14) required attribute set.
/// </summary>
public static class QueueAttributes
{
    /// <summary>Formats we accept. Jobs are passed through to the physical printer in the same format.</summary>
    public static readonly string[] DocumentFormats =
        ["application/pdf", "image/jpeg", "image/pwg-raster", "image/urf", "application/octet-stream"];

    public static readonly int[] Operations =
    [
        IppOperation.PrintJob, IppOperation.ValidateJob, IppOperation.CreateJob, IppOperation.SendDocument,
        IppOperation.CancelJob, IppOperation.GetJobAttributes, IppOperation.GetJobs,
        IppOperation.GetPrinterAttributes, IppOperation.CancelMyJobs, IppOperation.CloseJob, IppOperation.IdentifyPrinter,
    ];

    // PWG media name → size in hundredths of a millimetre.
    private static readonly (string Name, int X, int Y)[] Media =
    [
        ("na_letter_8.5x11in", 21590, 27940),
        ("na_legal_8.5x14in", 21590, 35560),
        ("na_executive_7.25x10.5in", 18415, 26670),
        ("iso_a4_210x297mm", 21000, 29700),
    ];

    private const int Margin = 423; // 4.23 mm, typical for office lasers

    private static readonly HashSet<string> GroupKeywords = ["all", "printer-description", "job-template"];

    /// <param name="httpBaseUrl">The server's http:// address as the client sees it, e.g. http://192.0.2.1:8631</param>
    public static IppGroup Build(QueueRecord queue, string printerUri, string httpBaseUrl, int upTimeSeconds, int queuedJobCount, IReadOnlyCollection<string>? requested)
    {
        var all = new IppGroup(IppTag.PrinterAttributes);
        var keywords = (IEnumerable<string> values) => values.Select(IppValue.Keyword);

        // Identity
        all.Add("printer-uri-supported", IppValue.Uri(printerUri))
            .Add("uri-security-supported", IppValue.Keyword("none"))
            .Add("uri-authentication-supported", IppValue.Keyword("requesting-user-name"))
            .Add("printer-name", IppValue.Name(queue.Name))
            .Add("printer-info", IppValue.Text(queue.Description))
            .Add("printer-location", IppValue.Text(queue.Location))
            .Add("printer-make-and-model", IppValue.Text("TapQueue Print Queue"))
            .Add("printer-more-info", IppValue.Uri(httpBaseUrl + "/"))
            .Add("printer-icons", new[] { 48, 128, 512 }.Select(size => IppValue.Uri($"{httpBaseUrl}/icons/{size}.png")))
            .Add("printer-organization", IppValue.Text(""))
            .Add("printer-organizational-unit", IppValue.Text(""))
            .Add("printer-geo-location", IppValue.Unknown())
            .Add("printer-uuid", IppValue.Uri($"urn:uuid:{StableUuid(queue.Id)}"))
            .Add("printer-device-id", IppValue.Text($"MFG:TapQueue;MDL:{queue.Name};CMD:PDF,PWGRaster,URF;CLS:PRINTER;"))
            .Add("printer-kind", IppValue.Keyword("document"));

        // State
        all.Add("printer-state", IppValue.Enum(IppPrinterState.Idle))
            .Add("printer-state-reasons", IppValue.Keyword("none"))
            .Add("printer-state-message", IppValue.Text(""))
            .Add("printer-is-accepting-jobs", IppValue.Boolean(true))
            .Add("queued-job-count", IppValue.Integer(queuedJobCount))
            .Add("printer-up-time", IppValue.Integer(Math.Max(1, upTimeSeconds)))
            .Add("printer-state-change-time", IppValue.Integer(1))
            .Add("printer-state-change-date-time", IppValue.DateTime(ServerClock.StartedAt))
            .Add("printer-config-change-time", IppValue.Integer(1))
            .Add("printer-config-change-date-time", IppValue.DateTime(ServerClock.StartedAt))
            // A hold queue has no toner or paper of its own.
            .Add("printer-supply", IppValue.Unknown())
            .Add("printer-supply-description", IppValue.Unknown())
            .Add("printer-supply-info-uri", IppValue.Unknown());

        // Protocol
        all.Add("ipp-versions-supported", keywords(["1.0", "1.1", "2.0"]))
            .Add("ipp-features-supported", IppValue.Keyword("ipp-everywhere"))
            .Add("operations-supported", Operations.Select(IppValue.Enum))
            .Add("charset-configured", IppValue.Charset("utf-8"))
            .Add("charset-supported", IppValue.Charset("utf-8"))
            .Add("natural-language-configured", IppValue.Language("en"))
            .Add("generated-natural-language-supported", IppValue.Language("en"))
            .Add("compression-supported", IppValue.Keyword("none"))
            .Add("pdl-override-supported", IppValue.Keyword("attempted"))
            .Add("multiple-document-jobs-supported", IppValue.Boolean(false))
            .Add("multiple-operation-time-out", IppValue.Integer(60))
            .Add("multiple-operation-time-out-action", IppValue.Keyword("abort-job"))
            .Add("job-ids-supported", IppValue.Boolean(true))
            .Add("preferred-attributes-supported", IppValue.Boolean(false))
            .Add("printer-get-attributes-supported", IppValue.Keyword("document-format"))
            .Add("which-jobs-supported", keywords(["completed", "not-completed"]))
            .Add("identify-actions-default", IppValue.Keyword("display"))
            .Add("identify-actions-supported", IppValue.Keyword("display"))
            .Add("job-creation-attributes-supported", keywords([
                "copies", "media", "media-col", "sides", "print-color-mode", "print-quality",
                "printer-resolution", "orientation-requested", "print-scaling", "output-bin", "finishings",
                "page-ranges", "overrides", "print-content-optimize", "print-rendering-intent"]));

        // Document formats
        all.Add("document-format-default", IppValue.MimeType("application/pdf"))
            .Add("document-format-supported", DocumentFormats.Select(IppValue.MimeType));

        // Job template
        var colorModes = queue.Color ? new[] { "auto", "color", "monochrome" } : ["monochrome"];
        var sides = queue.Duplex ? new[] { "one-sided", "two-sided-long-edge", "two-sided-short-edge" } : ["one-sided"];
        all.Add("color-supported", IppValue.Boolean(queue.Color))
            .Add("print-color-mode-default", IppValue.Keyword(queue.Color ? "auto" : "monochrome"))
            .Add("print-color-mode-supported", keywords(colorModes))
            .Add("sides-default", IppValue.Keyword("one-sided"))
            .Add("sides-supported", keywords(sides))
            .Add("copies-default", IppValue.Integer(1))
            .Add("copies-supported", IppValue.Range(1, 999))
            .Add("finishings-default", IppValue.Enum(3))
            .Add("finishings-supported", IppValue.Enum(3))
            .Add("orientation-requested-default", IppValue.Enum(3))
            .Add("orientation-requested-supported", IppValue.Enum(3), IppValue.Enum(4), IppValue.Enum(5), IppValue.Enum(6))
            .Add("output-bin-default", IppValue.Keyword("face-down"))
            .Add("output-bin-supported", IppValue.Keyword("face-down"))
            .Add("print-quality-default", IppValue.Enum(4))
            .Add("print-quality-supported", IppValue.Enum(3), IppValue.Enum(4), IppValue.Enum(5))
            .Add("print-scaling-default", IppValue.Keyword("auto"))
            .Add("print-scaling-supported", keywords(["auto", "auto-fit", "fill", "fit", "none"]))
            .Add("printer-resolution-default", IppValue.Resolution(600, 600))
            .Add("printer-resolution-supported", IppValue.Resolution(600, 600))
            .Add("number-up-default", IppValue.Integer(1))
            .Add("number-up-supported", IppValue.Integer(1))
            .Add("job-sheets-default", IppValue.Keyword("none"))
            .Add("job-sheets-supported", IppValue.Keyword("none"))
            // Job options are replayed to the physical printer at release (see JobTemplate).
            .Add("page-ranges-supported", IppValue.Boolean(true))
            .Add("overrides-supported", keywords(["document-numbers", "pages"]))
            .Add("print-content-optimize-default", IppValue.Keyword("auto"))
            .Add("print-content-optimize-supported", IppValue.Keyword("auto"))
            .Add("print-rendering-intent-default", IppValue.Keyword("auto"))
            .Add("print-rendering-intent-supported", IppValue.Keyword("auto"))
            .Add("pages-per-minute", IppValue.Integer(40));
        if (queue.Color)
            all.Add("pages-per-minute-color", IppValue.Integer(40));

        // Raster formats (used if the client chooses PWG raster or Apple raster over PDF)
        all.Add("pwg-raster-document-resolution-supported", IppValue.Resolution(300, 300), IppValue.Resolution(600, 600))
            .Add("pwg-raster-document-type-supported", keywords(queue.Color ? ["black_1", "sgray_8", "srgb_8"] : ["black_1", "sgray_8"]))
            .Add("pwg-raster-document-sheet-back", IppValue.Keyword("normal"))
            .Add("urf-supported", keywords([
                "V1.4", "CP1", "PQ3-4-5", "RS300-600", "W8",
                .. queue.Color ? new[] { "SRGB24" } : [],
                .. queue.Duplex ? new[] { "DM1" } : []]));

        // Media
        var defaultMedia = Media.FirstOrDefault(m => m.Name == queue.DefaultMedia);
        if (defaultMedia.Name is null) defaultMedia = Media[0];
        all.Add("media-default", IppValue.Keyword(defaultMedia.Name))
            .Add("media-supported", Media.Select(m => IppValue.Keyword(m.Name)))
            .Add("media-ready", IppValue.Keyword(defaultMedia.Name))
            .Add("media-col-default", IppValue.Collection(MediaCol(defaultMedia.X, defaultMedia.Y)))
            .Add("media-col-ready", IppValue.Collection(MediaCol(defaultMedia.X, defaultMedia.Y)))
            .Add("media-col-database", Media.Select(m => IppValue.Collection(MediaCol(m.X, m.Y))))
            .Add("media-col-supported", keywords([
                "media-size", "media-top-margin", "media-bottom-margin", "media-left-margin",
                "media-right-margin", "media-source", "media-type"]))
            .Add("media-size-supported", Media.Select(m => IppValue.Collection(MediaSize(m.X, m.Y))))
            .Add("media-source-supported", IppValue.Keyword("auto"))
            .Add("media-type-supported", IppValue.Keyword("stationery"))
            .Add("media-top-margin-supported", IppValue.Integer(Margin))
            .Add("media-bottom-margin-supported", IppValue.Integer(Margin))
            .Add("media-left-margin-supported", IppValue.Integer(Margin))
            .Add("media-right-margin-supported", IppValue.Integer(Margin));

        if (requested is null || requested.Count == 0 || requested.Any(GroupKeywords.Contains))
            return all;

        var filtered = new IppGroup(IppTag.PrinterAttributes);
        filtered.Attributes.AddRange(all.Attributes.Where(a => requested.Contains(a.Name)));
        return filtered;
    }

    private static IppCollection MediaSize(int x, int y) => new IppCollection()
        .Add("x-dimension", IppValue.Integer(x))
        .Add("y-dimension", IppValue.Integer(y));

    private static IppCollection MediaCol(int x, int y) => new IppCollection()
        .Add("media-size", IppValue.Collection(MediaSize(x, y)))
        .Add("media-top-margin", IppValue.Integer(Margin))
        .Add("media-bottom-margin", IppValue.Integer(Margin))
        .Add("media-left-margin", IppValue.Integer(Margin))
        .Add("media-right-margin", IppValue.Integer(Margin))
        .Add("media-source", IppValue.Keyword("auto"))
        .Add("media-type", IppValue.Keyword("stationery"));

    /// <summary>Same queue id → same UUID across restarts, so Windows doesn't see a "new" printer.</summary>
    private static Guid StableUuid(string queueId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("tapqueue-queue:" + queueId)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5-style
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}
