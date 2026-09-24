using Microsoft.Extensions.Logging;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using TapQueue.Shared;

namespace TapQueue.Client.Windows;

/// <summary>What the service found when the tray app asked it to check for updates.</summary>
public sealed record UpdateCheckReply(UpdateOutcome Outcome, string? Version, string? Error);

/// <summary>
/// "Check for updates" in the tray menu. The tray app runs as the signed-in user and can't write
/// to Program Files, so it asks the TapQueue service over the local pipe <c>\\.\pipe\TapQueue</c>:
/// the tray app writes one line (<see cref="CheckCommand"/>), the service checks in with the
/// server straight away, installs the published build if it differs, and writes back one line of
/// JSON (<see cref="UpdateCheckReply"/>). The service only ever installs the build the server
/// publishes, checked against its SHA-256, so any signed-in user may ask.
/// </summary>
public static class UpdateCheckPipe
{
    public const string PipeName = "TapQueue";
    public const string CheckCommand = "check-updates";

    /// <summary>A check asked for by a tray app, waiting for the service's next check-in.</summary>
    public sealed class Request
    {
        public TaskCompletionSource<UpdateCheckReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the reply has been written (or couldn't be), so the service can restart after that.</summary>
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Service side: accepts connections until <paramref name="ct"/> is cancelled and queues each check on <paramref name="requests"/>.</summary>
    public static async Task ServeAsync(ChannelWriter<Request> requests, ILogger logger, CancellationToken ct)
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
            _ = HandleAsync(pipe, requests, logger, ct);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, ChannelWriter<Request> requests, ILogger logger, CancellationToken ct)
    {
        var request = new Request();
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (await reader.ReadLineAsync(readTimeout.Token) != CheckCommand)
                    return;

                logger.LogInformation("Checking for updates (asked from this PC)");
                await requests.WriteAsync(request, ct);
                var reply = await request.Reply.Task.WaitAsync(ct);

                // Written even while the service stops to restart into an update.
                using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(reply, TapQueueJson.Options), writeTimeout.Token);
                pipe.WriteByte((byte)'\n');
                await pipe.FlushAsync(writeTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException)
            {
                // The tray app went away, or the service is stopping.
            }
            finally
            {
                request.Sent.TrySetResult();
            }
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
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
        await using (var writer = new StreamWriter(pipe, leaveOpen: true))
            await writer.WriteLineAsync(CheckCommand.AsMemory(), ct);

        using var reader = new StreamReader(pipe);
        var line = await reader.ReadLineAsync(ct)
                   ?? throw new IOException("The TapQueue service stopped before it answered.");
        return JsonSerializer.Deserialize<UpdateCheckReply>(line, TapQueueJson.Options)
               ?? throw new InvalidDataException("The TapQueue service sent an empty answer.");
    }
}
