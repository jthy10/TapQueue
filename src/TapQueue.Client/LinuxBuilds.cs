namespace TapQueue.Client;

/// <summary>
/// How the Linux client is laid out so it can be updated while it runs. A single-file .NET program
/// loads parts of itself from its own file as it goes, so a running tray app breaks if its file is
/// replaced. Each build therefore gets a file of its own that is never overwritten, and the program
/// everyone starts is a symlink to the current one:
/// <code>
///   /opt/tapqueue-client/tapqueue-client  ->  builds/tapqueue-client-&lt;sha256 prefix&gt;
/// </code>
/// An update adds a build and repoints the symlink; a build is deleted once no process runs it.
/// (Windows can't replace a running exe at all, so there the exe is renamed and swapped instead.)
/// </summary>
public static class LinuxBuilds
{
    public const string FolderName = "builds";
    public const string ProgramName = "tapqueue-client";

    /// <summary>
    /// The symlink that starts the current build, when <paramref name="programPath"/> is one of the
    /// installed builds; null for a program run from anywhere else (a development build).
    /// </summary>
    public static string? LauncherFor(string programPath)
    {
        var builds = Path.GetDirectoryName(programPath);
        if (!OperatingSystem.IsLinux() || builds is null || Path.GetFileName(builds) != FolderName)
            return null;
        var launcher = Path.Combine(Path.GetDirectoryName(builds)!, ProgramName);
        return new FileInfo(launcher).LinkTarget is null ? null : launcher;
    }

    /// <summary>The build the launcher points at now, or null if it can't be read.</summary>
    public static string? CurrentBuild(string launcher)
    {
        try
        {
            return File.ResolveLinkTarget(launcher, returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>File name for the build with this SHA-256.</summary>
    public static string BuildFileName(string sha256) => $"{ProgramName}-{sha256[..16].ToLowerInvariant()}";

    /// <summary>Points <paramref name="launcher"/> at <paramref name="build"/> in one step (rename over the old link).</summary>
    public static void Repoint(string launcher, string build)
    {
        var temp = launcher + ".new";
        File.Delete(temp);
        File.CreateSymbolicLink(temp, Path.Combine(FolderName, Path.GetFileName(build)));
        File.Move(temp, launcher, overwrite: true);
    }

    /// <summary>
    /// Deletes builds that are neither current nor running (any process's /proc/&lt;pid&gt;/exe).
    /// Returns what was deleted.
    /// </summary>
    public static List<string> DeleteUnused(string launcher)
    {
        var builds = Path.Combine(Path.GetDirectoryName(launcher)!, FolderName);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (CurrentBuild(launcher) is { } current)
            keep.Add(current);
        foreach (var proc in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (File.ResolveLinkTarget(Path.Combine(proc, "exe"), returnFinalTarget: false) is { } exe)
                    keep.Add(exe.FullName.Replace(" (deleted)", ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not a process, gone already, or not ours to look at.
            }
        }

        var deleted = new List<string>();
        foreach (var build in Directory.EnumerateFiles(builds))
        {
            if (keep.Contains(build))
                continue;
            try
            {
                File.Delete(build);
                deleted.Add(Path.GetFileName(build));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return deleted;
    }
}
