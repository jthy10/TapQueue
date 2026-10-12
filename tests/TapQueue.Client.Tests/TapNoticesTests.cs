using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

public sealed class TapNoticesTests
{
    [Fact]
    public async Task EachNoticeIsShownOnceAndOldOnesNotAtAll()
    {
        var onServer = new List<UserNoticeDto> { new(1, "from before the tray started", 400), new(2, "just now", 5) };
        var asked = new List<long>();
        var notices = new TapNotices((after, _) =>
        {
            asked.Add(after);
            return Task.FromResult<List<UserNoticeDto>?>(onServer.Where(n => n.Id > after).ToList());
        });

        Assert.Equal(["just now"], await notices.NewAsync());
        Assert.Empty(await notices.NewAsync());
        onServer.Add(new(3, "a slow one, picked up late", 200));
        Assert.Equal(["a slow one, picked up late"], await notices.NewAsync());
        Assert.Equal([0, 2, 2], asked);
    }

    [Fact]
    public async Task AServerWithoutNoticesIsAskedOnce()
    {
        var asked = 0;
        var notices = new TapNotices((_, _) =>
        {
            asked++;
            return Task.FromResult<List<UserNoticeDto>?>(null);
        });

        Assert.Empty(await notices.NewAsync());
        Assert.Empty(await notices.NewAsync());
        Assert.Equal(1, asked);
    }
}
