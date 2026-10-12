using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using TapQueue.Server.Config;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The server.toml settings a restart applies, changed from the console and laid over the file at the next start.</summary>
public sealed class StartupSettingsTests : IAsyncLifetime
{
    private TestServer _server = null!;

    // A real port, as in a real server.toml: what's saved is checked the way the file is at startup, and that refuses port 0.
    private static Task<TestServer> StartAsync(string authMode = "token") =>
        TestServer.StartAsync(authMode, configure: config => config.Server.Listen = $"127.0.0.1:{FreePort()}");

    public async Task InitializeAsync() => _server = await StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<HttpResponseMessage> Patch(UpdateStartupSettingsRequest request) => Patch(_server, request);

    private static Task<HttpResponseMessage> Patch(TestServer server, UpdateStartupSettingsRequest request) =>
        server.Admin.PatchAsJsonAsync("/api/v1/admin/server/startup", request, TapQueueJson.Options);

    /// <summary>What a server started on this one's server.toml and data directory would run with.</summary>
    private ServerConfig AfterRestart() => StartupSettings.Effective(_server.Service<StartupSettings>().File);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task NothingIsSetHereUntilSomeoneChangesIt()
    {
        var startup = (await _server.Admin.GetFromJsonAsync<StartupSettingsDto>("/api/v1/admin/server/startup", TapQueueJson.Options))!;

        Assert.Empty(startup.SetHere);
        Assert.False(startup.RestartNeeded);
        Assert.Equal(startup.File, startup.Next with { TrustedProxies = startup.File.TrustedProxies, TlsNames = startup.File.TlsNames });
        Assert.Equal("token", startup.Running.AuthMode);
        Assert.Equal(_server.BaseUri.Authority, startup.Listen);
    }

    [Fact]
    public async Task AChangeIsKeptForTheNextStartAndTheRunningServerKeepsItsOwn()
    {
        var saved = await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(DiscoveryPort: 9000, TrustedProxies: ["10.0.0.5", " 10.0.1.0/24 "])));

        Assert.Equal(["discoveryPort", "trustedProxies"], saved.SetHere);
        Assert.True(saved.RestartNeeded);
        Assert.Equal(0, saved.Running.DiscoveryPort);
        Assert.Equal(9000, saved.Next.DiscoveryPort);
        Assert.Equal(["10.0.0.5", "10.0.1.0/24"], saved.Next.TrustedProxies);

        var next = AfterRestart();
        Assert.Equal(9000, next.Server.DiscoveryPort);
        Assert.Equal(["10.0.0.5", "10.0.1.0/24"], next.Server.TrustedProxies);
        // What the console doesn't offer stays the file's.
        Assert.Equal(TestServer.AdminToken, next.Admin.Token);
        Assert.Equal(_server.BaseUri.Authority, next.Server.Listen);

        var logged = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);
        Assert.Contains(logged!, e => e.Message.Contains("discovery on UDP port 9000") && e.Message.Contains("next restart"));
    }

    [Fact]
    public async Task AValueTheSameAsTheFilesFollowsTheFileAgain()
    {
        await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(DiscoveryPort: 9000, BackupCopyTo: "/mnt/nas/tapqueue")));

        var back = await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(DiscoveryPort: 0)));
        Assert.Equal(["backupCopyTo"], back.SetHere);

        var reset = await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(Reset: ["backupCopyTo"])));
        Assert.Empty(reset.SetHere);
        Assert.False(reset.RestartNeeded);
        Assert.Equal("", AfterRestart().Backup.CopyTo);
    }

    [Fact]
    public async Task ThePassphraseIsSavedButNeverSentBack()
    {
        var response = await Patch(new(BackupPassphrase: "correct horse battery"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("correct horse", body);
        Assert.True((await _server.Admin.GetFromJsonAsync<StartupSettingsDto>("/api/v1/admin/server/startup", TapQueueJson.Options))!.Next.BackupEncrypted);
        Assert.Equal("correct horse battery", AfterRestart().Backup.Passphrase);
        var logged = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);
        Assert.DoesNotContain(logged!, e => e.Message.Contains("correct horse"));
    }

    [Fact]
    public async Task WhatTheServerCouldntStartWithIsRefused()
    {
        async Task Refused(UpdateStartupSettingsRequest request, string mentions)
        {
            var response = await Patch(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(mentions, (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error);
        }

        await Refused(new(TrustedProxies: ["the proxy"]), "trusted_proxies");
        await Refused(new(TlsRequire: true), "tls.require"); // this server has no HTTPS
        await Refused(new(BackupPassphrase: "short"), "backup.passphrase");
        await Refused(new(AuthMode: "open"), "auth.mode");
        await Refused(new(DiscoveryPort: 70000), "discovery_port");
        await Refused(new(TlsListen: $"127.0.0.1:{FreePort()}", TlsCertFile: "/nowhere/server.crt", TlsKeyFile: "/nowhere/server.key"), "/nowhere/server.crt");
        await Refused(new(Reset: ["listen"]), "listen");

        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        await Refused(new(TlsListen: taken.LocalEndpoint.ToString()), "using that port");

        Assert.Empty(StartupSettings.Read(_server.Service<ServerConfig>().Server.DataDir));
    }

    [Fact]
    public async Task HttpsCanBeTurnedOnForTheNextStart()
    {
        var listen = $"127.0.0.1:{FreePort()}";

        var saved = await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(TlsListen: listen, TlsRequire: true, TlsNames: ["print.example.com"])));

        Assert.Equal("", saved.Running.TlsListen);
        Assert.Equal(listen, saved.Next.TlsListen);
        var next = AfterRestart();
        next.Validate();
        Assert.True(next.Tls.Require);
        Assert.Equal(["print.example.com"], next.Tls.Names);
    }

    [Fact]
    public async Task TokenModeNeedsSomeoneWhoCanSignInToTheConsole()
    {
        await using var dev = await StartAsync("dev");

        var refused = await Patch(dev, new(AuthMode: "token"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await TestServer.ReadAsync<UserTokenResponse>(await dev.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("olive", null), TapQueueJson.Options));
        (await dev.Admin.PutAsJsonAsync("/api/v1/admin/users/olive/password", new SetPasswordRequest("a long enough password"), TapQueueJson.Options)).EnsureSuccessStatusCode();
        (await dev.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("olive", null, AdminRole.Admin), TapQueueJson.Options)).EnsureSuccessStatusCode();

        var saved = await TestServer.ReadAsync<StartupSettingsDto>(await Patch(dev, new(AuthMode: "token")));
        Assert.Equal("dev", saved.Running.AuthMode);
        Assert.Equal("token", saved.Next.AuthMode);
        Assert.True(saved.RestartNeeded);
    }

    [Fact]
    public async Task ForgettingThemGoesBackToTheFile()
    {
        await TestServer.ReadAsync<StartupSettingsDto>(await Patch(new(DiscoveryPort: 9000, BackupCopyTo: "/mnt/nas/tapqueue")));
        var dataDir = _server.Service<ServerConfig>().Server.DataDir;

        Assert.Equal(["discoveryPort", "backupCopyTo"], StartupSettings.Forget(dataDir));

        Assert.Empty(StartupSettings.Read(dataDir));
        Assert.Equal(0, AfterRestart().Server.DiscoveryPort);
        Assert.Empty(StartupSettings.Forget(dataDir));
    }

    [Fact]
    public void ADataDirectoryWithNoDatabaseYetHasNoneSaved()
    {
        var dir = Directory.CreateTempSubdirectory("tapqueue-startup").FullName;
        try
        {
            var file = new ServerConfig { Server = { DataDir = dir }, Auth = { Mode = "dev" } };
            Assert.Equal("dev", StartupSettings.Effective(file).Auth.Mode);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
