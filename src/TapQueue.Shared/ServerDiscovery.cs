using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TapQueue.Shared;

/// <summary>What a server says about itself when asked on the discovery port.</summary>
public sealed record DiscoveryReply(string Service, string Name, int Port, string Version);

/// <summary>A TapQueue server found on the network.</summary>
public sealed record DiscoveredServer(string Url, string Name, string Version);

/// <summary>
/// Finds TapQueue servers on the local network, for the Windows installer and anything else that
/// would otherwise ask for the server's address. Two ways, at the same time:
///   1. A UDP broadcast to <see cref="DefaultPort"/>; servers answer with a <see cref="DiscoveryReply"/>.
///   2. A sweep of every address on the local subnets for an HTTP server on <see cref="DefaultHttpPort"/>
///      that says it's TapQueue, for servers whose firewall only lets TCP 8631 in.
/// Neither crosses routers, so servers on another subnet still have to be typed in.
/// </summary>
public static class ServerDiscovery
{
    /// <summary>UDP port servers listen on for <see cref="Request"/>. Same number as the HTTP port.</summary>
    public const int DefaultPort = 8631;
    public const int DefaultHttpPort = 8631;
    public const string Request = "TAPQUEUE-DISCOVER 1";
    public const string ServiceName = "tapqueue";

    /// <summary>Subnets bigger than this (a /22) aren't swept, only broadcast to.</summary>
    private const int MaxSweepHosts = 1022;

    public static byte[] RequestBytes => Encoding.ASCII.GetBytes(Request);

    public static bool IsRequest(ReadOnlySpan<byte> datagram) => datagram.SequenceEqual(RequestBytes);

    public static byte[] EncodeReply(DiscoveryReply reply) => JsonSerializer.SerializeToUtf8Bytes(reply, TapQueueJson.Options);

    public static DiscoveryReply? ParseReply(ReadOnlySpan<byte> datagram)
    {
        try
        {
            var reply = JsonSerializer.Deserialize<DiscoveryReply>(datagram, TapQueueJson.Options);
            return reply is { Service: ServiceName, Port: > 0 and <= 65535 } ? reply : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every server that answers within <paramref name="timeout"/>, one per address, sorted by name.</summary>
    public static async Task<IReadOnlyList<DiscoveredServer>> FindAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        var found = new ConcurrentDictionary<IPAddress, (string Name, int Port, string Version)>();
        var subnets = LocalSubnets();

        await Task.WhenAll(BroadcastAsync(subnets, found, deadline.Token), SweepAsync(subnets, found, deadline.Token));

        var servers = await Task.WhenAll(found.Select(async entry =>
        {
            var host = await HostNameAsync(entry.Key, entry.Value.Name) ?? entry.Key.ToString();
            return new DiscoveredServer($"http://{host}:{entry.Value.Port}", entry.Value.Name, entry.Value.Version);
        }));
        return [.. servers.DistinctBy(s => s.Url).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Url)];
    }

    private sealed record Subnet(IPAddress Local, IPAddress Broadcast, uint Network, int PrefixLength);

    private static List<Subnet> LocalSubnets()
    {
        var subnets = new List<Subnet>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.PrefixLength is < 8 or > 30)
                    continue;
                var address = ToUInt(unicast.Address);
                var mask = uint.MaxValue << (32 - unicast.PrefixLength);
                subnets.Add(new Subnet(unicast.Address, ToAddress(address | ~mask), address & mask, unicast.PrefixLength));
            }
        }
        return subnets;
    }

    private static async Task BroadcastAsync(List<Subnet> subnets, ConcurrentDictionary<IPAddress, (string, int, string)> found, CancellationToken cancel)
    {
        // One socket per local address, so the broadcast goes out of every network the PC is on.
        var listeners = new List<Task>();
        foreach (var subnet in subnets)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            try
            {
                socket.Bind(new IPEndPoint(subnet.Local, 0));
                foreach (var target in new[] { subnet.Broadcast, IPAddress.Broadcast })
                    await socket.SendToAsync(RequestBytes, SocketFlags.None, new IPEndPoint(target, DefaultPort), cancel);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                continue;
            }
            listeners.Add(ReceiveRepliesAsync(socket, found, cancel));
        }
        await Task.WhenAll(listeners);
    }

    private static async Task ReceiveRepliesAsync(Socket socket, ConcurrentDictionary<IPAddress, (string, int, string)> found, CancellationToken cancel)
    {
        using var _ = socket;
        var buffer = new byte[2048];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancel);
                var from = ((IPEndPoint)result.RemoteEndPoint).Address;
                if (ParseReply(buffer.AsSpan(0, result.ReceivedBytes)) is { } reply)
                    found[from] = (reply.Name, reply.Port, reply.Version);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Time's up, or the network went away.
        }
    }

    private static async Task SweepAsync(List<Subnet> subnets, ConcurrentDictionary<IPAddress, (string, int, string)> found, CancellationToken cancel)
    {
        var hosts = subnets
            .Where(s => (1L << (32 - s.PrefixLength)) - 2 <= MaxSweepHosts)
            .SelectMany(s => Enumerable.Range(1, (1 << (32 - s.PrefixLength)) - 2).Select(i => ToAddress(s.Network + (uint)i)))
            .Distinct()
            .ToList();
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) });
        using var slots = new SemaphoreSlim(128);
        await Task.WhenAll(hosts.Select(async host =>
        {
            try
            {
                await slots.WaitAsync(cancel);
                try
                {
                    // GET / answers "TapQueue server <version>".
                    var body = await http.GetStringAsync($"http://{host}:{DefaultHttpPort}/", cancel);
                    const string prefix = "TapQueue server ";
                    if (body.StartsWith(prefix, StringComparison.Ordinal))
                        found.TryAdd(host, (host.ToString(), DefaultHttpPort, body[prefix.Length..].Trim()));
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Nothing there, or not TapQueue.
            }
        }));
    }

    /// <summary>
    /// A name for the server that resolves back to <paramref name="address"/> from this PC, so the
    /// saved address keeps working if the server's IP changes. Null when there isn't one.
    /// </summary>
    private static async Task<string?> HostNameAsync(IPAddress address, string advertisedName)
    {
        var candidates = new List<string>();
        if (!IPAddress.TryParse(advertisedName, out _))
            candidates.Add(advertisedName);
        try
        {
            var reverse = await Dns.GetHostEntryAsync(address.ToString()).WaitAsync(TimeSpan.FromSeconds(2));
            if (!IPAddress.TryParse(reverse.HostName, out _))
                candidates.Add(reverse.HostName.TrimEnd('.'));
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
        }
        foreach (var name in candidates)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(2));
                if (addresses.Contains(address))
                    return name.ToLowerInvariant();
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            {
            }
        }
        return null;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
