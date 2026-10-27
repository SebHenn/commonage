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

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public void Publish(DateTime now)
    {
        Status = ItemStatus.Published;
        PublishedAt ??= now;
    }

    public void Unpublish() => Status = ItemStatus.Draft;
}
