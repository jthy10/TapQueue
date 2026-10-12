using System.Text;
using TapQueue.Server.Notifications;

namespace TapQueue.Server.Tests.Notifications;

public sealed class MailMessageTests
{
    [Theory]
    [InlineData("it@example.org", true)]
    [InlineData("first.last+printers@mail.example.co.uk", true)]
    [InlineData("", false)]
    [InlineData("example.org", false)]
    [InlineData("@example.org", false)]
    [InlineData("it@", false)]
    [InlineData("a@b@example.org", false)]
    [InlineData("it @example.org", false)]
    [InlineData("it@example.org>\r\nRCPT TO:<evil@example.com", false)]
    [InlineData("Someone <it@example.org>", false)]
    public void AddressesAreOneNameAtOneDomainAndNothingElse(string address, bool valid)
    {
        Assert.Equal(valid, MailMessage.IsValidAddress(address));
    }

    [Fact]
    public void ASubjectWithOtherAlphabetsOrLineBreaksStaysOneValidHeader()
    {
        var subject = "TapQueue: Drucker Büro über dem Café in der Königstraße ist nicht erreichbar\r\nBcc: evil@example.com";

        var message = new MailMessage(subject, "x").Format("a@example.org", "Druckerüberwachung", ["b@example.org"], DateTimeOffset.UnixEpoch);

        var headers = message[..message.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");
        Assert.All(headers, line => Assert.True(line.Length <= 78 && line.All(char.IsAscii), line));
        Assert.DoesNotContain(headers, line => line.StartsWith("Bcc", StringComparison.OrdinalIgnoreCase));
        var words = headers.SkipWhile(l => !l.StartsWith("Subject: ")).TakeWhile((l, i) => i == 0 || l.StartsWith(' '))
            .Select(l => l.Replace("Subject: ", "").Trim());
        var decoded = string.Concat(words.Select(w => Encoding.UTF8.GetString(Convert.FromBase64String(w["=?utf-8?B?".Length..^2]))));
        Assert.Equal("TapQueue: Drucker Büro über dem Café in der Königstraße ist nicht erreichbar  Bcc: evil@example.com", decoded);
        Assert.Contains("Date: Thu, 01 Jan 1970 00:00:00 +0000", headers);
    }

    [Fact]
    public void BodyLinesAreShortAndNeverStartWithADot()
    {
        var message = new MailMessage("s", string.Join("\n", Enumerable.Repeat(". a line that starts with a dot and goes on for a while", 40)))
            .Format("a@example.org", "", ["b@example.org"], DateTimeOffset.UtcNow);

        Assert.Contains("From: a@example.org\r\n", message);
        Assert.All(message.Split("\r\n"), line => Assert.True(line.Length <= 78 && !line.StartsWith('.'), line));
    }
}
