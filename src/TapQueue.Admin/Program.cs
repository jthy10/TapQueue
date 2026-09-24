using System.Net.Http.Json;
using TapQueue.Admin;
using TapQueue.Shared;
using TapQueue.Shared.Api;

const string Usage = """
    tapqueue-admin — manage a TapQueue server

    Usage:
      tapqueue-admin status                          Server version and a summary of what's set up
      tapqueue-admin users                           List users
      tapqueue-admin users add <username> [--name "Display Name"]
                                                     Create a user and print their client token
      tapqueue-admin users reset-token <username>    Issue a new client token (the old one stops working)
      tapqueue-admin users quota <username>          Their page limits and how much they've used
      tapqueue-admin users quota <username> <pages> day|week|month
                                                     Give a user their own page limit (overrides their groups')
      tapqueue-admin users quota <username> none     Remove it, so their groups' limits apply
      tapqueue-admin groups quota <group-id> <pages> day|week|month
                                                     Limit a group's members (the most generous group counts)
      tapqueue-admin groups quota <group-id> none    Remove a group's limit
      tapqueue-admin quotas                          Everyone with a page limit and how much of it they've used
      tapqueue-admin jobs [--status held|released|expired|canceled]
                                                     List recent jobs
      tapqueue-admin printers [--refresh]            List printers with their health and toner levels
      tapqueue-admin printers add <printer-id> <uri> [--name "Office printer"] [--location ...] [--tls-skip-verify]
                                                     Add a printer, e.g. uri ipp://192.0.2.10/ipp/print
      tapqueue-admin printers edit <printer-id> [--uri ...] [--name ...] [--location ...] [--tls-skip-verify on|off]
      tapqueue-admin printers remove <printer-id>
      tapqueue-admin release <username> <printer-id> [job-id ...]
                                                     Send a user's held jobs (all, or just these) to a printer

      tapqueue-admin queues                          List queues (the printers users see in Windows)
      tapqueue-admin queues add <queue-id> --name "TapQueue Secure Print" [--description ...] [--location ...]
                                [--color] [--duplex] [--media iso_a4_210x297mm]
                                                     Add a queue; clients print to ipp://<server>:8631/ipp/<queue-id>
      tapqueue-admin queues edit <queue-id> [--name ...] [--description ...] [--location ...]
                                [--color on|off] [--duplex on|off] [--media ...]
      tapqueue-admin queues remove <queue-id>

      tapqueue-admin badges [username]               List badges
      tapqueue-admin badges add <username> <card>    Link a card number to a user
      tapqueue-admin badges add <username> --last-tap
                                                     Link the card most recently tapped at any station
      tapqueue-admin badges unknown                  Cards tapped recently that nobody owns
      tapqueue-admin badges remove <badge-id>        Unlink a badge

      tapqueue-admin stations                        List release stations
      tapqueue-admin stations add <station-id> <printer-id>
                                                     Create a station and print its token
      tapqueue-admin stations move <station-id> <printer-id>
                                                     Make a station release to a different printer
      tapqueue-admin stations reset-token <station-id>
                                                     Issue a new station token (the old one stops working)
      tapqueue-admin stations remove <station-id>    Delete a station

      tapqueue-admin clients                         Signed-in clients and the version each runs
      tapqueue-admin clients publish <TapQueue_client_X.Y.Z_win-x64.zip | TapQueue_client_X.Y.Z_linux-x64.tar.gz
                                      | TapQueueClient.exe | tapqueue-client> [--platform win-x64|linux-x64] [--version-name ...]
                                                     Push a client build out; every PC of that platform
                                                     installs it when it next checks in (about a minute)
      tapqueue-admin clients builds                  Published client builds, newest (the one PCs run) first
      tapqueue-admin clients sign-out <session>      Sign a client out (session number from `clients`); jobs
                                                     from that PC stop going to that user

      tapqueue-admin workstations                    PCs running the TapQueue service, their client and who's signed in
      tapqueue-admin workstations update <computer>  Install the published client now, even if it failed before
      tapqueue-admin workstations forget <computer>  Drop a PC that's gone from the list

      tapqueue-admin crashes [<computer>]            Crash reports sent by clients, newest first
      tapqueue-admin crashes show <id>               One report with its full error
      tapqueue-admin crashes clear [<computer>]      Delete crash reports (all, or one PC's)

      tapqueue-admin directory                       Active Directory sync: settings, scope, next and recent syncs
      tapqueue-admin directory sync [--dry-run] [--force]
                                                     Sync now (--dry-run: only show what would change;
                                                     --force: go ahead even past the disable limit)
      tapqueue-admin directory scope add <dn>        Sync the users under an OU, in a group (with nested
                                                     groups) or one user. Set up the connection in the console
      tapqueue-admin directory scope remove <id>     Stop syncing a scope item (id from `directory`)
      tapqueue-admin directory sign-in <pc|domain>   How the tray signs in: as the PC's user (default), or
                                                     with a domain account entered in the tray

      tapqueue-admin server                          Settings (and whether each is set here or in server.toml)
      tapqueue-admin server set hold-hours|session-timeout <number|default>
                                                     Change a setting now, or go back to server.toml's
      tapqueue-admin server set quota-overrun allow|deny|default
                                                     allow (default): a job prints in full if they're under their
                                                     page limit when it starts; deny: only jobs that fit
      tapqueue-admin server log [--follow]           The server's recent log lines, and new ones with --follow
      tapqueue-admin server restart                  Restart tapqueue-server (only when systemd runs it)

    Connection (flag > environment > ~/.config/tapqueue/admin.toml > /etc/tapqueue/server.toml):
      --server <url>    TAPQUEUE_SERVER       default http://localhost:8631
      --token <token>   TAPQUEUE_ADMIN_TOKEN  admin.token from server.toml
    """;

var positional = new List<string>();
var options = new Dictionary<string, string?>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "-h" or "--help")
    {
        Console.WriteLine(Usage);
        return 0;
    }
    if (args[i] is "--version")
    {
        Console.WriteLine($"tapqueue-admin {TapQueueVersion.Current}");
        return 0;
    }
    if (args[i].StartsWith("--"))
    {
        var takesValue = args[i] is "--server" or "--token" or "--name" or "--status" or "--uri" or "--location" or "--description" or "--media" or "--version-name" or "--platform";
        var switchWithValue = args[i] is "--tls-skip-verify" or "--color" or "--duplex" && i + 1 < args.Length && ParseSwitch(args[i + 1]) is not null;
        options[args[i]] = (takesValue || switchWithValue) && i + 1 < args.Length ? args[++i] : null;
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count == 0)
{
    Console.WriteLine(Usage);
    return 2;
}

var fileConfig = LoadFileConfig() ?? AdminConfig.FromServerConfig() ?? new AdminConfig();
var serverUrl = options.GetValueOrDefault("--server") ?? Environment.GetEnvironmentVariable("TAPQUEUE_SERVER") ?? fileConfig.ServerUrl;
var token = options.GetValueOrDefault("--token") ?? Environment.GetEnvironmentVariable("TAPQUEUE_ADMIN_TOKEN") ?? fileConfig.Token;
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("No admin token. Pass --token, set TAPQUEUE_ADMIN_TOKEN, put it in ~/.config/tapqueue/admin.toml, " +
        "or run with sudo on the server so /etc/tapqueue/server.toml can be read.");
    return 2;
}

using var http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/api/v1/admin/"), Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);

try
{
    return (positional[0], positional.Count > 1 ? positional[1] : null) switch
    {
        ("status", null) => await Status(),
        ("users", null) => await ListUsers(),
        ("users", "add") when positional.Count == 3 => await AddUser(positional[2], options.GetValueOrDefault("--name")),
        ("users", "reset-token") when positional.Count == 3 => await ResetToken(positional[2]),
        ("users", "quota") when positional.Count == 3 => await ShowUserQuota(positional[2]),
        ("users", "quota") when positional.Count is 4 or 5 => await SetQuota("users", positional[2], positional.Skip(3).ToList()),
        ("groups", "quota") when positional.Count is 4 or 5 => await SetQuota("groups", positional[2], positional.Skip(3).ToList()),
        ("quotas", null) => await ListQuotas(),
        ("jobs", null) => await ListJobs(options.GetValueOrDefault("--status")),
        ("printers", null) => await ListPrinters(options.ContainsKey("--refresh")),
        ("printers", "add") when positional.Count == 4 => await AddPrinter(positional[2], positional[3]),
        ("printers", "edit") when positional.Count == 3 => await EditPrinter(positional[2]),
        ("printers", "remove") when positional.Count == 3 => await Delete($"printers/{Uri.EscapeDataString(positional[2])}", $"Removed printer {positional[2]}."),
        ("queues", null) => await ListQueues(),
        ("queues", "add") when positional.Count == 3 => await AddQueue(positional[2]),
        ("queues", "edit") when positional.Count == 3 => await EditQueue(positional[2]),
        ("queues", "remove") when positional.Count == 3 => await Delete($"queues/{Uri.EscapeDataString(positional[2])}", $"Removed queue {positional[2]}."),
        ("release", not null) when positional.Count >= 3 => await Release(positional[1], positional[2], positional.Skip(3).ToList()),
        ("badges", null) => await ListBadges(null),
        ("badges", "add") when positional.Count == 4 => await AddBadge(positional[2], positional[3]),
        ("badges", "add") when positional.Count == 3 && options.ContainsKey("--last-tap") => await AddBadgeFromLastTap(positional[2]),
        ("badges", "unknown") when positional.Count == 2 => await ListUnknownTaps(),
        ("badges", "remove") when positional.Count == 3 => await RemoveBadge(long.Parse(positional[2])),
        ("badges", not null) when positional.Count == 2 => await ListBadges(positional[1]),
        ("stations", null) => await ListStations(),
        ("stations", "add") when positional.Count == 4 => await AddStation(positional[2], positional[3]),
        ("stations", "move") when positional.Count == 4 => await MoveStation(positional[2], positional[3]),
        ("stations", "reset-token") when positional.Count == 3 => await ResetStationToken(positional[2]),
        ("stations", "remove") when positional.Count == 3 => await RemoveStation(positional[2]),
        ("clients", null) => await ListClients(),
        ("clients", "publish") when positional.Count == 3 => await PublishClient(positional[2], options.GetValueOrDefault("--version-name"), options.GetValueOrDefault("--platform")),
        ("clients", "builds") when positional.Count == 2 => await ListClientBuilds(),
        ("clients", "sign-out") when positional.Count == 3 => await Delete($"clients/{long.Parse(positional[2])}", $"Signed out session {positional[2]}."),
        ("workstations", null) => await ListWorkstations(),
        ("workstations", "update") when positional.Count == 3 => await UpdateWorkstation(positional[2]),
        ("workstations", "forget") when positional.Count == 3 => await Delete($"workstations/{Uri.EscapeDataString(positional[2])}", $"Forgot {positional[2]}."),
        ("crashes", "show") when positional.Count == 3 && long.TryParse(positional[2], out var crashId) => await ShowCrash(crashId),
        ("crashes", "clear") when positional.Count <= 3 => await ClearCrashes(positional.ElementAtOrDefault(2)),
        ("crashes", _) when positional.Count <= 2 => await ListCrashes(positional.ElementAtOrDefault(1)),
        ("directory", null) => await ShowDirectory(),
        ("directory", "sync") when positional.Count == 2 => await SyncDirectory(options.ContainsKey("--dry-run"), options.ContainsKey("--force")),
        ("directory", "scope") when positional.Count == 4 && positional[2] == "add" => await AddDirectoryScope(positional[3]),
        ("directory", "sign-in") when positional.Count == 3 => await SetClientSignIn(positional[2]),
        ("directory", "scope") when positional.Count == 4 && positional[2] == "remove" =>
            await Delete($"directory/scope/{long.Parse(positional[3])}", $"Removed scope item {positional[3]}; the next sync disables users only it brought in."),
        ("server", null) => await ShowServer(),
        ("server", "set") when positional.Count == 4 => await SetServerSetting(positional[2], positional[3]),
        ("server", "log") when positional.Count == 2 => await ServerLog(options.ContainsKey("--follow")),
        ("server", "restart") when positional.Count == 2 => await RestartServer(),
        _ => BadUsage(),
    };
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Could not reach {serverUrl}: {ex.Message}");
    return 1;
}
catch (FormatException)
{
    Console.Error.WriteLine("Job, badge and session ids, and setting values, must be numbers.");
    return 2;
}

async Task<int> Status()
{
    using var root = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
    var server = (await root.GetStringAsync("")).Trim();
    var queues = await Get<List<QueueAdminDto>>("queues");
    var printers = await Get<List<PrinterAdminDto>>("printers");
    var stations = await Get<List<StationDto>>("stations");
    var users = await Get<List<UserDto>>("users");
    if (queues is null || printers is null || stations is null || users is null) return 1;
    Console.WriteLine($"{server} at {serverUrl}");
    Console.WriteLine($"tapqueue-admin {TapQueueVersion.Current}");
    Console.WriteLine($"{Plural(queues.Count, "queue")}, {Plural(printers.Count, "printer")} ({printers.Count(p => p.Printer.Online)} online), " +
        $"{Plural(stations.Count, "station")}, {Plural(users.Count, "user")}");
    return 0;
}

static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

async Task<int> ListUsers()
{
    var users = await Get<List<UserDto>>("users");
    if (users is null) return 1;
    Table(["ID", "USERNAME", "NAME"], users.Select(u => new[] { u.Id.ToString(), u.Username, u.DisplayName }));
    return 0;
}

async Task<int> AddUser(string username, string? displayName)
{
    var result = await Send<UserTokenResponse>(HttpMethod.Post, "users", new CreateUserRequest(username, displayName));
    if (result is null) return 1;
    Console.WriteLine($"Created user {result.User.Username} (id {result.User.Id}).");
    Console.WriteLine($"Client token: {result.Token}");
    Console.WriteLine("Put it in the user's client.toml as `token = \"...\"`. It won't be shown again.");
    return 0;
}

async Task<int> ResetToken(string username)
{
    var result = await Send<UserTokenResponse>(HttpMethod.Post, $"users/{Uri.EscapeDataString(username)}/token", null);
    if (result is null) return 1;
    Console.WriteLine($"New client token for {result.User.Username}: {result.Token}");
    return 0;
}

async Task<int> ListJobs(string? status)
{
    var jobs = await Get<List<JobDto>>(status is null ? "jobs" : $"jobs?status={Uri.EscapeDataString(status)}");
    if (jobs is null) return 1;
    Table(["ID", "OWNER", "STATUS", "NAME", "PAGES", "SIZE", "SUBMITTED", "PRINTER"], jobs.Select(j => new[]
    {
        j.Id.ToString(), j.Owner ?? $"unowned ({j.ClaimedUser ?? "?"})", j.Status, Truncate(j.Name, 40),
        (j.Pages?.ToString() ?? "?") + (j.Copies > 1 ? $" x{j.Copies}" : ""), FormatSize(j.SizeBytes),
        j.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), j.ReleasedPrinterId ?? "",
    }));
    return 0;
}

async Task<int> SetClientSignIn(string mode)
{
    if (!ClientSignIn.All.Contains(mode))
    {
        Console.Error.WriteLine($"Expected one of: {string.Join(", ", ClientSignIn.All)}.");
        return 2;
    }
    if (await Send<DirectoryStatusDto>(HttpMethod.Patch, "directory/config", new UpdateDirectoryConfigRequest(ClientSignIn: mode)) is null)
        return 1;
    Console.WriteLine(mode == ClientSignIn.Domain
        ? "The tray now asks for a domain account: people choose Sign in as… and enter their AD name and password."
        : "The tray now signs in as the PC's user.");
    return 0;
}

async Task<int> ShowDirectory()
{
    var d = await Get<DirectoryStatusDto>("directory");
    if (d is null) return 1;
    var c = d.Config;
    Console.WriteLine(c.Host.Length == 0
        ? "Not set up: set the connection on the console's Active Directory page."
        : $"Domain controller {c.Host}:{c.Port} (LDAPS), bind as {c.BindDn}{(c.HasPassword ? "" : " (no password saved)")}" +
          $"{(c.CaCertificate.Length > 0 ? ", trusting its own CA" : "")}.");
    Console.WriteLine(c.BadgeAttribute.Length > 0 ? $"Cards come from {c.BadgeAttribute}." : "Cards are managed in TapQueue.");
    Console.WriteLine(c.Enabled
        ? $"Daily sync at {c.SyncTime}{(d.NextRunAt is { } next ? $", next {next.ToLocalTime():yyyy-MM-dd HH:mm}" : "")}; stops before disabling more than {c.MaxDisablePercent}%."
        : "Daily sync is off.");
    Console.WriteLine(c.ClientSignIn == ClientSignIn.Domain
        ? "The tray signs in with a domain account (Sign in as…), remembered until they sign out."
        : "The tray signs in as the PC's user.");
    Console.WriteLine();
    if (d.Scope.Count == 0)
        Console.WriteLine("Nothing in the scope yet: tapqueue-admin directory scope add <dn>");
    else
        Table(["ID", "KIND", "NAME", "DN"], d.Scope.Select(s => new[] { s.Id.ToString(), s.Kind, s.Name, s.Dn }));
    if (d.Runs.Count > 0)
    {
        Console.WriteLine();
        Table(["WHEN", "BY", "OUTCOME", "RESULT"], d.Runs.Take(5).Select(r => new[]
        {
            Ago(r.StartedAt), r.Trigger, r.Outcome + (r.DryRun ? " (preview)" : ""), Truncate(r.Error ?? r.Summary, 90),
        }));
    }
    return 0;
}

async Task<int> SyncDirectory(bool dryRun, bool force)
{
    var r = await Send<DirectorySyncResultDto>(HttpMethod.Post, "directory/sync", new DirectorySyncRequest(dryRun, force));
    if (r is null) return 1;
    if (r.Outcome == DirectoryRunOutcome.Failed)
    {
        Console.Error.WriteLine($"Sync failed, nothing changed: {r.Error}");
        return 1;
    }
    Console.WriteLine($"{Plural(r.Users, "user")} and {Plural(r.Groups, "group")} in the scope. {(dryRun ? "Would do: " : "")}{r.Summary}");
    foreach (var change in r.Changes)
        Console.WriteLine($"  {change.Description}");
    foreach (var warning in r.Warnings)
        Console.WriteLine($"  warning: {warning}");
    if (r.Outcome == DirectoryRunOutcome.Stopped)
    {
        Console.Error.WriteLine(r.Error);
        return 1;
    }
    return 0;
}

async Task<int> AddDirectoryScope(string dn)
{
    var item = await Send<DirectoryScopeDto>(HttpMethod.Post, "directory/scope", new AddDirectoryScopeRequest(Dn: dn));
    if (item is null) return 1;
    Console.WriteLine($"Added {item.Kind} {item.Name} to the scope (id {item.Id}). Preview with: tapqueue-admin directory sync --dry-run");
    return 0;
}

async Task<int> ShowUserQuota(string username)
{
    var quota = await Get<UserQuotaDto>($"users/{Uri.EscapeDataString(username)}/quota");
    if (quota is null) return 1;
    if (quota.Applies.Count == 0)
    {
        Console.WriteLine($"{quota.Username} has no page limit.");
        return 0;
    }
    PrintQuotaUsage([quota]);
    if (quota.Applies.Count > 1)
        Console.WriteLine("A job prints if any of these limits allows it.");
    return 0;
}

async Task<int> ListQuotas()
{
    var quotas = await Get<List<UserQuotaDto>>("quotas");
    if (quotas is null) return 1;
    PrintQuotaUsage(quotas);
    return 0;
}

static void PrintQuotaUsage(List<UserQuotaDto> quotas) =>
    Table(["USER", "LIMIT", "FROM", "USED", "LEFT", "STARTS OVER"], quotas.SelectMany(q => q.Applies.Select(a => new[]
    {
        q.Username, $"{a.Pages} per {a.Period}", a.Source == "user" ? "user" : a.Source["group:".Length..],
        a.Used.ToString(), a.Remaining.ToString(), a.ResetsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
    })));

async Task<int> SetQuota(string kind, string id, List<string> args)
{
    var path = $"{kind}/{Uri.EscapeDataString(id)}/quota";
    var who = kind == "users" ? id : $"group {id}";
    if (args is ["none"])
        return await Delete(path, $"Removed the page limit on {who}.");
    if (args.Count != 2 || !int.TryParse(args[0], out var pages) || !QuotaPeriod.All.Contains(args[1]))
    {
        Console.Error.WriteLine($"Give a number of pages and day, week or month, e.g. `tapqueue-admin {kind} quota {id} 100 month`, or none.");
        return 2;
    }
    var body = new QuotaDto(pages, args[1]);
    if (kind == "users")
    {
        if (await Send<UserQuotaDto>(HttpMethod.Put, path, body) is null) return 1;
        return await ShowUserQuota(id);
    }
    if (await Send<GroupDto>(HttpMethod.Put, path, body) is null) return 1;
    Console.WriteLine($"Members of {who} may print {Plural(pages, "page")} a {args[1]} (a user's own limit overrides it).");
    return 0;
}

async Task<int> ListPrinters(bool refresh)
{
    var printers = await Get<List<PrinterAdminDto>>(refresh ? "printers?refresh=true" : "printers");
    if (printers is null) return 1;
    if (printers.Count == 0)
    {
        Console.WriteLine("No printers yet. Add one with: tapqueue-admin printers add <printer-id> ipp://<printer-ip>/ipp/print");
        return 0;
    }
    Table(["ID", "NAME", "URI", "HEALTH", "SUPPLIES", "STATUS"], printers.Select(p => new[]
    {
        p.Printer.Id, p.Printer.Name, p.Uri, Health(p), Supplies(p.Health?.Supplies ?? []),
        p.Health is { Problems.Count: > 0 } h ? string.Join(", ", h.Problems) : p.Printer.StateMessage ?? "",
    }));
    return 0;
}

// "error (held)" means releases to it are held back until it's fixed.
static string Health(PrinterAdminDto p) => p.Health is null ? "checking"
    : p.Health.Level == PrinterHealthLevel.Offline ? "offline"
    : p.Health.Level + (p.Health.CanPrint ? "" : " (held)");

// "black 60%, cyan 6% low"
static string Supplies(IReadOnlyList<PrinterSupplyDto> supplies) =>
    string.Join(", ", supplies.Select(s => $"{s.Name.Replace(" cartridge", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()} " +
        (s.Level is { } level ? $"{level}%" : "?") + (s.Low ? " low" : "")));

async Task<int> AddPrinter(string id, string uri)
{
    var printer = await Send<PrinterAdminDto>(HttpMethod.Post, "printers", new CreatePrinterRequest(
        id, uri, options.GetValueOrDefault("--name"), options.GetValueOrDefault("--location"), Switch("--tls-skip-verify")));
    if (printer is null) return 1;
    PrintPrinter("Added", printer);
    return 0;
}

async Task<int> EditPrinter(string id)
{
    var printer = await Send<PrinterAdminDto>(HttpMethod.Patch, $"printers/{Uri.EscapeDataString(id)}", new UpdatePrinterRequest(
        options.GetValueOrDefault("--uri"), options.GetValueOrDefault("--name"), options.GetValueOrDefault("--location"), Switch("--tls-skip-verify")));
    if (printer is null) return 1;
    PrintPrinter("Updated", printer);
    return 0;
}

static void PrintPrinter(string verb, PrinterAdminDto p) =>
    Console.WriteLine($"{verb} printer {p.Printer.Id} ({p.Printer.Name}) at {p.Uri}: " +
        (p.Printer.Online ? $"online, {p.Printer.MakeAndModel}" : $"not reachable yet ({p.Printer.StateMessage})"));

async Task<int> ListQueues()
{
    var queues = await Get<List<QueueAdminDto>>("queues");
    if (queues is null) return 1;
    if (queues.Count == 0)
    {
        Console.WriteLine("No queues yet. Add one with: tapqueue-admin queues add secure --name \"TapQueue Secure Print\"");
        return 0;
    }
    Table(["ID", "NAME", "PATH", "COLOR", "DUPLEX", "MEDIA"], queues.Select(q => new[]
    {
        q.Id, q.Name, q.IppPath, q.Color ? "yes" : "no", q.Duplex ? "yes" : "no", q.DefaultMedia,
    }));
    return 0;
}

async Task<int> AddQueue(string id)
{
    if (options.GetValueOrDefault("--name") is not { } name)
    {
        Console.Error.WriteLine("--name is required: it's the printer name users see in Windows.");
        return 2;
    }
    var queue = await Send<QueueAdminDto>(HttpMethod.Post, "queues", new CreateQueueRequest(id, name,
        options.GetValueOrDefault("--description"), options.GetValueOrDefault("--location"), Switch("--color"), Switch("--duplex"),
        options.GetValueOrDefault("--media")));
    if (queue is null) return 1;
    Console.WriteLine($"Added queue \"{queue.Name}\". Clients print to ipp://<server>:<port>{queue.IppPath}");
    return 0;
}

async Task<int> EditQueue(string id)
{
    var queue = await Send<QueueAdminDto>(HttpMethod.Patch, $"queues/{Uri.EscapeDataString(id)}", new UpdateQueueRequest(
        options.GetValueOrDefault("--name"), options.GetValueOrDefault("--description"), options.GetValueOrDefault("--location"),
        Switch("--color"), Switch("--duplex"), options.GetValueOrDefault("--media")));
    if (queue is null) return 1;
    Console.WriteLine($"Updated queue {queue.Id} (\"{queue.Name}\"). Windows picks up changes the next time it asks the queue for its settings.");
    return 0;
}

/// <summary>"--duplex" alone means on; "--duplex off" turns it off; not given means unchanged.</summary>
bool? Switch(string name) =>
    !options.TryGetValue(name, out var value) ? null : value is null || ParseSwitch(value) == true;

static bool? ParseSwitch(string value) => value.ToLowerInvariant() switch
{
    "on" or "yes" or "true" => true,
    "off" or "no" or "false" => false,
    _ => null,
};

async Task<int> Release(string username, string printerId, List<string> jobIds)
{
    var ids = jobIds.Count == 0 ? null : jobIds.Select(long.Parse).ToList();
    var result = await Send<ReleaseResponse>(HttpMethod.Post, "release", new AdminReleaseRequest(username, printerId, ids));
    if (result is null) return 1;
    if (result.Results.Count == 0)
    {
        Console.WriteLine($"{username} has no held jobs.");
        return 0;
    }
    foreach (var r in result.Results)
        Console.WriteLine(r.Success ? $"released  #{r.JobId} {r.JobName}" : $"FAILED    #{r.JobId} {r.JobName}: {r.Error}");
    return result.Results.All(r => r.Success) ? 0 : 1;
}

async Task<int> ListBadges(string? username)
{
    var badges = await Get<List<BadgeDto>>(username is null ? "badges" : $"badges?username={Uri.EscapeDataString(username)}");
    if (badges is null) return 1;
    Table(["ID", "USER", "CARD", "ADDED", "LAST USED"], badges.Select(b => new[]
    {
        b.Id.ToString(), b.Username, b.CardHint, b.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        b.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never",
    }));
    return 0;
}

async Task<int> AddBadge(string username, string card)
{
    var badge = await Send<BadgeDto>(HttpMethod.Post, "badges", new CreateBadgeRequest(username, card));
    if (badge is null) return 1;
    Console.WriteLine($"Linked card {badge.CardHint} to {badge.Username} (badge {badge.Id}).");
    return 0;
}

async Task<int> AddBadgeFromLastTap(string username)
{
    var taps = await Get<List<UnknownTapDto>>("badges/unknown");
    if (taps is null) return 1;
    if (taps.Count == 0)
    {
        Console.Error.WriteLine("No unknown cards have been tapped in the last hour. Tap the card at a station, then try again.");
        return 1;
    }
    var tap = taps[0];
    Console.WriteLine($"Using card {tap.Card}, tapped at {tap.StationId} {Ago(tap.At)}.");
    return await AddBadge(username, tap.Card);
}

async Task<int> ListUnknownTaps()
{
    var taps = await Get<List<UnknownTapDto>>("badges/unknown");
    if (taps is null) return 1;
    Table(["CARD", "STATION", "WHEN"], taps.Select(t => new[] { t.Card, t.StationId, Ago(t.At) }));
    return 0;
}

async Task<int> RemoveBadge(long id) => await Delete($"badges/{id}", $"Removed badge {id}.");

async Task<int> ListStations()
{
    var stations = await Get<List<StationDto>>("stations");
    if (stations is null) return 1;
    Table(["ID", "PRINTER", "LAST SEEN", "ADDRESS"], stations.Select(s => new[]
    {
        s.Id, s.PrinterId, s.LastSeenAt is { } seen ? Ago(seen) : "never", s.LastIp ?? "",
    }));
    return 0;
}

async Task<int> AddStation(string id, string printerId)
{
    var result = await Send<StationTokenResponse>(HttpMethod.Post, "stations", new CreateStationRequest(id, printerId));
    if (result is null) return 1;
    Console.WriteLine($"Created station {result.Station.Id}, releasing to printer {result.Station.PrinterId}.");
    Console.WriteLine($"Station token: {result.Token}");
    Console.WriteLine("Put it in the station's /etc/tapqueue/station.toml as `token = \"...\"`. It won't be shown again.");
    return 0;
}

async Task<int> MoveStation(string id, string printerId)
{
    var station = await Send<StationDto>(HttpMethod.Patch, $"stations/{Uri.EscapeDataString(id)}", new UpdateStationRequest(printerId));
    if (station is null) return 1;
    Console.WriteLine($"Station {station.Id} now releases to printer {station.PrinterId}.");
    return 0;
}

async Task<int> ResetStationToken(string id)
{
    var result = await Send<StationTokenResponse>(HttpMethod.Post, $"stations/{Uri.EscapeDataString(id)}/token", null);
    if (result is null) return 1;
    Console.WriteLine($"New token for station {result.Station.Id}: {result.Token}");
    return 0;
}

async Task<int> RemoveStation(string id) => await Delete($"stations/{Uri.EscapeDataString(id)}", $"Removed station {id}.");

async Task<int> ListClients()
{
    var clients = await Get<List<ClientSessionDto>>("clients");
    var builds = await Get<List<ClientBuildDto>>("client-builds");
    if (clients is null || builds is null) return 1;
    var latest = builds.GroupBy(b => b.Platform).Select(g => g.First()).ToList(); // newest first
    Console.WriteLine(latest.Count == 0
        ? "No client build published; clients keep whatever they run."
        : string.Join("\n", latest.Select(b => $"Published {ClientPlatform.DisplayName(b.Platform)} client: {b.Version} ({Ago(b.PublishedAt)})")));
    Table(["SESSION", "USER", "COMPUTER", "PC USER", "VERSION", "ADDRESS", "LAST SEEN"], clients.Select(c => new[]
    {
        c.Id.ToString(), c.Username, c.Hostname ?? "", c.WindowsUser ?? "",
        (c.ClientVersion ?? "unknown") + (latest.Count > 0 && latest.All(b => b.Version != c.ClientVersion) ? " (updating)" : ""),
        c.RemoteIp, Ago(c.LastSeenAt),
    }));
    return 0;
}

async Task<int> ListWorkstations()
{
    var workstations = await Get<List<WorkstationDto>>("workstations");
    if (workstations is null) return 1;
    Table(["COMPUTER", "OS", "STATUS", "CLIENT", "SIGNED IN", "ADDRESS", "LAST SEEN"], workstations.Select(w => new[]
    {
        w.Hostname, ClientPlatform.DisplayName(w.Platform),
        !w.Online ? "offline" : w.UpdateError is not null ? "update failed" : "online",
        (w.Version ?? "unknown") + (w.UpToDate ? "" : " (behind)"),
        string.Join(", ", w.Sessions.Select(s => s.Username)),
        w.LastIp, Ago(w.LastSeenAt),
    }));
    foreach (var w in workstations.Where(w => w.UpdateError is not null))
        Console.WriteLine($"{w.Hostname}: last update failed: {w.UpdateError}");
    return 0;
}

async Task<int> ListCrashes(string? computer)
{
    var crashes = await Get<List<CrashReportDto>>("crashes" + (computer is null ? "" : $"?computer={Uri.EscapeDataString(computer)}"));
    if (crashes is null) return 1;
    if (crashes.Count == 0)
    {
        Console.WriteLine(computer is null ? "No crash reports." : $"No crash reports from {computer}.");
        return 0;
    }
    Table(["ID", "WHEN", "COMPUTER", "OS", "PROGRAM", "VERSION", "ERROR"], crashes.Select(c => new[]
    {
        c.Id.ToString(), Ago(c.OccurredAt), c.Computer, ClientPlatform.DisplayName(c.Platform), c.Program, c.Version ?? "?",
        c.Message.Length > 80 ? c.Message[..79] + "…" : c.Message,
    }));
    Console.WriteLine("Full error: tapqueue-admin crashes show <id>");
    return 0;
}

async Task<int> ShowCrash(long id)
{
    var crash = (await Get<List<CrashReportDto>>($"crashes?limit={500}"))?.FirstOrDefault(c => c.Id == id);
    if (crash is null)
    {
        Console.Error.WriteLine($"No crash report {id}.");
        return 1;
    }
    Console.WriteLine($"Crash report {crash.Id}: the {crash.Program} on {crash.Computer} ({ClientPlatform.DisplayName(crash.Platform)}, {crash.Ip})");
    Console.WriteLine($"Client {crash.Version ?? "?"}, crashed {crash.OccurredAt.ToLocalTime():g}, reported {crash.ReceivedAt.ToLocalTime():g}");
    Console.WriteLine();
    Console.WriteLine(crash.Details ?? crash.Message);
    return 0;
}

async Task<int> ClearCrashes(string? computer)
{
    using var response = await http.DeleteAsync("crashes" + (computer is null ? "" : $"?computer={Uri.EscapeDataString(computer)}"));
    if (!response.IsSuccessStatusCode)
    {
        await PrintError(response);
        return 1;
    }
    Console.WriteLine(computer is null ? "Cleared all crash reports." : $"Cleared crash reports from {computer}.");
    return 0;
}

async Task<int> UpdateWorkstation(string computer)
{
    using var response = await http.PostAsync($"workstations/{Uri.EscapeDataString(computer)}/update", null);
    if (!response.IsSuccessStatusCode)
    {
        await PrintError(response);
        return 1;
    }
    Console.WriteLine($"{computer} will install the published client when it next checks in, within about a minute.");
    return 0;
}

async Task<int> ShowServer()
{
    var server = await Get<ServerInfoDto>("server");
    if (server is null) return 1;
    string Source(string key) => server.ChangedSettings?.Contains(key) == true ? "set here" : "server.toml";
    Console.WriteLine($"tapqueue-server {server.Version}, up since {server.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}, schema {server.SchemaVersion}");
    Console.WriteLine($"hold-hours       {server.HoldHours,-6} ({Source("holdHours")})");
    Console.WriteLine($"session-timeout  {server.SessionTimeoutMinutes,-6} minutes ({Source("sessionTimeoutMinutes")})");
    Console.WriteLine($"quota-overrun    {server.QuotaOverrun,-6} ({(server.ChangedSettings?.Contains("quotaOverrun") == true ? "set here" : "default")})");
    Console.WriteLine($"auth.mode        {server.AuthMode,-6} (server.toml; restart to change)");
    return 0;
}

async Task<int> SetServerSetting(string name, string value)
{
    var key = name switch
    {
        "hold-hours" => "holdHours",
        "session-timeout" => "sessionTimeoutMinutes",
        "quota-overrun" => "quotaOverrun",
        _ => null,
    };
    if (key is null)
    {
        Console.Error.WriteLine("Settings are hold-hours, session-timeout and quota-overrun.");
        return 2;
    }
    if (key == "quotaOverrun")
    {
        var overrun = value == "default" ? new UpdateServerSettingsRequest(Reset: [key]) : new UpdateServerSettingsRequest(QuotaOverrun: value);
        if (await Send<ServerInfoDto>(HttpMethod.Patch, "server/settings", overrun) is null) return 1;
        return await ShowServer();
    }
    int? number = value == "default" ? null : int.Parse(value);
    var request = number is null ? new UpdateServerSettingsRequest(Reset: [key])
        : key == "holdHours" ? new UpdateServerSettingsRequest(HoldHours: number)
        : new UpdateServerSettingsRequest(SessionTimeoutMinutes: number);
    if (await Send<ServerInfoDto>(HttpMethod.Patch, "server/settings", request) is null) return 1;
    return await ShowServer();
}

async Task<int> ServerLog(bool follow)
{
    var lines = await Get<List<LogLineDto>>("server/log?limit=200");
    if (lines is null) return 1;
    foreach (var line in lines)
        PrintLogLine(line);
    if (!follow) return 0;

    // Poll rather than hold a stream open, so a restarting server is simply picked up again.
    var last = lines.LastOrDefault()?.Id ?? 0;
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        List<LogLineDto>? more;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            more = await http.GetFromJsonAsync<List<LogLineDto>>($"server/log?after={last}", TapQueueJson.Options, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            continue; // restarting, or the network blinked
        }
        foreach (var line in more ?? [])
            PrintLogLine(line);
        if (more is { Count: > 0 }) last = more[^1].Id;
    }
}

static void PrintLogLine(LogLineDto line) =>
    Console.WriteLine($"{line.At.ToLocalTime():HH:mm:ss} {line.Level,-7} {line.Category}: {line.Message}");

async Task<int> RestartServer()
{
    using var response = await http.PostAsync("server/restart", null);
    if (!response.IsSuccessStatusCode)
    {
        await PrintError(response);
        return 1;
    }
    Console.WriteLine("Restarting; the server is back in a few seconds.");
    return 0;
}

async Task<int> ListClientBuilds()
{
    var builds = await Get<List<ClientBuildDto>>("client-builds");
    if (builds is null) return 1;
    Table(["PLATFORM", "VERSION", "PUBLISHED", "SIZE", "SHA-256"], builds.Select(b => new[]
    {
        b.Platform, b.Version, Ago(b.PublishedAt), FormatSize(b.SizeBytes), b.Sha256[..16] + "…",
    }));
    return 0;
}

// Takes a release package from scripts/package.sh (the Windows zip or the Linux tarball: the client
// program plus version.txt) or a bare program. The platform comes from the program itself.
async Task<int> PublishClient(string path, string? version, string? platform)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"{path} not found.");
        return 2;
    }
    if (platform is not null && ClientPlatform.Parse(platform) is null)
    {
        Console.Error.WriteLine($"Unknown platform \"{platform}\". Use one of: {string.Join(", ", ClientPlatform.All)}.");
        return 2;
    }

    // The program is copied out of the package so its first bytes can be checked before uploading.
    var program = Path.GetTempFileName();
    try
    {
        string? packagedVersion;
        try
        {
            packagedVersion = await ClientPackage.ExtractAsync(path, program);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        version ??= packagedVersion;
        if (string.IsNullOrEmpty(version))
        {
            Console.Error.WriteLine("Can't tell which version this is. Pass --version-name, e.g. --version-name 0.2.0+1a2b3c4.");
            return 2;
        }
        var detected = ClientPackage.PlatformOf(program);
        platform = ClientPlatform.Parse(platform ?? detected);
        if (platform is null)
        {
            Console.Error.WriteLine($"{path} isn't a Windows or Linux program. Pass --platform if it really is a TapQueue client.");
            return 2;
        }
        if (detected is not null && detected != platform)
        {
            Console.Error.WriteLine($"{path} is a {ClientPlatform.DisplayName(detected)} program, not {ClientPlatform.DisplayName(platform)}.");
            return 2;
        }

        await using var upload = File.OpenRead(program);
        using var content = new StreamContent(upload);
        content.Headers.ContentType = new("application/octet-stream");
        using var response = await http.PostAsync(
            $"client-builds?version={Uri.EscapeDataString(version)}&platform={platform}", content);
        if (!response.IsSuccessStatusCode)
        {
            await PrintError(response);
            return 1;
        }
        var build = (await response.Content.ReadFromJsonAsync<ClientBuildDto>(TapQueueJson.Options))!;
        Console.WriteLine($"Published {ClientPlatform.DisplayName(build.Platform)} client {build.Version} ({FormatSize(build.SizeBytes)}).");
        Console.WriteLine("PCs install it when they next check in, within about a minute. Watch with: tapqueue-admin workstations");
        return 0;
    }
    finally
    {
        File.Delete(program);
    }
}

async Task<int> Delete(string path, string doneMessage)
{
    using var response = await http.DeleteAsync(path);
    if (!response.IsSuccessStatusCode)
    {
        await PrintError(response);
        return 1;
    }
    Console.WriteLine(doneMessage);
    return 0;
}

async Task<T?> Get<T>(string path) => await Send<T>(HttpMethod.Get, path, null);

async Task<T?> Send<T>(HttpMethod method, string path, object? body)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null)
        request.Content = JsonContent.Create(body, body.GetType(), options: TapQueueJson.Options);
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        await PrintError(response);
        return default;
    }
    return await response.Content.ReadFromJsonAsync<T>(TapQueueJson.Options);
}

static async Task PrintError(HttpResponseMessage response)
{
    string? error = null;
    try
    {
        error = (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options))?.Error;
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
    {
        // Not a TapQueue error body; fall back to the HTTP reason.
    }
    Console.Error.WriteLine($"Error ({(int)response.StatusCode}): {error ?? response.ReasonPhrase}");
}

int BadUsage()
{
    Console.Error.WriteLine(Usage);
    return 2;
}

static AdminConfig? LoadFileConfig()
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tapqueue", "admin.toml");
    return File.Exists(path) ? TomlConfig.Load<AdminConfig>(path) : null;
}

static void Table(string[] headers, IEnumerable<string[]> rows)
{
    var all = rows.Prepend(headers).ToList();
    var widths = headers.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
    foreach (var row in all)
        Console.WriteLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
}

static string Ago(DateTimeOffset at)
{
    var age = DateTimeOffset.UtcNow - at;
    return age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
        : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string FormatSize(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
    _ => $"{bytes / (1024.0 * 1024):0.#} MB",
};
