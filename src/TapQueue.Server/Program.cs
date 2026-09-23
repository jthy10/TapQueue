using TapQueue.Server;
using TapQueue.Server.Config;
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

var app = ServerApp.Build(config, args);

app.Logger.LogInformation("Config: {Path} | auth mode: {Mode} | data: {DataDir}", configPath, config.Auth.Mode, config.Server.DataDir);
foreach (var queue in config.Queues)
    app.Logger.LogInformation("Queue \"{Name}\" at ipp://<this-host>:{Port}/ipp/{Id}", queue.Name, ServerApp.ParseEndpoint(config.Server.Listen).Port, queue.Id);
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
