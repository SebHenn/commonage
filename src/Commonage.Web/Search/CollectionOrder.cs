using System.Globalization;
using Commonage.Web.Domain;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Search;

/// <summary>The years a collection's published items are dated in, as its page and card show them.</summary>
public record YearSpan(int? From, int? To)
{
    public static readonly YearSpan None = new(null, null);

    /// <summary>"1930–1965", or one year when both are the same. Nothing when no published item has a date.</summary>
    public string? Describe(IStringLocalizer localizer) => (From, To) switch
    {
        ({ } from, { } to) when from == to => from.ToString(CultureInfo.CurrentCulture),
        ({ } from, { } to) => localizer["{0}–{1}", from, to],
        _ => null,
    };
}

/// <summary>
/// A collection's order applied to its items: on its page, and for "previous" and "next" on an item's page, which must
/// agree with the page. Every order ends with the id, so items that share a date still have one place each.
/// </summary>
public static class CollectionOrder
{
    public static ItemSort ToItemSort(this CollectionSort sort) => sort switch
    {
        CollectionSort.OldestFirst => ItemSort.OldestFirst,
        CollectionSort.NewestFirst => ItemSort.NewestFirst,
        _ => ItemSort.RecentlyAdded,
    };

    /// <summary>The items that come after <paramref name="item"/> on the collection's page, the next one first.</summary>
    public static IQueryable<ArchiveItem> After(this IQueryable<ArchiveItem> items, ArchiveItem item, CollectionSort sort)
    {
        var id = item.Id;
        switch (sort)
        {
            case CollectionSort.OldestFirst when OldestKey(item) is { } key:
                // Undated items come last.
                return items
                    .Where(i => i.DateFrom == null && i.DateTo == null
                        || (i.DateFrom ?? i.DateTo) > key || ((i.DateFrom ?? i.DateTo) == key && i.Id > id))
                    .Sort(ItemSort.OldestFirst, null);
            case CollectionSort.OldestFirst:
                return items.Where(i => i.DateFrom == null && i.DateTo == null && i.Id > id).Sort(ItemSort.OldestFirst, null);
            case CollectionSort.NewestFirst when NewestKey(item) is { } key:
                return items
                    .Where(i => i.DateFrom == null && i.DateTo == null
                        || (i.DateTo ?? i.DateFrom) < key || ((i.DateTo ?? i.DateFrom) == key && i.Id < id))
                    .Sort(ItemSort.NewestFirst, null);
            case CollectionSort.NewestFirst:
                return items.Where(i => i.DateFrom == null && i.DateTo == null && i.Id < id).Sort(ItemSort.NewestFirst, null);
            default:
                var at = item.PublishedAt;
                return items.Where(i => i.PublishedAt < at || (i.PublishedAt == at && i.Id < id)).Sort(ItemSort.RecentlyAdded, null);
        }
    }

    /// <summary>The items that come before <paramref name="item"/> on the collection's page, the one just before it first.</summary>
    public static IQueryable<ArchiveItem> Before(this IQueryable<ArchiveItem> items, ArchiveItem item, CollectionSort sort)
    {
        var id = item.Id;
        switch (sort)
        {
            case CollectionSort.OldestFirst when OldestKey(item) is { } key:
                return items
                    .Where(i => (i.DateFrom != null || i.DateTo != null)
                        && ((i.DateFrom ?? i.DateTo) < key || ((i.DateFrom ?? i.DateTo) == key && i.Id < id)))
                    .OrderByDescending(i => i.DateFrom ?? i.DateTo).ThenByDescending(i => i.Id);
            case CollectionSort.OldestFirst:
                // Before an undated item: the undated ones before it, then every dated one, latest first.
                return items
                    .Where(i => i.DateFrom != null || i.DateTo != null || i.Id < id)
                    .OrderByDescending(i => i.DateFrom == null && i.DateTo == null)
                    .ThenByDescending(i => i.DateFrom ?? i.DateTo).ThenByDescending(i => i.Id);
            case CollectionSort.NewestFirst when NewestKey(item) is { } key:
                return items
                    .Where(i => (i.DateFrom != null || i.DateTo != null)
                        && ((i.DateTo ?? i.DateFrom) > key || ((i.DateTo ?? i.DateFrom) == key && i.Id > id)))
                    .OrderBy(i => i.DateTo ?? i.DateFrom).ThenBy(i => i.Id);
            case CollectionSort.NewestFirst:
                return items
                    .Where(i => i.DateFrom != null || i.DateTo != null || i.Id > id)
                    .OrderByDescending(i => i.DateFrom == null && i.DateTo == null)
                    .ThenBy(i => i.DateTo ?? i.DateFrom).ThenBy(i => i.Id);
            default:
                var at = item.PublishedAt;
                return items.Where(i => i.PublishedAt > at || (i.PublishedAt == at && i.Id > id)).OrderBy(i => i.PublishedAt).ThenBy(i => i.Id);
        }
    }

    // The same keys as ItemSearch.Sort uses.
    private static DateOnly? OldestKey(ArchiveItem item) => item.DateFrom ?? item.DateTo;
    private static DateOnly? NewestKey(ArchiveItem item) => item.DateTo ?? item.DateFrom;
}
