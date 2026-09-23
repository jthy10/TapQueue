using System.Reflection;

namespace TapQueue.Shared;

public static class TapQueueVersion
{
    /// <summary>
    /// The version from Directory.Build.props plus the commit it was built from, e.g. "0.2.0+1a2b3c4".
    /// All TapQueue programs are built from the same tree, so they share it.
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(TapQueueVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}
