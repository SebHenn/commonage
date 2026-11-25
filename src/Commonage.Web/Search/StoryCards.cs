using Commonage.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <summary>A story as visitors find it in a list: its words and a picture of a visible item, if it has one.</summary>
public record StoryCard(int Id, string Title, string? Summary, int? CoverMediaFileId);

public static class StoryCards
{
    /// <summary>A page of the list stays small however many stories there are.</summary>
    public const int PageSize = 24;

    private sealed record Head(int Id, string Title, string? Summary);

    /// <summary>
    /// One page of the stories visitors may read, in the order curators gave them. The total, the paging and the covers are
    /// computed from visible rows only, so a story that is hidden changes nothing about the page.
    /// </summary>
    public static async Task<PagedList<StoryCard>> LoadPageAsync(this CommonageDbContext db, int page, CancellationToken ct)
    {
        var heads = await db.VisibleStories
            .OrderBy(s => s.Position).ThenBy(s => s.Id)
            .Select(s => new Head(s.Id, s.Title, s.Summary))
            .ToPagedListAsync(page, PageSize, ct);
        return new PagedList<StoryCard>(await WithCoversAsync(db, heads.Items, ct), heads.Page, PageSize, heads.TotalCount);
    }

    /// <summary>The first <paramref name="count"/> stories, for the start page.</summary>
    public static async Task<List<StoryCard>> LoadFirstAsync(this CommonageDbContext db, int count, CancellationToken ct)
    {
        var heads = await db.VisibleStories
            .OrderBy(s => s.Position).ThenBy(s => s.Id)
            .Take(count)
            .Select(s => new Head(s.Id, s.Title, s.Summary))
            .ToListAsync(ct);
        return await WithCoversAsync(db, heads, ct);
    }

    private static async Task<List<StoryCard>> WithCoversAsync(CommonageDbContext db, IReadOnlyList<Head> heads, CancellationToken ct)
    {
        // The cover is the picture of an item visitors see, never an original and never a hidden item's. It is shown on the
        // website only: a story cover must never go into og:image without a MayTravel check.
        var covers = await VisibleStoryBlocks.CoversAsync(db, heads.Select(h => h.Id).ToList(), ct);
        return heads.Select(h => new StoryCard(h.Id, h.Title, h.Summary, covers.TryGetValue(h.Id, out var cover) ? cover : null)).ToList();
    }
}
