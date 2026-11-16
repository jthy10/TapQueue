using Microsoft.Data.Sqlite;

namespace TapQueue.Server.Tests;

/// <summary>Cleaning up after a test, on Windows as well as Linux.</summary>
public static class TestFiles
{
    /// <summary>
    /// Deletes a test's folder. Windows won't delete a file that's open, and Microsoft.Data.Sqlite keeps
    /// closed connections (and so the database file) open in a pool, so the pools are emptied first; a
    /// background task that's still letting go of a file gets a moment to.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>A test of something only Linux has (file modes, symbolic links without admin rights): skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Only runs on Linux.";
    }
}
