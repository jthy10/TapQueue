using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Admins;
using TapQueue.Server.Tests.ActiveDirectory;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Signing in to the admin console, and what each role may do there (AdminAccess).</summary>
public sealed class AdminRolesTests
{
    private const string Password = "correct horse battery";

    [Theory]
    [InlineData("GET", "/jobs", AdminArea.Jobs, AdminRole.Viewer)]
    [InlineData("DELETE", "/jobs/5", AdminArea.Jobs, AdminRole.Operator)]
    [InlineData("POST", "/release", AdminArea.Jobs, AdminRole.Operator)]
    [InlineData("POST", "/badges", AdminArea.People, AdminRole.Operator)]
    [InlineData("PATCH", "/users/alice", AdminArea.People, AdminRole.Admin)]
    [InlineData("PATCH", "/Printers/office", AdminArea.Fleet, AdminRole.Admin)]
    [InlineData("POST", "/stations/office/restart", AdminArea.Fleet, AdminRole.Operator)]
    [InlineData("POST", "/directory/sync", AdminArea.Directory, AdminRole.Operator)]
    [InlineData("PATCH", "/directory/config", AdminArea.Directory, AdminRole.Admin)]
    [InlineData("POST", "/client-builds", AdminArea.Updates, AdminRole.Admin)]
    [InlineData("POST", "/server/restart", AdminArea.Server, AdminRole.Admin)]
    public void EachEndpointNeedsARoleInItsArea(string method, string path, string area, string role)
    {
        Assert.Equal(new AdminRequirement(area, role), AdminAccess.Requirement(method, path));
    }

    [Theory]
    [InlineData("GET", "/admins")]
    [InlineData("POST", "/admins/grants")]
    [InlineData("PUT", "/users/alice/password")]
    [InlineData("PUT", "/users/alice/PASSWORD")]
    [InlineData("GET", "/something-new")]
    public void AdminsPasswordsAndUnknownPathsNeedAFullAdmin(string method, string path)
    {
        Assert.Equal(AdminRequirement.FullAdminOnly, AdminAccess.Requirement(method, path));
    }

    [Theory]
    [InlineData("/server")]
    [InlineData("/events")]
    public void AnyAdminReadsTheServerAndTheActivityLog(string path)
    {
        Assert.Equal(AdminRequirement.AnyAdmin, AdminAccess.Requirement("GET", path));
    }

    [Fact]
    public void GrantsAddUpToTheHighestRolePerArea()
    {
        var permissions = AdminPermissions.From([(AdminArea.All, AdminRole.Viewer), (AdminArea.Jobs, AdminRole.Operator)]);

        Assert.Equal(AdminRole.Operator, permissions.Roles[AdminArea.Jobs]);
        Assert.Equal(AdminRole.Viewer, permissions.Roles[AdminArea.Server]);
        Assert.False(permissions.IsFullAdmin);
        Assert.True(AdminPermissions.From([(AdminArea.All, AdminRole.Admin)]).IsFullAdmin);
    }

    [Fact]
    public void PasswordsHashAndVerify()
    {
        var hash = PasswordHasher.Hash(Password);

        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(PasswordHasher.Verify(Password, hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify(Password, null));
        Assert.NotNull(PasswordHasher.Problem("short"));
    }

    [Fact]
    public async Task AnOperatorForJobsCancelsJobsButCantSeePeople()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "olive", AdminRole.Operator, [AdminArea.Jobs]);
        using var tray = await server.SignInAsync("alice");
        await server.PrintAsync("report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        using var olive = await Console(server, "olive");

        var me = await olive.GetFromJsonAsync<AdminMeDto>("/api/v1/admin-auth/me", TapQueueJson.Options);
        var job = (await olive.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs?status=held", TapQueueJson.Options))!.Single();
        var users = await olive.GetAsync("/api/v1/admin/users");
        var canceled = await olive.DeleteAsync($"/api/v1/admin/jobs/{job.Id}");
        var activity = await olive.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);

        Assert.Equal("olive", me!.Username);
        Assert.Equal(AdminRole.Operator, me.Roles[AdminArea.Jobs]);
        Assert.False(me.FullAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        Assert.Contains("People", (await users.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error);
        Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
        Assert.Contains(activity!, e => e.Actor == "olive" && e.Message.Contains($"job #{job.Id}"));
    }

    [Fact]
    public async Task ChangesWithoutTheConsoleHeaderAreRefused()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Admin);
        using var ada = await Console(server, "ada");
        ada.DefaultRequestHeaders.Remove(AdminAccess.ConsoleHeader);

        var read = await ada.GetAsync("/api/v1/admin/users");
        var change = await ada.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("mallory", null), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
    }

    [Fact]
    public async Task SignInNeedsTheRightPasswordAndARole()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Admin);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("nobody", null), TapQueueJson.Options);
        await server.Admin.PutAsJsonAsync("/api/v1/admin/users/nobody/password", new SetPasswordRequest(Password), TapQueueJson.Options);

        var wrong = await SignIn(server, "ada", "not the password");
        var unknown = await SignIn(server, "ghost", Password);
        var noRole = await SignIn(server, "nobody", Password);
        var right = await SignIn(server, "ada", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Contains(AdminAccess.CookieName, right.Headers.GetValues("Set-Cookie").Single());
        Assert.Contains("httponly", right.Headers.GetValues("Set-Cookie").Single());
    }

    [Fact]
    public async Task FiveWrongPasswordsLockTheNameForAWhile()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Admin);

        for (var i = 0; i < 5; i++)
            await SignIn(server, "ada", "guess " + i);
        var locked = await SignIn(server, "ada", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task SigningOutAndBeingDisabledEndTheSession()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Admin);
        await AddAdmin(server, "bea", AdminRole.Viewer);
        using var ada = await Console(server, "ada");
        using var bea = await Console(server, "bea");

        await ada.PostAsync("/api/v1/admin-auth/sign-out", null);
        var afterSignOut = await ada.GetAsync("/api/v1/admin/jobs");
        await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/bea", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        var afterDisable = await bea.GetAsync("/api/v1/admin/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
    }

    [Fact]
    public async Task TheLastFullAdminCantBeRemovedOutsideDevMode()
    {
        await using var server = await TestServer.StartAsync();
        var ada = await AddAdmin(server, "ada", AdminRole.Admin);

        var revoke = await server.Admin.DeleteAsync($"/api/v1/admin/admins/grants/{ada.Id}");
        var disable = await server.Admin.PatchAsJsonAsync("/api/v1/admin/users/ada", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        var noPassword = await server.Admin.PutAsJsonAsync("/api/v1/admin/users/ada/password", new SetPasswordRequest(null), TapQueueJson.Options);
        var demote = await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants",
            new GrantAdminRequest("ada", null, AdminRole.Operator), TapQueueJson.Options);
        await AddAdmin(server, "bea", AdminRole.Admin);
        var withAnother = await server.Admin.DeleteAsync($"/api/v1/admin/admins/grants/{ada.Id}");

        Assert.Equal(HttpStatusCode.Conflict, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withAnother.StatusCode);
    }

    [Fact]
    public async Task DevModeLetsYouSetUpAdminsBeforeAnyoneCanSignIn()
    {
        await using var server = await TestServer.StartAsync(authMode: "dev");
        using var open = server.NewClient();
        await open.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("ada", null), TapQueueJson.Options);

        var grant = await open.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("ada", null, AdminRole.Admin), TapQueueJson.Options);
        var admins = await open.GetFromJsonAsync<AdminsDto>("/api/v1/admin/admins", TapQueueJson.Options);
        var me = await open.GetFromJsonAsync<AdminMeDto>("/api/v1/admin-auth/me", TapQueueJson.Options);
        var revoke = await open.DeleteAsync($"/api/v1/admin/admins/grants/{admins!.Grants.Single().Id}");

        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        Assert.Equal("They have no password yet. Set one on their user page.", admins.People.Single().SignInProblem);
        Assert.Equal(0, admins.FullAdmins);
        Assert.Null(me!.Username);
        Assert.True(me.FullAdmin);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Fact]
    public async Task OnlyFullAdminsChangeAdminsAndAdminGroups()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Admin);
        await AddAdmin(server, "pat", AdminRole.Admin, [AdminArea.People]);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("it", "IT"), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("staff", "Staff"), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest(null, "it", AdminRole.Admin), TapQueueJson.Options);
        using var pat = await Console(server, "pat");

        var joinAdminGroup = await pat.PutAsync("/api/v1/admin/groups/it/members/pat", null);
        var joinStaff = await pat.PutAsync("/api/v1/admin/groups/staff/members/pat", null);
        var disableAda = await pat.PatchAsJsonAsync("/api/v1/admin/users/ada", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        var deleteAdminGroup = await pat.DeleteAsync("/api/v1/admin/groups/it");
        var grantSelf = await pat.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("pat", null, AdminRole.Admin), TapQueueJson.Options);
        var bulk = await TestServer.ReadAsync<BulkUsersResponse>(await pat.PostAsJsonAsync("/api/v1/admin/users/bulk",
            new BulkUsersRequest(["pat"], BulkUserAction.AddToGroup, "it"), TapQueueJson.Options));

        Assert.Equal(HttpStatusCode.Forbidden, joinAdminGroup.StatusCode);
        Assert.True(joinStaff.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disableAda.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleteAdminGroup.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, grantSelf.StatusCode);
        Assert.Equal(0, bulk.Changed);
    }

    [Fact]
    public async Task GroupMembersGetTheGroupsRole()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "vic", AdminRole.Viewer, [AdminArea.Fleet]);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("helpdesk", "Helpdesk"), TapQueueJson.Options);
        await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants",
            new GrantAdminRequest(null, "helpdesk", AdminRole.Operator, [AdminArea.Jobs, AdminArea.Fleet]), TapQueueJson.Options);
        await server.Admin.PutAsync("/api/v1/admin/groups/helpdesk/members/vic", null);
        using var vic = await Console(server, "vic");

        var me = await vic.GetFromJsonAsync<AdminMeDto>("/api/v1/admin-auth/me", TapQueueJson.Options);
        var admins = await server.Admin.GetFromJsonAsync<AdminsDto>("/api/v1/admin/admins", TapQueueJson.Options);

        Assert.Equal(AdminRole.Operator, me!.Roles[AdminArea.Fleet]);
        Assert.Equal(AdminRole.Operator, me.Roles[AdminArea.Jobs]);
        Assert.False(me.Roles.ContainsKey(AdminArea.People));
        Assert.Equal(["direct", "Helpdesk"], admins!.People.Single(p => p.Username == "vic").Via);
    }

    [Fact]
    public async Task ADUsersSignInWithTheirDomainPassword()
    {
        var ad = new FakeDirectory();
        var alice = ad.User("alice", ad.Ou("Staff").Dn, "Alice Anders");
        ad.SetPassword(alice, "alice-domain-pw");
        await using var server = await TestServer.StartAsync(configureServices: services => services.AddSingleton<IDirectorySourceFactory>(ad));
        await TestServer.ReadAsync<DirectoryStatusDto>(await server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config",
            new UpdateDirectoryConfigRequest(Host: "dc01.lab", BindDn: "svc@lab", Password: "secret"), TapQueueJson.Options));
        await TestServer.ReadAsync<DirectoryScopeDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope",
            new AddDirectoryScopeRequest(Guid: alice.Guid), TapQueueJson.Options));
        await TestServer.ReadAsync<DirectorySyncResultDto>(await server.Admin.PostAsJsonAsync("/api/v1/admin/directory/sync",
            new DirectorySyncRequest(), TapQueueJson.Options));
        await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("alice", null, AdminRole.Viewer), TapQueueJson.Options);

        var setPassword = await server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/password", new SetPasswordRequest(Password), TapQueueJson.Options);
        var wrong = await SignIn(server, "alice", "nope");
        var right = await SignIn(server, @"LAB\alice", "alice-domain-pw");

        Assert.Equal(HttpStatusCode.BadRequest, setPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("alice", (await TestServer.ReadAsync<AdminMeDto>(right)).Username);
    }

    [Fact]
    public async Task LocalAdminsChangeTheirOwnPassword()
    {
        await using var server = await TestServer.StartAsync();
        await AddAdmin(server, "ada", AdminRole.Viewer);
        using var ada = await Console(server, "ada");

        var wrongCurrent = await ada.PostAsJsonAsync("/api/v1/admin-auth/password", new ChangePasswordRequest("nope", "a new long password"), TapQueueJson.Options);
        var changed = await ada.PostAsJsonAsync("/api/v1/admin-auth/password", new ChangePasswordRequest(Password, "a new long password"), TapQueueJson.Options);
        var stillSignedIn = await ada.GetAsync("/api/v1/admin/jobs");
        var oldPassword = await SignIn(server, "ada", Password);
        var newPassword = await SignIn(server, "ada", "a new long password");

        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    /// <summary>Creates a local user with <see cref="Password"/> and gives them <paramref name="role"/> (in every area by default).</summary>
    private static async Task<AdminGrantDto> AddAdmin(TestServer server, string username, string role, IReadOnlyList<string>? areas = null)
    {
        await server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(username, null), TapQueueJson.Options);
        var password = await server.Admin.PutAsJsonAsync($"/api/v1/admin/users/{username}/password", new SetPasswordRequest(Password), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, password.StatusCode);
        return (await TestServer.ReadAsync<List<AdminGrantDto>>(await server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants",
            new GrantAdminRequest(username, null, role, areas), TapQueueJson.Options))).First();
    }

    private static async Task<HttpResponseMessage> SignIn(TestServer server, string username, string password)
    {
        using var browser = server.NewClient();
        browser.DefaultRequestHeaders.Add(AdminAccess.ConsoleHeader, "1");
        return await browser.PostAsJsonAsync("/api/v1/admin-auth/sign-in", new AdminSignInRequest(username, password), TapQueueJson.Options);
    }

    /// <summary>A browser signed in to the console as <paramref name="username"/>, sending the console's header.</summary>
    private static async Task<HttpClient> Console(TestServer server, string username)
    {
        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = server.BaseUri };
        browser.DefaultRequestHeaders.Add(AdminAccess.ConsoleHeader, "1");
        var signIn = await browser.PostAsJsonAsync("/api/v1/admin-auth/sign-in", new AdminSignInRequest(username, Password), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        return browser;
    }
}
