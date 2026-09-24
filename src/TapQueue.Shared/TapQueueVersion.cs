using System.Reflection;

namespace TapQueue.Shared;

public static class TapQueueVersion
{
    /// <summary>
    /// The running program's version plus the commit it was built from, e.g. "0.2.0+1a2b3c4".
    /// The server, station and admin CLI share one version; the Windows and Linux clients have their own
    /// (both are set in Directory.Build.props).
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = (Assembly.GetEntryAssembly() ?? typeof(TapQueueVersion).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}
