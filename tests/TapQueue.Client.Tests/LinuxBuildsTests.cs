namespace TapQueue.Client.Tests;

/// <summary>The builds/ + symlink layout that lets the Linux client update while tray apps run.</summary>
public sealed class LinuxBuildsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tapqueue-builds-").FullName;
    private readonly string _launcher;

    public LinuxBuildsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, LinuxBuilds.FolderName));
        _launcher = Path.Combine(_root, LinuxBuilds.ProgramName);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AddBuild(string sha256)
    {
        var path = Path.Combine(_root, LinuxBuilds.FolderName, LinuxBuilds.BuildFileName(sha256));
        File.WriteAllText(path, sha256);
        return path;
    }

    [Fact]
    public void BuildsAreFoundThroughTheirLauncher()
    {
        var build = AddBuild(new string('a', 64));
        LinuxBuilds.Repoint(_launcher, build);

        Assert.Equal(_launcher, LinuxBuilds.LauncherFor(build));
        Assert.Equal(build, LinuxBuilds.CurrentBuild(_launcher));
        Assert.Null(LinuxBuilds.LauncherFor(Path.Combine(_root, "somewhere-else")));
    }

    [Fact]
    public void RepointingSwitchesTheLauncherAndKeepsTheOldBuild()
    {
        var old = AddBuild(new string('a', 64));
        var next = AddBuild(new string('b', 64));
        LinuxBuilds.Repoint(_launcher, old);

        LinuxBuilds.Repoint(_launcher, next);

        Assert.Equal(next, LinuxBuilds.CurrentBuild(_launcher));
        Assert.Equal("builds/tapqueue-client-bbbbbbbbbbbbbbbb", new FileInfo(_launcher).LinkTarget); // relative, so /opt can move
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void UnusedBuildsAreDeletedButNotTheCurrentOne()
    {
        var old = AddBuild(new string('a', 64));
        var current = AddBuild(new string('b', 64));
        LinuxBuilds.Repoint(_launcher, current);

        var deleted = LinuxBuilds.DeleteUnused(_launcher);

        Assert.Equal([Path.GetFileName(old)], deleted);
        Assert.True(File.Exists(current));
    }
}
