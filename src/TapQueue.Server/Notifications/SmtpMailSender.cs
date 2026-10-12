using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Notifications;

/// <summary>
/// Sends email over SMTP to whatever mail server the admin entered: their provider's (Gmail, Fastmail,
/// an SMTP service like SES or Mailgun) or a relay of their own. One connection per message, signed in
/// with AUTH PLAIN or LOGIN if there's a username. The password is only sent over TLS unless the admin
/// chose no encryption.
/// </summary>
public sealed class SmtpMailSender : IMailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(NotificationConfig config, MailMessage message, CancellationToken ct)
    {
        if (!config.CanSend)
            throw new MailException("Set the mail server, the address to send from and at least one recipient first.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await DeliverAsync(config, message, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MailException($"{config.Host}:{config.Port} didn't answer within {Timeout.TotalSeconds:0} seconds. Check the port and the kind of encryption.");
        }
        catch (SocketException ex)
        {
            throw new MailException($"Couldn't connect to {config.Host}:{config.Port}: {ex.Message}.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new MailException($"Couldn't set up TLS with {config.Host}:{config.Port}: {ex.Message} Check the port and the kind of encryption.", ex);
        }
        catch (IOException ex)
        {
            throw new MailException($"The connection to {config.Host}:{config.Port} broke: {ex.Message} Check the port and the kind of encryption.", ex);
        }
    }

    private static async Task DeliverAsync(NotificationConfig config, MailMessage message, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(config.Host, config.Port, ct);
        Stream stream = tcp.GetStream();
        if (config.Security == MailSecurity.Tls)
            stream = await SecureAsync(stream, config, ct);
        var smtp = new Conversation(stream);
        await smtp.ExpectAsync("connecting", 220, ct);
        var offers = await HelloAsync(smtp, ct);

        if (config.Security == MailSecurity.StartTls)
        {
            if (!offers.Any(o => o.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                throw new MailException($"{config.Host}:{config.Port} doesn't offer STARTTLS. Try port 587, or TLS on port 465.");
            await smtp.CommandAsync("STARTTLS", 220, ct);
            // Anything already read past the answer was sent before encryption started, so it isn't the server's.
            if (smtp.HasUnread)
                throw new MailException($"{config.Host}:{config.Port} sent data before TLS started.");
            smtp = new Conversation(await SecureAsync(stream, config, ct));
            offers = await HelloAsync(smtp, ct);
        }

        if (config.Username.Length > 0)
            await SignInAsync(smtp, config, offers, ct);

        await smtp.CommandAsync($"MAIL FROM:<{config.From}>", 250, ct);
        foreach (var recipient in config.To)
            await smtp.CommandAsync($"RCPT TO:<{recipient}>", 250, ct, alsoOk: 251);
        await smtp.CommandAsync("DATA", 354, ct);
        await smtp.WriteAsync(message.Format(config.From, config.FromName, config.To, DateTimeOffset.UtcNow) + ".\r\n", ct);
        await smtp.ExpectAsync("the message", 250, ct);
        try
        {
            await smtp.CommandAsync("QUIT", 221, ct);
        }
        catch (Exception ex) when (ex is MailException or IOException)
        {
            // The message was accepted; a server that hangs up without saying goodbye doesn't change that.
        }
    }

    private static async Task<SslStream> SecureAsync(Stream stream, NotificationConfig config, CancellationToken ct)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions { TargetHost = config.Host };
        if (config.TlsSkipVerify)
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await tls.AuthenticateAsClientAsync(options, ct);
        return tls;
    }

    /// <summary>EHLO; the answer's other lines are what the server offers ("STARTTLS", "AUTH PLAIN LOGIN").</summary>
    private static async Task<List<string>> HelloAsync(Conversation smtp, CancellationToken ct) =>
        (await smtp.CommandAsync($"EHLO {LocalName()}", 250, ct)).Skip(1).ToList();

    private static async Task SignInAsync(Conversation smtp, NotificationConfig config, List<string> offers, CancellationToken ct)
    {
        // "AUTH PLAIN LOGIN", or "AUTH=PLAIN LOGIN" from some old servers.
        var mechanisms = offers.Where(o => o.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
            .SelectMany(o => o[4..].Split([' ', '='], StringSplitOptions.RemoveEmptyEntries))
            .Select(m => m.ToUpperInvariant()).ToHashSet();
        try
        {
            if (mechanisms.Contains("PLAIN"))
            {
                await smtp.CommandAsync("AUTH PLAIN " + Base64($"\0{config.Username}\0{config.Password}"), 235, ct, shown: "AUTH PLAIN");
            }
            else if (mechanisms.Contains("LOGIN"))
            {
                await smtp.CommandAsync("AUTH LOGIN", 334, ct);
                await smtp.CommandAsync(Base64(config.Username), 334, ct, shown: "the username");
                await smtp.CommandAsync(Base64(config.Password), 235, ct, shown: "the password");
            }
            else
            {
                throw new MailException(mechanisms.Count == 0
                    ? $"{config.Host}:{config.Port} doesn't let anyone sign in{(config.Security == MailSecurity.None ? " without encryption" : "")}. Clear the username if it doesn't need one."
                    : $"{config.Host}:{config.Port} only offers sign-in with {string.Join(", ", mechanisms.Order())}, not a username and password.");
            }
        }
        catch (SmtpReplyException ex) when (ex.Code is 535 or 534 or 530)
        {
            throw new MailException($"The mail server refused the username or password: {ex.Reply} Many providers want an app password here, not the account's own.", ex);
        }
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>This machine's name for EHLO, or an address literal if it has none a mail server would take.</summary>
    private static string LocalName()
    {
        try
        {
            var name = Dns.GetHostName();
            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
                return name;
        }
        catch (SocketException)
        {
        }
        return "[127.0.0.1]";
    }

    /// <summary>The server answered a command with something other than what goes ahead.</summary>
    private sealed class SmtpReplyException(int code, string reply, string during)
        : MailException($"The mail server answered \"{reply}\" to {during}.")
    {
        public int Code { get; } = code;
        public string Reply { get; } = reply;
    }

    /// <summary>Commands out, numbered replies back, a line at a time.</summary>
    private sealed class Conversation(Stream stream)
    {
        private const int MaxLineLength = 4096;
        private const int MaxReplyLines = 100;

        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public bool HasUnread => _start < _end;

        public Task WriteAsync(string text, CancellationToken ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

        /// <param name="shown">What to call the command in an error, for ones that carry a password.</param>
        /// <returns>The reply's lines without their codes.</returns>
        public async Task<List<string>> CommandAsync(string command, int expected, CancellationToken ct, int? alsoOk = null, string? shown = null)
        {
            await WriteAsync(command + "\r\n", ct);
            return await ExpectAsync(shown ?? command, expected, ct, alsoOk);
        }

        public async Task<List<string>> ExpectAsync(string during, int expected, CancellationToken ct, int? alsoOk = null)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out var code))
                    throw new MailException($"That doesn't look like a mail server: it answered \"{Shorten(line)}\".");
                lines.Add(line.Length > 4 ? line[4..] : "");
                // "250-…" has more lines coming; "250 …" is the last.
                if (line.Length > 3 && line[3] == '-' && lines.Count < MaxReplyLines)
                    continue;
                if (code != expected && code != alsoOk)
                    throw new SmtpReplyException(code, Shorten($"{code} {string.Join(" ", lines)}"), during);
                return lines;
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var line = new MemoryStream();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer, ct);
                    if (_end == 0)
                        throw new IOException("The mail server closed the connection.");
                }
                var b = _buffer[_start++];
                if (b == '\n')
                    return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                if (line.Length >= MaxLineLength)
                    throw new MailException("That doesn't look like a mail server: it sent a line with no end.");
                line.WriteByte(b);
            }
        }

        private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";
    }
}
