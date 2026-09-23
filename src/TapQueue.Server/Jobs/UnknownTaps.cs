using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Recent taps of cards nobody owns, kept in memory so an admin can enroll a badge by tapping it at
/// a station and then running `tapqueue-admin badges add &lt;user&gt; --last-tap`.
/// </summary>
public sealed class UnknownTaps
{
    private const int MaxEntries = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private readonly LinkedList<UnknownTapDto> _taps = new();

    public void Add(string card, string stationId)
    {
        lock (_taps)
        {
            _taps.AddFirst(new UnknownTapDto(card, stationId, DateTimeOffset.UtcNow));
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

    /// <summary>Newest first.</summary>
    public List<UnknownTapDto> Recent()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        lock (_taps)
            return _taps.Where(t => t.At >= cutoff).ToList();
    }
}
