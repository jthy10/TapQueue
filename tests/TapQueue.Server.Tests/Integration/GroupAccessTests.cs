using System.Net;
using System.Net.Http.Json;
using System.Text;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Groups decide which queues people print to and which printers they release at.</summary>
public sealed class GroupAccessTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 test document\n%%EOF\n");
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<GroupDto> CreateGroup(CreateGroupRequest request) =>
        await TestServer.ReadAsync<GroupDto>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups", request, TapQueueJson.Options));

    private async Task Join(string group, string username) =>
        (await _server.Admin.PutAsync($"/api/v1/admin/groups/{group}/members/{username}", null)).EnsureSuccessStatusCode();

    private async Task<StationTapResponse> Tap(string card)
    {
        var station = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        using var client = _server.NewClient(station.Token);
        return await TestServer.ReadAsync<StationTapResponse>(await client.PostAsJsonAsync("/api/v1/station/tap", new StationTapRequest(card), TapQueueJson.Options));
    }

    [Fact]
    public async Task NewGroupsAllowEverythingAndShowTheirMembers()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);
        var staff = await CreateGroup(new CreateGroupRequest("staff", "Staff"));
        await Join("staff", "alice");
        await Join("staff", "alice"); // already a member: no error

        var users = await _server.Admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users", TapQueueJson.Options);
        var members = await _server.Admin.GetFromJsonAsync<List<string>>("/api/v1/admin/groups/staff/members", TapQueueJson.Options);
        var group = await _server.Admin.GetFromJsonAsync<GroupDto>("/api/v1/admin/groups/staff", TapQueueJson.Options);

        Assert.True(staff.AllQueues && staff.AllPrinters);
        Assert.Equal(["staff"], users!.Single().Groups);
        Assert.Equal(["alice"], members);
        Assert.Equal(1, group!.MemberCount);
    }

    [Fact]
    public async Task GroupsCantNameQueuesOrPrintersThatDontExist()
    {
        var response = await _server.Admin.PostAsJsonAsync("/api/v1/admin/groups",
            new CreateGroupRequest("staff", "Staff", AllQueues: false, QueueIds: ["nope"]), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task JobsToAQueueTheirGroupsDontAllowAreRefused()
    {
        using var client = await _server.SignInAsync("alice");
        await CreateGroup(new CreateGroupRequest("visitors", "Visitors", AllQueues: false, QueueIds: []));
        await Join("visitors", "alice");

        var response = await _server.PrintAsync("report.pdf", Pdf);

        Assert.Equal(IppStatus.ClientErrorNotAuthorized, response.Code);
    }

    [Fact]
    public async Task SignInOnlyOffersWhatTheirGroupsAllow()
    {
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", null), TapQueueJson.Options);
        await CreateGroup(new CreateGroupRequest("visitors", "Visitors", AllQueues: false, AllPrinters: false));
        await Join("visitors", "alice");
        var token = (await TestServer.ReadAsync<UserTokenResponse>(await _server.Admin.PostAsync("/api/v1/admin/users/alice/token", null))).Token;

        var session = await TestServer.ReadAsync<ClientSessionResponse>(await _server.NewClient().PostAsJsonAsync("/api/v1/client/session",
            new ClientSessionRequest("alice", token, "alice", "TEST-PC", "test"), TapQueueJson.Options));

        Assert.Empty(session.Queues);
        Assert.Empty(session.Printers);
    }

    [Fact]
    public async Task TapsAtAPrinterTheirGroupsDontAllowReleaseNothing()
    {
        using var client = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", Pdf);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);
        await CreateGroup(new CreateGroupRequest("upstairs", "Upstairs only", AllPrinters: false, PrinterIds: []));
        await Join("upstairs", "alice");

        var tap = await Tap("CARD-1");

        Assert.Equal(TapOutcome.NotAllowed, tap.Outcome);
        Assert.Empty(_server.Printer.Jobs);
    }

    [Fact]
    public async Task AnyOfTheirGroupsCanAllowIt()
    {
        using var client = await _server.SignInAsync("alice");
        await _server.PrintAsync("report.pdf", Pdf);
        await _server.Admin.PostAsJsonAsync("/api/v1/admin/badges", new CreateBadgeRequest("alice", "CARD-1"), TapQueueJson.Options);
        await CreateGroup(new CreateGroupRequest("upstairs", "Upstairs only", AllPrinters: false, PrinterIds: []));
        await CreateGroup(new CreateGroupRequest("office", "Office", AllPrinters: false, PrinterIds: [TestServer.PrinterId]));
        await Join("upstairs", "alice");
        await Join("office", "alice");

        var tap = await Tap("CARD-1");

        Assert.Equal(TapOutcome.Released, tap.Outcome);
    }

    [Fact]
    public async Task RemovingTheLastGroupRestoresEverything()
    {
        using var client = await _server.SignInAsync("alice");
        await CreateGroup(new CreateGroupRequest("visitors", "Visitors", AllQueues: false));
        await Join("visitors", "alice");
        (await _server.Admin.DeleteAsync("/api/v1/admin/groups/visitors/members/alice")).EnsureSuccessStatusCode();

        var response = await _server.PrintAsync("report.pdf", Pdf);

        Assert.True(IppStatus.IsSuccess(response.Code));
    }

    [Fact]
    public async Task RestrictingAGroupLaterTakesEffect()
    {
        using var client = await _server.SignInAsync("alice");
        await CreateGroup(new CreateGroupRequest("staff", "Staff"));
        await Join("staff", "alice");

        var patched = await TestServer.ReadAsync<GroupDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/groups/staff",
            new UpdateGroupRequest(AllQueues: false, QueueIds: [TestServer.QueueId]), TapQueueJson.Options));
        var allowed = await _server.PrintAsync("report.pdf", Pdf);
        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/groups/staff", new UpdateGroupRequest(QueueIds: []), TapQueueJson.Options);
        var refused = await _server.PrintAsync("report.pdf", Pdf);

        Assert.Equal([TestServer.QueueId], patched.QueueIds);
        Assert.True(IppStatus.IsSuccess(allowed.Code));
        Assert.Equal(IppStatus.ClientErrorNotAuthorized, refused.Code);
    }
}
