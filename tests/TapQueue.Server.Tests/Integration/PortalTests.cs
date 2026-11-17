using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Admins;
using TapQueue.Server.Api;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// The web page at /my: people sign in from any browser with their password and see, release and
/// delete their own jobs, without the tray app.
/// </summary>
public sealed class PortalTests : IAsyncLifetime
{
    private const string Password = "correct horse battery";
    private static readonly byte[] ThreePages = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "three-pages.pdf"));
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>A browser, with cookies and the page's header.</summary>
    private HttpClient Browser()
    {
        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = _server.BaseUri };
        browser.DefaultRequestHeaders.Add(PortalApi.PageHeader, "1");
        return browser;
    }

    private static Task<HttpResponseMessage> SignIn(HttpClient browser, string username, string password = Password) =>
        browser.PostAsJsonAsync("/api/v1/portal/sign-in", new AdminSignInRequest(username, password), TapQueueJson.Options);

    /// <summary>Makes a user with a password and a held job, and signs a browser in as them.</summary>
    private async Task<HttpClient> SignedIn(string username, string job)
    {
        // The tray is how the job gets its owner; the page is how it's looked at afterwards.
        using (await _server.SignInAsync(username))
            await _server.PrintAsync(job, ThreePages, requestingUser: username);
        (await _server.Admin.PutAsJsonAsync($"/api/v1/admin/users/{username}/password", new SetPasswordRequest(Password), TapQueueJson.Options)).EnsureSuccessStatusCode();
        var browser = Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await SignIn(browser, username)).StatusCode);
        return browser;
    }

    [Fact]
    public async Task SomeoneSeesReleasesAndDeletesTheirOwnJobsFromABrowser()
    {
        using var alice = await SignedIn("alice", "report.pdf");
        await _server.PrintAsync("draft.pdf", ThreePages, requestingUser: "alice");

        var me = await alice.GetFromJsonAsync<PortalMeDto>("/api/v1/portal/me", TapQueueJson.Options);
        Assert.Equal("alice", me!.Username);
        Assert.True(me.CanChangePassword);
        var jobs = await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/portal/me/jobs", TapQueueJson.Options);
        Assert.Equal(["draft.pdf", "report.pdf"], jobs!.Select(j => j.Name).Order());
        Assert.Equal(TestServer.PrinterId, Assert.Single((await alice.GetFromJsonAsync<List<PrinterDto>>("/api/v1/portal/printers", TapQueueJson.Options))!).Id);

        var draft = jobs!.Single(j => j.Name == "draft.pdf");
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync($"/api/v1/portal/me/jobs/{draft.Id}")).StatusCode);
        var released = await TestServer.ReadAsync<ReleaseResponse>(await alice.PostAsJsonAsync("/api/v1/portal/me/release",
            new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options));

        Assert.True(Assert.Single(released.Results).Success);
        Assert.Equal("report.pdf", Assert.Single(_server.Printer.Jobs).Name);
        var log = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=job", TapQueueJson.Options);
        Assert.Contains(log!, e => e.Message.Contains("released to Office printer from the web page"));
    }

    [Fact]
    public async Task NobodySeesOrTouchesAnyoneElsesJobs()
    {
        using var alice = await SignedIn("alice", "alice's.pdf");
        using var bob = await SignedIn("bob", "bob's.pdf");

        var hers = Assert.Single((await alice.GetFromJsonAsync<List<JobDto>>("/api/v1/portal/me/jobs", TapQueueJson.Options))!);
        Assert.Equal("alice's.pdf", hers.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/portal/me/jobs/{hers.Id}")).StatusCode);
        var notHis = await TestServer.ReadAsync<ReleaseResponse>(await bob.PostAsJsonAsync("/api/v1/portal/me/release",
            new ReleaseRequest(TestServer.PrinterId, [hers.Id]), TapQueueJson.Options));
        Assert.False(Assert.Single(notHis.Results).Success);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task ItNeedsTheRightPasswordThePagesHeaderAndASignIn()
    {
        using var alice = await SignedIn("alice", "report.pdf");
        using var stranger = Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/v1/portal/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/v1/portal/me/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(stranger, "alice", "not her password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(stranger, "nobody")).StatusCode);

        // Another site's form can post with her cookie, but can't send the page's header.
        alice.DefaultRequestHeaders.Remove(PortalApi.PageHeader);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/v1/portal/me/release", new ReleaseRequest(TestServer.PrinterId), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/portal/me/jobs")).StatusCode);
        using var noHeader = new HttpClient { BaseAddress = _server.BaseUri };
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(noHeader, "alice")).StatusCode);

        // Signing out ends it.
        alice.DefaultRequestHeaders.Add(PortalApi.PageHeader, "1");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync("/api/v1/portal/sign-out", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/v1/portal/me")).StatusCode);
    }

    [Fact]
    public async Task BeingSignedInToThePageIsNotBeingAtAPc()
    {
        // Only the page: no tray app anywhere. A job from this address mustn't become hers because of it.
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/password", new SetPasswordRequest(Password), TapQueueJson.Options)).EnsureSuccessStatusCode();
        using var alice = Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await SignIn(alice, "alice")).StatusCode);

        var refused = await _server.PrintAsync("report.pdf", ThreePages, requestingUser: "alice");

        Assert.Equal(Ipp.IppStatus.ClientErrorNotAuthorized, refused.Code);
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task ChangingAPasswordSignsOutEverywhereElseAndADisabledUserIsOut()
    {
        using var phone = await SignedIn("alice", "report.pdf");
        using var laptop = Browser();
        Assert.Equal(HttpStatusCode.NoContent, (await SignIn(laptop, "alice")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/api/v1/portal/password", new ChangePasswordRequest("wrong", "another long password"), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/api/v1/portal/password", new ChangePasswordRequest(Password, "short"), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("/api/v1/portal/password", new ChangePasswordRequest(Password, "another long password"), TapQueueJson.Options)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/portal/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/v1/portal/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(laptop, "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SignIn(laptop, "alice", "another long password")).StatusCode);

        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/users/alice", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/v1/portal/me")).StatusCode);
    }

    [Fact]
    public async Task ThePageCanBeTurnedOff()
    {
        using var alice = await SignedIn("alice", "report.pdf");
        await TestServer.ReadAsync<ServerInfoDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(Options: new Dictionary<string, string> { ["userPortal"] = "off" }), TapQueueJson.Options));

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/portal/me")).StatusCode);
        using var browser = Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await SignIn(browser, "alice")).StatusCode);
    }

    [Fact]
    public async Task ThePageItselfIsServedWithHeadersThatKeepItToItself()
    {
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = _server.BaseUri };

        Assert.Equal("/my/", (await browser.GetAsync("/my")).Headers.Location?.OriginalString);
        var page = await browser.GetAsync("/my/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("portal.js", await page.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("text/javascript", (await browser.GetAsync("/my/portal.js")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/my/../server.toml")).StatusCode);
    }

    [Fact]
    public async Task APeopleAdminSetsPeoplesPasswordsButNotAnAdmins()
    {
        Assert.Equal(new AdminRequirement(AdminArea.People, AdminRole.Admin), AdminAccess.Requirement("PUT", "/users/alice/password"));

        // "peter" runs People; "ada" is a full admin; "alice" just prints.
        foreach (var name in new[] { "peter", "ada", "alice" })
            await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(name, null), TapQueueJson.Options);
        (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/peter/password", new SetPasswordRequest(Password), TapQueueJson.Options)).EnsureSuccessStatusCode();
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("ada", null, AdminRole.Admin), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("peter", null, AdminRole.Admin, [AdminArea.People]), TapQueueJson.Options);

        using var peter = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = _server.BaseUri };
        peter.DefaultRequestHeaders.Add(AdminAccess.ConsoleHeader, "1");
        (await peter.PostAsJsonAsync("/api/v1/admin-auth/sign-in", new AdminSignInRequest("peter", Password), TapQueueJson.Options)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await peter.PutAsJsonAsync("/api/v1/admin/users/alice/password", new SetPasswordRequest(Password), TapQueueJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await peter.PutAsJsonAsync("/api/v1/admin/users/ada/password", new SetPasswordRequest(Password), TapQueueJson.Options)).StatusCode);
    }
}
