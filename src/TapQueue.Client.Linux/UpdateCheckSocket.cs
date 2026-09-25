using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Threading.Channels;

namespace TapQueue.Client.Linux;

/// <summary>
/// The Linux transport for <see cref="UpdateCheck"/>: the Unix socket <see cref="SocketPath"/>, in
/// the runtime directory systemd makes for the service. Anyone signed in may connect to ask for a check.
/// </summary>
public sealed class UpdateCheckSocket : IUpdateCheckListener
{
    public const string SocketPath = "/run/tapqueue-client/update.sock";

    public async Task ServeAsync(ChannelWriter<UpdateCheck.Request> requests, ILogger logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket listener;
            try
            {
                File.Delete(SocketPath); // left behind by a previous run
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                File.SetUnixFileMode(SocketPath, (UnixFileMode)0b110_110_110); // rw for everyone
                listener.Listen();
            }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
            {
                logger.LogWarning("Can't listen for update checks on {Path}: {Error}", SocketPath, ex.Message);
                if (!await DelayAsync(TimeSpan.FromSeconds(30), ct))
                    return;
                continue;
            }

            using (listener)
            {
                while (!ct.IsCancellationRequested)
                {
                    Socket connection;
                    try
                    {
                        connection = await listener.AcceptAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        logger.LogWarning("Update check listener failed: {Error}", ex.Message);
                        break;
                    }
                    _ = UpdateCheck.HandleAsync(new NetworkStream(connection, ownsSocket: true), requests, logger, ct);
                }
            }
            try
            {
                File.Delete(SocketPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Tray side: asks the service to check for updates and install one. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<UpdateCheckReply> CheckAsync(CancellationToken ct)
    {
        await using var stream = await ConnectAsync(ct);
        return await UpdateCheck.AskAsync(stream, ct);
    }

    /// <summary>
    /// Tray side: asks the service to remove and add this PC's printers again. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<PrinterRefreshReply> RefreshPrintersAsync(CancellationToken ct)
    {
        await using var stream = await ConnectAsync(ct);
        return await UpdateCheck.AskRefreshPrintersAsync(stream, ct);
    }

    private static async Task<NetworkStream> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
        }
        catch (SocketException ex) // no socket file, or nobody listening on it
        {
            socket.Dispose();
            throw new TimeoutException("The TapQueue service isn't running.", ex);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
