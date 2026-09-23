using System.Text.RegularExpressions;

namespace TapQueue.Server.Config;

public sealed class ServerConfig
{
    public ServerSection Server { get; set; } = new();
    public AuthSection Auth { get; set; } = new();
    public AdminSection Admin { get; set; } = new();
    public JobsSection Jobs { get; set; } = new();

    /// <summary>
    /// Queues and printers used to live in server.toml (before v0.2). The TOML loader ignores sections it
    /// doesn't know, so an old config would otherwise start with no printers and no hint why.
    /// </summary>
    public static void RejectRemovedSections(string tomlText)
    {
        var removed = Regex.Matches(tomlText, @"^\s*\[\[?\s*(queues|printers)\s*\]", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        if (removed.Count > 0)
            throw new InvalidDataException(
                $"[[{string.Join("]] and [[", removed)}]] are no longer read from server.toml; they're stored in the database. " +
                "Add them with `tapqueue-admin queues add` and `tapqueue-admin printers add`, then delete those sections.");
    }

    public void Validate()
    {
        if (!System.Net.IPEndPoint.TryParse(Server.Listen, out var listen) || listen.Port == 0)
            throw new InvalidDataException($"server.listen \"{Server.Listen}\" must look like 0.0.0.0:8631.");
        if (Auth.Mode is not ("dev" or "token"))
            throw new InvalidDataException($"auth.mode must be \"dev\" or \"token\", not \"{Auth.Mode}\".");
        if (string.IsNullOrWhiteSpace(Admin.Token) || Admin.Token == "change-me")
            throw new InvalidDataException("Set admin.token to a long random value (e.g. `openssl rand -hex 32`).");
    }
}

public sealed class ServerSection
{
    /// <summary>Address and port to listen on for both IPP and the REST API.</summary>
    public string Listen { get; set; } = "0.0.0.0:8631";

    /// <summary>Where the database and spooled jobs live.</summary>
    public string DataDir { get; set; } = "/var/lib/tapqueue";
}

public sealed class AuthSection
{
    /// <summary>
    /// "dev": clients identify with a username only and unknown users are created automatically.
    /// "token": clients must present the per-user token issued by `tapqueue-admin users add`.
    /// </summary>
    public string Mode { get; set; } = "token";

    /// <summary>Client sessions with no heartbeat for this long are dropped.</summary>
    public int SessionTimeoutMinutes { get; set; } = 10;
}

public sealed class AdminSection
{
    public string Token { get; set; } = "";
}

public sealed class JobsSection
{
    /// <summary>Held jobs that are not released within this many hours are deleted.</summary>
    public int HoldHours { get; set; } = 24;
}
