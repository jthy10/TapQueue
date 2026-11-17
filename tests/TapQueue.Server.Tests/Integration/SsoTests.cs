using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using TapQueue.Server.Admins;
using TapQueue.Server.Api;
using TapQueue.Server.Sso;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// Single sign-on (OpenID Connect) for the admin console and the web page, against a pretend
/// provider that publishes its configuration and keys and signs ID tokens.
/// </summary>
public sealed class SsoTests : IAsyncLifetime
{
    private const string Authority = "https://idp.example.org/tenant";
    private const string ClientId = "tapqueue-client";

    /// <summary>The provider: answers the server's own requests to it. Tests say what the next ID token claims.</summary>
    private sealed class FakeIdp : HttpMessageHandler
    {
        public RSA Key { get; set; } = RSA.Create(2048);
        public RSA Published { get; }
        public Dictionary<string, object>? Claims { get; set; }
        public Dictionary<string, string>? LastTokenRequest { get; private set; }

        public FakeIdp() => Published = Key;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == Authority + "/.well-known/openid-configuration")
                return Json(new { issuer = Authority, authorization_endpoint = Authority + "/authorize", token_endpoint = Authority + "/token", jwks_uri = Authority + "/keys" });
            if (url == Authority + "/keys")
            {
                var p = Published.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = OidcSignIn.Base64Url(p.Modulus!), e = OidcSignIn.Base64Url(p.Exponent!) } } });
            }
            if (url == Authority + "/token")
            {
                LastTokenRequest = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct)) is var form
                    ? form.AllKeys.ToDictionary(k => k!, k => form[k]!) : null;
                return Claims is null
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                    : Json(new { id_token = Token(Claims), token_type = "Bearer" });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        private string Token(Dictionary<string, object> claims)
        {
            var head = OidcSignIn.Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT","kid":"k1"}"""));
            var body = OidcSignIn.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
            var signature = Key.SignData(Encoding.ASCII.GetBytes($"{head}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{head}.{body}.{OidcSignIn.Base64Url(signature)}";
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }

    private readonly FakeIdp _idp = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(configureServices: services => services.AddSingleton(new OutboundHttp { Handler = () => _idp }));
        // ada@example.org at the provider is the TapQueue user "ada", a full admin; "alice" just prints.
        foreach (var name in new[] { "ada", "alice" })
            await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(name, null), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/admins/grants", new GrantAdminRequest("ada", null, AdminRole.Admin), TapQueueJson.Options);
        await TestServer.ReadAsync<SsoSettingsDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/sso",
            new UpdateSsoRequest(true, Authority, ClientId, "the-secret", Label: "Example", Domain: "example.org"), TapQueueJson.Options));
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private HttpClient Browser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = _server.BaseUri };

    /// <summary>Starts a sign-in in <paramref name="browser"/> and returns what the provider was asked: its state and nonce among it.</summary>
    private static async Task<System.Collections.Specialized.NameValueCollection> Start(HttpClient browser, string target = "console")
    {
        var response = await browser.GetAsync($"/api/v1/sso/start?for={target}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Authority + "/authorize?", response.Headers.Location!.ToString());
        return HttpUtility.ParseQueryString(response.Headers.Location.Query);
    }

    private Dictionary<string, object> ClaimsFor(string name, string nonce) => new()
    {
        ["iss"] = Authority, ["aud"] = ClientId, ["sub"] = "subject-1", ["preferred_username"] = name, ["nonce"] = nonce,
        ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    /// <summary>The whole round trip; returns where the browser is sent at the end.</summary>
    private async Task<string> SignIn(HttpClient browser, string name, string target = "console", Action<Dictionary<string, object>>? tamper = null)
    {
        var asked = await Start(browser, target);
        _idp.Claims = ClaimsFor(name, asked["nonce"]!);
        tamper?.Invoke(_idp.Claims);
        var back = await browser.GetAsync($"/api/v1/sso/callback?code=the-code&state={Uri.EscapeDataString(asked["state"]!)}");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        return Uri.UnescapeDataString(back.Headers.Location!.OriginalString);
    }

    private static async Task<HttpResponseMessage> ConsoleMe(HttpClient browser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin-auth/me");
        request.Headers.Add(AdminAccess.ConsoleHeader, "1");
        return await browser.SendAsync(request);
    }

    [Fact]
    public async Task AnAdminSignsInToTheConsoleThroughTheProvider()
    {
        using var browser = Browser();
        var info = await browser.GetFromJsonAsync<SsoInfoDto>("/api/v1/sso", TapQueueJson.Options);
        Assert.Equal(new SsoInfoDto(true, "Example"), info);

        var asked = await Start(browser);
        Assert.Equal(ClientId, asked["client_id"]);
        Assert.Equal("code", asked["response_type"]);
        Assert.Equal("S256", asked["code_challenge_method"]);
        Assert.Equal($"{_server.BaseUri.GetLeftPart(UriPartial.Authority)}/api/v1/sso/callback", asked["redirect_uri"]);

        Assert.Equal("/admin/", await SignIn(browser, "ada@example.org"));

        var me = await TestServer.ReadAsync<AdminMeDto>(await ConsoleMe(browser));
        Assert.Equal("ada", me.Username);
        Assert.True(me.FullAdmin);
        // The code went to the provider with the secret and the PKCE verifier that matches the challenge.
        Assert.Equal("the-secret", _idp.LastTokenRequest!["client_secret"]);
        Assert.Equal("the-code", _idp.LastTokenRequest["code"]);
        Assert.NotEmpty(_idp.LastTokenRequest["code_verifier"]);
    }

    [Fact]
    public async Task SomeoneSignsInToTheWebPageAndAnyoneWithoutARoleStaysOutOfTheConsole()
    {
        using var browser = Browser();
        Assert.Equal("/my/", await SignIn(browser, "alice@example.org", "portal"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/portal/me");
        request.Headers.Add(PortalApi.PageHeader, "1");
        Assert.Equal("alice", (await TestServer.ReadAsync<PortalMeDto>(await browser.SendAsync(request))).Username);

        using var other = Browser();
        Assert.Contains("doesn't have a role in the TapQueue admin console", await SignIn(other, "alice@example.org"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConsoleMe(other)).StatusCode);
    }

    [Fact]
    public async Task ATokenThatIsntRightSignsNobodyIn()
    {
        async Task Refused(string because, Action<Dictionary<string, object>> tamper)
        {
            using var browser = Browser();
            Assert.Contains(because, await SignIn(browser, "ada@example.org", tamper: tamper));
            Assert.Equal(HttpStatusCode.Unauthorized, (await ConsoleMe(browser)).StatusCode);
        }

        await Refused("doesn't belong to this sign-in", c => c["nonce"] = "someone-elses");
        await Refused("issued to another application", c => c["aud"] = "another-app");
        await Refused("different provider", c => c["iss"] = "https://evil.example");
        await Refused("has expired", c => c["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());

        // Signed with a key the provider doesn't publish.
        _idp.Key = RSA.Create(2048);
        await Refused("isn't signed by the sign-in provider", _ => { });
    }

    [Fact]
    public async Task WhoTheProviderNamesHasToBeATapQueueUserInTheOrganisationsOwnDomain()
    {
        using var browser = Browser();
        // A guest, or an account at a public provider, whose name merely starts the same way.
        Assert.Contains("ada@gmail.com isn't set up in TapQueue", await SignIn(browser, "ada@gmail.com"));
        Assert.Contains("mallory@example.org isn't set up in TapQueue", await SignIn(browser, "mallory@example.org"));

        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/users/ada", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        Assert.Contains("account is disabled", await SignIn(browser, "ada@example.org"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConsoleMe(browser)).StatusCode);
    }

    [Fact]
    public async Task ASignInOnlyFinishesInTheBrowserThatStartedItAndOnlyOnce()
    {
        using var browser = Browser();
        var asked = await Start(browser);
        _idp.Claims = ClaimsFor("ada@example.org", asked["nonce"]!);
        var callback = $"/api/v1/sso/callback?code=the-code&state={Uri.EscapeDataString(asked["state"]!)}";

        using var victim = Browser(); // someone sent the attacker's link
        var elsewhere = await victim.GetAsync(callback);
        Assert.Contains("didn't start in this browser", Uri.UnescapeDataString(elsewhere.Headers.Location!.OriginalString));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConsoleMe(victim)).StatusCode);

        Assert.Equal("/admin/", (await browser.GetAsync(callback)).Headers.Location!.OriginalString);
        // The state was used up, and its cookie is gone.
        Assert.NotEqual("/admin/", (await browser.GetAsync(callback)).Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ItIsSetUpByFullAdminsAndNeedsEverythingToBeTurnedOn()
    {
        Assert.Equal(AdminRequirement.FullAdminOnly, AdminAccess.Requirement("PATCH", "/sso"));
        Assert.Equal(AdminRequirement.FullAdminOnly, AdminAccess.Requirement("GET", "/sso"));
        Task<HttpResponseMessage> Patch(UpdateSsoRequest request) => _server.Admin.PatchAsJsonAsync("/api/v1/admin/sso", request, TapQueueJson.Options);

        var saved = await _server.Admin.GetFromJsonAsync<SsoSettingsDto>("/api/v1/admin/sso", TapQueueJson.Options);
        Assert.True(saved!.HasSecret);
        Assert.EndsWith("/api/v1/sso/callback", saved.RedirectUri);
        Assert.Equal("example.org", saved.Domain);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Authority: "http://idp.example.org"))).StatusCode);          // not https
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Authority: "https://other.example.org"))).StatusCode);       // elsewhere, without the secret again
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Domain: "not a domain"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(new(Enabled: false))).StatusCode);

        using var browser = Browser();
        Assert.False((await browser.GetFromJsonAsync<SsoInfoDto>("/api/v1/sso", TapQueueJson.Options))!.Enabled);
        var start = await browser.GetAsync("/api/v1/sso/start?for=console");
        Assert.Contains("isn't set up", Uri.UnescapeDataString(start.Headers.Location!.OriginalString));
    }
}
