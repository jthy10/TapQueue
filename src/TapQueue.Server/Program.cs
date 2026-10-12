using TapQueue.Server;
using TapQueue.Server.Backups;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Server.Tls;
using TapQueue.Shared;

if (args.Contains("--version"))
{
    Console.WriteLine($"tapqueue-server {TapQueueVersion.Current}");
    return 0;
}

// For update-server.sh, which asks an older release's program what schema it needs before going back to it.
if (args.Contains("--schema-version"))
{
    Console.WriteLine(Database.LatestSchemaVersion);
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

if (Array.IndexOf(args, "--restore") is >= 0 and var restoreAt)
{
    if (restoreAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("tapqueue-server: --restore needs a backup file.");
        return 1;
    }
    string? passphrase = null;
    if (Array.IndexOf(args, "--passphrase-file") is >= 0 and var passphraseAt)
    {
        if (passphraseAt + 1 >= args.Length || !File.Exists(args[passphraseAt + 1]))
        {
            Console.Error.WriteLine("tapqueue-server: --passphrase-file needs a file holding the backup's passphrase.");
            return 1;
        }
        passphrase = File.ReadAllText(args[passphraseAt + 1]).Trim();
    }
    return Restore(config, args[restoreAt + 1], args.Contains("--as-root"), passphrase);
}

if (Array.IndexOf(args, "--downgrade-schema") is >= 0 and var downgradeAt)
{
    if (downgradeAt + 1 >= args.Length || !int.TryParse(args[downgradeAt + 1], out var schema))
    {
        Console.Error.WriteLine("tapqueue-server: --downgrade-schema needs the schema number to go back to.");
        return 1;
    }
    return DowngradeSchema(config, schema);
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
if (app.Services.GetRequiredService<BadgeStore>().KeyMismatch)
    app.Logger.LogError("No card will be recognised: {Path} isn't the key the cards in the database were stored with. " +
        "Put back the {Folder} folder from a backup made with this database (tapqueue-server --restore brings both).",
        Path.Combine(config.Server.DataDir, BadgeKey.Folder, "badge.key"), BadgeKey.Folder);
if (config.Server.TrustedProxies.Count > 0)
    app.Logger.LogInformation("Callers' addresses are taken from X-Forwarded-For when a request comes from {Proxies}", string.Join(", ", config.Server.TrustedProxies));
if (app.Services.GetRequiredService<BackupStore>() is { CopyTo: { } copyTo } backupStore)
    app.Logger.LogInformation("Backups are {Encrypted}copied to {CopyTo}", backupStore.Encrypts ? "encrypted and " : "", copyTo);
if (config.Auth.Mode == "dev")
    app.Logger.LogWarning("auth.mode = \"dev\": anyone can sign in as any username. Use \"token\" outside of testing.");

app.Run();
// Non-zero when an admin asked for a restart (see AdminServerApi), so systemd starts it again.
return Environment.ExitCode;

static int Restore(ServerConfig config, string file, bool asRoot, string? passphrase)
{
    // Restored files belong to whoever runs this, and the service has to be able to open them.
    if (Environment.IsPrivilegedProcess && !asRoot)
    {
        Console.Error.WriteLine(
            "tapqueue-server: run the restore as the user the server runs as, so it can read what's restored:\n" +
            $"  sudo -u tapqueue {Environment.ProcessPath} --restore {file}\n" +
            "Add --as-root if the server itself runs as root.");
        return 1;
    }
    try
    {
        var result = BackupRestore.Run(config, Path.GetFullPath(file), passphrase: passphrase);
        Console.WriteLine($"Restored the backup made {result.Manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} by TapQueue server {result.Manifest.ServerVersion} into {config.Server.DataDir}.");
        if (result.ReplacedDir is not null)
            Console.WriteLine($"What was there before is in {result.ReplacedDir}; delete it once you're happy.");
        if (result.LostJobs > 0)
            Console.WriteLine($"{result.LostJobs} jobs that were waiting then are canceled: backups don't hold documents. Their owners have to print again.");
        if (result.ForgottenBuilds > 0)
            Console.WriteLine($"{result.ForgottenBuilds} published client or station builds aren't on this server; publish the current ones again.");
        Console.WriteLine("Start the server: sudo systemctl start tapqueue-server");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"tapqueue-server: {ex.Message}");
        return 1;
    }
}

// Run by update-server.sh, as the server's user and with the server stopped, just before it installs
// an older release: the older program can't read a newer database, so this one steps it back first.
static int DowngradeSchema(ServerConfig config, int schema)
{
    try
    {
        var path = Path.Combine(config.Server.DataDir, "tapqueue.db");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"tapqueue-server: there's no database at {path}.");
            return 1;
        }
        var database = new Database(path);
        if (database.SchemaVersion() == schema)
        {
            Console.WriteLine($"The database is at schema {schema} already.");
            return 0;
        }
        // What the newer version added is lost going back, so it's kept here.
        var backup = new BackupStore(config).Create(TapQueue.Shared.Api.BackupKind.Downgrade);
        var from = database.DowngradeTo(schema);
        Console.WriteLine($"Took the database from schema {from} back to {schema}. As it was before: {backup.Name}");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    {
        Console.Error.WriteLine($"tapqueue-server: {ex.Message}");
        return 1;
    }
}

static string ConfigPath(string[] args)
{
    var index = Array.IndexOf(args, "--config");
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return Environment.GetEnvironmentVariable("TAPQUEUE_CONFIG") ?? "/etc/tapqueue/server.toml";
}
