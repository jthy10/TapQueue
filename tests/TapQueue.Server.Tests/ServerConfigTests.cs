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

    [Fact]
    public void TlsIsOnByDefaultWithASelfSignedCertificate()
    {
        var config = TapQueue.Shared.TomlConfig.Load<ServerConfig>(Path.Combine(RepoRoot(), "config", "server.example.toml"));
        Assert.Equal("0.0.0.0:8632", config.Tls.Listen);
        Assert.Equal("", config.Tls.CertFile);
        Assert.False(config.Tls.Require);
        Assert.Equal("0.0.0.0:8632", new ServerConfig().Tls.Listen);
    }

    [Theory]
    [InlineData("0.0.0.0:8631", "", "", false)] // same port as plain HTTP
    [InlineData("8632", "", "", false)]
    [InlineData("0.0.0.0:8632", "/etc/tapqueue/cert.pem", "", false)] // a certificate without its key
    [InlineData("", "", "", true)] // requiring TLS with no TLS
    public void TlsSettingsMustMakeSense(string listen, string certFile, string keyFile, bool require)
    {
        var config = new ServerConfig { Admin = { Token = "secret" }, Tls = { Listen = listen, CertFile = certFile, KeyFile = keyFile, Require = require } };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void TlsCanBeTurnedOff()
    {
        var config = new ServerConfig { Admin = { Token = "secret" }, Tls = { Listen = "" } };
        config.Validate();
        Assert.False(config.Tls.Enabled);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "TapQueue.slnx")))
            dir = dir.Parent!;
        return dir.FullName;
    }
}
