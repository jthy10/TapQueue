using TapQueue.Server.Data;
using TapQueue.Server.Ipp;

namespace TapQueue.Server.Printers;

/// <summary>A paper size by its PWG name, in hundredths of a millimetre.</summary>
public sealed record MediaSize(string Name, int X, int Y)
{
    /// <summary>
    /// Reads the size out of a PWG self-describing name: "iso_a4_210x297mm", "na_letter_8.5x11in".
    /// Null for names that don't end in one.
    /// </summary>
    public static MediaSize? Parse(string name)
    {
        var size = name.AsSpan(name.LastIndexOf('_') + 1);
        var perUnit = size.EndsWith("mm") ? 100 : size.EndsWith("in") ? 2540 : 0;
        if (perUnit == 0)
            return null;
        size = size[..^2];
        var by = size.IndexOf('x');
        if (by <= 0
            || !decimal.TryParse(size[..by], System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !decimal.TryParse(size[(by + 1)..], System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var y)
            || x <= 0 || y <= 0 || x > 5000 || y > 5000)
            return null;
        return new MediaSize(name, (int)Math.Round(x * perUnit), (int)Math.Round(y * perUnit));
    }

    /// <summary>What to call it in a message: "A4", "Letter", or its measurements.</summary>
    public string Label
    {
        get
        {
            var parts = Name.Split('_');
            return parts.Length == 3 && parts[1].Length > 0 && !parts[0].StartsWith("custom", StringComparison.Ordinal)
                ? char.ToUpperInvariant(parts[1][0]) + parts[1][1..].Replace('-', ' ')
                : $"{X / 100m:0.#} x {Y / 100m:0.#} mm";
        }
    }
}

/// <summary>
/// What a printer says it can do, from its Get-Printer-Attributes answer. Null and empty mean the
/// printer didn't say, which is not the same as "can't".
/// </summary>
/// <param name="Media">PWG media names, including any custom_min_… and custom_max_… pair.</param>
/// <param name="Resolutions">Dots per inch, for resolutions that are the same both ways.</param>
public sealed record PrinterCapabilities(
    IReadOnlyList<string> Media,
    bool? Color,
    bool? Duplex,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> OutputBins,
    IReadOnlyList<int> Finishings)
{
    /// <summary>The attributes <see cref="Read"/> uses, to ask for in Get-Printer-Attributes.</summary>
    public static readonly string[] Attributes =
    [
        "media-supported", "color-supported", "print-color-mode-supported", "sides-supported", "printer-resolution-supported",
        "media-source-supported", "media-type-supported", "output-bin-supported", "finishings-supported",
    ];

    /// <summary>Lists longer than this are cut: they end up in every answer to every PC.</summary>
    private const int MaxValues = 80;

    public static PrinterCapabilities Read(IppMessage response)
    {
        List<string> Strings(string name) => response.Find(IppTag.PrinterAttributes, name)?.Values
            .Select(v => v.AsString()).OfType<string>().Where(s => s.Length is > 0 and <= 255).Distinct().Take(MaxValues).ToList() ?? [];

        var colorModes = Strings("print-color-mode-supported");
        var sides = Strings("sides-supported");
        return new PrinterCapabilities(
            Media: Strings("media-supported"),
            Color: response.Find(IppTag.PrinterAttributes, "color-supported")?.First?.AsBool() ?? (colorModes.Count == 0 ? null : colorModes.Contains("color")),
            Duplex: sides.Count == 0 ? null : sides.Any(s => s.StartsWith("two-sided", StringComparison.Ordinal)),
            Resolutions: response.Find(IppTag.PrinterAttributes, "printer-resolution-supported")?.Values
                .Select(v => v.Value).OfType<IppResolution>().Where(r => r.Units == IppResolution.DotsPerInch && r.CrossFeed == r.Feed && r.Feed is > 0 and <= 9600)
                .Select(r => r.Feed).Distinct().Take(MaxValues).ToList() ?? [],
            Sources: Strings("media-source-supported"),
            Types: Strings("media-type-supported"),
            OutputBins: Strings("output-bin-supported"),
            Finishings: response.Find(IppTag.PrinterAttributes, "finishings-supported")?.Values
                .Select(v => v.AsInt()).OfType<int>().Distinct().Take(MaxValues).ToList() ?? []);
    }

    /// <summary>
    /// Whether the printer takes paper of this size, either way up: one of its sizes within 2 mm, or
    /// inside its custom range. True when the printer didn't list sizes that can be read.
    /// </summary>
    public bool Takes(int x, int y)
    {
        const int tolerance = 200;
        var sizes = Media.Select(MediaSize.Parse).OfType<MediaSize>().ToList();
        if (sizes.Count == 0)
            return true;
        bool Near(MediaSize s, int a, int b) => Math.Abs(s.X - a) <= tolerance && Math.Abs(s.Y - b) <= tolerance;
        if (sizes.Any(s => !s.Name.StartsWith("custom_", StringComparison.Ordinal) && (Near(s, x, y) || Near(s, y, x))))
            return true;
        var min = sizes.FirstOrDefault(s => s.Name.StartsWith("custom_min_", StringComparison.Ordinal));
        var max = sizes.FirstOrDefault(s => s.Name.StartsWith("custom_max_", StringComparison.Ordinal));
        bool Within(int a, int b) => a >= min!.X - tolerance && a <= max!.X + tolerance && b >= min.Y - tolerance && b <= max.Y + tolerance;
        return min is not null && max is not null && (Within(x, y) || Within(y, x));
    }
}

/// <summary>
/// What a hold queue tells PCs it can do: what the print dialog offers. A job can be released at
/// any printer, so it's put together from all of them. The document formats are the ones every
/// printer takes, because the PC picks one before anyone knows where the job will print. Paper
/// sizes, trays, finishing and so on are whatever any printer offers; a job that asks for a paper
/// size is only released at a printer that has it. Colour and two-sided are offered when the
/// queue's settings allow them and some printer can do them.
/// Until a printer has answered, a queue offers what TapQueue always did.
/// </summary>
public sealed record QueueCapabilities(
    IReadOnlyList<string> Formats,
    IReadOnlyList<MediaSize> Media,
    bool Color,
    bool Duplex,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> OutputBins,
    IReadOnlyList<int> Finishings)
{
    /// <summary>Formats a queue accepts. Jobs are passed through to the physical printer in the same format.</summary>
    public static readonly string[] AcceptedFormats =
        ["application/pdf", "image/jpeg", "image/pwg-raster", "image/urf", "application/octet-stream"];

    public static readonly MediaSize[] DefaultMedia =
    [
        new("na_letter_8.5x11in", 21590, 27940),
        new("na_legal_8.5x14in", 21590, 35560),
        new("na_executive_7.25x10.5in", 18415, 26670),
        new("iso_a4_210x297mm", 21000, 29700),
    ];

    private const int MaxMedia = 60;

    public static QueueCapabilities For(QueueRecord queue, IEnumerable<PrinterStatus?> printers)
    {
        var known = printers.OfType<PrinterStatus>().ToList();
        var reported = known.Select(p => p.Capabilities).OfType<PrinterCapabilities>().ToList();

        // octet-stream ("you work it out") is what a printer falls back to, so it's never the only thing offered.
        var formats = known.Where(p => p.DocumentFormats.Count > 0).Aggregate((IEnumerable<string>)AcceptedFormats,
            (all, p) => all.Where(f => p.DocumentFormats.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
        if (!formats.Exists(f => f != "application/octet-stream"))
            formats = [.. AcceptedFormats];

        var media = reported.SelectMany(c => c.Media).Distinct()
            .Where(name => !name.StartsWith("custom_", StringComparison.Ordinal)).Select(MediaSize.Parse).OfType<MediaSize>()
            .OrderBy(m => Array.FindIndex(DefaultMedia, d => d.Name == m.Name) is >= 0 and var known ? known : int.MaxValue)
            .ThenBy(m => m.Name, StringComparer.Ordinal).Take(MaxMedia).ToList();

        static List<T> Union<T>(IEnumerable<IReadOnlyList<T>> lists, params T[] otherwise)
        {
            var all = lists.SelectMany(l => l).Distinct().ToList();
            return all.Count > 0 ? all : [.. otherwise];
        }

        return new QueueCapabilities(
            formats,
            media.Count > 0 ? media : DefaultMedia,
            Color: queue.Color && !(reported.Count > 0 && reported.All(c => c.Color == false)),
            Duplex: queue.Duplex && !(reported.Count > 0 && reported.All(c => c.Duplex == false)),
            Resolutions: [.. Union(reported.Select(c => c.Resolutions), 600).Order()],
            Sources: Union(reported.Select(c => c.Sources), "auto"),
            Types: Union(reported.Select(c => c.Types), "stationery"),
            OutputBins: Union(reported.Select(c => c.OutputBins), "face-down"),
            Finishings: [.. Union(reported.Select(c => c.Finishings), 3).Order()]);
    }
}
