namespace Commonage.Web.Domain;

/// <summary>How a group sets up its archive. There is one row, created when an admin first saves the settings.</summary>
public class ArchiveSettings
{
    public const int TheOnlyId = 1;

    public int Id { get; set; } = TheOnlyId;

    /// <summary>For visitors whose browser asks for no language this archive offers, and the language its own texts are in.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>A colour such as #1a5f7a, or null for the standard one.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Which logo is current. A new logo gets a new id, so browsers cannot keep showing the old one.</summary>
    public Guid? LogoId { get; set; }

    /// <summary>An IANA time zone such as Europe/Berlin, or null for the server's clock.</summary>
    public string? TimeZone { get; set; }

    public bool AllowSearchEngines { get; set; } = true;

    /// <summary>An admin has read the privacy text and found it right for this archive, even if it stays as it came.</summary>
    public bool PrivacyReviewed { get; set; }

    /// <summary>The group needs no imprint, which depends on the country it is in.</summary>
    public bool ImprintNotNeeded { get; set; }

    public DateTime? RestoreTestedAt { get; set; }
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
