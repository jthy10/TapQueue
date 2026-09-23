namespace TapQueue.Station.Tests;

public sealed class PcProxReaderTests
{
    [Fact]
    public void Decodes26BitCardLittleEndianAndMasksExtraBits()
    {
        // 0x02_3F_1A_2B in the low 26 bits; the top bits of the 4th byte are noise.
        byte[] buffer = [0x2B, 0x1A, 0x3F, 0xFE, 0, 0, 0, 0];
        Assert.Equal((0x023F1A2BL).ToString(), PcProxReader.DecodeCard(buffer, 26));
    }

    [Fact]
    public void DecodesWholeBytes() =>
        Assert.Equal("258", PcProxReader.DecodeCard([0x02, 0x01, 0, 0, 0, 0, 0, 0], 16));

    [Fact]
    public void FindsUsbDevnumThroughSysfsSymlinks()
    {
        var node = Directory.Exists("/sys/class/hidraw") ? Directory.GetDirectories("/sys/class/hidraw").FirstOrDefault() : null;
        if (node is null || !File.ReadAllText(Path.Combine(node, "device", "uevent")).Contains("HID_ID=0003:"))
            return; // no USB HID device on this machine
        Assert.Matches(@"^\d+$", PcProxReader.UsbDevnum("/dev/" + Path.GetFileName(node)) ?? "");
    }
}
