using System.Net;
using System.Net.Sockets;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

/// <summary>Crash reports are saved first and sent when the server can be reached.</summary>
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("tapqueue-crash-").FullName;
    private readonly string? _oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

    public CrashReporterTests() => Environment.SetEnvironmentVariable("XDG_STATE_HOME", _state); // where the tray app's reports wait

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _oldState);
        Directory.Delete(_state, recursive: true);
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [Fact]
    public async Task AReportIsKeptUntilTheServerTakesIt()
    {
        var port = FreePort();
        var config = new ClientConfig { ServerUrl = $"http://127.0.0.1:{port}" };
        var pending = CrashReporter.PendingDirectory(CrashProgram.Tray);

        // Nothing listening yet: the report stays on disk.
        CrashReporter.Report(CrashProgram.Tray, new InvalidOperationException("Boom\nsecond line"), config);
        Assert.Single(Directory.GetFiles(pending, "*.json"));

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
            return (context.Request.Url!.AbsolutePath, body);
        });

        Assert.Equal(1, await CrashReporter.SendPendingAsync(CrashProgram.Tray, config, TimeSpan.FromSeconds(5)));
        var (path, json) = await received;
        Assert.Equal("/api/v1/client/crash", path);
        Assert.Contains("\"message\":\"InvalidOperationException: Boom\"", json);
        Assert.Contains("\"program\":\"tray\"", json);
        Assert.Empty(Directory.GetFiles(pending, "*.json"));
    }
}
