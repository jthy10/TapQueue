using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Tests.ActiveDirectory;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>The tray signing in with a domain account instead of as the PC's user (ClientSignIn.Domain).</summary>
public sealed class DomainSignInTests
{
    private readonly FakeDirectory _ad = new();
    private readonly DirectoryEntry _alice;
    private readonly DirectoryEntry _carol;

    public DomainSignInTests()
    {
        var staff = _ad.Ou("Staff");
        _alice = _ad.User("alice", staff.Dn, "Alice Anders");
        _ad.SetPassword(_alice, "alice-pw");
        // In AD, but not in the sync's scope.
        _carol = _ad.User("carol", _ad.Ou("Elsewhere").Dn);
        _ad.SetPassword(_carol, "carol-pw");
    }

    /// <summary>A server in token mode with alice synced from AD and domain sign-in on.</summary>
    private async Task<TestServer> StartAsync(string signIn = ClientSignIn.Domain)
    {
        var server = await TestServer.StartAsync(configureServices: services => services.AddSingleton<IDirectorySourceFactory>(_ad));
        await Patch(server, new UpdateDirectoryConfigRequest(Host: "dc01.lab", BindDn: "svc@lab", Password: "secret"));
        await TestServer.ReadAsync<DirectoryScopeDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope",
            new AddDirectoryScopeRequest(Guid: _alice.Guid), TapQueueJson.Options));
        await TestServer.ReadAsync<DirectorySyncResultDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/sync",
            new DirectorySyncRequest(), TapQueueJson.Options));
        await Patch(server, new UpdateDirectoryConfigRequest(ClientSignIn: signIn));
        return server;
    }

    private static async Task Patch(TestServer server, UpdateDirectoryConfigRequest request) =>
        await TestServer.ReadAsync<DirectoryStatusDto>(await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config", request, TapQueueJson.Options));

    private static async Task<HttpResponseMessage> SignIn(TestServer server, string username, string? password = null, string? remember = null)
    {
        using var anonymous = server.NewClient();
        return await anonymous.PostAsJsonAsync("/api/v1/client/session",
            new ClientSessionRequest(username, null, "pcuser", "TEST-PC", "test", ClientPlatform.Windows, password, remember), TapQueueJson.Options);
    }

    [Fact]
    public async Task PcSignInIsTheDefault()
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.NewClient();

        var info = await anonymous.GetFromJsonAsync<ClientSignInInfoDto>("/api/v1/client/sign-in", TapQueueJson.Options);
        var withoutAd = await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config",
            new UpdateDirectoryConfigRequest(ClientSignIn: ClientSignIn.Domain), TapQueueJson.Options);
        var bogus = await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config",
            new UpdateDirectoryConfigRequest(ClientSignIn: "badge"), TapQueueJson.Options);

        Assert.Equal(ClientSignIn.Pc, info!.Mode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutAd.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
    }

    [Fact]
    public async Task PasswordSignInIsRememberedUntilSignOut()
    {
        await using var server = await StartAsync();
        using var anonymous = server.NewClient();

        var info = await anonymous.GetFromJsonAsync<ClientSignInInfoDto>("/api/v1/client/sign-in", TapQueueJson.Options);
        var noPassword = await SignIn(server, "alice");
        var wrong = await SignIn(server, "alice", "nope");
        var outOfScope = await SignIn(server, "carol", "carol-pw");
        var first = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn(server, @"LAB\alice", "alice-pw"));
        var again = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn(server, "alice", remember: first.RememberToken));

        using var tray = server.NewClient(again.SessionToken);
        var jobs = await tray.GetAsync("/api/v1/me/jobs");
        var signOut = await tray.PostAsJsonAsync("/api/v1/client/sign-out", new ClientSignOutRequest(first.RememberToken), TapQueueJson.Options);
        var afterSignOut = await SignIn(server, "alice", remember: first.RememberToken);
        var sessionAfterSignOut = await tray.GetAsync("/api/v1/me/jobs");

        Assert.Equal(ClientSignIn.Domain, info!.Mode);
        Assert.Equal(HttpStatusCode.Unauthorized, noPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
        Assert.Equal("alice", first.User.Username);
        Assert.NotNull(first.RememberToken);
        Assert.Null(again.RememberToken); // the same one stays in use
        Assert.Equal("alice", again.User.Username);
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sessionAfterSignOut.StatusCode);
    }

    [Fact]
    public async Task AdminSignOutAndDisablingForgetTheRememberedSignIn()
    {
        await using var server = await StartAsync();

        var first = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn(server, "alice", "alice-pw"));
        var sessions = await server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options);
        await server.Admin.DeleteAsync($"/api/v1/admin/clients/{sessions!.Single().Id}");
        var afterAdminSignOut = await SignIn(server, "alice", remember: first.RememberToken);

        var second = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn(server, "alice", "alice-pw"));
        await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/alice", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        var afterDisable = await SignIn(server, "alice", remember: second.RememberToken);

        Assert.Equal(HttpStatusCode.Unauthorized, afterAdminSignOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
    }

    [Fact]
    public async Task UnreachableDomainControllerIsNotAWrongPassword()
    {
        await using var server = await StartAsync();
        _ad.ConnectError = "DC down";

        var response = await SignIn(server, "alice", "alice-pw");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PcModeIgnoresPasswords()
    {
        await using var server = await StartAsync(ClientSignIn.Pc);

        // Token mode: a PC-user sign-in needs the user's token, and a domain password isn't one.
        var response = await SignIn(server, "alice", "alice-pw");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
