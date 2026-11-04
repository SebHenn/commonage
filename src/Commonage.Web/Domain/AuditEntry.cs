namespace Commonage.Web.Domain;

/// <summary>What happened, recorded in the activity log. Stored by name, so new actions can be added anywhere in the list.</summary>
public enum AuditAction
{
    ItemCreated,
    ItemChanged,
    ItemPublished,
    ItemUnpublished,
    ItemDeleted,
    PhotosAdded,
    PhotoDeleted,
    ContributionReceived,
    ContributionAccepted,
    ContributionRejected,
    ContributionWithdrawn,
    ContributorDetailsRemoved,
    CollectionCreated,
    CollectionChanged,
    ContributionLinkCreated,
    ContributionLinkChanged,
    AccountCreated,
    AccountRoleChanged,
    AccountDisabled,
    AccountEnabled,
    AccountDeleted,
    SetupLinkCreated,
    PasswordChanged,
    ArchiveExported,
    ArchiveExportedWithPersonalData,
    ItemMovedToBin,
    ItemRestored,
    ItemPurged,
    PhotoMovedToBin,
    PhotoRestored,
    PhotoPurged,
}

/// <summary>
/// One line of the activity log: who did what, and when. Entries are never changed or deleted by the app.
/// They hold no contributor details, so removing a contributor's personal data never has to touch the log.
/// </summary>
public class AuditEntry
{
    public const int SubjectMaxLength = 300;

    public long Id { get; set; }
    public DateTime At { get; set; }

    /// <summary>The account that acted, or null for contributors and for what the archive does by itself.</summary>
    public string? UserId { get; set; }

    /// <summary>The account's name at the time, kept when the account is deleted later.</summary>
    public string? UserName { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>The item concerned, if any. Set to null when the item is deleted; <see cref="Subject"/> keeps its title.</summary>
    public int? ArchiveItemId { get; set; }
    public ArchiveItem? ArchiveItem { get; set; }

    /// <summary>What the action was about, as it was called at the time: an item's or collection's title, an account name.</summary>
    public string? Subject { get; set; }
}
