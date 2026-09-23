using TapQueue.Shared.Api;

namespace TapQueue.Server.Data;

/// <param name="Source">Where the limit comes from: "user", or "group:&lt;id&gt;".</param>
public sealed record QuotaLimit(int Pages, string Period, string Source);

/// <summary>A limit and how much of it has been used in the current period.</summary>
public sealed record QuotaUsage(QuotaLimit Limit, int Used, DateTimeOffset PeriodStart, DateTimeOffset ResetsAt)
{
    public int Remaining => Math.Max(0, Limit.Pages - Used);
}

/// <summary>
/// Page limits, checked when jobs are released. A limit set on the user wins. Otherwise their
/// groups' limits apply and the most generous one counts: a job may print if any of them allows
/// it. Groups without a limit don't lift anyone's; a user with no limit anywhere is unlimited.
/// Pages count when a job is released, as its pages times copies (1 page if it couldn't be counted).
/// </summary>
public sealed class QuotaPolicy(Database database, ServerSettings settings)
{
    /// <summary>The limits that apply to a user and what's left of each; empty if they're unlimited.</summary>
    public IReadOnlyList<QuotaUsage> UsageFor(long userId, DateTimeOffset now) =>
        LimitsFor(userId).Select(limit =>
        {
            var (start, end) = Period(limit.Period, now, TimeZoneInfo.Local);
            return new QuotaUsage(limit, PagesReleased(userId, start), start, end);
        }).ToList();

    /// <summary>Why a job of <paramref name="pages"/> pages can't be released now, or null if it can.</summary>
    public string? Refusal(long userId, int pages, DateTimeOffset now)
    {
        var usage = UsageFor(userId, now);
        if (usage.Count == 0)
            return null;
        var deny = settings.QuotaOverrun == QuotaOverrun.Deny;
        if (usage.Any(u => deny ? u.Used + pages <= u.Limit.Pages : u.Used < u.Limit.Pages))
            return null;

        var best = usage.MaxBy(u => u.Remaining)!;
        var resets = $"it starts over {TimeZoneInfo.ConvertTime(best.ResetsAt, TimeZoneInfo.Local):ddd MMM d}";
        return best.Remaining == 0
            ? $"You've used all {best.Limit.Pages} pages you get {Describe(best.Limit.Period)}; {resets}."
            : $"This job's {Pages(pages)} would go over your page limit: you have {best.Remaining} of {best.Limit.Pages} left {Current(best.Limit.Period)}; {resets}.";
    }

    private List<QuotaLimit> LimitsFor(long userId)
    {
        var own = database.QueryOne("SELECT quota_pages, quota_period FROM users WHERE id = $u AND quota_pages IS NOT NULL",
            r => new QuotaLimit(r.GetInt32(0), r.GetString(1), "user"), ("$u", userId));
        if (own is not null)
            return [own];
        return database.Query("""
            SELECT g.quota_pages, g.quota_period, g.id FROM groups g JOIN group_members m ON m.group_id = g.id
            WHERE m.user_id = $u AND g.quota_pages IS NOT NULL ORDER BY g.id
            """, r => new QuotaLimit(r.GetInt32(0), r.GetString(1), $"group:{r.GetString(2)}"), ("$u", userId));
    }

    private int PagesReleased(long userId, DateTimeOffset since) =>
        Convert.ToInt32(database.Scalar("""
            SELECT COALESCE(SUM(COALESCE(pages, 1) * copies), 0) FROM jobs
            WHERE user_id = $u AND status = $released AND released_at >= $since
            """, ("$u", userId), ("$released", JobStatus.Released), ("$since", since.ToUniversalTime())));

    /// <summary>The period <paramref name="now"/> falls in: a calendar day, a week from Monday, or a calendar month.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Period(string period, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var (start, end) = period switch
        {
            QuotaPeriod.Day => (today, today.AddDays(1)),
            QuotaPeriod.Week => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7))),
            QuotaPeriod.Month => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1)),
            _ => throw new ArgumentException($"Unknown quota period \"{period}\".", nameof(period)),
        };
        return (At(start), At(end));

        DateTimeOffset At(DateTime local) => new(local, zone.GetUtcOffset(local));
    }

    public static string Describe(string period) => period switch
    {
        QuotaPeriod.Day => "a day",
        QuotaPeriod.Week => "a week",
        _ => "a month",
    };

    private static string Current(string period) => period switch
    {
        QuotaPeriod.Day => "today",
        QuotaPeriod.Week => "this week",
        _ => "this month",
    };

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";
}
