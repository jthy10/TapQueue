using System.Globalization;
using System.Text;
using TapQueue.Server.Reports;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>/api/v1/admin/reports: what was printed, for anyone who may see Jobs.</summary>
public static class AdminReportsApi
{
    public static void MapReportsApi(this RouteGroupBuilder admin)
    {
        // ?from=2026-10-01&to=2026-10-31&by=user. Without dates: this month so far. &format=csv downloads it.
        admin.MapGet("/reports/usage", (UsageReport reports, string? from, string? to, string? by, string? format) =>
        {
            var zone = TimeZoneInfo.Local;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
            var last = today;
            var first = new DateOnly(today.Year, today.Month, 1);
            if (to is not null && !TryDate(to, out last) || from is not null && !TryDate(from, out first))
                return Results.BadRequest(new ErrorResponse("Dates must look like 2026-10-31."));
            if (from is null && to is not null)
                first = new DateOnly(last.Year, last.Month, 1);
            if (last < first)
                return Results.BadRequest(new ErrorResponse("The report's last day is before its first."));
            if (last.DayNumber - first.DayNumber >= UsageReport.MaxDays)
                return Results.BadRequest(new ErrorResponse("A report covers at most ten years."));
            by ??= ReportGrouping.User;
            if (!ReportGrouping.All.Contains(by))
                return Results.BadRequest(new ErrorResponse($"by must be one of {string.Join(", ", ReportGrouping.All)}."));

            var report = reports.Run(first, last, by, zone);
            return format switch
            {
                null or "json" => Results.Ok(report),
                // The BOM tells Excel it's UTF-8, or names with accents come out wrong.
                "csv" => Results.File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(UsageReport.ToCsv(report))], "text/csv",
                    $"tapqueue-pages-by-{by}-{report.From}-to-{report.To}.csv"),
                _ => Results.BadRequest(new ErrorResponse("format must be json or csv.")),
            };
        });
    }

    private static bool TryDate(string text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}
