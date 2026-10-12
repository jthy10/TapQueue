using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TapQueue.Server.Tests.Notifications;

/// <summary>
/// A mail server on a localhost port that takes one message per connection and remembers what it was
/// told. It offers STARTTLS and AUTH PLAIN unless told otherwise, with a self-signed certificate.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate = SelfSigned();
    private readonly Task _accepting;

    public FakeSmtpServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>TLS from the first byte, like port 465.</summary>
    public bool ImplicitTls { get; set; }
    public bool OffersStartTls { get; set; } = true;
    /// <summary>What follows "AUTH " in the EHLO answer; empty to offer none.</summary>
    public string AuthMechanisms { get; set; } = "PLAIN LOGIN";
    /// <summary>The only password taken; null takes any.</summary>
    public string? Password { get; set; }
    /// <summary>Answers RCPT TO for this address with 550.</summary>
    public string? RefusedRecipient { get; set; }

    /// <summary>Every command line received, in order, across connections.</summary>
    public List<string> Commands { get; } = [];
    /// <summary>Whether each command arrived over TLS.</summary>
    public List<bool> Encrypted { get; } = [];
    /// <summary>The messages taken, as sent after DATA.</summary>
    public List<string> Messages { get; } = [];

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            try
            {
                using (client)
                    await ServeAsync(client.GetStream());
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            {
                // The sender hung up or refused the certificate, which some tests are about.
            }
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        var tls = false;
        if (ImplicitTls)
        {
            stream = await SecureAsync(stream);
            tls = true;
        }
        var reader = new StreamReader(stream, Encoding.UTF8, false, 1, leaveOpen: true);
        async Task Say(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"));

        await Say("220 fake.test ESMTP");
        while (await reader.ReadLineAsync(_stop.Token) is { } line)
        {
            lock (Commands)
            {
                Commands.Add(line);
                Encrypted.Add(tls);
            }
            var verb = line.Split(' ')[0].ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    await Say("250-fake.test");
                    if (OffersStartTls && !tls)
                        await Say("250-STARTTLS");
                    if (AuthMechanisms.Length > 0)
                        await Say($"250-AUTH {AuthMechanisms}");
                    await Say("250 SIZE 1000000");
                    break;
                case "STARTTLS":
                    await Say("220 go ahead");
                    stream = await SecureAsync(stream);
                    reader = new StreamReader(stream, Encoding.UTF8, false, 1, leaveOpen: true);
                    tls = true;
                    break;
                case "AUTH" when line.StartsWith("AUTH PLAIN ", StringComparison.Ordinal):
                    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(line["AUTH PLAIN ".Length..])).Split('\0');
                    await Say(Password is null || parts[2] == Password ? "235 2.7.0 welcome" : "535 5.7.8 Username and Password not accepted");
                    break;
                case "AUTH" when line == "AUTH LOGIN":
                    await Say("334 VXNlcm5hbWU6");
                    await reader.ReadLineAsync(_stop.Token);
                    await Say("334 UGFzc3dvcmQ6");
                    var given = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync(_stop.Token) ?? ""));
                    await Say(Password is null || given == Password ? "235 2.7.0 welcome" : "535 5.7.8 Username and Password not accepted");
                    break;
                case "MAIL":
                    await Say("250 ok");
                    break;
                case "RCPT":
                    await Say(RefusedRecipient is not null && line.Contains(RefusedRecipient) ? "550 5.1.1 no such user" : "250 ok");
                    break;
                case "DATA":
                    await Say("354 end with .");
                    var message = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } data && data != ".")
                        message.Append(data).Append("\r\n");
                    lock (Commands)
                        Messages.Add(message.ToString());
                    await Say("250 queued");
                    break;
                case "QUIT":
                    await Say("221 bye");
                    return;
                default:
                    await Say("500 what?");
                    break;
            }
        }
    }

    private async Task<SslStream> SecureAsync(Stream stream)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
        return tls;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Re-imported so the private key is usable for TLS on every platform.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
        _certificate.Dispose();
    }
}
