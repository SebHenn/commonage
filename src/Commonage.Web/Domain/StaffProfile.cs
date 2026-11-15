namespace Commonage.Web.Domain;

/// <summary>
/// What the app knows about a curator, helper or admin besides what Identity keeps: a name to show, and when they last
/// logged in. Staff only, and only on the accounts pages an admin sees; never on a public page, in a feed or in the export.
/// It lives in its own table so that Identity's own tables stay as they are.
/// </summary>
public class StaffProfile
{
    public const int DisplayNameMaxLength = 100;

    /// <summary>The id of the Identity account. The profile goes when the account does.</summary>
    public required string UserId { get; set; }

    public string? DisplayName { get; set; }

    public DateTime? LastLoginAt { get; set; }
}
