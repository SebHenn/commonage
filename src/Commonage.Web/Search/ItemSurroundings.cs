using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <summary>An item's neighbour in its collection, in the order of the collection's page.</summary>
public record ItemLink(int Id, string Title);

/// <summary>Where a visitor can go from an item, so that it is not the end of the road.</summary>
public record ItemSurroundings(ItemLink? Previous, ItemLink? Next, IReadOnlyList<ItemSummary> SameCollection, IReadOnlyList<ItemSummary> SameTime)
{
    private const int HowMany = 4;

    public static async Task<ItemSurroundings> LoadAsync(CommonageDbContext db, ArchiveItem item, CancellationToken cancellationToken)
    {
        var inCollection = db.PublishedItems.Where(i => i.CollectionId == item.CollectionId && i.Id != item.Id);

        // The collection's page shows the most recently added first, with the id deciding between equals.
        var at = item.PublishedAt;
        var next = await inCollection
            .Where(i => i.PublishedAt < at || (i.PublishedAt == at && i.Id < item.Id))
            .OrderByDescending(i => i.PublishedAt).ThenByDescending(i => i.Id)
            .Select(i => new ItemLink(i.Id, i.Title))
            .FirstOrDefaultAsync(cancellationToken);
        var previous = await inCollection
            .Where(i => i.PublishedAt > at || (i.PublishedAt == at && i.Id > item.Id))
            .OrderBy(i => i.PublishedAt).ThenBy(i => i.Id)
            .Select(i => new ItemLink(i.Id, i.Title))
            .FirstOrDefaultAsync(cancellationToken);

        var sameCollection = await inCollection
            .OrderByDescending(i => i.PublishedAt).ThenByDescending(i => i.Id)
            .Select(ItemSummary.FromItem)
            .Take(HowMany)
            .ToListAsync(cancellationToken);

        // Only with a beginning and an end. "Before 1965" says too little to find what else happened then.
        List<ItemSummary> sameTime = [];
        if (item is { DateFrom: { } from, DateTo: { } to })
        {
            var alreadyShown = sameCollection.Select(i => i.Id).ToList();
            sameTime = await db.PublishedItems
                .Where(i => i.Id != item.Id && !alreadyShown.Contains(i.Id) && i.DateFrom != null && i.DateTo != null && i.DateFrom <= to && i.DateTo >= from)
                // Closest to the same period first, so that "summer 1953" comes before "1930–1955".
                .OrderBy(i => Math.Abs(i.DateFrom!.Value.DayNumber - from.DayNumber) + Math.Abs(i.DateTo!.Value.DayNumber - to.DayNumber))
                .ThenByDescending(i => i.Id)
                .Select(ItemSummary.FromItem)
                .Take(HowMany)
                .ToListAsync(cancellationToken);
        }

        return new ItemSurroundings(previous, next, sameCollection, sameTime);
    }
}

public static class ItemSurroundingsQueries
{
    public static Task<ItemSurroundings> LoadSurroundingsAsync(this CommonageDbContext db, ArchiveItem item, CancellationToken cancellationToken) =>
        ItemSurroundings.LoadAsync(db, item, cancellationToken);
}
