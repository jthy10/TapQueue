using TapQueue.Shared.Api;

namespace TapQueue.Station.Tests;

public sealed class EffectiveSettingsTests
{
    private static readonly StationConfig Local = new() { Reader = "pcprox", Device = "", RepeatSeconds = 5, MinCardLength = 4 };

    private static StationSettingsDto Server(string? reader = null, string? device = null, int? repeat = null) =>
        new(7, "office", reader, device, repeat, null, "none", true, "");

    [Fact]
    public void StationTomlFillsInWhatTheServerLeavesOpen()
    {
        var s = EffectiveSettings.From(Local, Server(repeat: 9));

        Assert.Equal(("pcprox", "", 9, 4, 7), (s.Reader, s.Device, s.RepeatSeconds, s.MinCardLength, s.Version));
    }

    [Fact]
    public void AReaderFromTheServerComesWithItsOwnDevice()
    {
        var s = EffectiveSettings.From(Local, Server(reader: "keyboard", device: "/dev/input/event3"));

        Assert.Equal(("keyboard", "/dev/input/event3"), (s.Reader, s.Device));
    }

    [Fact]
    public void AKeyboardReaderWithoutADeviceKeepsTheLocalReader()
    {
        var s = EffectiveSettings.From(Local, Server(reader: "keyboard"));

        Assert.Equal(("pcprox", ""), (s.Reader, s.Device));
    }

    [Fact]
    public void OnlyAReaderChangeNeedsARestart()
    {
        var now = EffectiveSettings.From(Local, Server());

        Assert.False(now.NeedsRestartFor(EffectiveSettings.From(Local, Server(repeat: 30))));
        Assert.True(now.NeedsRestartFor(EffectiveSettings.From(Local, Server(reader: "keyboard", device: "/dev/input/event3"))));
    }
}
