namespace TapQueue.Admin;

/// <summary>~/.config/tapqueue/admin.toml. Flags and environment variables override it.</summary>
public sealed class AdminConfig
{
    public string ServerUrl { get; set; } = "http://localhost:8631";
    public string Token { get; set; } = "";
}
