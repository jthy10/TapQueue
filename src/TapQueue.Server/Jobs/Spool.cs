namespace TapQueue.Server.Jobs;

/// <summary>Stores the document data of held jobs on disk, readable only by the server's user.</summary>
public sealed class Spool
{
    private readonly string _directory;

    public Spool(string dataDir)
    {
        _directory = Path.Combine(dataDir, "spool");
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string PathFor(long jobId) => Path.Combine(_directory, $"{jobId}.job");

    public FileStream Create(long jobId) => Open(jobId, FileMode.Create);

    /// <summary>For Send-Document, which may deliver a job's data in more than one request.</summary>
    public FileStream OpenAppend(long jobId) => Open(jobId, FileMode.Append);

    private FileStream Open(long jobId, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(PathFor(jobId), options);
    }

    public FileStream OpenRead(long jobId) =>
        new(PathFor(jobId), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    public void Delete(long jobId) => File.Delete(PathFor(jobId));
}
