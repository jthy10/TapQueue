using System.Runtime.InteropServices;

namespace TapQueue.Shared;

/// <summary>
/// Which build of the client a PC runs. The server publishes one client build per platform, and each
/// PC's TapQueue service installs the one for its own. Named like .NET runtime identifiers.
/// </summary>
public static class ClientPlatform
{
    public const string Windows = "win-x64";
    public const string Linux = "linux-x64";

    public static IReadOnlyList<string> All { get; } = [Windows, Linux];

    /// <summary>
    /// The platform of the running build. Uses the process's architecture, not the OS's, so an x64
    /// build running under emulation on an Arm PC keeps getting x64 builds.
    /// </summary>
    public static string Current =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unknown") + "-" +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// A platform sent by a client or admin, or null if it isn't one TapQueue has a client for.
    /// Clients older than 0.5 don't send one; they're all Windows.
    /// </summary>
    public static string? Parse(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? Windows : All.FirstOrDefault(p => p.Equals(platform.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string platform) => platform switch
    {
        Windows => "Windows",
        Linux => "Linux",
        _ => platform,
    };
}
