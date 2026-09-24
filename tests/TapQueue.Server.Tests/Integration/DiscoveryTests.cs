using System.Net;
using System.Net.Sockets;
using TapQueue.Shared;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The server answering the Windows installer's "is there a TapQueue server here?".</summary>
public sealed class DiscoveryTests
{
    [Fact]
    public async Task TheServerAnswersADiscoveryRequestWithItsHttpPortAndVersion()
    {
        var discoveryPort = FreeUdpPort();
        await using var server = await TestServer.StartAsync(discoveryPort: discoveryPort);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await udp.SendAsync(ServerDiscovery.RequestBytes, new IPEndPoint(IPAddress.Loopback, discoveryPort));
        var answer = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var reply = ServerDiscovery.ParseReply(answer.Buffer);
        Assert.NotNull(reply);
        Assert.Equal(server.BaseUri.Port, reply.Port);
        Assert.Equal(TapQueueVersion.Current, reply.Version);
        Assert.Equal(Dns.GetHostName(), reply.Name);
    }

    [Fact]
    public async Task OtherDatagramsGetNoAnswer()
    {
        var discoveryPort = FreeUdpPort();
        await using var server = await TestServer.StartAsync(discoveryPort: discoveryPort);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await udp.SendAsync("hello"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, discoveryPort));

        await Assert.ThrowsAsync<TimeoutException>(() => udp.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Theory]
    [InlineData("""{"service":"tapqueue","name":"srv","port":8631,"version":"0.4.0"}""", true)]
    [InlineData("""{"service":"ipp","name":"srv","port":8631,"version":"0.4.0"}""", false)]
    [InlineData("""{"service":"tapqueue","name":"srv","port":0,"version":"0.4.0"}""", false)]
    [InlineData("not json", false)]
    public void OnlyTapQueueRepliesAreAccepted(string datagram, bool accepted) =>
        Assert.Equal(accepted, ServerDiscovery.ParseReply(System.Text.Encoding.UTF8.GetBytes(datagram)) is not null);

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
