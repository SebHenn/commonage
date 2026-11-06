namespace Commonage.Web.Domain;

public enum RightsStatement
{
    /// <summary>The contributor created the material, for example took the photos.</summary>
    CreatedByContributor = 0,

    /// <summary>Someone else created it and the contributor has their permission or inherited the rights.</summary>
    PermissionFromRightsHolder = 1,

    Unknown = 2,
}

/// <summary>
/// Who sent in an archive item through a contribution link, and what they agreed to. Only curators see it,
/// except the contributor's name on the public page when <see cref="ShowName"/> is set.
/// </summary>
public class Contribution
{
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 254;
    public const int RightsHolderMaxLength = 300;
    public const int ReferenceCodeLength = 9;
    public const int SendingFingerprintLength = 64;

    /// <summary>
    /// Recognises the same form sent again, for example after the connection dropped before the answer came, so it does not
    /// arrive twice. A hash that holds nothing about the contributor. See <see cref="Contributions.SendingFingerprints"/>.
    /// </summary>
    public string? SendingFingerprint { get; set; }

    /// <summary>
    /// What a contributor can quote when they ask about what they sent, since they have no account. Random, so it reveals
    /// nothing, and without letters that are easily mixed up.
    /// </summary>
    public string? ReferenceCode { get; set; }

    public static string NewReferenceCode()
    {
        var code = System.Security.Cryptography.RandomNumberGenerator.GetString("23456789ABCDEFGHJKMNPQRSTVWXYZ", 8);
        return $"{code[..4]}-{code[4..]}";
    }

    public int Id { get; set; }

    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;

    /// <summary>The link the contribution came through. Null for contributions recorded without a link.</summary>
    public int? ContributionLinkId { get; set; }
    public ContributionLink? ContributionLink { get; set; }

    /// <summary>Null once a curator removed the contributor's details at their request.</summary>
    public string? ContributorName { get; set; }

    /// <summary>Optional. Only used by curators to ask about this contribution.</summary>
    public string? ContributorEmail { get; set; }

    /// <summary>Whether the contributor wants to be credited by name on the public page.</summary>
    public bool ShowName { get; set; }

    public RightsStatement Rights { get; set; }

    /// <summary>Who took the photos or wrote the text, if the contributor knows and it is not them.</summary>
    public string? RightsHolder { get; set; }

    /// <summary>The consent statement exactly as the contributor saw and accepted it, in their language.</summary>
    public required string ConsentText { get; set; }

    public DateTime SubmittedAt { get; set; }

    /// <summary>When a curator first published, accepted or rejected the contribution.</summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>When a curator removed the contributor's name and email address, usually because they asked for it.</summary>
    public DateTime? PersonalDetailsRemovedAt { get; set; }

    /// <summary>
    /// Removes what identifies the contributor: name and email address. The rights statement, the photographer or
    /// author and the consent text stay, because the archive needs them to keep using the material.
    /// </summary>
    public void RemovePersonalDetails(DateTime now)
    {
        ContributorName = null;
        ContributorEmail = null;
        ShowName = false;
        PersonalDetailsRemovedAt ??= now;
    }
}
