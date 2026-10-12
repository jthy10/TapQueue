namespace TapQueue.Client.Tests;

/// <summary>The command line one tray app hands the next when it restarts.</summary>
public sealed class RestartArgsTests
{
    [Fact]
    public void AnOrdinaryCommandLineIsLeftAlone()
    {
        var parsed = RestartArgs.Parse(["--config", "client.toml"]);

        Assert.Equal(["--config", "client.toml"], parsed.AppArgs);
        Assert.Equal((null, null, null, false), (parsed.WaitFor, parsed.UpdatedFrom, parsed.Refreshed, parsed.RefreshFailed));
    }

    [Fact]
    public void AnUpdateRestartSaysWhatItReplaced()
    {
        var parsed = RestartArgs.Parse([.. RestartArgs.AfterUpdate(["--config", "client.toml"], 4321, "0.9.7+abc")]);

        Assert.Equal(["--config", "client.toml"], parsed.AppArgs);
        Assert.Equal((4321, "0.9.7+abc", null), (parsed.WaitFor, parsed.UpdatedFrom, parsed.Refreshed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefreshRestartCarriesWhatHappenedToThePrinters(bool failed)
    {
        var parsed = RestartArgs.Parse([.. RestartArgs.AfterRefresh([], 7, "2 printers reinstalled.", failed)]);

        Assert.Empty(parsed.AppArgs);
        Assert.Equal((7, null, "2 printers reinstalled.", failed), (parsed.WaitFor, parsed.UpdatedFrom, parsed.Refreshed, parsed.RefreshFailed));
    }

    [Fact]
    public void ARestartArgumentWithoutItsValueIsNotOne()
    {
        Assert.Equal(["--refreshed"], RestartArgs.Parse(["--refreshed"]).AppArgs);
        Assert.Equal(["--wait-for", "soon"], RestartArgs.Parse(["--wait-for", "soon"]).AppArgs);
    }

    [Fact]
    public void TheServicesAnswerBecomesWhatTheUserIsTold()
    {
        Assert.Equal(("TapQueue reloaded and 1 TapQueue printer reinstalled.", false), RestartArgs.Describe(new PrinterRefreshReply(true, 1, null)));
        Assert.Contains("no printers for this PC", RestartArgs.Describe(new PrinterRefreshReply(true, 0, null)).Text);
        var failed = RestartArgs.Describe(new PrinterRefreshReply(false, 0, "lpadmin said no"));
        Assert.True(failed.Failed);
        Assert.EndsWith("couldn't be reinstalled: lpadmin said no", failed.Text);
    }
}
