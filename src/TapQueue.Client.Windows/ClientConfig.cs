using TapQueue.Shared;

namespace TapQueue.Client.Windows;

/// <summary>
/// client.toml. Looked for at --config, then %ProgramData%\TapQueue\client.toml, then next to the exe.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>The TapQueue server, e.g. http://tapqueue.example.local:8631.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>TapQueue username. Leave empty to use the Windows username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Per-user token from `tapqueue-admin users add`. Not needed when the server runs in dev auth mode.</summary>
    public string Token { get; set; } = "";

    /// <summary>Add the server's print queues to this PC automatically.</summary>
    public bool InstallPrinters { get; set; } = true;

    public string EffectiveUsername => string.IsNullOrWhiteSpace(Username) ? Environment.UserName : Username.Trim();

    public static (ClientConfig Config, string Path) Load(string[] args)
    {
        var path = FindPath(args) ?? throw new FileNotFoundException(
            "No client.toml found. Create one at " +
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TapQueue", "client.toml"));

        var config = TomlConfig.Load<ClientConfig>(path);
        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"server_url in {path} must be an http:// or https:// address.");
        return (config, path);
    }

    private static string? FindPath(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        if (index >= 0 && index + 1 < args.Length)
            return args[index + 1];

        string[] candidates =
        [
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TapQueue", "client.toml"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "client.toml"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
