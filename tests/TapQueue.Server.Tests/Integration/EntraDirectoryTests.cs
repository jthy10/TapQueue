using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// Entra ID as the directory: users and groups read through Microsoft Graph, by the same sync that
/// reads Active Directory. A pretend Graph answers the server's requests.
/// </summary>
public sealed class EntraDirectoryTests : IAsyncLifetime
{
    private const string Staff = "11111111-0000-0000-0000-000000000001";
    private const string Teachers = "11111111-0000-0000-0000-000000000002";
    private const string Alice = "22222222-0000-0000-0000-00000000000a";
    private const string Bob = "22222222-0000-0000-0000-00000000000b";
    private const string Carol = "22222222-0000-0000-0000-00000000000c";
    private const string Guest = "22222222-0000-0000-0000-00000000000d";

    /// <summary>Microsoft's token endpoint and Graph, with one group holding two people and a nested group.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public bool BobEnabled { get; set; } = true;
        public string Secret { get; set; } = "the-secret";
        public List<string> Asked { get; } = [];

        private object User(string id) => id switch
        {
            Alice => new { id, userPrincipalName = "alice@example.org", displayName = "Alice Anders", accountEnabled = true, employeeId = "04A1B2C3" },
            Bob => new { id, userPrincipalName = "bob@example.org", displayName = "Bob Berg", accountEnabled = BobEnabled, employeeId = (string?)null },
            Carol => new { id, userPrincipalName = "carol.c@example.org", displayName = "Carol Chen", accountEnabled = true, employeeId = (string?)null,
                onPremisesSamAccountName = "cchen" },
            Guest => new { id, userPrincipalName = "visitor_gmail.com#EXT#@example.onmicrosoft.com", displayName = "A Visitor", accountEnabled = true, employeeId = (string?)null },
            _ => throw new KeyNotFoundException(),
        };

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Answer(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Answer(request));

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!;
            if (url.Host == "login.microsoftonline.com")
            {
                var form = System.Web.HttpUtility.ParseQueryString(request.Content!.ReadAsStringAsync().Result);
                return form["client_secret"] == Secret && url.AbsolutePath == "/example.org/oauth2/v2.0/token"
                    ? Json(new { access_token = "graph-token", token_type = "Bearer" })
                    : Json(new { error = "invalid_client", error_description = "AADSTS7000215: Invalid client secret provided." }, HttpStatusCode.Unauthorized);
            }
            Assert.Equal("graph.microsoft.com", url.Host);
            Assert.Equal("graph-token", request.Headers.Authorization?.Parameter);
            var path = Uri.UnescapeDataString(url.AbsolutePath["/v1.0/".Length..]);
            Asked.Add(path);
            try
            {
                return path.Split('/') switch
                {
                    ["users", var id] => Json(User(id)),
                    ["groups", Staff] => Json(new { id = Staff, displayName = "Staff", description = "Everyone who works here", mailNickname = "staff" }),
                    ["groups", Teachers] => Json(new { id = Teachers, displayName = "Teachers", description = (string?)null, mailNickname = "teachers" }),
                    // Two pages, to see that the second is followed.
                    ["groups", Staff, "members"] when !url.Query.Contains("page=2") => Json(new Dictionary<string, object>
                    {
                        ["value"] = new object[] { Member("user", Alice), Member("user", Bob) },
                        ["@odata.nextLink"] = $"https://graph.microsoft.com/v1.0/groups/{Staff}/members?page=2",
                    }),
                    ["groups", Staff, "members"] => Json(new { value = new object[] { Member("group", Teachers), Member("user", Guest), Member("device", "33333333-0000-0000-0000-000000000000") } }),
                    ["groups", Teachers, "members"] => Json(new { value = new object[] { Member("user", Carol), Member("user", Alice) } }),
                    ["groups"] => Json(new { value = new object[] { new { id = Staff, displayName = "Staff", description = "", mailNickname = "staff" } } }),
                    _ => Json(new { error = new { message = "Resource not found." } }, HttpStatusCode.NotFound),
                };
            }
            catch (KeyNotFoundException)
            {
                return Json(new { error = new { message = "Resource not found." } }, HttpStatusCode.NotFound);
            }
        }

        private static Dictionary<string, object> Member(string type, string id) => new() { ["@odata.type"] = $"#microsoft.graph.{type}", ["id"] = id };

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
    }

    private readonly FakeGraph _graph = new();
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(configureServices: services => services.AddSingleton(new OutboundHttp { Handler = () => _graph }));
        await TestServer.ReadAsync<DirectoryStatusDto>(await Config(new UpdateDirectoryConfigRequest(Kind: DirectoryKind.Entra, TenantId: "example.org",
            ClientId: "app-id", Password: "the-secret", BadgeAttribute: "employeeId")));
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<HttpResponseMessage> Config(UpdateDirectoryConfigRequest request) =>
        _server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config", request, TapQueueJson.Options);

    private async Task<DirectorySyncResultDto> Sync() =>
        await TestServer.ReadAsync<DirectorySyncResultDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/directory/sync", new DirectorySyncRequest(), TapQueueJson.Options));

    private async Task<List<UserAdminDto>> Users() =>
        (await _server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options))!;

    [Fact]
    public async Task AGroupsPeopleBecomeUsersThroughNestedGroupsToo()
    {
        var test = await TestServer.ReadAsync<DirectoryTestResultDto>(await _server.Admin.PostAsync("/api/v1/admin/directory/test", null));
        Assert.True(test.Success, test.Message);
        var found = await _server.Admin.GetFromJsonAsync<List<DirectoryObjectDto>>("/api/v1/admin/directory/search?kind=group&q=Sta", TapQueueJson.Options);
        Assert.Equal(Staff, Assert.Single(found!).Guid);
        var scoped = await TestServer.ReadAsync<DirectoryScopeDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope",
            new AddDirectoryScopeRequest(Guid: Staff), TapQueueJson.Options));
        Assert.Equal(DirectoryScopeKind.Group, scoped.Kind);

        var result = await Sync();
        Assert.Equal(DirectoryRunOutcome.Succeeded, result.Outcome);

        // Named as Windows names them on a PC joined to Entra ID; the guest has no such name and is left out.
        var users = await Users();
        Assert.Equal(["alice", "bob", "cchen"], users.Select(u => u.Username).Order());
        Assert.All(users, u => Assert.Equal("ad", u.Source));
        Assert.Equal("Alice Anders", users.Single(u => u.Username == "alice").DisplayName);
        Assert.Contains("staff", users.Single(u => u.Username == "cchen").Groups); // through Teachers
        Assert.Contains(_graph.Asked, path => path == $"groups/{Staff}/members");
        // Her card came from the attribute the directory keeps it in.
        var cards = await _server.Admin.GetFromJsonAsync<List<BadgeDto>>("/api/v1/admin/badges?username=alice", TapQueueJson.Options);
        Assert.Equal("…B2C3", Assert.Single(cards!).CardHint);
    }

    [Fact]
    public async Task SomeoneDisabledInEntraIsDisabledHereAtTheNextSync()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Guid: Staff), TapQueueJson.Options);
        await Sync();
        Assert.Null((await Users()).Single(u => u.Username == "bob").DisabledAt);

        _graph.BobEnabled = false;
        await Sync();

        var bob = (await Users()).Single(u => u.Username == "bob");
        Assert.NotNull(bob.DisabledAt);
        Assert.Equal("directory", bob.DisabledBy);
    }

    [Fact]
    public async Task WhatEntraSaysWhenItRefusesIsPassedOnAndItsWaysAreNotActiveDirectorys()
    {
        _graph.Secret = "rotated";
        var test = await TestServer.ReadAsync<DirectoryTestResultDto>(await _server.Admin.PostAsync("/api/v1/admin/directory/test", null));
        Assert.False(test.Success);
        Assert.Contains("Invalid client secret", test.Message);
        _graph.Secret = "the-secret";

        // No OUs to pick, no passwords to check, and the scope belongs to the directory it was picked from.
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<DirectoryObjectDto>>("/api/v1/admin/directory/search?kind=ou&q=", TapQueueJson.Options))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Config(new UpdateDirectoryConfigRequest(ClientSignIn: ClientSignIn.Domain))).StatusCode);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/directory/scope", new AddDirectoryScopeRequest(Guid: Staff), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, (await Config(new UpdateDirectoryConfigRequest(Kind: DirectoryKind.ActiveDirectory, Password: "the-secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Config(new UpdateDirectoryConfigRequest(Kind: "ldap"))).StatusCode);
    }
}
