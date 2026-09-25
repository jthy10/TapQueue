using Microsoft.Extensions.Logging;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;

namespace TapQueue.Client.Windows;

/// <summary>
/// The Windows transport for <see cref="UpdateCheck"/>: the local pipe <c>\\.\pipe\TapQueue</c>.
/// </summary>
public sealed class UpdateCheckPipe : IUpdateCheckListener
{
    public const string PipeName = "TapQueue";

    /// <summary>Service side: accepts connections until <paramref name="ct"/> is cancelled and queues each check on <paramref name="requests"/>.</summary>
    public async Task ServeAsync(ChannelWriter<UpdateCheck.Request> requests, ILogger logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                logger.LogWarning("Can't listen for update checks: {Error}", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            _ = UpdateCheck.HandleAsync(pipe, requests, logger, ct);
        }
    }

    /// <summary>SYSTEM and administrators own the pipe; any signed-in user may connect to ask for a check.</summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Tray side: asks the service to check for updates and install one. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<UpdateCheckReply> CheckAsync(CancellationToken ct)
    {
        await using var pipe = await ConnectAsync(ct);
        return await UpdateCheck.AskAsync(pipe, ct);
    }

    /// <summary>
    /// Tray side: asks the service to remove and add this PC's printers again. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<PrinterRefreshReply> RefreshPrintersAsync(CancellationToken ct)
    {
        await using var pipe = await ConnectAsync(ct);
        return await UpdateCheck.AskRefreshPrintersAsync(pipe, ct);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
