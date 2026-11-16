using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Admins;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// A new server in token mode has no admin who can sign in. The console creates the first one for
/// whoever has the setup code, so nobody has to open the console to the network with dev mode first.
/// </summary>
public sealed class FirstRunSetupTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private HttpClient Browser()
    {
        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = _server.BaseUri };
        browser.DefaultRequestHeaders.Add(AdminAccess.ConsoleHeader, "1");
        return browser;
    }

    private async Task<string?> Code() =>
        (await _server.Admin.GetFromJsonAsync<SetupCodeResponse>("/api/v1/admin/server/setup-code", TapQueueJson.Options))!.Code;

    private static Task<HttpResponseMessage> SetUp(HttpClient browser, string? code, string username = "ada", string password = "correct horse battery") =>
        browser.PostAsJsonAsync("/api/v1/admin-auth/setup", new FirstRunSetupRequest(code ?? "", username, "Ada Admin", password), TapQueueJson.Options);

    [Fact]
    public async Task TheFirstAdminIsCreatedWithTheSetupCodeAndIsSignedIn()
    {
        using var browser = Browser();
        Assert.True((await browser.GetFromJsonAsync<FirstRunSetupDto>("/api/v1/admin-auth/setup", TapQueueJson.Options))!.Needed);
        var code = await Code();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);

        // Typed in lower case and without the dashes still counts.
        var me = await TestServer.ReadAsync<AdminMeDto>(await SetUp(browser, code!.Replace("-", "").ToLowerInvariant()));
        Assert.Equal("ada", me.Username);
        Assert.True(me.FullAdmin);

        // The cookie it set is a console session.
        var again = await browser.GetFromJsonAsync<AdminMeDto>("/api/v1/admin-auth/me", TapQueueJson.Options);
        Assert.True(again!.FullAdmin);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/admin/admins")).StatusCode);

        // And that was the only time: there's an admin now, so no code and no setup.
        Assert.False((await browser.GetFromJsonAsync<FirstRunSetupDto>("/api/v1/admin-auth/setup", TapQueueJson.Options))!.Needed);
        Assert.Null(await Code());
        using var another = Browser();
        Assert.Equal(HttpStatusCode.Conflict, (await SetUp(another, code, "mallory")).StatusCode);
    }

    [Fact]
    public async Task ItNeedsTheRightCodeAGoodPasswordAndTheConsole()
    {
        using var browser = Browser();
        var code = await Code();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SetUp(browser, "AAAA-BBBB-CCCC")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SetUp(browser, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUp(browser, code, password: "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetUp(browser, code, username: " ")).StatusCode);

        using var otherSite = _server.NewClient(); // no console header, as a form on another site would be
        Assert.Equal(HttpStatusCode.Forbidden, (await SetUp(otherSite, code)).StatusCode);

        Assert.True((await browser.GetFromJsonAsync<FirstRunSetupDto>("/api/v1/admin-auth/setup", TapQueueJson.Options))!.Needed);
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task GuessingAtTheCodeIsSlowedDown()
    {
        using var browser = Browser();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await SetUp(browser, "AAAA-BBBB-CCCC")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SetUp(browser, await Code())).StatusCode);
    }

    [Fact]
    public async Task OnlyAFullAdminCanReadTheCodeAndDevModeHasNone()
    {
        using var anonymous = _server.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/server/setup-code")).StatusCode);
        Assert.Equal(AdminRequirement.FullAdminOnly, AdminAccess.Requirement("GET", "/server/setup-code"));

        await using var dev = await TestServer.StartAsync("dev");
        using var browser = new HttpClient { BaseAddress = dev.BaseUri };
        Assert.False((await browser.GetFromJsonAsync<FirstRunSetupDto>("/api/v1/admin-auth/setup", TapQueueJson.Options))!.Needed);
        Assert.Null((await dev.Admin.GetFromJsonAsync<SetupCodeResponse>("/api/v1/admin/server/setup-code", TapQueueJson.Options))!.Code);
    }
}
