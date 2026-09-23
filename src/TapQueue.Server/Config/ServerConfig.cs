namespace TapQueue.Server.Config;

public sealed class ServerConfig
{
    public ServerSection Server { get; set; } = new();
    public AuthSection Auth { get; set; } = new();
    public AdminSection Admin { get; set; } = new();
    public JobsSection Jobs { get; set; } = new();
    public List<QueueConfig> Queues { get; set; } = [];
    public List<PrinterConfig> Printers { get; set; } = [];

    public void Validate()
    {
        if (Queues.Count == 0)
            throw new InvalidDataException("Config must define at least one [[queues]] entry.");
        if (Printers.Count == 0)
            throw new InvalidDataException("Config must define at least one [[printers]] entry.");
        if (Auth.Mode is not ("dev" or "token"))
            throw new InvalidDataException($"auth.mode must be \"dev\" or \"token\", not \"{Auth.Mode}\".");
        if (string.IsNullOrWhiteSpace(Admin.Token) || Admin.Token == "change-me")
            throw new InvalidDataException("Set admin.token to a long random value (e.g. `openssl rand -hex 32`).");

        foreach (var q in Queues)
        {
            if (string.IsNullOrWhiteSpace(q.Id) || !q.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new InvalidDataException($"Queue id \"{q.Id}\" must be letters, digits, '-' or '_' (it becomes part of the URL).");
            if (string.IsNullOrWhiteSpace(q.Name))
                throw new InvalidDataException($"Queue \"{q.Id}\" needs a name.");
        }
        foreach (var p in Printers)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
                throw new InvalidDataException("Every [[printers]] entry needs an id.");
            if (!Uri.TryCreate(p.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("ipp" or "ipps" or "http" or "https"))
                throw new InvalidDataException($"Printer \"{p.Id}\" uri must be ipp://, ipps://, http:// or https://.");
        }
        if (Queues.GroupBy(q => q.Id).Any(g => g.Count() > 1) || Printers.GroupBy(p => p.Id).Any(g => g.Count() > 1))
            throw new InvalidDataException("Queue and printer ids must be unique.");
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

public sealed class QueueConfig
{
    public string Id { get; set; } = "";

    /// <summary>The printer name users see in the Windows print dialog.</summary>
    public string Name { get; set; } = "";

    public string Description { get; set; } = "Tap your badge at any TapQueue printer to release your print.";
    public string Location { get; set; } = "";
    public bool Color { get; set; }
    public bool Duplex { get; set; }

    /// <summary>PWG media name, e.g. na_letter_8.5x11in or iso_a4_210x297mm.</summary>
    public string DefaultMedia { get; set; } = "na_letter_8.5x11in";
}

public sealed class PrinterConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";

    /// <summary>The printer's own IPP endpoint, e.g. ipp://192.0.2.10/ipp/print.</summary>
    public string Uri { get; set; } = "";

    /// <summary>Printers ship with self-signed certificates, so ipps:// usually needs this.</summary>
    public bool TlsSkipVerify { get; set; }
}
