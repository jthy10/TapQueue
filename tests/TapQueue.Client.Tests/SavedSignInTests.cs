namespace TapQueue.Client.Tests;

public sealed class SavedSignInTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-signin").FullName;

    private string PathFor => System.IO.Path.Combine(_dir, "sub", "sign-in.json");

    [Fact]
    public void RoundTripsForTheSameServerOnly()
    {
        new SavedSignIn("http://tq:8631/", "alice", "token").Save(PathFor);

        Assert.Equal("alice", SavedSignIn.Load("http://TQ:8631", PathFor)?.Username);
        Assert.Null(SavedSignIn.Load("http://other:8631", PathFor));

        SavedSignIn.Delete(PathFor);
        Assert.Null(SavedSignIn.Load("http://tq:8631", PathFor));
    }

    [Fact]
    public void OnlyTheUserCanReadIt()
    {
        if (OperatingSystem.IsWindows()) return;
        new SavedSignIn("http://tq:8631", "alice", "token").Save(PathFor);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PathFor));
    }

    [Fact]
    public void AnUnreadableFileIsNoSignIn()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFor)!);
        File.WriteAllText(PathFor, "{ not json");
        Assert.Null(SavedSignIn.Load("http://tq:8631", PathFor));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
