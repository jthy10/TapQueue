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
    public async Task ServeAsync(ChannelWriter<UpdateCheck.Request> requests, UpdateCheck.SessionVerifier verify, ILogger logger, CancellationToken ct)
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
            _ = UpdateCheck.HandleAsync(pipe, requests, logger, ct, () => PcUser(pipe, logger), verify);
        }
    }

    /// <summary>
    /// The Windows account at the other end of the pipe, as DOMAIN\user, from the token Windows gives
    /// the service for it (the tray app connects allowing identification, not impersonation). Null for
    /// anonymous connections and for SYSTEM, which no tray app runs as.
    /// </summary>
    private static string? PcUser(NamedPipeServerStream pipe, ILogger logger)
    {
        SecurityIdentifier? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                if (!identity.IsAnonymous && !identity.IsSystem)
                    sid = identity.User;
            });
            // Looked up once back to the service's own account: an identification token can't be used to ask the domain.
            return sid?.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IdentityNotMappedException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Couldn't tell which PC user the tray app on the pipe runs as: {Error}", ex.Message);
            return null;
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

    /// <summary>
    /// Tray side: asks the service to vouch for this tray app's session with the server. Throws
    /// <see cref="TimeoutException"/> if the service isn't running.
    /// </summary>
    public static async Task<SessionVerifyReply> VerifySessionAsync(string sessionToken, CancellationToken ct)
    {
        await using var pipe = await ConnectAsync(ct);
        return await UpdateCheck.AskVerifySessionAsync(pipe, sessionToken, ct);
    }

    /// <summary>Lets the service see which Windows account connected (identification only; it can't act as us).</summary>
    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
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
