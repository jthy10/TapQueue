using TapQueue.Shared.Api;

namespace TapQueue.Server.Admins;

/// <summary>
/// The role someone has in each admin area (<see cref="AdminArea"/>): the highest one any of their
/// grants, direct or through a group, gives there.
/// </summary>
public sealed class AdminPermissions
{
    private readonly Dictionary<string, string> _roles;

    private AdminPermissions(Dictionary<string, string> roles) => _roles = roles;

    public static readonly AdminPermissions None = new([]);

    /// <summary>Dev mode's open console and the admin token: admin everywhere.</summary>
    public static readonly AdminPermissions Full = From(AdminArea.Each.Select(a => (a, AdminRole.Admin)));

    /// <summary>Adds up grants given as (area, role); an area of <see cref="AdminArea.All"/> counts for every area.</summary>
    public static AdminPermissions From(IEnumerable<(string Area, string Role)> grants)
    {
        var roles = new Dictionary<string, string>();
        foreach (var (area, role) in grants)
            foreach (var each in area == AdminArea.All ? AdminArea.Each : [area])
                if (AdminRole.Rank(role) > AdminRole.Rank(roles.GetValueOrDefault(each)))
                    roles[each] = role;
        return new AdminPermissions(roles);
    }

    /// <summary>Area to role, for the areas they have a role in.</summary>
    public IReadOnlyDictionary<string, string> Roles => _roles;

    public bool Any => _roles.Count > 0;

    /// <summary>Admin in every area. Only full admins manage admins and passwords, so nobody can give themselves more.</summary>
    public bool IsFullAdmin => AdminArea.Each.All(a => _roles.GetValueOrDefault(a) == AdminRole.Admin);

    public bool Allows(string area, string role) => AdminRole.Rank(_roles.GetValueOrDefault(area)) >= AdminRole.Rank(role);
}
