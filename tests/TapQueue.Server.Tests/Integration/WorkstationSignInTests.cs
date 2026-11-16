using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// ClientSignIn.Workstation: the TapQueue service on a PC says who is signed in to it, and that PC
/// user is the TapQueue user of the same name. No tokens, no passwords, and jobs are theirs whether
/// or not their tray app runs. Also local users' passwords in the tray's "Sign in as…".
/// </summary>
public sealed class WorkstationSignInTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private string _enrollmentCode = "";

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        await TestServer.ReadAsync<UserTokenResponse>(await _server.Admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest("alice", "Alice Anders"), TapQueueJson.Options));
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<HttpResponseMessage> SetMode(string mode) =>
        _server.Admin.PatchAsJsonAsync("/api/v1/admin/directory/config", new UpdateDirectoryConfigRequest(ClientSignIn: mode), TapQueueJson.Options);

    /// <summary>Makes an enrollment code, turns the mode on, and enrolls LAB-PC with the code.</summary>
    private async Task<(string Key, string IppPath)> TurnOnAndEnroll(string computer = "LAB-PC")
    {
        _enrollmentCode = (await TestServer.ReadAsync<EnrollmentCodeResponse>(await _server.Admin.PostAsync("/api/v1/admin/server/enrollment-code", null))).Code;
        Assert.Equal(HttpStatusCode.OK, (await SetMode(ClientSignIn.Workstation)).StatusCode);
        using var service = _server.NewClient();
        var setup = await TestServer.ReadAsync<ClientSetupResponse>(await service.PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest(computer, "0.19.0+test", "aaaa", null, ClientPlatform.Windows, "", _enrollmentCode), TapQueueJson.Options));
        return (setup.WorkstationKey!, Assert.Single(setup.Queues).IppPath);
    }

    private async Task<HttpResponseMessage> ServiceSignIn(string key, string pcUser, string computer = "LAB-PC")
    {
        using var service = _server.NewClient();
        return await service.PostAsJsonAsync("/api/v1/client/workstation-sign-in",
            new WorkstationSignInRequest(computer, key, pcUser, "0.19.0+test", ClientPlatform.Windows), TapQueueJson.Options);
    }

    [Fact]
    public async Task ItNeedsAnEnrollmentCodeAndTheCodeCantBeTurnedOffWhileItsOn()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await SetMode(ClientSignIn.Workstation)).StatusCode);

        await TurnOnAndEnroll();
        using var anonymous = _server.NewClient();
        var info = await anonymous.GetFromJsonAsync<ClientSignInInfoDto>("/api/v1/client/sign-in", TapQueueJson.Options);
        Assert.Equal(ClientSignIn.Workstation, info!.Mode);

        Assert.Equal(HttpStatusCode.Conflict, (await _server.Admin.DeleteAsync("/api/v1/admin/server/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetMode(ClientSignIn.Pc)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync("/api/v1/admin/server/enrollment-code")).StatusCode);
    }

    [Fact]
    public async Task ThePcsServiceSignsItsUserInAndTheirJobsAreTheirs()
    {
        var (key, path) = await TurnOnAndEnroll();

        var session = await TestServer.ReadAsync<ClientSessionResponse>(await ServiceSignIn(key, @"CORP\Alice"));
        Assert.Equal("alice", session.User.Username);
        Assert.Equal(TestServer.QueueId, Assert.Single(session.Queues).Id);

        await _server.PrintAsync("report.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: @"CORP\alice", ippPath: path);
        using var tray = _server.NewClient(session.SessionToken);
        var job = Assert.Single((await tray.GetFromJsonAsync<List<JobDto>>("/api/v1/me/jobs", TapQueueJson.Options))!);
        Assert.Equal("report.pdf", job.Name);

        var pcs = await _server.Admin.GetFromJsonAsync<List<WorkstationDto>>("/api/v1/admin/workstations", TapQueueJson.Options);
        Assert.True(Assert.Single(Assert.Single(pcs!).Sessions).Verified);
    }

    [Fact]
    public async Task AJobIsItsPcUsersEvenWithoutATrayApp()
    {
        var (_, path) = await TurnOnAndEnroll();

        // Nobody signed in anywhere: only the service is on this PC.
        await _server.PrintAsync("lab.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice@corp.example", ippPath: path);

        var job = Assert.Single((await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);
        Assert.Equal("alice", job.Owner);

        // Someone who isn't a TapQueue user is still turned away, and so is a job that didn't come through an enrolled PC.
        var stranger = await _server.PrintAsync("x.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "mallory", ippPath: path);
        Assert.Equal(Ipp.IppStatus.ClientErrorNotAuthorized, stranger.Code);
        var keyless = await _server.PrintAsync("y.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice");
        Assert.Equal(Ipp.IppStatus.ClientErrorNotAuthorized, keyless.Code);
    }

    [Fact]
    public async Task OnlyAnEnrolledPcWithItsKeyCanSignAnyoneIn()
    {
        var (key, _) = await TurnOnAndEnroll();

        Assert.Equal(HttpStatusCode.Forbidden, (await ServiceSignIn("not-the-key", "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ServiceSignIn(key, "alice", computer: "OTHER-PC")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ServiceSignIn(key, "nobody")).StatusCode);   // not a TapQueue user
        Assert.Equal(HttpStatusCode.BadRequest, (await ServiceSignIn(key, " ")).StatusCode);

        // And nobody signs in on their own word any more, token or not.
        using var anonymous = _server.NewClient();
        var byName = await anonymous.PostAsJsonAsync("/api/v1/client/session",
            new ClientSessionRequest("alice", "whatever", "alice", "LAB-PC", "0.18.0"), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, byName.StatusCode);
        Assert.Contains("Update TapQueue", (await byName.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))!.Error);
    }

    [Fact]
    public async Task ADisabledUserIsNotSignedInAndTheModeHasToBeOn()
    {
        var (key, _) = await TurnOnAndEnroll();
        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/users/alice", new UpdateUserRequest(Disabled: true), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, (await ServiceSignIn(key, "alice")).StatusCode);

        await _server.Admin.PatchAsJsonAsync("/api/v1/admin/users/alice", new UpdateUserRequest(Disabled: false), TapQueueJson.Options);
        Assert.Equal(HttpStatusCode.OK, (await ServiceSignIn(key, "alice")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetMode(ClientSignIn.Pc)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ServiceSignIn(key, "alice")).StatusCode);
    }

    [Fact]
    public async Task ChangingTheModeSignsEveryTrayOutSoItSignsInTheNewWay()
    {
        var (key, _) = await TurnOnAndEnroll();
        var session = await TestServer.ReadAsync<ClientSessionResponse>(await ServiceSignIn(key, "alice"));
        using var tray = _server.NewClient(session.SessionToken);
        Assert.Equal(HttpStatusCode.OK, (await tray.PostAsync("/api/v1/client/heartbeat", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetMode(ClientSignIn.Domain)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tray.PostAsync("/api/v1/client/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task ALocalUserSignsInToTheTrayWithTheirTapQueuePassword()
    {
        // Name-and-password sign-in no longer needs Active Directory: local users have passwords of their own.
        Assert.Equal(HttpStatusCode.OK, (await SetMode(ClientSignIn.Domain)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.PutAsJsonAsync("/api/v1/admin/users/alice/password",
            new SetPasswordRequest("correct horse battery"), TapQueueJson.Options)).StatusCode);

        using var anonymous = _server.NewClient();
        Task<HttpResponseMessage> SignIn(string name, string? password, string? remember = null) =>
            anonymous.PostAsJsonAsync("/api/v1/client/session",
                new ClientSessionRequest(name, null, "pcuser", "TEST-PC", "test", ClientPlatform.Windows, password, remember), TapQueueJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn("alice", "wrong password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn("nobody", "correct horse battery")).StatusCode);
        var first = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn("alice", "correct horse battery"));
        Assert.NotNull(first.RememberToken);
        var again = await TestServer.ReadAsync<ClientSessionResponse>(await SignIn("alice", null, first.RememberToken));
        Assert.Equal("alice", again.User.Username);
    }
}
