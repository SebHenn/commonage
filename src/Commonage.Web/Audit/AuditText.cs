using Commonage.Web.Domain;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Audit;

/// <summary>What an entry of the activity log is about, for its filter.</summary>
public enum AuditKind
{
    Items,
    Photos,
    Contributions,
    Collections,
    Tags,
    Accounts,
    Archive,
}

/// <summary>How the activity log describes each action, in the reader's language.</summary>
public static class AuditText
{
    public static string Describe(IStringLocalizer localizer, AuditAction action) => action switch
    {
        AuditAction.ItemCreated => localizer["Created the item"],
        AuditAction.ItemChanged => localizer["Changed the item"],
        AuditAction.ItemPublished => localizer["Published the item"],
        AuditAction.ItemUnpublished => localizer["Unpublished the item"],
        AuditAction.ItemDeleted => localizer["Deleted the item for good"],
        AuditAction.ItemMovedToBin => localizer["Moved the item to the bin"],
        AuditAction.ItemRestored => localizer["Restored the item from the bin"],
        AuditAction.ItemPurged => localizer["Deleted the item for good after its time in the bin"],
        AuditAction.PhotosAdded => localizer["Added photos"],
        AuditAction.PhotosArranged => localizer["Changed the order or direction of photos"],
        AuditAction.PhotoDeleted => localizer["Deleted a photo for good"],
        AuditAction.PhotoMovedToBin => localizer["Moved a photo to the bin"],
        AuditAction.PhotoRestored => localizer["Restored a photo from the bin"],
        AuditAction.PhotoPurged => localizer["Deleted a photo for good after its time in the bin"],
        AuditAction.ContributionReceived => localizer["Contribution received"],
        AuditAction.ContributionAccepted => localizer["Accepted the contribution as a draft"],
        AuditAction.ContributionRejected => localizer["Rejected the contribution"],
        AuditAction.ContributionWithdrawn => localizer["Marked the contribution as withdrawn"],
        AuditAction.ContributorDetailsRemoved => localizer["Removed the contributor's name and email address"],
        AuditAction.CollectionCreated => localizer["Created the collection"],
        AuditAction.CollectionChanged => localizer["Changed the collection"],
        AuditAction.ContributionLinkCreated => localizer["Created the contribution link"],
        AuditAction.ContributionLinkChanged => localizer["Changed the contribution link"],
        AuditAction.AccountCreated => localizer["Created the account"],
        AuditAction.AccountRoleChanged => localizer["Changed the account's role"],
        AuditAction.AccountDisabled => localizer["Disabled the account"],
        AuditAction.AccountEnabled => localizer["Enabled the account"],
        AuditAction.AccountDeleted => localizer["Deleted the account"],
        AuditAction.SetupLinkCreated => localizer["Created a setup link for the account"],
        AuditAction.PasswordChanged => localizer["Changed their password"],
        AuditAction.ArchiveExported => localizer["Exported the archive"],
        AuditAction.ArchiveExportedWithPersonalData => localizer["Exported the archive with contributors' personal data"],
        AuditAction.SettingsChanged => localizer["Changed the archive settings"],
        AuditAction.RestoreTested => localizer["Noted that a restore was tested"],
        AuditAction.ContributorDetailsChanged => localizer["Corrected the contributor's details"],
        AuditAction.TwoFactorEnabled => localizer["Started logging in with an authenticator app"],
        AuditAction.TwoFactorDisabled => localizer["Stopped logging in with an authenticator app"],
        AuditAction.TwoFactorTurnedOff => localizer["Turned off the account's authenticator app"],
        AuditAction.RecoveryCodesCreated => localizer["Created new recovery codes"],
        AuditAction.RecoveryCodeUsed => localizer["Logged in with a recovery code"],
        AuditAction.ItemMovedToBinAfterKeptTime => localizer["Moved the rejected or withdrawn contribution to the bin, at the end of the time the archive keeps them"],
        AuditAction.ActivityLogShortened => localizer["Deleted activity log entries older than the archive keeps them"],
        AuditAction.PreviewRetried => localizer["Asked for a failed preview to be made again"],
        AuditAction.TagChanged => localizer["Changed the tag"],
        AuditAction.TagsMerged => localizer["Merged two tags into one"],
        AuditAction.TagDeleted => localizer["Deleted the unused tag"],
        AuditAction.UnusedTagsDeleted => localizer["Deleted all unused tags"],
        _ => action.ToString(),
    };

    public static AuditKind KindOf(AuditAction action) => action switch
    {
        AuditAction.ItemCreated or AuditAction.ItemChanged or AuditAction.ItemPublished or AuditAction.ItemUnpublished
            or AuditAction.ItemDeleted or AuditAction.ItemMovedToBin or AuditAction.ItemRestored or AuditAction.ItemPurged
            or AuditAction.ItemMovedToBinAfterKeptTime => AuditKind.Items,
        AuditAction.PhotosAdded or AuditAction.PhotosArranged or AuditAction.PhotoDeleted or AuditAction.PhotoMovedToBin
            or AuditAction.PhotoRestored or AuditAction.PhotoPurged or AuditAction.PreviewRetried => AuditKind.Photos,
        AuditAction.ContributionReceived or AuditAction.ContributionAccepted or AuditAction.ContributionRejected
            or AuditAction.ContributionWithdrawn or AuditAction.ContributorDetailsRemoved or AuditAction.ContributorDetailsChanged
            or AuditAction.ContributionLinkCreated or AuditAction.ContributionLinkChanged => AuditKind.Contributions,
        AuditAction.CollectionCreated or AuditAction.CollectionChanged => AuditKind.Collections,
        AuditAction.TagChanged or AuditAction.TagsMerged or AuditAction.TagDeleted or AuditAction.UnusedTagsDeleted => AuditKind.Tags,
        AuditAction.AccountCreated or AuditAction.AccountRoleChanged or AuditAction.AccountDisabled or AuditAction.AccountEnabled
            or AuditAction.AccountDeleted or AuditAction.SetupLinkCreated or AuditAction.PasswordChanged
            or AuditAction.TwoFactorEnabled or AuditAction.TwoFactorDisabled or AuditAction.TwoFactorTurnedOff
            or AuditAction.RecoveryCodesCreated or AuditAction.RecoveryCodeUsed => AuditKind.Accounts,
        AuditAction.ArchiveExported or AuditAction.ArchiveExportedWithPersonalData or AuditAction.SettingsChanged
            or AuditAction.RestoreTested or AuditAction.ActivityLogShortened => AuditKind.Archive,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Every action needs a kind, for the activity log's filter."),
    };

    public static string Describe(IStringLocalizer localizer, AuditKind kind) => kind switch
    {
        AuditKind.Items => localizer["Items"],
        AuditKind.Photos => localizer["Photos"],
        AuditKind.Contributions => localizer["Contributions and links"],
        AuditKind.Collections => localizer["Collections"],
        AuditKind.Tags => localizer["Tags"],
        AuditKind.Accounts => localizer["Accounts and logins"],
        AuditKind.Archive => localizer["Export, settings and the log itself"],
        _ => kind.ToString(),
    };

    /// <summary>Who did it: the account's name, a contributor, or the archive itself.</summary>
    public static string Who(IStringLocalizer localizer, AuditEntry entry) =>
        entry.UserName ?? (entry.Action == AuditAction.ContributionReceived ? localizer["Contributor"] : localizer["Automatic"]);
}
