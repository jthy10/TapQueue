using System.Text;
using TapQueue.Server.Notifications;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Tests.Notifications;

/// <summary>Sending through a mail server: encryption, signing in, and what's said when it goes wrong.</summary>
public sealed class SmtpMailSenderTests : IAsyncLifetime
{
    private static readonly MailMessage Message = new("TapQueue: Office printer is unreachable", "Since: 2026-10-12 09:00\nWhy: no answer\n");
    private readonly FakeSmtpServer _smtp = new();
    private readonly SmtpMailSender _sender = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _smtp.DisposeAsync();

    private NotificationConfig Config(string security = MailSecurity.StartTls, string username = "alerts@example.org") => new(
        Host: "localhost", Port: _smtp.Port, Security: security, TlsSkipVerify: true, Username: username, Password: "app-password",
        From: "alerts@example.org", Recipients: ["it@example.org", "boss@example.org"]);

    private static string Body(string message) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].Replace("\r\n", "")));

    [Fact]
    public async Task StartTlsSignsInOnlyOnceTheConnectionIsEncrypted()
    {
        await _sender.SendAsync(Config(), Message, default);

        var message = Assert.Single(_smtp.Messages);
        Assert.Contains("From: \"TapQueue\" <alerts@example.org>\r\n", message);
        Assert.Contains("To: it@example.org,\r\n boss@example.org\r\n", message);
        Assert.Contains("Subject: TapQueue: Office printer is unreachable\r\n", message);
        Assert.Equal("Since: 2026-10-12 09:00\r\nWhy: no answer\r\n", Body(message));
        Assert.Equal(["EHLO", "STARTTLS", "EHLO", "AUTH", "MAIL", "RCPT", "RCPT", "DATA", "QUIT"], _smtp.Commands.Select(c => c.Split(' ')[0]));
        Assert.False(_smtp.Encrypted[_smtp.Commands.IndexOf("STARTTLS")]);
        Assert.All(_smtp.Encrypted.Skip(2), Assert.True);
        Assert.Contains("MAIL FROM:<alerts@example.org>", _smtp.Commands);
        Assert.Contains("RCPT TO:<boss@example.org>", _smtp.Commands);
    }

    [Fact]
    public async Task TlsFromTheStartWorksLikePort465()
    {
        _smtp.ImplicitTls = true;

        await _sender.SendAsync(Config(MailSecurity.Tls), Message, default);

        Assert.Single(_smtp.Messages);
        Assert.All(_smtp.Encrypted, Assert.True);
    }

    [Fact]
    public async Task ARelayWithoutEncryptionOrSignInIsJustHandedTheMessage()
    {
        await _sender.SendAsync(Config(MailSecurity.None, username: ""), Message, default);

        Assert.Single(_smtp.Messages);
        Assert.DoesNotContain(_smtp.Commands, c => c.StartsWith("AUTH") || c == "STARTTLS");
    }

    [Fact]
    public async Task SignsInWithLoginWhenThatsAllTheServerOffers()
    {
        _smtp.AuthMechanisms = "LOGIN";
        _smtp.Password = "app-password";

        await _sender.SendAsync(Config(), Message, default);

        Assert.Single(_smtp.Messages);
        Assert.Contains("AUTH LOGIN", _smtp.Commands);
    }

    [Fact]
    public async Task AnUntrustedCertificateIsRefusedUnlessToldToAcceptAny()
    {
        var error = await Assert.ThrowsAsync<MailException>(() => _sender.SendAsync(Config() with { TlsSkipVerify = false }, Message, default));

        Assert.StartsWith($"Couldn't set up TLS with localhost:{_smtp.Port}", error.Message);
        Assert.Empty(_smtp.Messages);
        Assert.DoesNotContain(_smtp.Commands, c => c.StartsWith("AUTH"));
    }

    [Fact]
    public async Task ThePasswordIsNotSentWhenTheServerHasNoStartTls()
    {
        _smtp.OffersStartTls = false;

        var error = await Assert.ThrowsAsync<MailException>(() => _sender.SendAsync(Config(), Message, default));

        Assert.Contains("doesn't offer STARTTLS", error.Message);
        Assert.DoesNotContain(_smtp.Commands, c => c.StartsWith("AUTH"));
    }

    [Fact]
    public async Task ARefusedPasswordSaysSoWithoutRepeatingIt()
    {
        _smtp.Password = "something else";

        var error = await Assert.ThrowsAsync<MailException>(() => _sender.SendAsync(Config(), Message, default));

        Assert.StartsWith("The mail server refused the username or password: 535 5.7.8 Username and Password not accepted", error.Message);
        Assert.DoesNotContain("app-password", error.Message);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alerts@example.org\0app-password")), error.Message);
        Assert.Empty(_smtp.Messages);
    }

    [Fact]
    public async Task ARefusedRecipientStopsTheMessage()
    {
        _smtp.RefusedRecipient = "boss@example.org";

        var error = await Assert.ThrowsAnyAsync<MailException>(() => _sender.SendAsync(Config(), Message, default));

        Assert.Equal("The mail server answered \"550 5.1.1 no such user\" to RCPT TO:<boss@example.org>.", error.Message);
        Assert.Empty(_smtp.Messages);
    }

    [Fact]
    public async Task NothingListeningIsExplained()
    {
        var port = _smtp.Port;
        await _smtp.DisposeAsync();

        var error = await Assert.ThrowsAsync<MailException>(() => _sender.SendAsync(Config() with { Port = port }, Message, default));

        Assert.StartsWith($"Couldn't connect to localhost:{port}", error.Message);
    }
}
