namespace Commonage.Web.Domain;

/// <summary>The order a collection's page shows its items in. Stored as a number, so new orders go at the end.</summary>
public enum CollectionSort
{
    RecentlyAdded,
    OldestFirst,
    NewestFirst,
}

/// <summary>A thematic or geographic group of archive items, such as a street, a club or an event.</summary>
public class Collection
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Where visitors find the collection among the others, lowest first. Collections with the same number go by title.</summary>
    public int Position { get; set; }

    /// <summary>The order of the items on the collection's page, and of "previous" and "next" on an item's page.</summary>
    public CollectionSort Sort { get; set; }

    /// <summary>
    /// The photo a curator chose to stand for the collection. Visitors only see it while its item is published and in this
    /// collection; otherwise, and when none is chosen, they see the first photo of the collection's items.
    /// </summary>
    public int? CoverMediaFileId { get; set; }

    public List<ArchiveItem> Items { get; set; } = [];
}
