using Microsoft.Extensions.Logging.Abstractions;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Tests;

public sealed class PrinterSyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-printers").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Remembers what it was asked to add and remove instead of touching the PC's printers.</summary>
    private sealed class FakeInstaller : IPrinterInstaller
    {
        public Dictionary<string, string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; } = [];

        public Task<(bool Success, string Output)> EnsureInstalledAsync(string name, Uri ippUrl, bool reinstall)
        {
            Installed[name] = ippUrl.ToString();
            return Task.FromResult((true, "installed"));
        }

        public Task<(bool Success, string Output)> RemoveAsync(string name)
        {
            Installed.Remove(name);
            Removed.Add(name);
            return Task.FromResult((true, "removed"));
        }
    }

    private static readonly Uri Server = new("http://server:8631/");

    private PrinterSync NewSync(FakeInstaller installer) =>
        new(installer, NullLogger.Instance, Path.Combine(_dir, "printers.txt"));

    [Fact]
    public async Task AddsTheServersQueuesAndRemovesTheOnesThatWentAway()
    {
        var installer = new FakeInstaller();
        Assert.Null(await NewSync(installer).SyncAsync(
            [new QueueDto("secure", "Secure Print", "", "/ipp/secure"), new QueueDto("color", "Color", "", "/ipp/color")], Server));
        Assert.Equal("http://server:8631/ipp/secure", installer.Installed["Secure Print"]);
        Assert.Equal(2, installer.Installed.Count);

        // A new service (after a restart) knows what the last one added from printers.txt.
        Assert.Null(await NewSync(installer).SyncAsync([new QueueDto("secure", "Secure Print", "", "/ipp/secure")], Server));
        Assert.Equal(["Color"], installer.Removed);
        Assert.Equal(["Secure Print"], installer.Installed.Keys);
    }

    [Fact]
    public async Task TwoQueuesWithOneNameDontStopTheService()
    {
        // A server older than 0.10 lets an admin give two queues the same name, which a PC can only have one printer for.
        var installer = new FakeInstaller();
        var sync = NewSync(installer);
        QueueDto[] queues =
        [
            new("first", "Secure Print", "", "/ipp/first"),
            new("second", "secure print", "", "/ipp/second"),
            new("other", "Color", "", "/ipp/other"),
        ];

        Assert.Null(await sync.SyncAsync(queues, Server));
        Assert.Null(await sync.SyncAsync(queues, Server));

        Assert.Equal("http://server:8631/ipp/first", installer.Installed["Secure Print"]);
        Assert.Equal("http://server:8631/ipp/other", installer.Installed["Color"]);
        Assert.Equal(2, installer.Installed.Count);
    }
}
