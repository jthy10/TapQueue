using System.Net;
using TapQueue.Shared;

namespace TapQueue.Admin;

/// <summary>~/.config/tapqueue/admin.toml. Flags and environment variables override it.</summary>
public sealed class AdminConfig
{
    public string ServerUrl { get; set; } = "http://localhost:8631";
    public string Token { get; set; } = "";

    /// <summary>
    /// On the server itself (e.g. under sudo), fall back to the server's own config, so
    /// tapqueue-admin works there without copying the admin token anywhere.
    /// </summary>
    public static AdminConfig? FromServerConfig(string path = "/etc/tapqueue/server.toml")
    {
        try
        {
            if (!File.Exists(path)) return null;
            var server = TomlConfig.Load<ServerToml>(path);
            if (string.IsNullOrEmpty(server.Admin.Token)) return null;
            var port = IPEndPoint.TryParse(server.Server.Listen, out var listen) ? listen.Port : 8631;
            return new AdminConfig { ServerUrl = $"http://localhost:{port}", Token = server.Admin.Token };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The two bits of server.toml tapqueue-admin needs; the rest is ignored.</summary>
    private sealed class ServerToml
    {
        public ServerPart Server { get; set; } = new();
        public AdminPart Admin { get; set; } = new();

        public sealed class ServerPart { public string Listen { get; set; } = "0.0.0.0:8631"; }
        public sealed class AdminPart { public string Token { get; set; } = ""; }
    }
}
