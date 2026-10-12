using System.Net;
using System.Net.Http.Json;
using TapQueue.Server.Ipp;
using TapQueue.Server.Notifications;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Integration;

/// <summary>Email alerts: the settings, and what goes out when printers, stations and clients have trouble.</summary>
public sealed class NotificationTests : IAsyncLifetime
{
    /// <summary>Collects what would have been sent, or refuses it with <see cref="Error"/>.</summary>
    private sealed class Outbox : IMailSender
    {
        public List<MailMessage> Sent { get; } = [];
        public NotificationConfig? LastConfig { get; private set; }
        public string? Error { get; set; }

        public Task SendAsync(NotificationConfig config, MailMessage message, CancellationToken ct)
        {
            LastConfig = config;
            if (Error is not null)
                throw new MailException(Error);
            lock (Sent)
                Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private const string Url = "/api/v1/admin/server/notifications";
    private readonly Outbox _outbox = new();
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.StartAsync(configureServices: services => services.AddSingleton<IMailSender>(_outbox));

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpResponseMessage> Patch(UpdateNotificationConfigRequest request) =>
        await _server.Admin.PatchAsJsonAsync(Url, request, TapQueueJson.Options);

    private async Task<NotificationsDto> Status() => (await _server.Admin.GetFromJsonAsync<NotificationsDto>(Url, TapQueueJson.Options))!;

    private async Task TurnOn(int delayMinutes = 0, bool warnings = false) =>
        await TestServer.ReadAsync<NotificationsDto>(await Patch(new UpdateNotificationConfigRequest(
            Enabled: true, Host: "smtp.example.org", Username: "alerts@example.org", Password: "app-password", From: "alerts@example.org",
            Recipients: ["it@example.org"], PrinterWarnings: warnings, DelayMinutes: delayMinutes)));

    private Task Check(TimeSpan? ahead = null) => _server.Service<NotificationService>().CheckAsync(DateTimeOffset.UtcNow + (ahead ?? TimeSpan.Zero));

    private async Task ProbePrinter() =>
        (await _server.Admin.GetAsync("/api/v1/admin/printers?refresh=true")).EnsureSuccessStatusCode();

    [Fact]
    public async Task SettingsAreSavedAndThePasswordIsNeverSentBack()
    {
        var before = await Status();
        Assert.False(before.Config.Enabled);
        Assert.Equal((587, MailSecurity.StartTls, 5), (before.Config.Port, before.Config.Security, before.Config.DelayMinutes));

        await TurnOn(delayMinutes: 10);

        var raw = await _server.Admin.GetStringAsync(Url);
        Assert.DoesNotContain("app-password", raw);
        var config = (await Status()).Config;
        Assert.True(config.Enabled && config.HasPassword);
        Assert.Equal(("smtp.example.org", "alerts@example.org", 10), (config.Host, config.From, config.DelayMinutes));
        Assert.Equal(["it@example.org"], config.Recipients);
        var logged = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);
        Assert.Contains(logged!, e => e.Message.StartsWith("Notifications: turned on; mail server smtp.example.org:587 (STARTTLS)") && !e.Message.Contains("app-password"));
    }

    [Fact]
    public async Task BadSettingsAreRefused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Enabled: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(From: "not an address"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Recipients: ["it@example.org", "it@example.org>\r\nDATA"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Security: "ssl"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Port: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new(Host: "smtp.example.org\r\nEHLO evil"))).StatusCode);
        Assert.Equal(new NotificationConfig().ToDto() with { Recipients = [] }, (await Status()).Config with { Recipients = [] });
    }

    [Fact]
    public async Task PointingTheSavedPasswordAtAnotherServerTakesThePasswordAgain()
    {
        await TurnOn();

        var moved = await Patch(new(Host: "evil.example.com"));
        var unencrypted = await Patch(new(Security: MailSecurity.None, Port: 25));
        var unchecked_ = await Patch(new(TlsSkipVerify: true));
        var recipients = await Patch(new(Recipients: ["it@example.org", "boss@example.org"]));
        var movedWithPassword = await Patch(new(Host: "smtp.other.example", Password: "another"));

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unencrypted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unchecked_.StatusCode);
        Assert.Equal(2, (await TestServer.ReadAsync<NotificationsDto>(recipients)).Config.Recipients.Count);
        Assert.Equal("smtp.other.example", (await TestServer.ReadAsync<NotificationsDto>(movedWithPassword)).Config.Host);
    }

    [Fact]
    public async Task ATestEmailGoesToEveryRecipientEvenWhileNotificationsAreOff()
    {
        await TestServer.ReadAsync<NotificationsDto>(await Patch(new(Host: "smtp.example.org", From: "alerts@example.org", Recipients: ["it@example.org", "boss@example.org"])));

        var result = await TestServer.ReadAsync<NotificationTestResultDto>(await _server.Admin.PostAsync(Url + "/test", null));

        Assert.True(result.Success, result.Message);
        Assert.Equal("TapQueue: test email", Assert.Single(_outbox.Sent).Subject);
        Assert.Equal(["it@example.org", "boss@example.org"], _outbox.LastConfig!.To);
        Assert.NotNull((await Status()).LastSentAt);

        _outbox.Error = "The mail server refused the username or password: 535 no.";
        var failed = await TestServer.ReadAsync<NotificationTestResultDto>(await _server.Admin.PostAsync(Url + "/test", null));
        Assert.False(failed.Success);
        Assert.Equal(_outbox.Error, failed.Message);
        Assert.Equal(_outbox.Error, (await Status()).LastError);
    }

    [Fact]
    public async Task APrinterProblemIsEmailedOnceAndAgainWhenItsFixed()
    {
        await TurnOn();
        await ProbePrinter();
        await Check();
        Assert.Empty(_outbox.Sent);

        _server.Printer.State = IppPrinterState.Stopped;
        _server.Printer.Reasons = ["media-jam-error"];
        await ProbePrinter();
        await Check();
        await Check();

        var problem = Assert.Single(_outbox.Sent);
        Assert.Equal("TapQueue: Office printer: paper jam", problem.Subject);
        Assert.Contains("Jobs released to it stay on hold until it's fixed.", problem.Body);
        var open = Assert.Single((await Status()).Alerts);
        Assert.Equal(($"printer:{TestServer.PrinterId}", "Office printer: paper jam"), (open.Subject, open.Summary));

        _server.Printer.State = IppPrinterState.Idle;
        _server.Printer.Reasons = ["none"];
        await ProbePrinter();
        await Check();
        await Check();

        Assert.Equal(["TapQueue: Office printer: paper jam", "TapQueue: Office printer is ready again"], _outbox.Sent.Select(m => m.Subject));
        Assert.Empty((await Status()).Alerts);
    }

    [Fact]
    public async Task AProblemThatDoesntLastAsLongAsTheDelaySendsNothing()
    {
        await TurnOn(delayMinutes: 5);
        await _server.Admin.PatchAsJsonAsync($"/api/v1/admin/printers/{TestServer.PrinterId}",
            new UpdatePrinterRequest(Uri: "ipp://127.0.0.1:9/ipp/print"), TapQueueJson.Options);
        await ProbePrinter();

        await Check(TimeSpan.FromMinutes(4));
        Assert.Empty(_outbox.Sent);

        await Check(TimeSpan.FromMinutes(6));
        var sent = Assert.Single(_outbox.Sent);
        Assert.Equal("TapQueue: Office printer is unreachable", sent.Subject);
        Assert.Contains("Why: ", sent.Body);
    }

    [Fact]
    public async Task WarningsLikeLowTonerAreOnlyEmailedWhenAskedFor()
    {
        await TurnOn();
        _server.Printer.TonerLevel = 4;
        await ProbePrinter();
        await Check();
        Assert.Empty(_outbox.Sent);

        await TestServer.ReadAsync<NotificationsDto>(await Patch(new(PrinterWarnings: true)));
        await Check();

        var sent = Assert.Single(_outbox.Sent);
        Assert.Equal("TapQueue: Office printer: black cartridge low (4%)", sent.Subject);
        Assert.Contains("It still prints.", sent.Body);
    }

    [Fact]
    public async Task AStationThatStopsCheckingInIsEmailedAndAgainWhenItsBack()
    {
        await TurnOn();
        var created = await TestServer.ReadAsync<StationTokenResponse>(await _server.Admin.PostAsJsonAsync(
            "/api/v1/admin/stations", new CreateStationRequest("lobby", TestServer.PrinterId), TapQueueJson.Options));
        using var station = _server.NewClient(created.Token);
        async Task Heartbeat() => (await station.PostAsJsonAsync("/api/v1/station/heartbeat",
            new StationHeartbeatRequest("0.15.0+test", DateTimeOffset.UtcNow, "pcprox", "ok", null, 1), TapQueueJson.Options)).EnsureSuccessStatusCode();

        // Never heard from: nothing to miss.
        await Check(TimeSpan.FromHours(1));
        Assert.Empty(_outbox.Sent);

        await Heartbeat();
        await Check();
        Assert.Empty(_outbox.Sent);
        await Check(TimeSpan.FromMinutes(10));
        await Check(TimeSpan.FromMinutes(11));

        var offline = Assert.Single(_outbox.Sent);
        Assert.Equal("TapQueue: Station lobby is offline", offline.Subject);
        Assert.Contains("Printer: Office printer", offline.Body);

        await Heartbeat();
        await Check();
        Assert.Equal(["TapQueue: Station lobby is offline", "TapQueue: Station lobby is back online"], _outbox.Sent.Select(m => m.Subject));
        Assert.Empty((await Status()).Alerts);
    }

    [Fact]
    public async Task ACrashReportIsEmailedButAPcCrashingOverAndOverIsnt()
    {
        async Task Crash(string computer, string message) => (await _server.NewClient().PostAsJsonAsync("/api/v1/client/crash",
            new CrashReportRequest(computer, CrashProgram.Tray, ClientPlatform.Windows, "0.10.0+test", DateTimeOffset.UtcNow, message, "at Foo()"),
            TapQueueJson.Options)).EnsureSuccessStatusCode();

        // From before notifications were on: not news.
        await Crash("OLD-PC", "Old");
        await TurnOn();
        await Check();
        Assert.Empty(_outbox.Sent);

        await Crash("UP", "Boom");
        await Crash("UP", "Boom again");
        await Crash("P14S", "Bang");
        await Check();
        await Crash("UP", "And again");
        await Check();

        var sent = Assert.Single(_outbox.Sent);
        Assert.Equal("TapQueue: 2 things need a look", sent.Subject);
        Assert.Contains("The tray app on UP crashed\n  Boom\n", sent.Body);
        Assert.Contains("The tray app on P14S crashed\n  Bang\n", sent.Body);
        Assert.DoesNotContain("again", sent.Body);
    }

    [Fact]
    public async Task AnEmailTheMailServerRefusesIsKeptAndTriedAgain()
    {
        await TurnOn();
        _outbox.Error = "Couldn't connect to smtp.example.org:587: Connection refused.";
        _server.Printer.State = IppPrinterState.Stopped;
        _server.Printer.Reasons = ["media-jam-error"];
        await ProbePrinter();

        await Check();
        await Check();

        var status = await Status();
        Assert.Equal((1, _outbox.Error), (status.Waiting, status.LastError));
        var logged = await _server.Admin.GetFromJsonAsync<List<EventDto>>("/api/v1/admin/events?category=admin", TapQueueJson.Options);
        Assert.Single(logged!, e => e.Message.StartsWith("Couldn't send a notification email: Couldn't connect to smtp.example.org:587"));

        _outbox.Error = null;
        await Check(TimeSpan.FromMinutes(5));

        Assert.Equal("TapQueue: Office printer: paper jam", Assert.Single(_outbox.Sent).Subject);
        status = await Status();
        Assert.Equal((0, null), (status.Waiting, status.LastError));
    }

    [Fact]
    public async Task TurnedOffNothingIsSentAndWhatWasWrongIsForgotten()
    {
        await TurnOn();
        _server.Printer.State = IppPrinterState.Stopped;
        _server.Printer.Reasons = ["media-jam-error"];
        await ProbePrinter();
        await Check();
        Assert.Single(_outbox.Sent);

        await TestServer.ReadAsync<NotificationsDto>(await Patch(new(Enabled: false)));
        _server.Printer.State = IppPrinterState.Idle;
        _server.Printer.Reasons = ["none"];
        await ProbePrinter();
        await Check();

        Assert.Single(_outbox.Sent);
        Assert.Empty((await Status()).Alerts);
    }
}
