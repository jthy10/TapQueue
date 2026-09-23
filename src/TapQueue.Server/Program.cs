using System.Net;
using TapQueue.Server.Api;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Ipp;
using TapQueue.Server.Jobs;
using TapQueue.Server.Printers;
using TapQueue.Shared;

var configPath = ConfigPath(args);
ServerConfig config;
try
{
    config = TomlConfig.Load<ServerConfig>(configPath);
    config.Validate();
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"tapqueue-server: {ex.Message}");
    return 1;
}

Directory.CreateDirectory(config.Server.DataDir);
var database = new Database(Path.Combine(config.Server.DataDir, "tapqueue.db"));
database.Migrate();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(ParseEndpoint(config.Server.Listen));
    kestrel.Limits.MaxRequestBodySize = 512L * 1024 * 1024; // big scanned PDFs
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = TapQueueJson.Options.PropertyNamingPolicy);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(new Spool(config.Server.DataDir));
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<BadgeStore>();
builder.Services.AddSingleton<StationStore>();
builder.Services.AddSingleton<UnknownTaps>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobOwnerResolver>();
builder.Services.AddSingleton<PrinterRegistry>();
builder.Services.AddSingleton<ReleaseService>();
builder.Services.AddSingleton<IppPrinterEndpoint>();
builder.Services.AddHostedService<PrinterMonitor>();
builder.Services.AddHostedService<JobCleanupService>();

var app = builder.Build();

app.MapPost("/ipp/{queueId}", (HttpContext http, string queueId, IppPrinterEndpoint ipp) => ipp.HandleAsync(http, queueId));
app.MapGet("/", () => "TapQueue server " + typeof(Program).Assembly.GetName().Version?.ToString(3));
app.MapGet("/icons/{size:int}.png", (int size) =>
    typeof(Program).Assembly.GetManifestResourceStream($"TapQueue.Server.Assets.icon-{size}.png") is { } png
        ? Results.Stream(png, "image/png")
        : Results.NotFound());
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapClientApi();
app.MapAdminApi();
app.MapStationApi();

app.Logger.LogInformation("Config: {Path} | auth mode: {Mode} | data: {DataDir}", configPath, config.Auth.Mode, config.Server.DataDir);
foreach (var queue in config.Queues)
    app.Logger.LogInformation("Queue \"{Name}\" at ipp://<this-host>:{Port}/ipp/{Id}", queue.Name, ParseEndpoint(config.Server.Listen).Port, queue.Id);
if (config.Auth.Mode == "dev")
    app.Logger.LogWarning("auth.mode = \"dev\": anyone can sign in as any username. Use \"token\" outside of testing.");

app.Run();
return 0;

static string ConfigPath(string[] args)
{
    var index = Array.IndexOf(args, "--config");
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return Environment.GetEnvironmentVariable("TAPQUEUE_CONFIG") ?? "/etc/tapqueue/server.toml";
}

static IPEndPoint ParseEndpoint(string listen)
{
    if (!IPEndPoint.TryParse(listen, out var endpoint) || endpoint.Port == 0)
        throw new InvalidDataException($"server.listen \"{listen}\" must look like 0.0.0.0:8631.");
    return endpoint;
}
