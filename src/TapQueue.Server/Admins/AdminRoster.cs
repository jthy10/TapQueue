using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Config;
using TapQueue.Server.Data;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Admins;

/// <summary>A change to check before making it: a user gone (deleted or disabled), a group gone, or one membership gone.</summary>
public sealed record RosterChange(long? UserGone = null, string? GroupGone = null, (long UserId, string GroupId)? MembershipGone = null);

/// <summary>
/// Everyone the grants give a role to, worked out from a list of grants, so a change can be checked
/// before it's made: in token mode, the last full admin who can sign in can't be removed.
/// </summary>
public sealed class AdminRoster(UserStore users, GroupStore groups, DirectoryStore directory, AdminStore admins, ServerConfig config)
{
    public List<AdminPersonDto> People(IReadOnlyList<AdminGrant> grants, RosterChange? change = null)
    {
        var memberships = groups.Memberships();
        var adConfigured = directory.Config() is { Host.Length: > 0, Password.Length: > 0 };
        var people = new List<AdminPersonDto>();
        foreach (var user in users.List())
        {
            if (user.Id == change?.UserGone)
                continue;
            var mine = (memberships.GetValueOrDefault(user.Id) ?? [])
                .Where(g => g != change?.GroupGone && change?.MembershipGone != (user.Id, g)).ToList();
            var applying = grants.Where(g => g.UserId == user.Id || (g.GroupId is not null && mine.Contains(g.GroupId))).ToList();
            if (applying.Count == 0)
                continue;
            var permissions = AdminPermissions.From(applying.Select(g => (g.Area, g.Role)));
            var via = applying.Select(g => g.UserId is not null ? "direct" : g.GroupName ?? g.GroupId!).Distinct()
                .OrderBy(v => v != "direct").ToList();
            people.Add(new AdminPersonDto(user.Username, user.DisplayName, user.Source, permissions.Roles, permissions.IsFullAdmin, via,
                SignInProblem(user, adConfigured)));
        }
        return people;
    }

    /// <summary>Full admins who could sign in now (or after <paramref name="change"/>).</summary>
    public int FullAdmins(IReadOnlyList<AdminGrant> grants, RosterChange? change = null) =>
        People(grants, change).Count(p => p.FullAdmin && p.SignInProblem is null);

    /// <summary>
    /// Outside dev mode the console needs someone who can sign in as a full admin; without one only
    /// admin.token (or going back to dev mode) could manage admins. True if the change would take away the last.
    /// </summary>
    public bool WouldLockOut(RosterChange change) => WouldLockOut(admins.Grants(), null, change);

    public bool WouldLockOut(List<AdminGrant> before, List<AdminGrant>? after, RosterChange? change = null) =>
        config.Auth.Mode != "dev" && FullAdmins(before) > 0 && FullAdmins(after ?? before, change) == 0;

    public const string LockOutMessage = "That would leave no full admin who can sign in. Make someone else a full admin first.";

    public static string? SignInProblem(UserRecord user, bool adConfigured) =>
        user.Disabled ? "Their account is disabled."
        : user.FromDirectory ? (adConfigured ? null : "They sign in with their domain password, but Active Directory isn't set up.")
        : user.HasPassword ? null
        : "They have no password yet. Set one on their user page.";
}
