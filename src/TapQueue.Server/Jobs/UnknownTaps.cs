using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Recent taps of cards nobody owns, kept in memory so an admin can enroll a badge by tapping it at
/// a station and then picking it in the console or running `tapqueue-admin badges add &lt;user&gt; --last-tap`.
/// Each tap remembers its station, so with several readers the admin can say which one they're standing at.
/// </summary>
public sealed class UnknownTaps
{
    /// <summary>Kept per station, so a busy reader elsewhere can't push out the card just tapped at this one.</summary>
    private const int MaxPerStation = 20;
    private const int MaxEntries = 500;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private readonly LinkedList<UnknownTapDto> _taps = new();

    public void Add(string card, string stationId)
    {
        lock (_taps)
        {
            _taps.AddFirst(new UnknownTapDto(card, stationId, DateTimeOffset.UtcNow));
            var fromStation = 0;
            for (var node = _taps.First; node is not null;)
            {
                var next = node.Next;
                if (SameStation(node.Value, stationId) && ++fromStation > MaxPerStation)
                    _taps.Remove(node);
                node = next;
            }
            while (_taps.Count > MaxEntries)
                _taps.RemoveLast();
        }
    }

    public void Remove(string normalizedCard)
    {
        lock (_taps)
        {
            for (var node = _taps.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Card == normalizedCard)
                    _taps.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>Newest first; with <paramref name="stationId"/>, only the taps at that station.</summary>
    public List<UnknownTapDto> Recent(string? stationId = null)
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        lock (_taps)
            return _taps.Where(t => t.At >= cutoff && (stationId is null || SameStation(t, stationId))).ToList();
    }

    private static bool SameStation(UnknownTapDto tap, string stationId) =>
        string.Equals(tap.StationId, stationId, StringComparison.OrdinalIgnoreCase);
}
