namespace Commonage.Web.Domain;

/// <summary>
/// The text read from an item's PDF files, so that search finds an item by what its documents say. It lives apart from the
/// item on purpose: it is written by the background queue some time after an upload, and a change to the item's own row
/// would make a curator's open form look out of date. It is never shown to anyone, only searched, and only for items the
/// page that searches may see (public search starts from published items). Kept in step by <c>FileTextIndex</c>.
/// </summary>
public class ItemFileText
{
    /// <summary>Most characters kept per item, so that a very long document cannot grow the index without bound.</summary>
    public const int MaxLength = 200_000;

    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;

    public required string Text { get; set; }
}
