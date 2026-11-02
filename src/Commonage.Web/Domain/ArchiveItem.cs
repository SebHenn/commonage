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

    /// <summary>Withdrawn contributions cannot be published again; the contributor would have to send them in anew.</summary>
    public bool CanBePublished => Status != ItemStatus.Withdrawn;

    public void Publish(DateTime now)
    {
        if (!CanBePublished)
            throw new InvalidOperationException("A withdrawn contribution cannot be published.");
        MarkReviewed(now);
        Status = ItemStatus.Published;
        PublishedAt ??= now;
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
    }

    public void Reject(DateTime now)
    {
        MarkReviewed(now);
        Status = ItemStatus.Rejected;
    }

    /// <summary>Hides a contribution for good because the contributor withdrew their consent.</summary>
    public void Withdraw(DateTime now)
    {
        if (Contribution is null)
            throw new InvalidOperationException("Only contributions can be withdrawn.");
        MarkReviewed(now);
        Status = ItemStatus.Withdrawn;
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
