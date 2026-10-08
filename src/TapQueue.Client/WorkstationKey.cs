using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using TapQueue.Shared;

namespace TapQueue.Client;

/// <summary>
/// The key the server gave this PC, which the TapQueue service checks in and vouches for tray apps
/// with (<see cref="Shared.Api.WorkstationSessionRequest"/>). The PC's printers carry a print key made
/// from it, so the server knows which PC a job came from without going by its address.
///
/// Only the service may read it: anyone holding it could say which PC user runs which tray app, and
/// so take other people's jobs. Kept in workstation-key.json in <see cref="ClientConfig.StateDirectory"/>,
/// readable only by SYSTEM and administrators on Windows, and by root on Linux.
/// </summary>
/// <param name="ServerUrl">The server it's from; a PC pointed at another server asks that one for a key.</param>
public sealed record WorkstationKey(string ServerUrl, string Key)
{
    public static string DefaultPath { get; } = Path.Combine(ClientConfig.StateDirectory, "workstation-key.json");

    /// <summary>This PC's key for <paramref name="serverUrl"/>, or null if it has none (or it can't be read).</summary>
    public static string? Load(string serverUrl, string? path = null)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<WorkstationKey>(File.ReadAllText(path ?? DefaultPath), TapQueueJson.Options);
            return saved is { Key.Length: > 0 } &&
                   string.Equals(saved.ServerUrl.TrimEnd('/'), serverUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? saved.Key
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.Delete(temp);
        using (var file = OperatingSystem.IsWindows()
            ? CreatePrivateWindowsFile(temp)
            : new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            JsonSerializer.Serialize(file, this, TapQueueJson.Options);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// %ProgramData%\TapQueue lets every user read what's in it, so the file gets its own access list:
    /// SYSTEM and administrators only, nothing inherited.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream CreatePrivateWindowsFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }
}
