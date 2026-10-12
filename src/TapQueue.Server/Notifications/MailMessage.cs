using System.Globalization;
using System.Text;

namespace TapQueue.Server.Notifications;

/// <summary>A plain-text email. Who it's from and to comes from the settings when it's sent.</summary>
public sealed record MailMessage(string Subject, string Body)
{
    private const int LineLength = 76;

    /// <summary>
    /// What an address may look like: something@domain, with nothing that could end the SMTP command or
    /// header it goes in. Whether it exists is the mail server's business.
    /// </summary>
    public static bool IsValidAddress(string? address) =>
        address is { Length: > 2 and <= 254 }
        && address.IndexOf('@') is > 0 and var at && at == address.LastIndexOf('@') && at < address.Length - 1
        && address.All(c => c is > ' ' and < (char)127 and not ('<' or '>' or ',' or ';' or ':' or '"' or '(' or ')' or '[' or ']' or '\\'));

    /// <summary>
    /// The message as it goes after DATA: headers, then the body as base64 so any text survives any
    /// server. Lines end in CRLF and none starts with a dot, so nothing needs escaping.
    /// </summary>
    public string Format(string from, string fromName, IReadOnlyList<string> to, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append("From: ").Append(fromName.Length > 0 ? $"{Phrase(fromName)} <{from}>" : from).Append("\r\n");
        text.Append("To: ").Append(string.Join(",\r\n ", to)).Append("\r\n");
        text.Append("Subject: ").Append(Encoded(Clean(Subject))).Append("\r\n");
        text.Append("Date: ").Append(now.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)).Append(" +0000\r\n");
        text.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append(from[from.IndexOf('@')..]).Append(">\r\n");
        // Tells mail servers not to answer with out-of-office replies.
        text.Append("Auto-Submitted: auto-generated\r\n");
        text.Append("MIME-Version: 1.0\r\n");
        text.Append("Content-Type: text/plain; charset=utf-8\r\n");
        text.Append("Content-Transfer-Encoding: base64\r\n");
        text.Append("\r\n");
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Body.ReplaceLineEndings("\r\n")));
        for (var i = 0; i < body.Length; i += LineLength)
            text.Append(body, i, Math.Min(LineLength, body.Length - i)).Append("\r\n");
        return text.ToString();
    }

    /// <summary>One line, whatever a printer or PC put in its name.</summary>
    private static string Clean(string value) =>
        new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    private static string Phrase(string name)
    {
        name = Clean(name);
        return name.All(char.IsAscii) ? $"\"{name.Replace("\\", "").Replace("\"", "'")}\"" : Encoded(name);
    }

    /// <summary>
    /// Header text as it is if it's short ASCII, otherwise as RFC 2047 encoded words, each short enough
    /// for a header line and cut between characters.
    /// </summary>
    private static string Encoded(string value)
    {
        if (value.All(char.IsAscii) && value.Length <= 60)
            return value;
        var words = new List<string>();
        var chunk = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(chunk.ToString()) + rune.Utf8SequenceLength > 39)
            {
                words.Add(Word(chunk.ToString()));
                chunk.Clear();
            }
            chunk.Append(rune.ToString());
        }
        if (chunk.Length > 0)
            words.Add(Word(chunk.ToString()));
        return string.Join("\r\n ", words);

        static string Word(string part) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(part))}?=";
    }
}

/// <summary>An email couldn't be sent. The message is written for the admin who set the mail server up.</summary>
public class MailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends an email through the mail server in the settings. Tests swap in one that only collects them.</summary>
public interface IMailSender
{
    /// <exception cref="MailException">The server couldn't be reached, refused the sign-in or refused the message.</exception>
    Task SendAsync(NotificationConfig config, MailMessage message, CancellationToken ct);
}
