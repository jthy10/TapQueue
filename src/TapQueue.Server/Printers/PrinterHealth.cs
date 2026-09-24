using TapQueue.Server.Ipp;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Printers;

/// <summary>What a printer's Get-Printer-Attributes answer says about whether it can print.</summary>
public sealed record PrinterHealthReport(
    string Level,
    string State,
    bool CanPrint,
    IReadOnlyList<string> Problems,
    IReadOnlyList<PrinterSupplyDto> Supplies);

/// <summary>Turns printer-state, printer-state-reasons and the marker-* attributes into a health report.</summary>
public static class PrinterHealth
{
    /// <summary>The attributes <see cref="Assess"/> reads, to ask for in Get-Printer-Attributes.</summary>
    public static readonly string[] Attributes =
    [
        "printer-state", "printer-state-reasons", "printer-is-accepting-jobs",
        "marker-names", "marker-types", "marker-colors", "marker-levels", "marker-low-levels", "marker-high-levels",
    ];

    // RFC 8011 says a reason without a -error/-warning/-report suffix is an error, but printers
    // send plenty of harmless ones that way, so only these count as errors; others are warnings.
    private static readonly HashSet<string> ErrorReasons =
    [
        "media-empty", "media-needed", "media-jam", "door-open", "cover-open", "interlock-open",
        "toner-empty", "marker-supply-empty", "marker-waste-full", "input-tray-missing", "output-tray-missing",
        "output-area-full", "fuser-over-temp", "fuser-under-temp", "paused", "offline", "shutdown", "spool-area-full",
    ];

    // Replaced by the matching supply's own "… low (5%)" line when the printer reports levels.
    private static readonly HashSet<string> SupplyReasons =
        ["toner-low", "toner-empty", "marker-supply-low", "marker-supply-empty", "marker-waste-almost-full", "marker-waste-full"];

    private static readonly Dictionary<string, string> Words = new()
    {
        ["media-empty"] = "Out of paper",
        ["media-needed"] = "Paper needed",
        ["media-low"] = "Paper low",
        ["media-jam"] = "Paper jam",
        ["door-open"] = "Door open",
        ["cover-open"] = "Cover open",
        ["interlock-open"] = "Door open",
        ["toner-low"] = "Toner low",
        ["toner-empty"] = "Out of toner",
        ["marker-supply-low"] = "Ink or toner low",
        ["marker-supply-empty"] = "Out of ink or toner",
        ["marker-waste-almost-full"] = "Waste toner almost full",
        ["marker-waste-full"] = "Waste toner full",
        ["input-tray-missing"] = "Paper tray missing",
        ["output-tray-missing"] = "Output tray missing",
        ["output-area-almost-full"] = "Output tray almost full",
        ["output-area-full"] = "Output tray full",
        ["fuser-over-temp"] = "Fuser too hot",
        ["fuser-under-temp"] = "Fuser warming up",
        ["paused"] = "Paused",
        ["moving-to-paused"] = "Pausing",
        ["offline"] = "Offline",
        ["shutdown"] = "Shutting down",
        ["spool-area-full"] = "Printer memory full",
        ["timed-out"] = "Not responding",
        ["connecting-to-device"] = "Connecting",
        ["other"] = "Needs attention",
    };

    public static PrinterHealthReport Assess(IppMessage response)
    {
        var state = Int(response, "printer-state") switch
        {
            IppPrinterState.Processing => "printing",
            IppPrinterState.Stopped => "stopped",
            _ => "idle",
        };
        var accepting = response.Find(IppTag.PrinterAttributes, "printer-is-accepting-jobs")?.First?.AsBool() ?? true;
        var supplies = Supplies(response);

        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var raw in Strings(response, "printer-state-reasons"))
        {
            var (reason, severity) = Split(raw);
            if (reason == "none" || severity == "report")
                continue;
            if (SupplyReasons.Contains(reason) && supplies.Any(s => s.Level is not null))
                continue;
            var isError = severity == "error" || (severity is null && ErrorReasons.Contains(reason));
            Add(isError ? errors : warnings, Describe(reason));
        }
        foreach (var supply in supplies.Where(s => s.Low))
            Add(supply.Level == 0 ? errors : warnings, supply.Level == 0 ? $"{supply.Name} empty" : $"{supply.Name} low ({supply.Level}%)");

        var canPrint = state != "stopped" && accepting;
        if (!accepting)
            errors.Insert(0, "Not accepting jobs");
        if (state == "stopped" && errors.Count == 0)
            errors.Add("Stopped");

        var level = errors.Count > 0 ? PrinterHealthLevel.Error
            : warnings.Count > 0 ? PrinterHealthLevel.Warning
            : PrinterHealthLevel.Ok;
        return new PrinterHealthReport(level, state, canPrint, [.. errors, .. warnings], supplies);
    }

    /// <summary>"media-jam-error" → ("media-jam", "error"); "media-jam" → ("media-jam", null).</summary>
    private static (string Reason, string? Severity) Split(string keyword)
    {
        foreach (var severity in new[] { "error", "warning", "report" })
        {
            if (keyword.EndsWith("-" + severity, StringComparison.Ordinal))
                return (keyword[..^(severity.Length + 1)], severity);
        }
        return (keyword, null);
    }

    private static string Describe(string reason) =>
        Words.TryGetValue(reason, out var words) ? words
            : char.ToUpperInvariant(reason[0]) + reason[1..].Replace('-', ' ');

    private static void Add(List<string> list, string problem)
    {
        if (!list.Contains(problem))
            list.Add(problem);
    }

    private static List<PrinterSupplyDto> Supplies(IppMessage response)
    {
        var names = Strings(response, "marker-names");
        var types = Strings(response, "marker-types");
        var colors = Strings(response, "marker-colors");
        var levels = Ints(response, "marker-levels");
        var lows = Ints(response, "marker-low-levels");
        var highs = Ints(response, "marker-high-levels");

        var supplies = new List<PrinterSupplyDto>();
        for (var i = 0; i < names.Count; i++)
        {
            // Negative levels mean unknown (-1, -2) or "some left" (-3).
            var raw = i < levels.Count ? levels[i] : -2;
            var high = i < highs.Count && highs[i] > 0 ? highs[i] : 100;
            int? level = raw >= 0 ? Math.Clamp((int)Math.Round(raw * 100.0 / high), 0, 100) : null;
            var low = raw >= 0 && i < lows.Count && raw <= lows[i];
            var color = i < colors.Count && colors[i].StartsWith('#') ? colors[i][..Math.Min(7, colors[i].Length)] : null;
            supplies.Add(new PrinterSupplyDto(Capitalize(names[i]), i < types.Count ? types[i] : null, color, level, low));
        }
        return supplies;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int? Int(IppMessage response, string name) => response.Find(IppTag.PrinterAttributes, name)?.First?.AsInt();

    private static List<string> Strings(IppMessage response, string name) =>
        response.Find(IppTag.PrinterAttributes, name)?.Values.Select(v => v.AsString()).OfType<string>().ToList() ?? [];

    private static List<int> Ints(IppMessage response, string name) =>
        response.Find(IppTag.PrinterAttributes, name)?.Values.Select(v => v.AsInt() ?? -2).ToList() ?? [];
}
