using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Channels;
using TapQueue.Shared;

namespace TapQueue.Client;

/// <summary>What the service found when the tray app asked it to check for updates.</summary>
public sealed record UpdateCheckReply(UpdateOutcome Outcome, string? Version, string? Error);

/// <summary>What the service did when the tray app asked it to refresh printers: how many it reinstalled, or why it couldn't.</summary>
public sealed record PrinterRefreshReply(bool Success, int Printers, string? Error);

/// <summary>
/// "Check for updates" in the tray menu. The tray app runs as the signed-in user and can't write
/// to where the client is installed, so it asks the TapQueue service over a local connection
/// (<see cref="IUpdateCheckListener"/>: a named pipe on Windows, a Unix socket on Linux): the tray
/// app writes one line (<see cref="CheckCommand"/>), the service checks in with the server straight
/// away, installs the published build if it differs, and writes back one line of JSON
/// (<see cref="UpdateCheckReply"/>). The service only ever installs the build the server
/// publishes, checked against its SHA-256, so any signed-in user may ask.
/// "Refresh printers" goes the same way (<see cref="RefreshPrintersCommand"/>): the service checks
/// in, removes and adds every TapQueue printer again, and answers with a <see cref="PrinterRefreshReply"/>.
/// </summary>
public static class UpdateCheck
{
    public const string CheckCommand = "check-updates";
    public const string RefreshPrintersCommand = "refresh-printers";

    /// <summary>A check (or printer refresh) asked for by a tray app, waiting for the service's next check-in.</summary>
    public sealed class Request
    {
        /// <summary>True for "Refresh printers", answered through <see cref="PrintersReply"/> instead of <see cref="Reply"/>.</summary>
        public bool RefreshPrinters { get; init; }

        public TaskCompletionSource<UpdateCheckReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PrinterRefreshReply> PrintersReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the reply has been written (or couldn't be), so the service can restart after that.</summary>
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Service side: answers one tray app connected on <paramref name="connection"/>.</summary>
    public static async Task HandleAsync(Stream connection, ChannelWriter<Request> requests, ILogger logger, CancellationToken ct)
    {
        Request? request = null;
        await using (connection)
        {
            try
            {
                using var reader = new StreamReader(connection, leaveOpen: true);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var command = await reader.ReadLineAsync(readTimeout.Token);
                if (command is not (CheckCommand or RefreshPrintersCommand))
                    return;

                request = new Request { RefreshPrinters = command == RefreshPrintersCommand };
                logger.LogInformation(request.RefreshPrinters ? "Refreshing printers (asked from this PC)" : "Checking for updates (asked from this PC)");
                await requests.WriteAsync(request, ct);
                var reply = request.RefreshPrinters
                    ? JsonSerializer.SerializeToUtf8Bytes(await request.PrintersReply.Task.WaitAsync(ct), TapQueueJson.Options)
                    : JsonSerializer.SerializeToUtf8Bytes(await request.Reply.Task.WaitAsync(ct), TapQueueJson.Options);

                // Written even while the service stops to restart into an update.
                using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await connection.WriteAsync(reply, writeTimeout.Token);
                connection.WriteByte((byte)'\n');
                await connection.FlushAsync(writeTimeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException)
            {
                // The tray app went away, or the service is stopping.
            }
            finally
            {
                request?.Sent.TrySetResult();
            }
        }
    }

    /// <summary>Tray side: asks the service connected on <paramref name="connection"/> to check for updates and install one.</summary>
    public static Task<UpdateCheckReply> AskAsync(Stream connection, CancellationToken ct) =>
        SendAsync<UpdateCheckReply>(connection, CheckCommand, ct);

    /// <summary>Tray side: asks the service connected on <paramref name="connection"/> to remove and add this PC's printers again.</summary>
    public static Task<PrinterRefreshReply> AskRefreshPrintersAsync(Stream connection, CancellationToken ct) =>
        SendAsync<PrinterRefreshReply>(connection, RefreshPrintersCommand, ct);

    private static async Task<T> SendAsync<T>(Stream connection, string command, CancellationToken ct)
    {
        await using (var writer = new StreamWriter(connection, leaveOpen: true))
            await writer.WriteLineAsync(command.AsMemory(), ct);

        using var reader = new StreamReader(connection, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct)
                   ?? throw new IOException("The TapQueue service stopped before it answered.");
        return JsonSerializer.Deserialize<T>(line, TapQueueJson.Options)
               ?? throw new InvalidDataException("The TapQueue service sent an empty answer.");
    }
}

/// <summary>Accepts tray apps' update checks for the TapQueue service (see <see cref="UpdateCheck"/>).</summary>
public interface IUpdateCheckListener
{
    /// <summary>Accepts connections until <paramref name="ct"/> is cancelled, handing each to <see cref="UpdateCheck.HandleAsync"/>.</summary>
    Task ServeAsync(ChannelWriter<UpdateCheck.Request> requests, ILogger logger, CancellationToken ct);
}
