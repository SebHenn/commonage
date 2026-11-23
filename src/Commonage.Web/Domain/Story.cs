namespace Commonage.Web.Domain;

/// <summary>Whether visitors may read a story. Not <see cref="ItemStatus"/>: a story has no contribution states.</summary>
public enum StoryStatus
{
    Draft,
    Published,
}

/// <summary>What a block of a story is: words a curator wrote, or a reference to an item.</summary>
public enum StoryBlockKind
{
    Text,
    Item,
}

/// <summary>
/// A curator's telling put together from text and items: a title, a short summary and blocks in order. Only a published story
/// that is not in the bin and has at least one block visitors may see is public, see <c>CommonageDbContext.VisibleStories</c>.
/// </summary>
public class Story
{
    public const int TitleMaxLength = 200;
    public const int SummaryMaxLength = 1000;

    /// <summary>The most blocks one story holds, so that one page stays a page.</summary>
    public const int MaxBlocks = 200;

    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public StoryStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    /// <summary>When the story went to the bin, or null. Query filters hide a story in the bin.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>Where the story stands among the others, lowest first.</summary>
    public int Position { get; set; }

    /// <summary>Concurrency token (PostgreSQL <c>xmin</c>) for the title and summary form. Block changes never change it.</summary>
    public uint Version { get; set; }

    public List<StoryBlock> Blocks { get; set; } = [];

    public bool IsUntitled => string.IsNullOrWhiteSpace(Title);

    public bool CanBePublished => !IsUntitled;

    public void Publish(DateTime now)
    {
        if (!CanBePublished)
            throw new InvalidOperationException("A story without a title cannot be published.");

        Status = StoryStatus.Published;
        PublishedAt ??= now;
    }

    /// <summary>Takes the story back to a draft. <see cref="PublishedAt"/> stays, as it does for items.</summary>
    public void Unpublish() => Status = StoryStatus.Draft;

    public void MoveToBin(DateTime now) => DeletedAt = now;

    public void RestoreFromBin(DateTime now)
    {
        DeletedAt = null;
        UpdatedAt = now;
    }

    public void Touch(DateTime now) => UpdatedAt = now;
}

/// <summary>One block of a story, in <see cref="Position"/> order: text, or an item with a caption.</summary>
public class StoryBlock
{
    public const int TextMaxLength = 20000;
    public const int CaptionMaxLength = 1000;

    public int Id { get; set; }
    public int StoryId { get; set; }
    public Story Story { get; set; } = null!;

    /// <summary>0 to n-1, renumbered after every change.</summary>
    public int Position { get; set; }

    public StoryBlockKind Kind { get; set; }

    /// <summary>The words of a text block; null for an item block.</summary>
    public string? Text { get; set; }

    /// <summary>The item of an item block; null for a text block. Deleting the item for good deletes the block.</summary>
    public int? ArchiveItemId { get; set; }

    /// <summary>
    /// Admin pages only. Public code never follows this: it loads items through <c>VisibleItems</c> (see
    /// <c>VisibleStoryBlocks</c>), so that a hidden item leaves no trace.
    /// </summary>
    public ArchiveItem? ArchiveItem { get; set; }

    /// <summary>A curator's words about the item in this story; hidden whenever the item is.</summary>
    public string? Caption { get; set; }
}
