namespace TapQueue.Server.Data;

/// <summary>
/// Remembers which sources (an address, a PC) did something lately, so the activity log gets at most
/// <paramref name="max"/> lines per <paramref name="every"/> from each. For things anyone who can
/// reach the server can make happen as often as they like, which would otherwise bury the log and
/// fill the disk with it.
/// </summary>
public sealed class RecentSources(TimeSpan every, int max = 1)
{
    private const int MaxTracked = 1000;
    private readonly Dictionary<string, (DateTimeOffset Since, int Count)> _recent = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldRecord(string source)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_recent)
        {
            if (_recent.TryGetValue(source, out var seen) && now - seen.Since < every)
            {
                if (seen.Count >= max)
                    return false;
                _recent[source] = (seen.Since, seen.Count + 1);
                return true;
            }
            // Many sources at once is someone making them up: start over rather than grow.
            if (_recent.Count >= MaxTracked)
                _recent.Clear();
            _recent[source] = (now, 1);
            return true;
        }
    }
}

/// <summary>
/// Limits on the activity log lines that callers who haven't signed in can cause (see <see cref="RecentSources"/>).
/// What they report is still kept where it's bounded (crash reports) or written to the server's log.
/// </summary>
public sealed class AnonymousEventLimits
{
    /// <summary>Crash reports, by the address they came from.</summary>
    public RecentSources Crashes { get; } = new(TimeSpan.FromMinutes(1), max: 5);

    /// <summary>Tray sign-ins turned away for an unknown user or wrong token, by address.</summary>
    public RecentSources RejectedSignIns { get; } = new(TimeSpan.FromMinutes(1), max: 5);
}
