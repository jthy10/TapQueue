using TapQueue.Server.Users;

namespace TapQueue.Server.Tests;

public sealed class CsvTests
{
    [Fact]
    public void ParsesQuotesCommasAndLineEndings()
    {
        var rows = Csv.Parse("username,display_name\r\nalice,\"Liddell, Alice\"\nbob,\"Bob \"\"the builder\"\"\"\n\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["alice", "Liddell, Alice"], rows[1]);
        Assert.Equal(["bob", "Bob \"the builder\""], rows[2]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("'quoted")]
    [InlineData("''=x")]
    [InlineData("plain")]
    public void GuardedFieldsCantStartAFormulaAndComeBackAsTheyWere(string field)
    {
        var written = Csv.Parse(Csv.Write([[field]], guardFormulas: true))[0][0];

        Assert.DoesNotMatch("^[=+\\-@]", written);
        Assert.Equal(field, Csv.Unguard(written));
    }

    [Fact]
    public void WritesWhatItParses()
    {
        string[][] rows = [["a", "b,c"], ["\"quoted\"", " padded "]];

        Assert.Equal(rows, Csv.Parse(Csv.Write(rows)));
    }
}
