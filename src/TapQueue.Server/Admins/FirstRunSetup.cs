using System.Security.Cryptography;
using TapQueue.Server.Config;
using TapQueue.Server.Data;

namespace TapQueue.Server.Admins;

/// <summary>
/// Setting up a server nobody can sign in to yet. While there's no full admin who could sign in to the
/// console, the console offers to create the first one to whoever has the setup code. The code is
/// made when the server starts, written to its log, and shown by <c>tapqueue-admin server setup-code</c>
/// (which needs admin.token, so root on the server): whoever can read those could already do anything.
/// It's kept in memory only, so a restart makes a new one, and it stops working the moment someone
/// can sign in as a full admin. Not in dev mode, where the console is open anyway.
/// </summary>
public sealed class FirstRunSetup(AdminRoster roster, AdminStore admins, ServerConfig config)
{
    // No 0/O, 1/I/L: it's read off a terminal and typed into a browser.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly Lock _lock = new();
    private string? _code;

    /// <summary>Nobody could sign in to the console as a full admin, so the first one has yet to be made.</summary>
    public bool Needed => config.Auth.Mode != "dev" && roster.FullAdmins(admins.Grants()) == 0;

    /// <summary>The setup code, like <c>K7QM-2XWD-9RTB</c>; null when setup isn't needed (any more).</summary>
    public string? Code
    {
        get
        {
            if (!Needed)
                return null;
            lock (_lock)
                return _code ??= string.Join('-', Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString(Alphabet, 4)));
        }
    }

    /// <summary>Whether <paramref name="code"/> is the setup code, however it was typed (case, spaces, dashes).</summary>
    public bool Check(string? code)
    {
        if (Code is not { } expected || string.IsNullOrWhiteSpace(code))
            return false;
        return Tokens.FixedTimeEquals(Normalize(code), Normalize(expected));
    }

    private static string Normalize(string code) => new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
