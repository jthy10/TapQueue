namespace TapQueue.Station.Tests;

public sealed class KeyDecoderTests
{
    // Linux key codes
    private const int Key1 = 2, Key0 = 11, KeyA = 30, KeyEnter = 28, KeyTab = 15, KeyLeftShift = 42, KeyKp5 = 76, KeyKpEnter = 96;

    private static string? Type(KeyDecoder decoder, IEnumerable<int> codes, ref TimeSpan time)
    {
        string? card = null;
        foreach (var code in codes)
        {
            time += TimeSpan.FromMilliseconds(5);
            card = decoder.Feed(code, 1, time) ?? card;
            decoder.Feed(code, 0, time);
        }
        return card;
    }

    [Fact]
    public void DigitsThenEnterMakeACard()
    {
        var time = TimeSpan.Zero;
        Assert.Equal("1230", Type(new KeyDecoder(), [Key1, Key1 + 1, Key1 + 2, Key0, KeyEnter], ref time));
    }

    [Fact]
    public void TabAndKeypadEnterAlsoEndACard()
    {
        var time = TimeSpan.Zero;
        var decoder = new KeyDecoder();
        Assert.Equal("5", Type(decoder, [KeyKp5, KeyTab], ref time));
        Assert.Equal("5", Type(decoder, [KeyKp5, KeyKpEnter], ref time));
    }

    [Fact]
    public void ShiftGivesUppercase()
    {
        var decoder = new KeyDecoder();
        var t = TimeSpan.Zero;
        decoder.Feed(KeyLeftShift, 1, t);
        decoder.Feed(KeyA, 1, t);
        decoder.Feed(KeyA, 0, t);
        decoder.Feed(KeyLeftShift, 0, t);
        decoder.Feed(KeyA, 1, t);
        Assert.Equal("Aa", decoder.Feed(KeyEnter, 1, t));
    }

    [Fact]
    public void AutorepeatIsIgnored()
    {
        var decoder = new KeyDecoder();
        decoder.Feed(Key1, 1, TimeSpan.Zero);
        decoder.Feed(Key1, 2, TimeSpan.Zero);
        Assert.Equal("1", decoder.Feed(KeyEnter, 1, TimeSpan.Zero));
    }

    [Fact]
    public void StalePartialReadIsDropped()
    {
        var decoder = new KeyDecoder();
        decoder.Feed(Key1, 1, TimeSpan.Zero);
        decoder.Feed(Key0, 1, TimeSpan.FromSeconds(5));
        Assert.Equal("0", decoder.Feed(KeyEnter, 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void EnterAloneIsNotACard() =>
        Assert.Null(new KeyDecoder().Feed(KeyEnter, 1, TimeSpan.Zero));
}
