using System.Collections.Concurrent;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Api;

/// <summary>
/// Things a person should hear about that happened away from their PC: a tap at a printer that
/// printed nothing, and why; a job the printer gave up on. Their tray app asks for these with its
/// other polling and shows them as notifications. Kept in memory for a few minutes only: one that
/// nobody's tray picked up by then is old news, and the activity log has it anyway.
/// </summary>
public sealed class UserNotices
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(10);
    private const int MostPerUser = 20;

    private sealed record Notice(long Id, DateTimeOffset At, string Message);

    private readonly ConcurrentDictionary<long, List<Notice>> _notices = new();
    private long _lastId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Add(long userId, string message)
    {
        var list = _notices.GetOrAdd(userId, _ => []);
        lock (list)
        {
            list.RemoveAll(n => n.At < DateTimeOffset.UtcNow - KeptFor);
            if (list.Count >= MostPerUser)
                list.RemoveAt(0);
            list.Add(new Notice(Interlocked.Increment(ref _lastId), DateTimeOffset.UtcNow, message));
        }
    }

    /// <summary>The person's notices newer than <paramref name="after"/>, oldest first.</summary>
    public List<UserNoticeDto> For(long userId, long after)
    {
        if (!_notices.TryGetValue(userId, out var list))
            return [];
        var now = DateTimeOffset.UtcNow;
        lock (list)
            return list.Where(n => n.Id > after && n.At >= now - KeptFor)
                .Select(n => new UserNoticeDto(n.Id, n.Message, (int)(now - n.At).TotalSeconds)).ToList();
    }
}
