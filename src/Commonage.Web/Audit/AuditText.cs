using Commonage.Web.Domain;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Audit;

/// <summary>How the activity log describes each action, in the reader's language.</summary>
public static class AuditText
{
    public static string Describe(IStringLocalizer localizer, AuditAction action) => action switch
    {
        AuditAction.ItemCreated => localizer["Created the item"],
        AuditAction.ItemChanged => localizer["Changed the item"],
        AuditAction.ItemPublished => localizer["Published the item"],
        AuditAction.ItemUnpublished => localizer["Unpublished the item"],
        AuditAction.ItemDeleted => localizer["Deleted the item"],
        AuditAction.PhotosAdded => localizer["Added photos"],
        AuditAction.PhotoDeleted => localizer["Deleted a photo"],
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
        _ => action.ToString(),
    };
}
