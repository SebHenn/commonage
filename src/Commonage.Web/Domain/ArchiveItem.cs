using Microsoft.Extensions.Localization;

namespace Commonage.Web.Domain;

/// <summary>
/// The central record of the archive. A photograph, a letter or an oral history are all archive items.
/// Only items with <see cref="ItemStatus.Published"/> are visible to the public.
/// Items sent in through a contribution link start as <see cref="ItemStatus.Submitted"/> and carry a <see cref="Contribution"/>.
/// </summary>
public class ArchiveItem
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public ItemStatus Status { get; set; }

    public int CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    public List<MediaFile> MediaFiles { get; set; } = [];

    /// <summary>Who contributed the item and what they agreed to. Null for items curators created.</summary>
    public Contribution? Contribution { get; set; }

    /// <summary>The date as the curator wrote it, for example "circa 1950". Shown to visitors.</summary>
    public string? DateText { get; private set; }

    /// <summary>Earliest and latest day <see cref="DateText"/> can stand for. Used for filtering and sorting.</summary>
    public DateOnly? DateFrom { get; private set; }

    public DateOnly? DateTo { get; private set; }

    private readonly List<Tag> _tags = [];
    public IReadOnlyList<Tag> Tags => _tags;

    /// <summary>The tag names in one string, so the full-text index can include them. Kept in sync by <see cref="SetTags"/>.</summary>
    public string? TagNames { get; private set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    /// <summary>When the item was moved to the bin. Items in the bin are hidden from everything but the bin page.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>
    /// When a curator rejected it, or it was withdrawn. An archive can keep such contributions for a set time, see
    /// <see cref="ArchiveSettings.RejectedKeptMonths"/>, which counts from here. Null for every other status.
    /// </summary>
    public DateTime? RejectedOrWithdrawnAt { get; private set; }

    /// <summary>Changes with every save. Lets a form notice that someone else changed the item since it was opened.</summary>
    public uint Version { get; private set; }

    /// <summary>What others may do with the item, see <see cref="RightsOptions"/>. Null falls back to the archive's default.</summary>
    public string? RightsUri { get; set; }

    /// <summary>Who holds the rights. Only curators see this, because a name given for one purpose is not public by itself.</summary>
    public string? RightsHolder { get; set; }

    /// <summary>The credit for visitors, for example "Photo: Anna Krause". A curator writes it on purpose.</summary>
    public string? Credit { get; set; }

    // What curators keep for themselves: never on a public page and not in the public search.
    public string? ReferenceNumber { get; set; }
    public string? OriginalLocation { get; set; }
    public string? ReceivedFrom { get; set; }

    /// <summary>When it came to the archive, as written. Often that is all anyone knows: "summer 2019".</summary>
    public string? ReceivedOn { get; set; }

    public string? InternalNotes { get; set; }

    /// <summary>
    /// Somebody still has to write out what the scan says. Handwriting cannot be searched or read aloud, so the words of a letter
    /// belong in <see cref="Description"/>. Only curators see this.
    /// </summary>
    public bool NeedsTranscription { get; set; }

    /// <summary>Whether its picture and text may go along when a link is shared or in a feed. Needs <see cref="Contribution"/> to be loaded.</summary>
    public bool MayTravel => Contribution is null || Contribution.ConsentCoversSharing;

    public void MoveToBin(DateTime now) => DeletedAt = now;

    /// <summary>A rejected or withdrawn contribution someone took back out of the bin is kept for the full time again.</summary>
    public void RestoreFromBin(DateTime now)
    {
        DeletedAt = null;
        if (RejectedOrWithdrawnAt is not null)
            RejectedOrWithdrawnAt = now;
    }

    /// <summary>
    /// A draft made from a photo that nobody has named yet. Its title is empty rather than a stored word like "Untitled",
    /// which would be in one language only and could be taken for a real title. <see cref="ShownTitle"/> says it in any language.
    /// </summary>
    public bool IsUntitled => Title.Length == 0;

    /// <summary>What to show curators as the title of an item, also for a title kept in the activity log.</summary>
    public static string ShownTitle(IStringLocalizer localizer, string title) => title.Length == 0 ? localizer["Untitled"] : title;

    /// <summary>Withdrawn contributions cannot be published again; the contributor would have to send them in anew. And nothing goes public without a real title.</summary>
    public bool CanBePublished => Status != ItemStatus.Withdrawn && !IsUntitled;

    public void Publish(DateTime now)
    {
        if (!CanBePublished)
            throw new InvalidOperationException("Withdrawn contributions and items without a title cannot be published.");
        MarkReviewed(now);
        Status = ItemStatus.Published;
        PublishedAt ??= now;
        RejectedOrWithdrawnAt = null;
    }

    public void Unpublish()
    {
        if (Status == ItemStatus.Published)
            Status = ItemStatus.Draft;
    }

    /// <summary>Takes a contribution into the archive as a draft, for curators to complete before publishing.</summary>
    public void Accept(DateTime now)
    {
        MarkReviewed(now);
        Status = ItemStatus.Draft;
        RejectedOrWithdrawnAt = null;
    }

    public void Reject(DateTime now)
    {
        MarkReviewed(now);
        Status = ItemStatus.Rejected;
        RejectedOrWithdrawnAt = now;
    }

    /// <summary>Hides a contribution for good because the contributor withdrew their consent.</summary>
    public void Withdraw(DateTime now)
    {
        if (Contribution is null)
            throw new InvalidOperationException("Only contributions can be withdrawn.");
        MarkReviewed(now);
        Status = ItemStatus.Withdrawn;
        RejectedOrWithdrawnAt ??= now;
    }

    /// <summary>Sets the date from free text. Returns false, and changes nothing, if no date can be read from it.</summary>
    public bool TrySetDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            (DateText, DateFrom, DateTo) = (null, null, null);
            return true;
        }

        if (!DateRange.TryParse(text, out var range))
            return false;

        (DateText, DateFrom, DateTo) = (text.Trim(), range.From, range.To);
        return true;
    }

    private void MarkReviewed(DateTime now)
    {
        if (Contribution is not null && Status == ItemStatus.Submitted)
            Contribution.ReviewedAt ??= now;
    }

    public void SetTags(IEnumerable<Tag> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags);
        TagNames = _tags.Count == 0 ? null : string.Join(" ", _tags.Select(t => t.Name));
    }
}
