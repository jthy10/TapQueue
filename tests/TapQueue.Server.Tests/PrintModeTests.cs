using System.Buffers.Binary;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;

namespace TapQueue.Server.Tests;

/// <summary>Working out whether a job is colour and two-sided, for reports.</summary>
public sealed class PrintModeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-test").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Document(byte[] content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, content);
        return path;
    }

    private string Pdf() => Document("%PDF-1.4\n%%EOF\n"u8.ToArray());

    private static IppGroup Attributes(string? colorMode = null, string? sides = null)
    {
        var group = new IppGroup(IppTag.JobAttributes);
        if (colorMode is not null) group.Add("print-color-mode", IppValue.Keyword(colorMode));
        if (sides is not null) group.Add("sides", IppValue.Keyword(sides));
        return group;
    }

    private string PwgRaster(uint colors, uint duplex)
    {
        var bytes = new byte[4 + 1796];
        "RaS2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + 272), duplex);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + 388), colors * 8);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + 420), colors);
        return Document(bytes);
    }

    [Theory]
    [InlineData("color", "two-sided-long-edge", true, true)]
    [InlineData("monochrome", "one-sided", false, false)]
    [InlineData("process-monochrome", "two-sided-short-edge", false, true)]
    public void ThePrintDialogsChoicesDecide(string colorMode, string sides, bool color, bool duplex) =>
        Assert.Equal((color, duplex), PrintMode.Of(Attributes(colorMode, sides), Pdf(), queueColor: true));

    [Fact]
    public void APdfThatLeavesColourToThePrinterIsUnknownOnAColourQueue()
    {
        Assert.Equal((null, false), PrintMode.Of(Attributes("auto"), Pdf(), queueColor: true));
        Assert.Equal((null, false), PrintMode.Of(null, Pdf(), queueColor: true));
    }

    [Fact]
    public void NothingIsColourOnABlackAndWhiteQueue() =>
        Assert.Equal((false, false), PrintMode.Of(null, Pdf(), queueColor: false));

    [Fact]
    public void ARasterDocumentSaysForItself()
    {
        Assert.Equal((true, true), PrintMode.Of(null, PwgRaster(colors: 3, duplex: 1), queueColor: true));
        Assert.Equal((false, false), PrintMode.Of(Attributes("auto"), PwgRaster(colors: 1, duplex: 0), queueColor: true));

        var urf = new byte[12 + 32];
        "UNIRAST\0"u8.CopyTo(urf);
        (urf[12], urf[13], urf[14]) = (24, 1, 3);
        Assert.Equal((true, true), PrintMode.Of(null, Document(urf), queueColor: true));
    }

    [Fact]
    public void WhatTheJobAskedForWinsOverTheRaster() =>
        Assert.Equal((false, false), PrintMode.Of(Attributes("monochrome", "one-sided"), PwgRaster(colors: 3, duplex: 1), queueColor: true));
}
