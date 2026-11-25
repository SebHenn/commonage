using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>One block of a story as a visitor sees it: words, or a visible item with the curator's caption.</summary>
public abstract record StoryBlockView
{
    /// <summary>A block of text.</summary>
    public sealed record Text(string Value) : StoryBlockView;

    /// <summary>An item visitors may see, as <see cref="ItemSummary.FromItem"/> shows it, and a caption about it.</summary>
    public sealed record Item(ItemSummary Summary, string? Caption) : StoryBlockView;
}

/// <summary>
/// The blocks of a story that visitors may see, in order. An item block whose item is not visible (draft, embargoed, withdrawn,
/// in the bin, unknown) is left out together with its caption and leaves no trace. Public code and the curator preview
/// load blocks only through here, and never follow <see cref="StoryBlock.ArchiveItem"/>.
/// </summary>
public static class VisibleStoryBlocks
{
    public static Task<List<StoryBlockView>> LoadAsync(CommonageDbContext db, int storyId, CancellationToken ct = default) =>
        LoadAsync(db, storyId, db.Today, ct);

    /// <summary>For code that has fixed one <paramref name="today"/> for several decisions, such as an export.</summary>
    public static async Task<List<StoryBlockView>> LoadAsync(CommonageDbContext db, int storyId, DateOnly today, CancellationToken ct = default)
    {
        var blocks = await db.StoryBlocks.AsNoTracking()
            .Where(b => b.StoryId == storyId)
            .OrderBy(b => b.Position).ThenBy(b => b.Id)
            .Select(b => new { b.Kind, b.Text, b.ArchiveItemId, b.Caption })
            .ToListAsync(ct);

        var ids = blocks.Where(b => b.Kind == StoryBlockKind.Item && b.ArchiveItemId != null)
            .Select(b => b.ArchiveItemId!.Value).Distinct().ToList();
        var items = ids.Count == 0
            ? []
            : await db.ArchiveItems.Where(Visibility.Item(today))
                .Where(i => ids.Contains(i.Id))
                .Select(ItemSummary.FromItem)
                .ToDictionaryAsync(i => i.Id, ct);

        var result = new List<StoryBlockView>();
        foreach (var block in blocks)
        {
            if (block.Kind == StoryBlockKind.Text)
            {
                if (!string.IsNullOrWhiteSpace(block.Text))
                    result.Add(new StoryBlockView.Text(block.Text));
            }
            else if (block.ArchiveItemId is { } id && items.TryGetValue(id, out var summary))
                result.Add(new StoryBlockView.Item(summary, block.Caption));
        }

        return result;
    }

    /// <summary>
    /// The picture each story shows in a list: that of its first block whose item visitors may see and that has a picture (never
    /// one for a record-only item). Two queries for all <paramref name="storyIds"/>, whatever their number. A story without such a
    /// block is not in the result.
    /// </summary>
    public static async Task<Dictionary<int, int>> CoversAsync(CommonageDbContext db, IReadOnlyCollection<int> storyIds, CancellationToken ct = default)
    {
        if (storyIds.Count == 0)
            return [];

        var blocks = await db.StoryBlocks.AsNoTracking()
            .Where(b => storyIds.Contains(b.StoryId) && b.Kind == StoryBlockKind.Item && b.ArchiveItemId != null)
            .Join(db.VisibleItems, b => b.ArchiveItemId!.Value, i => i.Id, (b, i) => new { b.StoryId, b.Position, BlockId = b.Id, ItemId = i.Id })
            .OrderBy(b => b.StoryId).ThenBy(b => b.Position).ThenBy(b => b.BlockId)
            .ToListAsync(ct);
        var itemIds = blocks.Select(b => b.ItemId).Distinct().ToList();
        if (itemIds.Count == 0)
            return [];

        var pictures = await db.VisibleItems
            .Where(i => itemIds.Contains(i.Id))
            .Select(ItemSummary.FromItem)
            .ToDictionaryAsync(i => i.Id, i => i.ThumbnailMediaFileId, ct);

        var covers = new Dictionary<int, int>();
        foreach (var block in blocks)
            if (!covers.ContainsKey(block.StoryId) && pictures.TryGetValue(block.ItemId, out var picture) && picture is { } file)
                covers[block.StoryId] = file;
        return covers;
    }
}

/// <summary>What the shared story partial shows: the words of a story and the blocks visitors may see.</summary>
public record StoryBody(string Title, string? Summary, IReadOnlyList<StoryBlockView> Blocks);
