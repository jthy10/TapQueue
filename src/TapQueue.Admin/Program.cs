using System.Net.Http.Json;
using TapQueue.Admin;
using TapQueue.Shared;
using TapQueue.Shared.Api;

const string Usage = """
    tapqueue-admin — manage a TapQueue server

    Usage:
      tapqueue-admin users                           List users
      tapqueue-admin users add <username> [--name "Display Name"]
                                                     Create a user and print their client token
      tapqueue-admin users reset-token <username>    Issue a new client token (the old one stops working)
      tapqueue-admin jobs [--status held|released|expired|canceled]
                                                     List recent jobs
      tapqueue-admin printers [--refresh]            List printers and whether they're reachable
      tapqueue-admin release <username> <printer-id> [job-id ...]
                                                     Send a user's held jobs (all, or just these) to a printer

    Connection (flag > environment > ~/.config/tapqueue/admin.toml):
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
    if (args[i].StartsWith("--"))
    {
        var takesValue = args[i] is "--server" or "--token" or "--name" or "--status";
        options[args[i]] = takesValue && i + 1 < args.Length ? args[++i] : null;
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

var fileConfig = LoadFileConfig();
var serverUrl = options.GetValueOrDefault("--server") ?? Environment.GetEnvironmentVariable("TAPQUEUE_SERVER") ?? fileConfig.ServerUrl;
var token = options.GetValueOrDefault("--token") ?? Environment.GetEnvironmentVariable("TAPQUEUE_ADMIN_TOKEN") ?? fileConfig.Token;
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("No admin token. Pass --token, set TAPQUEUE_ADMIN_TOKEN, or put it in ~/.config/tapqueue/admin.toml.");
    return 2;
}

using var http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/api/v1/admin/"), Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);

try
{
    return (positional[0], positional.Count > 1 ? positional[1] : null) switch
    {
        ("users", null) => await ListUsers(),
        ("users", "add") when positional.Count == 3 => await AddUser(positional[2], options.GetValueOrDefault("--name")),
        ("users", "reset-token") when positional.Count == 3 => await ResetToken(positional[2]),
        ("jobs", null) => await ListJobs(options.GetValueOrDefault("--status")),
        ("printers", null) => await ListPrinters(options.ContainsKey("--refresh")),
        ("release", not null) when positional.Count >= 3 => await Release(positional[1], positional[2], positional.Skip(3).ToList()),
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
    Console.Error.WriteLine("Job ids must be numbers.");
    return 2;
}

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
    Table(["ID", "OWNER", "STATUS", "NAME", "SIZE", "SUBMITTED", "PRINTER"], jobs.Select(j => new[]
    {
        j.Id.ToString(), j.Owner ?? $"unowned ({j.ClaimedUser ?? "?"})", j.Status, Truncate(j.Name, 40), FormatSize(j.SizeBytes),
        j.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), j.ReleasedPrinterId ?? "",
    }));
    return 0;
}

async Task<int> ListPrinters(bool refresh)
{
    var printers = await Get<List<PrinterDto>>(refresh ? "printers?refresh=true" : "printers");
    if (printers is null) return 1;
    Table(["ID", "NAME", "ONLINE", "MODEL", "STATUS"], printers.Select(p => new[]
    {
        p.Id, p.Name, p.Online ? "yes" : "no", p.MakeAndModel ?? "", p.StateMessage ?? "",
    }));
    return 0;
}

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

async Task<T?> Get<T>(string path) => await Send<T>(HttpMethod.Get, path, null);

async Task<T?> Send<T>(HttpMethod method, string path, object? body)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null)
        request.Content = JsonContent.Create(body, body.GetType(), options: TapQueueJson.Options);
    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode)
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
        return default;
    }
    return await response.Content.ReadFromJsonAsync<T>(TapQueueJson.Options);
}

int BadUsage()
{
    Console.Error.WriteLine(Usage);
    return 2;
}

static AdminConfig LoadFileConfig()
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tapqueue", "admin.toml");
    return File.Exists(path) ? TomlConfig.Load<AdminConfig>(path) : new AdminConfig();
}

static void Table(string[] headers, IEnumerable<string[]> rows)
{
    var all = rows.Prepend(headers).ToList();
    var widths = headers.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
    foreach (var row in all)
        Console.WriteLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string FormatSize(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
    _ => $"{bytes / (1024.0 * 1024):0.#} MB",
};
