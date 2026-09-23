using System.Net.Http.Json;
using System.Runtime.InteropServices;
using TapQueue.Shared;
using TapQueue.Shared.Api;
using TapQueue.Station;

const string Usage = """
    tapqueue-station — badge reader next to a printer; a tap releases that person's held jobs

    Usage:
      tapqueue-station [--config <path>]      Run the station (default /etc/tapqueue/station.toml)
      tapqueue-station --list-devices         Show input devices, to find the badge reader
      tapqueue-station --test [--reader keyboard|pcprox] [--device <path>]
                                              Print each card number read, without contacting the server
    """;

if (args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine(Usage);
    return 0;
}
if (args.Contains("--version"))
{
    Console.WriteLine($"tapqueue-station {TapQueueVersion.Current}");
    return 0;
}
if (args.Contains("--list-devices"))
    return ListDevices();

var configPath = Option("--config") ?? Environment.GetEnvironmentVariable("TAPQUEUE_STATION_CONFIG") ?? "/etc/tapqueue/station.toml";
var testMode = args.Contains("--test");
StationConfig config;
try
{
    config = File.Exists(configPath) || !testMode ? TomlConfig.Load<StationConfig>(configPath) : new StationConfig();
    config.Device = Option("--device") ?? config.Device;
    config.Reader = Option("--reader") ?? config.Reader;
    config.Validate(needServer: !testMode);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"tapqueue-station: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
// Let the default handling still end the process: a blocking read on the reader doesn't see the cancellation.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => cts.Cancel());

try
{
    if (testMode)
    {
        await foreach (var card in CreateReader(config.Reader, config.Device).ReadCardsAsync(cts.Token))
            Log($"Read card: {card} ({card.Length} characters)");
        return 0;
    }

    using var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/api/v1/station/"), Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.Authorization = new("Bearer", config.Token);

    Log($"tapqueue-station {TapQueueVersion.Current}, server {config.ServerUrl}");
    await CheckInAsync(http, cts.Token);

    // Settings held on the server (reader, repeat time…) win over station.toml.
    var runtime = new StationRuntime(http, config, Log);
    await runtime.StartAsync(cts.Token);
    var reader = CreateReader(runtime.Settings.Reader, runtime.Settings.Device);
    runtime.Reader = reader;
    _ = runtime.RunAsync(cts.Token).ContinueWith(t => Log($"Heartbeats stopped: {t.Exception?.GetBaseException().Message}"),
        TaskContinuationOptions.OnlyOnFaulted);

    string? lastCard = null;
    var lastCardAt = DateTimeOffset.MinValue;
    await foreach (var card in reader.ReadCardsAsync(cts.Token))
    {
        var settings = runtime.Settings;
        if (card.Length < settings.MinCardLength)
        {
            Log($"Ignored a {card.Length}-character read (min_card_length is {settings.MinCardLength}).");
            continue;
        }
        // Readers often report a card held on the pad more than once.
        var now = DateTimeOffset.UtcNow;
        if (card == lastCard && now - lastCardAt < TimeSpan.FromSeconds(settings.RepeatSeconds))
            continue;
        (lastCard, lastCardAt) = (card, now);

        await TapAsync(http, card, cts.Token);
    }
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    return 0;
}

IBadgeReader CreateReader(string kind, string device)
{
    IBadgeReader reader = kind == "pcprox" ? new PcProxReader(device, Log)
        : device is "stdin" or "" ? new StdinBadgeReader()
        : new EvdevBadgeReader(device, Log);
    if (reader is StdinBadgeReader)
        Log("Reading card numbers from standard input; type or scan one followed by Enter.");
    return reader;
}

// Confirms the token works and shows which printer this station releases to. Keeps retrying
// while the server is unreachable, so the station can boot before the server does.
static async Task CheckInAsync(HttpClient http, CancellationToken ct)
{
    var delay = TimeSpan.FromSeconds(5);
    while (true)
    {
        try
        {
            using var response = await http.GetAsync("", ct);
            if (response.IsSuccessStatusCode)
            {
                var info = await response.Content.ReadFromJsonAsync<StationInfoResponse>(TapQueueJson.Options, ct);
                Log($"Station \"{info!.StationId}\" releases to {info.Printer.Name} ({(info.Printer.Online ? "online" : "offline: " + info.Printer.StateMessage)}).");
                return;
            }
            Log($"Server refused this station: {await ErrorText(response, ct)}");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                Log("Check token in station.toml (create one with `tapqueue-admin stations add`).");
        }
        catch (HttpRequestException ex)
        {
            Log($"Can't reach the server: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            Log("Can't reach the server: timed out.");
        }
        Log($"Retrying in {delay.TotalSeconds:0} s.");
        await Task.Delay(delay, ct);
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
    }
}

// Taps aren't queued when the server is down: releasing someone's jobs minutes later, after
// they've walked away, is worse than making them tap again.
static async Task TapAsync(HttpClient http, string card, CancellationToken ct)
{
    try
    {
        using var response = await http.PostAsJsonAsync("tap", new StationTapRequest(card), TapQueueJson.Options, ct);
        if (!response.IsSuccessStatusCode)
        {
            Log($"Tap failed: {await ErrorText(response, ct)}");
            return;
        }
        var result = await response.Content.ReadFromJsonAsync<StationTapResponse>(TapQueueJson.Options, ct);
        var who = result!.User?.Username ?? $"card {card}";
        Log($"{who}: {result.Message}");
        foreach (var r in result.Results.Where(r => !r.Success))
            Log($"  job #{r.JobId} \"{r.JobName}\" not printed: {r.Error}");
    }
    catch (HttpRequestException ex)
    {
        Log($"Tap failed, can't reach the server: {ex.Message}");
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        Log("Tap failed: the server took too long to answer.");
    }
}

static async Task<string> ErrorText(HttpResponseMessage response, CancellationToken ct)
{
    try
    {
        if ((await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options, ct))?.Error is { } error)
            return error;
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
    {
        // Not a TapQueue error body; fall back to the HTTP status.
    }
    return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
}

static int ListDevices()
{
    const string byId = "/dev/input/by-id";
    if (!Directory.Exists(byId))
    {
        Console.Error.WriteLine($"{byId} doesn't exist. Is the reader plugged in?");
        return 1;
    }
    // Most readers are "-event-kbd"; some multi-interface ones show up as "-if01-event-kbd" or similar.
    Console.WriteLine("Input devices (a USB badge reader is usually one of the *-event-kbd ones):");
    foreach (var link in Directory.GetFiles(byId).Where(p => p.Contains("-event-")).Order())
        Console.WriteLine($"  {link}  ->  {Path.GetFileName(new FileInfo(link).ResolveLinkTarget(true)?.FullName)}");
    Console.WriteLine();
    Console.WriteLine("Not sure which is the reader? Run `tapqueue-station --test --device <path>` and tap a card.");
    return 0;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// systemd's journal adds its own timestamps.
static void Log(string message) => Console.WriteLine(message);
