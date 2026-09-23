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

    [Fact]
    public void WritesWhatItParses()
    {
        string[][] rows = [["a", "b,c"], ["\"quoted\"", " padded "]];

        Assert.Equal(rows, Csv.Parse(Csv.Write(rows)));
    }
}
