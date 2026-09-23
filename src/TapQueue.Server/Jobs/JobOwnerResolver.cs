using TapQueue.Server.Config;
using TapQueue.Server.Data;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Decides which TapQueue user a print job belongs to.
///
/// The requesting-user-name that Windows puts in an IPP job is just a string and can't be trusted,
/// so the primary signal is a signed-in user client on the machine the job came from. The
/// username is only used to pick between several sessions on one machine (e.g. a terminal
/// server), or, in dev mode, as a fallback when no client is running.
/// </summary>
public sealed class JobOwnerResolver(SessionStore sessions, UserStore users, ServerConfig config)
{
    public (long? UserId, string? OwnerHint) Resolve(string sourceIp, string? requestingUserName)
    {
        var windowsUser = NormalizeWindowsUser(requestingUserName);
        var active = sessions.ActiveForIp(sourceIp, TimeSpan.FromMinutes(config.Auth.SessionTimeoutMinutes));

        var distinctUsers = active.Select(s => s.UserId).Distinct().ToList();
        if (distinctUsers.Count == 1)
            return (distinctUsers[0], windowsUser);

        if (distinctUsers.Count > 1 && windowsUser is not null)
        {
            var match = active.FirstOrDefault(s => string.Equals(NormalizeWindowsUser(s.WindowsUser), windowsUser, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return (match.UserId, windowsUser);
        }

        if (config.Auth.Mode == "dev" && windowsUser is not null && users.FindByUsername(windowsUser) is { } user)
            return (user.Id, windowsUser);

        return (null, windowsUser);
    }

    /// <summary>"CORP\jake" and "jake@corp.local" both become "jake".</summary>
    public static string? NormalizeWindowsUser(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        var slash = n.LastIndexOf('\\');
        if (slash >= 0) n = n[(slash + 1)..];
        var at = n.IndexOf('@');
        if (at > 0) n = n[..at];
        return n.Length == 0 ? null : n;
    }
}
