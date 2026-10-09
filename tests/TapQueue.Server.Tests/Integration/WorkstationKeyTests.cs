using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>
/// Per-job identity: a PC's service gets a key, its printers carry the PC's print key, and the
/// service vouches for which PC user runs each signed-in tray app, so jobs are matched by PC and PC
/// user instead of address.
/// </summary>
public sealed class WorkstationKeyTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpResponseMessage> CheckInRaw(string? key, string computer = "LAB-PC")
    {
        using var service = _server.NewClient();
        return await service.PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest(computer, "0.8.0+test", "aaaa", null, ClientPlatform.Windows, key), TapQueueJson.Options);
    }

    private async Task<ClientSetupResponse> CheckIn(string? key, string computer = "LAB-PC") =>
        await TestServer.ReadAsync<ClientSetupResponse>(await CheckInRaw(key, computer));

    /// <summary>A new service checks in, gets its key, and the keyed path for the test queue.</summary>
    private async Task<(string Key, string IppPath)> Enroll(string computer = "LAB-PC")
    {
        var setup = await CheckIn("", computer);
        Assert.NotNull(setup.WorkstationKey);
        return (setup.WorkstationKey!, Assert.Single(setup.Queues).IppPath);
    }

    private async Task<HttpStatusCode> Vouch(string key, HttpClient tray, string pcUser, string computer = "LAB-PC")
    {
        using var service = _server.NewClient();
        using var response = await service.PostAsJsonAsync("/api/v1/client/workstation-session",
            new WorkstationSessionRequest(computer, key, tray.DefaultRequestHeaders.Authorization!.Parameter!, pcUser), TapQueueJson.Options);
        return response.StatusCode;
    }

    private async Task<JobDto> OnlyJob() =>
        Assert.Single((await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);

    [Fact]
    public async Task APcGetsAKeyOnceAndItsPrintersCarryAPrintKeyMadeFromIt()
    {
        var (key, path) = await Enroll();
        Assert.StartsWith($"/ipp/{TestServer.QueueId}/pc/", path);
        Assert.DoesNotContain(key, path); // the path is readable by anyone on the PC; the key isn't

        var again = await CheckIn(key);
        Assert.Null(again.WorkstationKey);
        Assert.Equal(path, Assert.Single(again.Queues).IppPath);

        var pc = Assert.Single((await _server.Admin.GetFromJsonAsync<List<WorkstationDto>>("/api/v1/admin/workstations", TapQueueJson.Options))!);
        Assert.True(pc.HasKey);
    }

    [Fact]
    public async Task OnceAPcHasAKeyNobodyElseCanCheckInAsIt()
    {
        var (key, _) = await Enroll();

        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInRaw("")).StatusCode);       // a new install claiming the name
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInRaw("wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInRaw(null)).StatusCode);     // or an old client
        Assert.Equal(HttpStatusCode.OK, (await CheckInRaw(key)).StatusCode);
    }

    [Fact]
    public async Task ForgettingAPcLetsItGetANewKeyAndOldPrintersStopWorking()
    {
        var (_, oldPath) = await Enroll();
        using var alice = await _server.SignInAsync("alice");

        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync("/api/v1/admin/workstations/LAB-PC")).StatusCode);
        var (_, newPath) = await Enroll();
        Assert.NotEqual(oldPath, newPath);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
            _server.PrintAsync("stale.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice", ippPath: oldPath));
        Assert.Contains("403", refused.Message);
    }

    [Fact]
    public async Task OldServicesStillGetKeylessPaths()
    {
        var setup = await CheckIn(null);
        Assert.Null(setup.WorkstationKey);
        Assert.Equal($"/ipp/{TestServer.QueueId}", Assert.Single(setup.Queues).IppPath);
    }

    [Fact]
    public async Task AJobThroughAKeyedPrinterGoesToTheSignedInPcUserWhoPrintedIt()
    {
        var (key, path) = await Enroll();
        using var alice = await _server.SignInAsync("alice", windowsUser: "a.smith");
        using var bob = await _server.SignInAsync("bob", windowsUser: "b.jones");
        Assert.Equal(HttpStatusCode.NoContent, await Vouch(key, alice, @"CORP\a.smith"));
        Assert.Equal(HttpStatusCode.NoContent, await Vouch(key, bob, @"CORP\b.jones"));

        await _server.PrintAsync("bobs.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "b.jones", ippPath: path);
        Assert.Equal("bob", (await OnlyJob()).Owner);
    }

    [Fact]
    public async Task OnAKeyedPrinterAPcUserWhoIsNotSignedInIsRefusedEvenIfOnlyOnePersonIsSignedIn()
    {
        var (key, path) = await Enroll();
        using var alice = await _server.SignInAsync("alice", windowsUser: "a.smith");
        await Vouch(key, alice, "a.smith");

        // Someone else on the same terminal server, without the tray app running.
        var refused = await _server.PrintAsync("someone-elses.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "c.brown", ippPath: path);
        Assert.Equal(IppStatus.ClientErrorNotAuthorized, refused.Code);
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task OnAKeyedPrinterOnlySessionsThePcVouchedForCount()
    {
        var (_, path) = await Enroll();
        // Signed in from this address, but no service vouched for it: address matching doesn't apply to keyed jobs.
        using var alice = await _server.SignInAsync("alice");

        var refused = await _server.PrintAsync("doc.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice", ippPath: path);
        Assert.Equal(IppStatus.ClientErrorNotAuthorized, refused.Code);
        Assert.Empty((await _server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);
    }

    [Fact]
    public async Task OnlyThePcsOwnServiceCanVouchForSessions()
    {
        var (key, _) = await Enroll();
        var (otherKey, _) = await Enroll("OTHER-PC");
        using var alice = await _server.SignInAsync("alice");

        Assert.Equal(HttpStatusCode.Forbidden, await Vouch(otherKey, alice, "alice"));
        Assert.Equal(HttpStatusCode.Forbidden, await Vouch("made-up", alice, "alice"));
        // Nor with the print key from a printer's path, which anyone on the PC can read.
        Assert.Equal(HttpStatusCode.Forbidden, await Vouch(Tokens.PrintKey(key), alice, "alice"));

        using var nobody = _server.NewClient("not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, await Vouch(key, nobody, "alice"));

        var session = Assert.Single((await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options))!);
        Assert.False(session.Verified);
        Assert.Equal(HttpStatusCode.NoContent, await Vouch(key, alice, "alice"));
        session = Assert.Single((await _server.Admin.GetFromJsonAsync<List<ClientSessionDto>>("/api/v1/admin/clients", TapQueueJson.Options))!);
        Assert.True(session.Verified);
    }

    [Fact]
    public async Task WithAnEnrollmentCodeOnlyPcsThatHaveItOrAKeyGetIn()
    {
        var (key, _) = await Enroll("OLD-PC");
        using var service = _server.NewClient();
        Task<HttpResponseMessage> CheckIn(string computer, string workstationKey, string? code) =>
            service.PostAsJsonAsync("/api/v1/client/setup",
                new ClientSetupRequest(computer, "0.9.0", null, null, ClientPlatform.Windows, workstationKey, code), TapQueueJson.Options);

        var made = await TestServer.ReadAsync<EnrollmentCodeResponse>(await _server.Admin.PostAsync("/api/v1/admin/server/enrollment-code", null));
        var info = await _server.Admin.GetFromJsonAsync<ServerInfoDto>("/api/v1/admin/server", TapQueueJson.Options);
        Assert.True(info!.EnrollmentCodeRequired);

        Assert.Equal(HttpStatusCode.Forbidden, (await CheckIn("NEW-PC", "", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckIn("NEW-PC", "", "a-guess")).StatusCode);
        // Nothing was stored about a PC that was turned away.
        var pcs = await _server.Admin.GetFromJsonAsync<List<WorkstationDto>>("/api/v1/admin/workstations", TapQueueJson.Options);
        Assert.Equal(["OLD-PC"], pcs!.Select(p => p.Hostname));

        // A PC that already has its key carries on without the code; a new one gets in with it.
        Assert.Equal(HttpStatusCode.OK, (await CheckIn("OLD-PC", key, null)).StatusCode);
        var admitted = await TestServer.ReadAsync<ClientSetupResponse>(await CheckIn("NEW-PC", "", made.Code));
        Assert.False(string.IsNullOrEmpty(admitted.WorkstationKey));

        // A new code replaces the old one, and turning it off lets anyone in again.
        await TestServer.ReadAsync<EnrollmentCodeResponse>(await _server.Admin.PostAsync("/api/v1/admin/server/enrollment-code", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckIn("THIRD-PC", "", made.Code)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _server.Admin.DeleteAsync("/api/v1/admin/server/enrollment-code")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CheckIn("THIRD-PC", "", null)).StatusCode);
    }

    [Fact]
    public async Task KeylessJobsAreMatchedByAddressUntilThatIsTurnedOff()
    {
        using var alice = await _server.SignInAsync("alice");
        await _server.PrintAsync("before.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice");
        Assert.Equal("alice", (await OnlyJob()).Owner);

        var server = await TestServer.ReadAsync<ServerInfoDto>(await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(AddressMatching: AddressMatching.Off), TapQueueJson.Options));
        Assert.Equal(AddressMatching.Off, server.AddressMatching);

        var refused = await _server.PrintAsync("after.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice");
        Assert.Equal(IppStatus.ClientErrorNotAuthorized, refused.Code);
        Assert.Equal("before.pdf", (await OnlyJob()).Name);

        Assert.Equal(HttpStatusCode.BadRequest, (await _server.Admin.PatchAsJsonAsync("/api/v1/admin/server/settings",
            new UpdateServerSettingsRequest(AddressMatching: "sometimes"), TapQueueJson.Options)).StatusCode);
    }

    [Fact]
    public async Task AnotherPcBehindTheSameAddressCantSeeAKeyedPcsJobs()
    {
        var (key, path) = await Enroll();
        using var alice = await _server.SignInAsync("alice");
        await Vouch(key, alice, "alice");
        var printed = await _server.PrintAsync("mine.pdf", "%PDF-1.4 test"u8.ToArray(), requestingUser: "alice", ippPath: path);
        var jobId = printed.Find(Ipp.IppTag.JobAttributes, "job-id")!.First?.AsInt() ?? throw new InvalidDataException("no job-id");

        // Same address (as behind NAT), but through the keyless queue: not this PC's job.
        var request = Ipp.IppMessage.CreateRequest(Ipp.IppOperation.GetJobAttributes, Ipp.IppClient.NextRequestId(), _server.QueueUri);
        request.Group(Ipp.IppTag.OperationAttributes).Add("job-id", Ipp.IppValue.Integer(jobId));
        using var ipp = new Ipp.IppClient(tlsSkipVerify: false, TimeSpan.FromSeconds(30));
        Assert.Equal(Ipp.IppStatus.ClientErrorNotFound, (await ipp.SendAsync(_server.QueueUri, request)).Code);

        var keyedUri = $"ipp://{_server.BaseUri.Authority}{path}";
        var own = Ipp.IppMessage.CreateRequest(Ipp.IppOperation.GetJobAttributes, Ipp.IppClient.NextRequestId(), keyedUri);
        own.Group(Ipp.IppTag.OperationAttributes).Add("job-id", Ipp.IppValue.Integer(jobId));
        Assert.Equal(Ipp.IppStatus.Ok, (await ipp.SendAsync(keyedUri, own)).Code);
    }

    [Fact]
    public async Task AKeyedPrinterWorksOverIpps()
    {
        await using var server = await TestServer.StartAsync(tls: true);
        using var service = server.NewClient();
        var setup = await TestServer.ReadAsync<ClientSetupResponse>(await service.PostAsJsonAsync("/api/v1/client/setup",
            new ClientSetupRequest("LAB-PC", "0.8.0+test", "aaaa", null, ClientPlatform.Linux, ""), TapQueueJson.Options));
        using var alice = await server.SignInAsync("alice");
        using (var vouch = await service.PostAsJsonAsync("/api/v1/client/workstation-session",
                   new WorkstationSessionRequest("LAB-PC", setup.WorkstationKey!, alice.DefaultRequestHeaders.Authorization!.Parameter!, "alice"), TapQueueJson.Options))
            Assert.Equal(HttpStatusCode.NoContent, vouch.StatusCode);

        var uri = $"ipps://{server.HttpsUri!.Authority}{Assert.Single(setup.Queues).IppPath}";
        var request = Ipp.IppMessage.CreateRequest(Ipp.IppOperation.PrintJob, Ipp.IppClient.NextRequestId(), uri);
        request.Group(Ipp.IppTag.OperationAttributes)
            .Add("requesting-user-name", Ipp.IppValue.Name("alice"))
            .Add("job-name", Ipp.IppValue.Name("secure.pdf"))
            .Add("document-format", Ipp.IppValue.MimeType("application/pdf"));
        using var ipp = new Ipp.IppClient(tlsSkipVerify: true, TimeSpan.FromSeconds(30));
        var printed = await ipp.SendAsync(uri, request, new MemoryStream("%PDF-1.4 test"u8.ToArray()));
        Assert.Equal(Ipp.IppStatus.Ok, printed.Code);
        Assert.StartsWith("ipps://", printed.Find(Ipp.IppTag.JobAttributes, "job-printer-uri")!.First?.AsString());

        var job = Assert.Single((await server.Admin.GetFromJsonAsync<List<JobDto>>("/api/v1/admin/jobs", TapQueueJson.Options))!);
        Assert.Equal("alice", job.Owner);
    }
}
