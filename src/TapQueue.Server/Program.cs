using TapQueue.Server;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared;

if (args.Contains("--version"))
{
    Console.WriteLine($"tapqueue-server {TapQueueVersion.Current}");
    return 0;
}

var configPath = ConfigPath(args);
ServerConfig config;
try
{
    config = TomlConfig.Load<ServerConfig>(configPath);
    ServerConfig.RejectRemovedSections(File.ReadAllText(configPath));
    config.Validate();
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"tapqueue-server: {ex.Message}");
    return 1;
}

var app = ServerApp.Build(config, args);

app.Logger.LogInformation("TapQueue server {Version} | config: {Path} | auth mode: {Mode} | data: {DataDir}",
    TapQueueVersion.Current, configPath, config.Auth.Mode, config.Server.DataDir);
var queues = app.Services.GetRequiredService<QueueStore>().List();
foreach (var queue in queues)
    app.Logger.LogInformation("Queue \"{Name}\" at ipp://<this-host>:{Port}/ipp/{Id}", queue.Name, ServerApp.ParseEndpoint(config.Server.Listen).Port, queue.Id);
if (queues.Count == 0)
    app.Logger.LogWarning("No queues yet, so there's nothing to print to. Add one with: tapqueue-admin queues add <id> --name \"TapQueue Secure Print\"");
if (app.Services.GetRequiredService<PrinterStore>().List().Count == 0)
    app.Logger.LogWarning("No printers yet, so held jobs can't be released. Add one with: tapqueue-admin printers add <id> ipp://<printer-ip>/ipp/print");
if (config.Auth.Mode == "dev")
    app.Logger.LogWarning("auth.mode = \"dev\": anyone can sign in as any username. Use \"token\" outside of testing.");

app.Run();
// Non-zero when an admin asked for a restart (see AdminServerApi), so systemd starts it again.
return Environment.ExitCode;

static string ConfigPath(string[] args)
{
    var index = Array.IndexOf(args, "--config");
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return Environment.GetEnvironmentVariable("TAPQUEUE_CONFIG") ?? "/etc/tapqueue/server.toml";
}
