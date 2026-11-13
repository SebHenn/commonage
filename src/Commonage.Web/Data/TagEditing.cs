using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>How many items have a tag: those curators can see, and those in the bin, which get it back when they are restored.</summary>
public record TagUse(int Items, int InBin)
{
    public bool IsUnused => Items == 0 && InBin == 0;
}

/// <summary>
/// Renaming, merging and deleting tags. Each item repeats its tag names for the search, so every item with a changed tag is
/// brought up to date, items in the bin included.
/// </summary>
public static class TagEditing
{
    private static IQueryable<ArchiveItem> ItemsWith(this CommonageDbContext db, int tagId) =>
        db.ArchiveItems.IgnoreQueryFilters().Where(i => i.Tags.Any(t => t.Id == tagId));

    public static async Task<TagUse> UseOfAsync(this CommonageDbContext db, int tagId, CancellationToken cancellationToken = default)
    {
        var inBin = await db.ItemsWith(tagId).CountAsync(i => i.DeletedAt != null, cancellationToken);
        var all = await db.ItemsWith(tagId).CountAsync(cancellationToken);
        return new TagUse(all - inBin, inBin);
    }

    /// <summary>Another tag that already has this name, apart from its case. Renaming onto it would make one tag of two, which is a merge.</summary>
    public static Task<Tag?> OtherTagNamedAsync(this CommonageDbContext db, Tag tag, string name, CancellationToken cancellationToken = default)
    {
        var normalized = Tag.Normalize(name);
        return db.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalized && t.Id != tag.Id, cancellationToken);
    }

    public static async Task RenameAsync(this CommonageDbContext db, Tag tag, string name, CancellationToken cancellationToken = default)
    {
        tag.Name = name;
        tag.NormalizedName = Tag.Normalize(name);
        var items = await db.ItemsWith(tag.Id).Include(i => i.Tags).ToListAsync(cancellationToken);
        foreach (var item in items)
            item.SetTags(item.Tags.ToList());
    }

    /// <summary>Gives every item of <paramref name="from"/> the tag <paramref name="into"/> instead, and deletes <paramref name="from"/>.</summary>
    /// <returns>How many items had <paramref name="from"/>.</returns>
    public static async Task<int> MergeAsync(this CommonageDbContext db, Tag from, Tag into, CancellationToken cancellationToken = default)
    {
        if (from.Id == into.Id)
            throw new ArgumentException("A tag cannot be merged into itself.", nameof(into));

        var items = await db.ItemsWith(from.Id).Include(i => i.Tags).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            var tags = item.Tags.Where(t => t.Id != from.Id).ToList();
            if (tags.All(t => t.Id != into.Id))
                tags.Add(into);
            item.SetTags(tags);
        }
        db.Tags.Remove(from);
        return items.Count;
    }

    /// <summary>Tags no item has, not even one in the bin.</summary>
    public static IQueryable<Tag> UnusedTags(this CommonageDbContext db) =>
        // Without the filter for the bin, which would otherwise hide the items there.
        db.Tags.IgnoreQueryFilters().Where(t => !t.Items.Any());
}
