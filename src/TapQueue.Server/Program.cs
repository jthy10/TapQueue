using TapQueue.Server;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Tls;
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

WebApplication app;
try
{
    app = ServerApp.Build(config, args);
}
catch (InvalidDataException ex) // a TLS certificate that can't be loaded
{
    Console.Error.WriteLine($"tapqueue-server: {ex.Message}");
    return 1;
}

app.Logger.LogInformation("TapQueue server {Version} | config: {Path} | auth mode: {Mode} | data: {DataDir}",
    TapQueueVersion.Current, configPath, config.Auth.Mode, config.Server.DataDir);
if (app.Services.GetService<ServerCertificate>() is { } certificate)
{
    app.Logger.LogInformation("HTTPS and IPPS on {Listen} with {Kind} certificate for {Names}, SHA-256 fingerprint {Fingerprint}",
        config.Tls.Listen, certificate.SelfSigned ? "a self-signed" : "the", string.Join(", ", certificate.Names), certificate.Fingerprint);
    if (certificate.Missing.Count > 0)
        app.Logger.LogWarning("The self-signed certificate doesn't cover {Names}, so PCs using those names will refuse it. " +
            "Delete {Dir} and restart to make a new one; PCs that trusted the old one have to trust the new one.",
            string.Join(", ", certificate.Missing), Path.GetDirectoryName(certificate.CertPath));
}
else
{
    app.Logger.LogWarning("TLS is off (tls.listen is empty): print jobs and tokens cross the network unencrypted.");
}
var queues = app.Services.GetRequiredService<QueueStore>().List();
foreach (var queue in queues)
    app.Logger.LogInformation("Queue \"{Name}\" at ipp://<this-host>:{Port}/ipp/{Id}{Secure}", queue.Name, ServerApp.ParseEndpoint(config.Server.Listen).Port, queue.Id,
        config.Tls.Enabled ? $" and ipps://<this-host>:{ServerApp.ParseEndpoint(config.Tls.Listen).Port}/ipp/{queue.Id}" : "");
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
