namespace TapQueue.Client.Tests;

/// <summary>A test of the Linux client, or of something only Linux has (file modes, symbolic links without admin rights): skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Only runs on Linux.";
    }
}

/// <summary><see cref="LinuxFactAttribute"/> for a theory.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Only runs on Linux.";
    }
}
