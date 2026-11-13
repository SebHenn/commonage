namespace Commonage.Web.Domain;

/// <summary>How a group sets up its archive. There is one row, created when an admin first saves the settings.</summary>
public class ArchiveSettings
{
    public const int TheOnlyId = 1;

    public int Id { get; set; } = TheOnlyId;

    /// <summary>For visitors whose browser asks for no language this archive offers, and the language its own texts are in.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>
    /// The language most of the material is written in, or <see cref="DateLanguages.Several"/>. Null until an admin chooses,
    /// and then dates are read in English and German, as they were before this setting.
    /// </summary>
    public string? ContentLanguage { get; set; }

    /// <summary>Other languages some of the material is in, separated by commas, so that dates written in them are read too.</summary>
    public string? OtherContentLanguages { get; set; }

    /// <summary>A colour such as #1a5f7a, or null for the standard one.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Which logo is current. A new logo gets a new id, so browsers cannot keep showing the old one.</summary>
    public Guid? LogoId { get; set; }

    /// <summary>An IANA time zone such as Europe/Berlin, or null for the server's clock.</summary>
    public string? TimeZone { get; set; }

    public bool AllowSearchEngines { get; set; } = true;

    /// <summary>Curators and admins must log in with an authenticator app as well as their password.</summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>Visitors can open photos at about 3000 pixels, to read a letter or look closer. Archives from before this existed start without.</summary>
    public bool LargePhotos { get; set; } = true;

    /// <summary>What applies to items without a statement of their own, see <see cref="RightsOptions"/>.</summary>
    public string? DefaultRightsUri { get; set; }

    /// <summary>An admin has read the privacy text and found it right for this archive, even if it stays as it came.</summary>
    public bool PrivacyReviewed { get; set; }

    /// <summary>The group needs no imprint, which depends on the country it is in.</summary>
    public bool ImprintNotNeeded { get; set; }

    public DateTime? RestoreTestedAt { get; set; }

    /// <summary>Activity log entries older than this many years are deleted. Null keeps them all, as before this setting.</summary>
    public int? ActivityLogKeptYears { get; set; }

    /// <summary>
    /// Rejected and withdrawn contributions go to the bin this many months after the decision, and are gone for good 30 days
    /// later. They hold a contributor's name and photos that will never be published. Null keeps them until a curator deletes them.
    /// </summary>
    public int? RejectedKeptMonths { get; set; }

    /// <summary>Whether published items ask visitors "Do you know more about this?". Off until a curator turns it on.</summary>
    public bool AcceptVisitorNotes { get; set; }

    public static readonly int[] ActivityLogYearChoices = [1, 2, 5, 10];
    public static readonly int[] RejectedMonthChoices = [3, 6, 12, 24];
}

public enum ArchiveTextKind
{
    Name,
    Tagline,
    Intro,
    About,
    Contact,
    Privacy,
    Imprint,
}

/// <summary>A text of the archive itself, in one language. Written by the group, so stored as typed, not translated.</summary>
public class ArchiveText
{
    public const int MaxLength = 20000;

    public required string Language { get; set; }
    public ArchiveTextKind Kind { get; set; }
    public required string Text { get; set; }
}
