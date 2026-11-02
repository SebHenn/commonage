namespace Commonage.Web.Identity;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Curator = "Curator";

    public static readonly IReadOnlyList<string> All = [Curator, Admin];
}

public static class Policies
{
    /// <summary>Admins and curators may manage the archive.</summary>
    public const string ManageArchive = "ManageArchive";

    /// <summary>Only admins manage accounts, export the archive and read the activity log.</summary>
    public const string Administer = "Administer";
}
