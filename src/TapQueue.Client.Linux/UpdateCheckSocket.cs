using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace TapQueue.Client.Linux;

/// <summary>
/// The Linux transport for <see cref="UpdateCheck"/>: the Unix socket <see cref="SocketPath"/>, in
/// the runtime directory systemd makes for the service. Anyone signed in may connect to ask for a check.
/// </summary>
public sealed class UpdateCheckSocket : IUpdateCheckListener
{
    public const string SocketPath = "/run/tapqueue-client/update.sock";

    public async Task ServeAsync(ChannelWriter<UpdateCheck.Request> requests, UpdateCheck.SessionVerifier verify, ILogger logger, CancellationToken ct)
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
                    var peer = connection;
                    _ = UpdateCheck.HandleAsync(new NetworkStream(connection, ownsSocket: true), requests, logger, ct, () => PcUser(peer, logger), verify);
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

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>The user at the other end of the socket, from the kernel (SO_PEERCRED), not from anything they sent.</summary>
    private static string? PcUser(Socket connection, ILogger logger)
    {
        try
        {
            Span<byte> credentials = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
            if (connection.GetRawSocketOption(SolSocket, SoPeerCred, credentials) < 12)
                return null;
            var uid = BinaryPrimitives.ReadUInt32LittleEndian(credentials[4..]);
            return UserName(uid);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            logger.LogWarning("Couldn't tell which user the tray app on the socket runs as: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>The login name of <paramref name="uid"/> (from /etc/passwd, LDAP or wherever NSS looks), or null.</summary>
    private static string? UserName(uint uid)
    {
        var passwd = Marshal.AllocHGlobal(128); // struct passwd is 48 bytes on 64-bit glibc and musl
        var buffer = Marshal.AllocHGlobal(16 * 1024);
        try
        {
            return getpwuid_r(uid, passwd, buffer, 16 * 1024, out var result) == 0 && result != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result)) // pw_name comes first
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(passwd);
        }
    }

    [DllImport("libc", SetLastError = false)]
    private static extern int getpwuid_r(uint uid, IntPtr pwd, IntPtr buf, nuint buflen, out IntPtr result);

    /// <summary>
    /// Tray side: asks the service to vouch for this tray app's session with the server. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<SessionVerifyReply> VerifySessionAsync(string sessionToken, CancellationToken ct)
    {
        await using var stream = await ConnectAsync(ct);
        return await UpdateCheck.AskVerifySessionAsync(stream, sessionToken, ct);
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
