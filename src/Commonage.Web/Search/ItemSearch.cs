using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Commonage.Web.Search;

public enum ItemSort
{
    /// <summary>Best matches first. Only meaningful together with search text; otherwise the same as <see cref="RecentlyAdded"/>.</summary>
    Relevance,
    RecentlyAdded,
    OldestFirst,
    NewestFirst,
}

public record ItemFilter(string? Text = null, string? Tag = null, int? CollectionId = null, int? YearFrom = null, int? YearTo = null);

/// <summary>
/// Filters and sorts archive items. It never decides what the caller may see: public pages pass
/// <see cref="CommonageDbContext.PublishedItems"/>, curator pages pass all items.
/// </summary>
public static class ItemSearch
{
    private const int MaxSearchWords = 10;

    public static IQueryable<ArchiveItem> Filter(this IQueryable<ArchiveItem> items, ItemFilter filter)
    {
        if (ToPrefixQuery(filter.Text) is { } query)
        {
            items = items.Where(i => EF.Property<NpgsqlTsVector>(i, CommonageDbContext.SearchVector)
                .Matches(EF.Functions.ToTsQuery(CommonageDbContext.SearchConfiguration, query)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var tag = Tag.Normalize(filter.Tag);
            items = items.Where(i => i.Tags.Any(t => t.NormalizedName == tag));
        }

        if (filter.CollectionId is { } collectionId)
            items = items.Where(i => i.CollectionId == collectionId);

        // An item matches a period when its possible dates overlap it. Undated items never match.
        if (filter.YearFrom is { } yearFrom)
        {
            var start = new DateOnly(yearFrom, 1, 1);
            items = items.Where(i => (i.DateFrom != null || i.DateTo != null) && (i.DateTo == null || i.DateTo >= start));
        }

        if (filter.YearTo is { } yearTo)
        {
            var end = new DateOnly(yearTo, 12, 31);
            items = items.Where(i => (i.DateFrom != null || i.DateTo != null) && (i.DateFrom == null || i.DateFrom <= end));
        }

        return items;
    }

    public static IQueryable<ArchiveItem> Sort(this IQueryable<ArchiveItem> items, ItemSort sort, string? text)
    {
        var query = ToPrefixQuery(text);
        if (sort == ItemSort.Relevance && query is null)
            sort = ItemSort.RecentlyAdded;

        // Every order ends with the id, so paging is stable when items share a date.
        return sort switch
        {
            ItemSort.Relevance => items
                .OrderByDescending(i => EF.Property<NpgsqlTsVector>(i, CommonageDbContext.SearchVector)
                    .Rank(EF.Functions.ToTsQuery(CommonageDbContext.SearchConfiguration, query!)))
                .ThenByDescending(i => i.PublishedAt)
                .ThenByDescending(i => i.Id),
            ItemSort.OldestFirst => items
                .OrderBy(i => i.DateFrom == null && i.DateTo == null)
                .ThenBy(i => i.DateFrom ?? i.DateTo)
                .ThenBy(i => i.Id),
            ItemSort.NewestFirst => items
                .OrderBy(i => i.DateFrom == null && i.DateTo == null)
                .ThenByDescending(i => i.DateTo ?? i.DateFrom)
                .ThenByDescending(i => i.Id),
            _ => items
                .OrderByDescending(i => i.PublishedAt)
                .ThenByDescending(i => i.Id),
        };
    }

    /// <summary>
    /// Turns what a visitor typed into a tsquery that requires every word, each matched at its beginning,
    /// so "mill schul" finds "Mill Street School" and "Schulen". Returns null if there is nothing to search for.
    /// Only letters and digits reach PostgreSQL, so the input cannot break the query syntax.
    /// </summary>
    public static string? ToPrefixQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.Append(' '))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        return words.Count == 0
            ? null
            : string.Join(" & ", words.Distinct().Take(MaxSearchWords).Select(w => w + ":*"));
    }
}
