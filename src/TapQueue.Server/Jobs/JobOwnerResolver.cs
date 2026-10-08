using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Jobs;

/// <summary>
/// Decides which TapQueue user a print job belongs to.
///
/// The requesting-user-name that Windows puts in an IPP job is just a string and can't be trusted
/// on its own. A job sent through a printer that a TapQueue service (0.8 on) added carries that PC's
/// key, so the server knows which PC it came from; the PC's spooler fills in the user name from
/// whoever printed, and the PC's service has vouched for which PC user runs each tray app there. The
/// job goes to the session whose PC user printed it, and to nobody if that user isn't signed in.
///
/// Keyless jobs (printers added by older clients) fall back to the old way, unless an admin turned
/// <see cref="AddressMatching"/> off: a signed-in user client on the address the job came from, with
/// the username only picking between several sessions on one address (e.g. a terminal server), or,
/// in dev mode, as a fallback when no client is running. Not with domain sign-in, where the PC's
/// user name says nothing about who signed in to TapQueue.
/// </summary>
public sealed class JobOwnerResolver(SessionStore sessions, UserStore users, ServerConfig config, ServerSettings settings, DirectoryStore directory)
{
    /// <param name="workstation">The PC whose keyed printer the job came through, or null for a keyless job.</param>
    public (long? UserId, string? OwnerHint) Resolve(string sourceIp, string? requestingUserName, string? workstation = null)
    {
        var windowsUser = NormalizeWindowsUser(requestingUserName);
        var owner = workstation is not null ? FromWorkstation(workstation, windowsUser)
            : settings.AddressMatching == AddressMatching.On ? FromAddress(sourceIp, windowsUser)
            : null;
        if (owner is not null)
            return (owner, windowsUser);

        if (config.Auth.Mode == "dev" && windowsUser is not null && !directory.Config().DomainSignIn && users.FindByUsername(windowsUser) is { } user)
            return (user.Id, windowsUser);

        return (null, windowsUser);
    }

    /// <summary>
    /// The session on that PC run by the PC user who printed. The name has to match even when only
    /// one person is signed in there: on a shared PC, someone without the tray app running mustn't
    /// have their jobs go to whoever has it. Only a job with no user name at all (which Windows and
    /// CUPS always fill in) goes to the one person signed in.
    /// </summary>
    private long? FromWorkstation(string workstation, string? windowsUser)
    {
        var active = sessions.ActiveOnWorkstation(workstation, settings.SessionTimeout);
        if (windowsUser is null)
            return active.Select(s => s.UserId).Distinct().ToList() is [var only] ? only : null;
        // Newest first, so someone who signed in again as another TapQueue user gets the new one.
        return active.FirstOrDefault(s => string.Equals(NormalizeWindowsUser(s.PcUser), windowsUser, StringComparison.OrdinalIgnoreCase))?.UserId;
    }

    private long? FromAddress(string sourceIp, string? windowsUser)
    {
        var active = sessions.ActiveForIp(sourceIp, settings.SessionTimeout);

        var distinctUsers = active.Select(s => s.UserId).Distinct().ToList();
        if (distinctUsers.Count == 1)
            return distinctUsers[0];

        if (distinctUsers.Count > 1 && windowsUser is not null)
            return active.FirstOrDefault(s => string.Equals(NormalizeWindowsUser(s.WindowsUser), windowsUser, StringComparison.OrdinalIgnoreCase))?.UserId;

        return null;
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
