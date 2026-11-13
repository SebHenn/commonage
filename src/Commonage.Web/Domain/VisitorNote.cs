namespace Commonage.Web.Domain;

/// <summary>What a visitor wants to tell the archive about an item. Stored as a number, so new kinds go at the end.</summary>
public enum VisitorNoteKind
{
    /// <summary>More about it: who is in the picture, where it was taken, a memory.</summary>
    Information,

    /// <summary>Something on the page is wrong.</summary>
    Correction,

    /// <summary>The item, or something in it, should not be public, for example because it shows the visitor.</summary>
    Removal,
}

/// <summary>
/// A message a visitor sent about a published item, through "Do you know more about this?". Only curators read it. It is
/// never shown publicly; what is worth keeping, a curator writes into the item in their own words.
/// </summary>
public class VisitorNote
{
    public const int MessageMaxLength = 4000;

    public int Id { get; set; }

    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;

    public VisitorNoteKind Kind { get; set; }
    public required string Message { get; set; }

    /// <summary>Only if the visitor gave it, to be answered.</summary>
    public string? Name { get; set; }

    /// <summary>Only if the visitor gave it, to be answered.</summary>
    public string? Email { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>When a curator marked it as dealt with. Open notes are those without.</summary>
    public DateTime? HandledAt { get; set; }

    /// <summary>When name and email address were removed, on request or once they were no longer needed.</summary>
    public DateTime? PersonalDetailsRemovedAt { get; set; }

    public void RemovePersonalDetails(DateTime now)
    {
        Name = null;
        Email = null;
        PersonalDetailsRemovedAt = now;
    }
}
