using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>
/// What the server has to tell this person about things that happened at a printer: a tap that
/// printed nothing and why, a job the printer gave up on (servers from 0.17). The tray asks with
/// its other polling and shows each one once.
/// </summary>
/// <param name="fetch">Asks the server for notices newer than an id; null from a server that has none to give.</param>
public sealed class TapNotices(Func<long, CancellationToken, Task<List<UserNoticeDto>?>> fetch)
{
    /// <summary>On the first ask, older ones than this aren't shown: they're from before this tray started.</summary>
    private const int FreshSeconds = 90;

    private long _after;
    private bool _asked;
    private bool _unsupported;

    /// <summary>The notices to show now, oldest first.</summary>
    public async Task<IReadOnlyList<string>> NewAsync(CancellationToken ct = default)
    {
        if (_unsupported)
            return [];
        if (await fetch(_after, ct) is not { } notices)
        {
            _unsupported = true;
            return [];
        }
        var fresh = notices.Where(n => n.Id > _after && (_asked || n.AgeSeconds <= FreshSeconds)).Select(n => n.Message).ToList();
        _asked = true;
        if (notices.Count > 0)
            _after = Math.Max(_after, notices.Max(n => n.Id));
        return fresh;
    }
}
