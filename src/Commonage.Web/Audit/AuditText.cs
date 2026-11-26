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
    Stories,
    People,
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
        AuditAction.ItemMediaLevelChanged => localizer["Changed what visitors get of the item"],
        AuditAction.ItemPublicFromChanged => localizer["Changed the date from which the item is public"],
        AuditAction.ItemFieldRestored => localizer["Restored an earlier text of the item"],
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
        AuditAction.CollectionMoved => localizer["Changed the order of the collections"],
        AuditAction.CollectionDeleted => localizer["Deleted the empty collection"],
        AuditAction.StoryCreated => localizer["Created the story"],
        AuditAction.StoryChanged => localizer["Changed the story"],
        AuditAction.StoryPublished => localizer["Published the story"],
        AuditAction.StoryUnpublished => localizer["Unpublished the story"],
        AuditAction.StoryItemAdded => localizer["Added an item to the story"],
        AuditAction.StoryItemRemoved => localizer["Removed an item from the story"],
        AuditAction.StoryBlocksArranged => localizer["Changed the order of the parts of the story"],
        AuditAction.StoriesArranged => localizer["Changed the order of the stories"],
        AuditAction.StoryMovedToBin => localizer["Moved the story to the bin"],
        AuditAction.StoryRestored => localizer["Took the story out of the bin"],
        AuditAction.StoryDeleted => localizer["Deleted the story for good"],
        AuditAction.StoryPurged => localizer["Deleted the story after its time in the bin"],
        AuditAction.PersonCreated => localizer["Created the person"],
        AuditAction.PersonChanged => localizer["Changed the person"],
        AuditAction.PersonDisclosureChanged => localizer["Changed who may see the person"],
        AuditAction.PersonNameAdded => localizer["Added another name to the person"],
        AuditAction.PersonNameRemoved => localizer["Removed another name from the person"],
        AuditAction.PersonLinked => localizer["Linked the person to an item"],
        AuditAction.PersonUnlinked => localizer["Took the person off an item"],
        AuditAction.PeopleMerged => localizer["Merged two people"],
        AuditAction.PersonItemsMoved => localizer["Moved items from one person to another"],
        AuditAction.PersonMovedToBin => localizer["Moved the person to the bin"],
        AuditAction.PersonRestored => localizer["Took the person out of the bin"],
        AuditAction.PersonDeleted => localizer["Deleted the person for good"],
        AuditAction.PersonPurged => localizer["Deleted the person after their time in the bin"],
        AuditAction.PersonTagsConverted => localizer["Made people from tags"],
        AuditAction.PeopleImported => localizer["Created people while importing"],
        AuditAction.VisitorNoteReceived => localizer["A visitor sent a note about the item"],
        AuditAction.VisitorNoteHandled => localizer["Marked a visitor's note as dealt with"],
        AuditAction.VisitorNoteReopened => localizer["Opened a visitor's note again"],
        AuditAction.VisitorNoteDetailsRemoved => localizer["Removed the name and email address from a visitor's note"],
        AuditAction.VisitorNoteDeleted => localizer["Deleted a visitor's note"],
        AuditAction.ContributionLinkCreated => localizer["Created the contribution link"],
        AuditAction.ContributionLinkChanged => localizer["Changed the contribution link"],
        AuditAction.AccountCreated => localizer["Created the account"],
        AuditAction.AccountRoleChanged => localizer["Changed the account's role"],
        AuditAction.AccountNameChanged => localizer["Changed the name of an account"],
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
        AuditAction.ItemsImported => localizer["Imported items from a spreadsheet"],
        AuditAction.ArchiveImported => localizer["Imported items from a Commonage export"],
        AuditAction.CollectionCreatedByImport => localizer["Created the collection while importing"],
        AuditAction.ImportPhotosMissing => localizer["Could not add every photo of this item while importing"],
        _ => action.ToString(),
    };

    public static AuditKind KindOf(AuditAction action) => action switch
    {
        AuditAction.ItemCreated or AuditAction.ItemChanged or AuditAction.ItemFieldRestored or AuditAction.ItemPublished or AuditAction.ItemUnpublished or AuditAction.ItemMediaLevelChanged or AuditAction.ItemPublicFromChanged
            or AuditAction.ItemDeleted or AuditAction.ItemMovedToBin or AuditAction.ItemRestored or AuditAction.ItemPurged
            or AuditAction.ItemMovedToBinAfterKeptTime or AuditAction.ItemsImported or AuditAction.ArchiveImported => AuditKind.Items,
        AuditAction.PhotosAdded or AuditAction.PhotosArranged or AuditAction.PhotoDeleted or AuditAction.PhotoMovedToBin
            or AuditAction.PhotoRestored or AuditAction.PhotoPurged or AuditAction.PreviewRetried or AuditAction.ImportPhotosMissing => AuditKind.Photos,
        AuditAction.ContributionReceived or AuditAction.ContributionAccepted or AuditAction.ContributionRejected
            or AuditAction.ContributionWithdrawn or AuditAction.ContributorDetailsRemoved or AuditAction.ContributorDetailsChanged
            or AuditAction.ContributionLinkCreated or AuditAction.ContributionLinkChanged
            or AuditAction.VisitorNoteReceived or AuditAction.VisitorNoteHandled or AuditAction.VisitorNoteReopened
            or AuditAction.VisitorNoteDetailsRemoved or AuditAction.VisitorNoteDeleted => AuditKind.Contributions,
        AuditAction.CollectionCreated or AuditAction.CollectionChanged or AuditAction.CollectionMoved
            or AuditAction.CollectionDeleted or AuditAction.CollectionCreatedByImport => AuditKind.Collections,
        AuditAction.StoryCreated or AuditAction.StoryChanged or AuditAction.StoryPublished or AuditAction.StoryUnpublished
            or AuditAction.StoryItemAdded or AuditAction.StoryItemRemoved or AuditAction.StoryBlocksArranged or AuditAction.StoriesArranged
            or AuditAction.StoryMovedToBin or AuditAction.StoryRestored or AuditAction.StoryDeleted or AuditAction.StoryPurged => AuditKind.Stories,
        AuditAction.PersonCreated or AuditAction.PersonChanged or AuditAction.PersonDisclosureChanged or AuditAction.PersonNameAdded
            or AuditAction.PersonNameRemoved or AuditAction.PersonLinked or AuditAction.PersonUnlinked or AuditAction.PeopleMerged
            or AuditAction.PersonItemsMoved or AuditAction.PersonMovedToBin or AuditAction.PersonRestored or AuditAction.PersonDeleted
            or AuditAction.PersonPurged or AuditAction.PersonTagsConverted or AuditAction.PeopleImported => AuditKind.People,
        AuditAction.TagChanged or AuditAction.TagsMerged or AuditAction.TagDeleted or AuditAction.UnusedTagsDeleted => AuditKind.Tags,
        AuditAction.AccountCreated or AuditAction.AccountRoleChanged or AuditAction.AccountNameChanged or AuditAction.AccountDisabled or AuditAction.AccountEnabled
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
        AuditKind.Contributions => localizer["Contributions, links and notes from visitors"],
        AuditKind.Collections => localizer["Collections"],
        AuditKind.Stories => localizer["Stories"],
        AuditKind.People => localizer["People"],
        AuditKind.Tags => localizer["Tags"],
        AuditKind.Accounts => localizer["Accounts and logins"],
        AuditKind.Archive => localizer["Export, settings and the log itself"],
        _ => kind.ToString(),
    };

    /// <summary>Who did it: the account's name, a contributor, or the archive itself.</summary>
    public static string Who(IStringLocalizer localizer, AuditEntry entry) =>
        entry.UserName ?? entry.Action switch
        {
            AuditAction.ContributionReceived => localizer["Contributor"],
            AuditAction.VisitorNoteReceived => localizer["Visitor"],
            _ => localizer["Automatic"],
        };
}
