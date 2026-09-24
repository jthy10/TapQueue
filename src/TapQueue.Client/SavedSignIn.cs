using System.Text.Json;
using TapQueue.Shared;

namespace TapQueue.Client;

/// <summary>
/// A remembered domain sign-in (<see cref="Shared.Api.ClientSignIn.Domain"/>), kept in the PC user's own
/// profile so it outlives restarts until they sign out. It holds the token the server handed out, never
/// the password. Windows: %LocalAppData%\TapQueue\sign-in.json. Linux: ~/.config/tapqueue/sign-in.json,
/// readable only by the user.
/// </summary>
/// <param name="ServerUrl">The server it's for; a PC pointed at another server starts signed out.</param>
public sealed record SavedSignIn(string ServerUrl, string Username, string RememberToken)
{
    public static string DefaultPath { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TapQueue", "sign-in.json")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "tapqueue", "sign-in.json");

    /// <summary>The saved sign-in for <paramref name="serverUrl"/>, or null if there's none (or it can't be read).</summary>
    public static SavedSignIn? Load(string serverUrl, string? path = null)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedSignIn>(File.ReadAllText(path ?? DefaultPath), TapQueueJson.Options);
            return saved is { Username.Length: > 0, RememberToken.Length: > 0 } && SameServer(saved.ServerUrl, serverUrl) ? saved : null;
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
            ? new FileStream(temp, FileMode.CreateNew, FileAccess.Write)
            : new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            JsonSerializer.Serialize(file, this, TapQueueJson.Options);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        try
        {
            File.Delete(path ?? DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool SameServer(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
