namespace Commonage.Web.Identity;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Curator = "Curator";
}

public static class Policies
{
    /// <summary>Admins and curators may manage the archive.</summary>
    public const string ManageArchive = "ManageArchive";
}
