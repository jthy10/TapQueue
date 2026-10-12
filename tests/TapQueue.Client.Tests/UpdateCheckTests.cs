using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;
using System.Threading.Channels;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

public sealed class UpdateCheckTests
{
    [Fact]
    public async Task UpdateCheckGetsAnUpdateReply()
    {
        var (service, tray) = Connect();
        var requests = Channel.CreateUnbounded<UpdateCheck.Request>();
        var handling = UpdateCheck.HandleAsync(service, requests.Writer, NullLogger.Instance, CancellationToken.None);

        var asking = UpdateCheck.AskAsync(tray, CancellationToken.None);
        var request = await requests.Reader.ReadAsync();
        Assert.False(request.RefreshPrinters);
        request.Reply.SetResult(new UpdateCheckReply(UpdateOutcome.UpToDate, "1.0.0", null));

        Assert.Equal(UpdateOutcome.UpToDate, (await asking).Outcome);
        await handling;
    }

    [Fact]
    public async Task RefreshPrintersGetsAPrintersReply()
    {
        var (service, tray) = Connect();
        var requests = Channel.CreateUnbounded<UpdateCheck.Request>();
        var handling = UpdateCheck.HandleAsync(service, requests.Writer, NullLogger.Instance, CancellationToken.None);

        var asking = UpdateCheck.AskRefreshPrintersAsync(tray, CancellationToken.None);
        var request = await requests.Reader.ReadAsync();
        Assert.True(request.RefreshPrinters);
        request.PrintersReply.SetResult(new PrinterRefreshReply(true, 2, null));

        var reply = await asking;
        Assert.True(reply.Success);
        Assert.Equal(2, reply.Printers);
        await handling;
    }

    [Fact]
    public async Task VerifySessionPassesTheTokenAndTheOsUserNotAnythingTheTrayAppSays()
    {
        var (service, tray) = Connect();
        var requests = Channel.CreateUnbounded<UpdateCheck.Request>();
        (string Token, string PcUser)? asked = null;
        var handling = UpdateCheck.HandleAsync(service, requests.Writer, NullLogger.Instance, CancellationToken.None,
            pcUser: () => "CORP\\alice",
            verify: (token, pcUser, _) =>
            {
                asked = (token, pcUser);
                return Task.FromResult(new SessionVerifyReply(true, null));
            });

        var reply = await UpdateCheck.AskVerifySessionAsync(tray, "session-token", CancellationToken.None);
        await handling;

        Assert.True(reply.Success);
        Assert.Equal(("session-token", "CORP\\alice"), asked);
        Assert.False(requests.Reader.TryRead(out _)); // not a check-in
    }

    [Fact]
    public async Task VerifySessionFailsWhenTheServiceCantTellWhoIsAsking()
    {
        var (service, tray) = Connect();
        var requests = Channel.CreateUnbounded<UpdateCheck.Request>();
        var handling = UpdateCheck.HandleAsync(service, requests.Writer, NullLogger.Instance, CancellationToken.None,
            pcUser: () => null,
            verify: (_, _, _) => throw new InvalidOperationException("must not be called"));

        var reply = await UpdateCheck.AskVerifySessionAsync(tray, "session-token", CancellationToken.None);
        await handling;
        Assert.False(reply.Success);
    }

    [Fact]
    public async Task ALineThatNeverEndsIsDroppedNotReadIntoMemory()
    {
        var (service, tray) = Connect();
        var requests = Channel.CreateUnbounded<UpdateCheck.Request>();
        var verified = false;
        var handling = UpdateCheck.HandleAsync(service, requests.Writer, NullLogger.Instance, CancellationToken.None,
            pcUser: () => "CORP\\alice",
            verify: (_, _, _) =>
            {
                verified = true;
                return Task.FromResult(new SessionVerifyReply(true, null));
            });

        // Far more than the service reads; it hangs up, so the rest of the write may fail.
        var flood = new byte[8 * 1024 * 1024];
        Array.Fill(flood, (byte)'x');
        var writing = tray.WriteAsync(flood).AsTask();

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await writing;
            await tray.WriteAsync(flood);
        });
        Assert.False(verified);
        Assert.False(requests.Reader.TryRead(out _));
    }

    /// <summary>A connected pair of local sockets: the service's end and the tray app's end.</summary>
    private static (Stream Service, Stream Tray) Connect()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tapqueue-check").FullName, "s.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        var tray = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        tray.Connect(new UnixDomainSocketEndPoint(path));
        var service = listener.Accept();
        return (new NetworkStream(service, ownsSocket: true), new NetworkStream(tray, ownsSocket: true));
    }
}
