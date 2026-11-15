namespace Commonage.Web.Identity;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Curator = "Curator";

    /// <summary>Describes items and adds photos, nothing else: no publishing, no bin, no contributors, no contributions to review.</summary>
    public const string Helper = "Helper";

    public static readonly IReadOnlyList<string> All = [Helper, Curator, Admin];

    /// <summary>Admins and curators, who may do everything a helper may and more.</summary>
    public static bool MayManage(this System.Security.Claims.ClaimsPrincipal user) => user.IsInRole(Admin) || user.IsInRole(Curator);
}

public static class Policies
{
    /// <summary>Everyone with an account here may describe items and add photos.</summary>
    public const string Describe = "Describe";

    /// <summary>Admins and curators may manage the archive.</summary>
    public const string ManageArchive = "ManageArchive";

    /// <summary>Only admins manage accounts, export the archive and read the activity log.</summary>
    public const string Administer = "Administer";
}
