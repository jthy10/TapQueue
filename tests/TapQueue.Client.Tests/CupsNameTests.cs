using TapQueue.Client.Linux;

namespace TapQueue.Client.Tests;

public sealed class CupsNameTests
{
    [Theory]
    [InlineData("TapQueue Secure Print", "TapQueue_Secure_Print")]
    [InlineData("secure", "secure")]
    [InlineData("  Floor 2 / Colour #1  ", "Floor_2_Colour_1")]
    [InlineData("It's \"quoted\"?", "It_s_quoted")]
    [InlineData("Büro-Drucker", "Büro-Drucker")]
    [InlineData("///", "TapQueue")]
    public void QueueNamesBecomeValidCupsNames(string queue, string cups) =>
        Assert.Equal(cups, CupsPrinterInstaller.CupsName(queue));

    [Fact]
    public void LongNamesAreCut() =>
        Assert.Equal(127, CupsPrinterInstaller.CupsName(new string('a', 300)).Length);
}
