using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TapQueue.Server.Config;
using TapQueue.Shared;

namespace TapQueue.Server.Discovery;

/// <summary>
/// Answers <see cref="ServerDiscovery"/> broadcasts on UDP <c>server.discovery_port</c>, so the
/// Windows installer can offer this server without anyone typing its address.
/// </summary>
public sealed class DiscoveryResponder(ServerConfig config, IServer server, ILogger<DiscoveryResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.Server.DiscoveryPort == 0)
            return;
        var listen = ServerApp.ParseEndpoint(config.Server.Listen);
        using var socket = new Socket(listen.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(listen.Address, config.Server.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            logger.LogWarning("Discovery is off: can't listen on UDP port {Port} ({Error}). Clients can still type the server's address.",
                config.Server.DiscoveryPort, ex.Message);
            return;
        }

        var buffer = new byte[512];
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(listen.Address, 0), stoppingToken);
                if (!ServerDiscovery.IsRequest(buffer.AsSpan(0, result.ReceivedBytes)))
                    continue;
                var reply = new DiscoveryReply(ServerDiscovery.ServiceName, Dns.GetHostName(), HttpPort(listen), TapQueueVersion.Current);
                await socket.SendToAsync(ServerDiscovery.EncodeReply(reply), SocketFlags.None, result.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // E.g. ICMP "port unreachable" from a client that already gave up.
                logger.LogDebug("Discovery: {Error}", ex.Message);
            }
        }
    }

    /// <summary>The port Kestrel actually listens on (server.listen may say 0, as in tests).</summary>
    private int HttpPort(IPEndPoint listen)
    {
        if (listen.Port != 0)
            return listen.Port;
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is null ? listen.Port : new Uri(address).Port;
    }
}
