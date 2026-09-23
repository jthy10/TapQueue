using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>CSV import and export, and bulk actions on users.</summary>
public sealed class UserBulkTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("staff", "Staff"), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups", new CreateGroupRequest("color", "Color printing"), TapQueueJson.Options);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", "Alice"), TapQueueJson.Options);
        await _server.Admin.PutAsync("/api/v1/admin/groups/staff/members/alice", null);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpResponseMessage> Import(string csv, bool apply) =>
        await _server.Admin.PostAsync($"/api/v1/admin/users/import{(apply ? "?apply=true" : "")}", new StringContent(csv, Encoding.UTF8, "text/csv"));

    private async Task<List<UserAdminDto>> Users() =>
        (await _server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options))!;

    private const string Csv = """
        username,display_name,groups,card,disabled
        alice,Alice Liddell,staff;color,,
        bob,Bob Builder,staff,0004 5678,
        carol,,nope,,
        alice,Again,,,
        dave,,,,maybe
        """;

    [Fact]
    public async Task APreviewSaysWhatWouldHappenAndChangesNothing()
    {
        var preview = await TestServer.ReadAsync<ImportResponse>(await Import(Csv, apply: false));

        Assert.False(preview.Applied);
        Assert.Equal((1, 1, 3), (preview.Creates, preview.Updates, preview.Errors));
        Assert.Equal(["rename to Alice Liddell", "add to Color printing"], preview.Rows[0].Changes);
        Assert.Equal(["create as Bob Builder", "add to Staff", "link card …5678"], preview.Rows[1].Changes);
        Assert.Equal("No group \"nope\".", preview.Rows[2].Error);
        Assert.Equal(5, preview.Rows[3].Row);
        Assert.Contains("earlier row", preview.Rows[3].Error);
        Assert.Contains("true or false", preview.Rows[4].Error);
        Assert.Equal(["alice"], (await Users()).Select(u => u.Username));
    }

    [Fact]
    public async Task ApplyingChangesTheRowsWithoutErrors()
    {
        var applied = await TestServer.ReadAsync<ImportResponse>(await Import(Csv, apply: true));
        var users = await Users();
        var badges = await _server.Admin.GetFromJsonAsync<List<BadgeDto>>("/api/v1/admin/badges", TapQueueJson.Options);

        Assert.True(applied.Applied);
        Assert.Equal(["alice", "bob"], users.Select(u => u.Username));
        Assert.Equal("Alice Liddell", users[0].DisplayName);
        Assert.Equal(["color", "staff"], users[0].Groups.Order());
        Assert.Equal(["staff"], users[1].Groups);
        Assert.Equal("bob", badges!.Single().Username);
    }

    [Fact]
    public async Task AnExportCanBeImportedBackUnchanged()
    {
        var export = await _server.Admin.GetStringAsync("/api/v1/admin/users/export");

        var preview = await TestServer.ReadAsync<ImportResponse>(await Import(export, apply: false));

        Assert.StartsWith("username,display_name,groups,disabled,card_hints,created_at\r\nalice,Alice,staff,false,,", export);
        Assert.All(preview.Rows, r => Assert.Equal(ImportAction.Unchanged, r.Action));
    }

    [Fact]
    public async Task UnknownColumnsAreRejected()
    {
        var response = await Import("username,email\nalice,a@example.com\n", apply: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkActionsApplyToEveryoneNamed()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("bob", null), TapQueueJson.Options);

        var grouped = await TestServer.ReadAsync<BulkUsersResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/users/bulk",
            new BulkUsersRequest(["alice", "bob", "nobody"], BulkUserAction.AddToGroup, "color"), TapQueueJson.Options));
        var disabled = await TestServer.ReadAsync<BulkUsersResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/users/bulk",
            new BulkUsersRequest(["alice", "bob"], BulkUserAction.Disable), TapQueueJson.Options));
        var users = await Users();

        Assert.Equal(2, grouped.Changed);
        Assert.Equal(["No user \"nobody\"."], grouped.Errors);
        Assert.Equal(2, disabled.Changed);
        Assert.All(users, u => Assert.NotNull(u.DisabledAt));
        Assert.All(users, u => Assert.Contains("color", u.Groups));
    }
}
