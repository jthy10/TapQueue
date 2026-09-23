using TapQueue.Server.Config;

namespace TapQueue.Server.Tests;

public sealed class ServerConfigTests
{
    [Theory]
    [InlineData("[[printers]]\nid = \"office\"\n", "[[printers]]")]
    [InlineData("[server]\nlisten = \"0.0.0.0:8631\"\n\n  [[ queues ]]\nid = \"secure\"\n", "[[queues]]")]
    public void OldQueueAndPrinterSectionsAreAnError(string toml, string mentioned)
    {
        var error = Assert.Throws<InvalidDataException>(() => ServerConfig.RejectRemovedSections(toml));
        Assert.Contains(mentioned, error.Message);
    }

    [Fact]
    public void CurrentConfigIsFine() =>
        ServerConfig.RejectRemovedSections(File.ReadAllText(Path.Combine(RepoRoot(), "config", "server.example.toml")));

    [Theory]
    [InlineData("0.0.0.0:0")]
    [InlineData("localhost:8631")]
    [InlineData("8631")]
    public void ListenMustBeAnAddressAndPort(string listen)
    {
        var config = new ServerConfig { Server = { Listen = listen }, Admin = { Token = "secret" } };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "TapQueue.slnx")))
            dir = dir.Parent!;
        return dir.FullName;
    }
}
