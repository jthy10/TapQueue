using System.Net;
using TapQueue.Server.Config;

namespace TapQueue.Server.Tests;

/// <summary>Whose address a request behind a reverse proxy is taken to come from.</summary>
public sealed class TrustedProxiesTests
{
    private static readonly List<IPNetwork> Proxies = new ServerSection { TrustedProxies = ["10.0.0.5", "10.9.0.0/16"] }.ParseTrustedProxies();

    private static string? Client(string remote, string forwardedFor, string proto = "") =>
        TrustedProxies.Resolve(IPAddress.Parse(remote), forwardedFor, proto, Proxies)?.Client.ToString();

    [Fact]
    public void AProxysForwardedAddressIsTheCaller() =>
        Assert.Equal("192.0.2.7", Client("10.0.0.5", "192.0.2.7"));

    [Fact]
    public void TheHeaderIsIgnoredFromAnyoneWhoIsntAProxy() =>
        Assert.Null(Client("192.0.2.66", "192.0.2.7"));

    [Fact]
    public void WhatTheCallerWroteAheadOfTheProxysEntryProvesNothing() =>
        // The caller sent "X-Forwarded-For: 127.0.0.1"; the proxy added the address it really came from.
        Assert.Equal("192.0.2.66", Client("10.0.0.5", "127.0.0.1, 192.0.2.66"));

    [Fact]
    public void AChainOfTrustedProxiesIsWalkedBackToTheFirstStranger() =>
        Assert.Equal("192.0.2.7", Client("10.0.0.5", "198.51.100.1, 192.0.2.7, 10.9.3.4"));

    [Fact]
    public void PortsAndMappedAddressesAreUnderstood()
    {
        Assert.Equal("192.0.2.7", Client("::ffff:10.0.0.5", "192.0.2.7:51234"));
        Assert.Equal("2001:db8::1", Client("10.0.0.5", "[2001:db8::1]:443"));
    }

    [Fact]
    public void AProxyThatSendsNoHeaderOrNonsenseIsTheCallerItself()
    {
        Assert.Null(Client("10.0.0.5", ""));
        Assert.Null(Client("10.0.0.5", "unknown"));
    }

    [Fact]
    public void TheSchemeComesFromTheProxyToo()
    {
        Assert.Equal("https", TrustedProxies.Resolve(IPAddress.Parse("10.0.0.5"), "192.0.2.7", "https", Proxies)?.Scheme);
        Assert.Equal("https", TrustedProxies.Resolve(IPAddress.Parse("10.0.0.5"), "192.0.2.7, 10.9.3.4", "https, http", Proxies)?.Scheme);
        Assert.Null(TrustedProxies.Resolve(IPAddress.Parse("10.0.0.5"), "192.0.2.7", "gopher", Proxies)?.Scheme);
    }

    [Fact]
    public void AProxyThatIsntAnAddressOrANetworkIsRefusedAtStartup()
    {
        var config = new ServerConfig { Admin = { Token = "a-long-test-token" }, Server = { TrustedProxies = ["proxy.example.com"] } };

        Assert.Contains("trusted_proxies", Assert.Throws<InvalidDataException>(config.Validate).Message);
    }
}
