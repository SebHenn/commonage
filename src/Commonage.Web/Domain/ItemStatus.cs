namespace Commonage.Web.Domain;

public enum ItemStatus
{
    Draft = 0,
    Published = 1,

    /// <summary>Sent in through a contribution link and waiting for a curator. Never public.</summary>
    Submitted = 2,

    /// <summary>A contribution a curator decided not to include. Kept, never public, until it is deleted.</summary>
    Rejected = 3,

    /// <summary>
    /// The contributor withdrew their consent. Never public, and cannot be published again without new consent.
    /// Kept until a curator deletes it, so the archive can see what was withdrawn.
    /// </summary>
    Withdrawn = 4,
}
