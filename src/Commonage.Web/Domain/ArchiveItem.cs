namespace Commonage.Web.Domain;

/// <summary>
/// The central record of the archive. A photograph, a letter or an oral history are all archive items.
/// Only items with <see cref="ItemStatus.Published"/> are visible to the public.
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

    public void Publish(DateTime now)
    {
        Status = ItemStatus.Published;
        PublishedAt ??= now;
    }

    public void Unpublish() => Status = ItemStatus.Draft;

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

    public void SetTags(IEnumerable<Tag> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags);
        TagNames = _tags.Count == 0 ? null : string.Join(" ", _tags.Select(t => t.Name));
    }
}
