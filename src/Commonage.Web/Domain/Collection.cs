namespace Commonage.Web.Domain;

/// <summary>A thematic or geographic group of archive items, such as a street, a club or an event.</summary>
public class Collection
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<ArchiveItem> Items { get; set; } = [];
}
