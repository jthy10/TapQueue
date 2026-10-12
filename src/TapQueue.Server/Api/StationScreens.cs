using System.Collections.Concurrent;

namespace TapQueue.Server.Api;

/// <summary>What a tap at a station came to, for its screen.</summary>
/// <param name="Seq">Goes up with every message, across restarts too, so a screen can tell a new one from the one it has.</param>
public sealed record ScreenMessage(long Seq, DateTimeOffset At, string Outcome, string Message);

/// <summary>
/// The last thing that happened at each station, for the screen next to it (<see cref="StationScreenApi"/>).
/// A person standing at the printer otherwise can't tell a tap that found nothing waiting from one
/// that was refused, or from a card nobody has. Kept in memory only: a screen shows a message for
/// a few seconds.
/// </summary>
public sealed class StationScreens
{
    private sealed class Screen
    {
        public ScreenMessage? Last;
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Screen> _screens = new(StringComparer.OrdinalIgnoreCase);
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Show(string stationId, string outcome, string message)
    {
        var screen = _screens.GetOrAdd(stationId, _ => new Screen());
        TaskCompletionSource waiting;
        lock (screen)
        {
            screen.Last = new ScreenMessage(Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow, outcome, message);
            waiting = screen.Changed;
            screen.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        waiting.TrySetResult();
    }

    public ScreenMessage? Last(string stationId) => _screens.TryGetValue(stationId, out var screen) ? screen.Last : null;

    /// <summary>
    /// The station's last message, once it's newer than <paramref name="after"/> or
    /// <paramref name="timeout"/> has passed, whichever comes first.
    /// </summary>
    public async Task<ScreenMessage?> WaitAsync(string stationId, long after, TimeSpan timeout, CancellationToken ct)
    {
        var screen = _screens.GetOrAdd(stationId, _ => new Screen());
        Task changed;
        lock (screen)
        {
            if (screen.Last is { } last && last.Seq > after)
                return last;
            changed = screen.Changed.Task;
        }
        try
        {
            await changed.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
        }
        return screen.Last;
    }
}
