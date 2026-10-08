namespace TapQueue.Client.Tests;

public sealed class WorkstationKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-key").FullName;

    private string KeyPath => Path.Combine(_dir, "workstation-key.json");

    [Fact]
    public void KeepsTheKeyForItsServerOnly()
    {
        new WorkstationKey("http://tapqueue:8631/", "the-key").Save(KeyPath);

        Assert.Equal("the-key", WorkstationKey.Load("http://TAPQUEUE:8631", KeyPath));
        Assert.Null(WorkstationKey.Load("http://other:8631", KeyPath));
        Assert.Null(WorkstationKey.Load("http://tapqueue:8631", Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void OnlyTheServiceCanReadIt()
    {
        if (OperatingSystem.IsWindows())
            return;
        new WorkstationKey("http://tapqueue:8631", "the-key").Save(KeyPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
