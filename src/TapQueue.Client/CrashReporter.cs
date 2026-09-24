using System.Net.Http.Json;
using System.Text.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>
/// Sends crash reports to the TapQueue server, so an admin sees on the console (and with
/// `tapqueue-admin crashes`) why a PC's TapQueue service or tray app stopped, without going to the PC.
/// Each report is saved first and deleted once the server has it, so one that couldn't be sent
/// (server unreachable, or the crash was reading client.toml) goes the next time the program starts.
/// </summary>
public static class CrashReporter
{
    /// <summary>Where reports wait to be sent: the service's state directory, or the user's own for the tray app.</summary>
    public static string PendingDirectory(string program) => program == CrashProgram.Service
        ? Path.Combine(ClientConfig.StateDirectory, "crash-reports")
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapQueue", "crash-reports")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
                ? state
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "tapqueue-client", "crash-reports");

    /// <summary>
    /// Saves a report of <paramref name="error"/> and tries to send it (and any saved earlier) for up
    /// to a few seconds. Never throws: it runs while the program is going down.
    /// </summary>
    public static void Report(string program, Exception error, ClientConfig? config)
    {
        try
        {
            var report = new CrashReportRequest(Environment.MachineName, program, ClientPlatform.Current, TapQueueVersion.Current,
                DateTimeOffset.UtcNow, FirstLine(error), error.ToString());
            var dir = PendingDirectory(program);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"),
                JsonSerializer.Serialize(report, TapQueueJson.Options));
            if (config is not null)
                SendPendingAsync(program, config, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TapQueue: couldn't save or send a crash report: {ex.Message}");
        }
    }

    /// <summary>
    /// For a crash before the program had its settings: reads client.toml (as <paramref name="args"/>
    /// say) to find the server if it can, and reports.
    /// </summary>
    public static void Report(string program, Exception error, string[] args)
    {
        ClientConfig? config = null;
        try
        {
            (config, _) = ClientConfig.Load(args);
        }
        catch (Exception)
        {
            // Saved, and sent once client.toml is readable again.
        }
        Report(program, error, config);
    }

    /// <summary>Sends saved reports, oldest first, deleting each once the server has it. Returns how many went.</summary>
    public static async Task<int> SendPendingAsync(string program, ClientConfig config, TimeSpan timeout)
    {
        var dir = PendingDirectory(program);
        if (!Directory.Exists(dir))
            return 0;
        using var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = timeout };
        var sent = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json").Order())
        {
            try
            {
                var report = JsonSerializer.Deserialize<CrashReportRequest>(await File.ReadAllTextAsync(file), TapQueueJson.Options);
                if (report is not null)
                {
                    using var response = await http.PostAsJsonAsync("api/v1/client/crash", report, TapQueueJson.Options);
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
                        break; // try again next time; a 400 means the server will never take it
                }
                File.Delete(file);
                sent++;
            }
            catch (JsonException)
            {
                File.Delete(file); // unreadable; nothing to send
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                break;
            }
        }
        return sent;
    }

    private static string FirstLine(Exception error)
    {
        var inner = error;
        while (inner is AggregateException or System.Reflection.TargetInvocationException && inner.InnerException is { } next)
            inner = next;
        var line = $"{inner.GetType().Name}: {inner.Message}";
        var newline = line.IndexOfAny(['\r', '\n']);
        return newline < 0 ? line : line[..newline];
    }
}
