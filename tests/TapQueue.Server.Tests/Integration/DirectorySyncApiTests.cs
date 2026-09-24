using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Tests.ActiveDirectory;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Setting up the AD sync from the admin API, and what admins can't change on users AD owns.</summary>
public sealed class DirectorySyncApiTests
{
    private readonly FakeDirectory _ad = new();

    private Task<TestServer> StartAsync() =>
        TestServer.StartAsync(configureServices: services => services.AddSingleton<IDirectorySourceFactory>(_ad));

    private static async Task<T> Post<T>(TestServer server, string path, object body) =>
        await TestServer.ReadAsync<T>(await server.Admin.PostAsJsonAsync(path, body, TapQueueJson.Options));

    [Fact]
    public async Task SetUpPreviewAndSync()
    {
        var staff = _ad.Ou("Staff");
        _ad.User("alice", staff.Dn, "Alice Anders");
        var bob = _ad.User("bob", staff.Dn, disabled: true);
        var admins = _ad.Group("Print-Admins", staff.Dn, bob);
        await using var server = await StartAsync();

        var status = await TestServer.ReadAsync<DirectoryStatusDto>(await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config",
            new UpdateDirectoryConfigRequest(Enabled: true, Host: "dc01.lab", BindDn: "svc@lab", Password: "secret"), TapQueueJson.Options));
        var test = await Post<DirectoryTestResultDto>(server, "/api/v1/admin/directory/test", new { });
        var found = await server.Admin.GetFromJsonAsync<List<DirectoryObjectDto>>("/api/v1/admin/directory/search?kind=ou&q=sta", TapQueueJson.Options);
        await Post<DirectoryScopeDto>(server, "/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Guid: found!.Single().Guid));
        await Post<DirectoryScopeDto>(server, "/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Dn: admins.Dn));
        var preview = await Post<DirectorySyncResultDto>(server, "/api/v1/admin/directory/sync", new DirectorySyncRequest(DryRun: true));
        var usersBefore = await server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options);
        var synced = await Post<DirectorySyncResultDto>(server, "/api/v1/admin/directory/sync", new DirectorySyncRequest());
        var users = await server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options);
        var after = await server.Admin.GetFromJsonAsync<DirectoryStatusDto>("/api/v1/admin/directory", TapQueueJson.Options);

        Assert.True(status.Config.HasPassword);
        Assert.DoesNotContain("secret", await server.Admin.GetStringAsync("/api/v1/admin/directory"));
        Assert.True(test.Success);
        Assert.Equal(3, preview.Changes.Count); // alice, bob, the group
        Assert.Empty(usersBefore!);
        Assert.True(synced.Applied);
        Assert.Equal(["alice", "bob"], users!.Select(u => u.Username));
        Assert.Equal(("directory", "disabled"), (users[1].DisabledBy, users[1].DirectoryState));
        Assert.Equal(["print-admins"], users[1].Groups);
        Assert.Equal(2, after!.Runs.Count);
        Assert.NotNull(after.NextRunAt);
    }

    [Fact]
    public async Task AdminsCantChangeWhatAdOwns()
    {
        var staff = _ad.Ou("Staff");
        var alice = _ad.User("alice", staff.Dn, disabled: true);
        _ad.User("bob", staff.Dn);
        var group = _ad.Group("Staff", staff.Dn, alice);
        await using var server = await StartAsync();
        await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config",
            new UpdateDirectoryConfigRequest(Host: "dc01.lab", BindDn: "svc@lab", Password: "secret"), TapQueueJson.Options);
        await Post<DirectoryScopeDto>(server, "/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Guid: staff.Guid));
        await Post<DirectoryScopeDto>(server, "/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Guid: group.Guid));
        await Post<DirectorySyncResultDto>(server, "/api/v1/admin/directory/sync", new DirectorySyncRequest());

        var enable = await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/alice", new UpdateUserRequest(Disabled: false), TapQueueJson.Options);
        var rename = await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/bob", new UpdateUserRequest(DisplayName: "Robert"), TapQueueJson.Options);
        var delete = await server.Admin.DeleteAsync("/api/v1/admin/users/bob");
        var addMember = await server.Admin.PutAsync("/api/v1/admin/groups/staff/members/bob", null);
        var deleteGroup = await server.Admin.DeleteAsync("/api/v1/admin/groups/staff");
        var restrict = await server.Admin.PatchAsJsonAsync("/api/v1/admin/groups/staff", new UpdateGroupRequest(AllQueues: false), TapQueueJson.Options);
        var disable = await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/bob", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, enable.StatusCode);
        Assert.Contains("enable them there", await enable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, addMember.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteGroup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, restrict.StatusCode); // what it may use is TapQueue's
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode); // an admin may still disable
    }

    [Fact]
    public async Task ScopeItemsMustBeOusGroupsOrUsers()
    {
        await using var server = await StartAsync();
        var missing = await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope",
            new AddDirectoryScopeRequest(Dn: "OU=Nope,DC=lab,DC=example,DC=org"), TapQueueJson.Options);
        _ad.ConnectError = "Couldn't connect to dc01.";
        var unreachable = await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope",
            new AddDirectoryScopeRequest(Dn: "OU=Staff,DC=lab,DC=example,DC=org"), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
    }
}
