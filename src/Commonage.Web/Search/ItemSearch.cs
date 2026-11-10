using System.Globalization;
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

/// <param name="Query">A full-text query from <see cref="ItemSearch.ToSearchQueryAsync"/>, or null to match all items.</param>
/// <param name="Dates">Dates typed into the search. Each one matches what is dated within it, or has its words.</param>
public record ItemFilter(
    string? Query = null, string? Tag = null, int? CollectionId = null, int? YearFrom = null, int? YearTo = null,
    IReadOnlyList<DateInSearch>? Dates = null);

/// <param name="Query">The full-text query for the words of the date itself, so that "1958" also finds "Team photo 1958".</param>
public record DateInSearch(SearchDate Date, string Query);

/// <summary>What someone typed into the search: the full-text query for its words, and the dates in it.</summary>
public record TextSearch(string? Query, IReadOnlyList<DateInSearch> Dates)
{
    public static readonly TextSearch None = new(null, []);
}

/// <summary>
/// Filters and sorts archive items. It never decides what the caller may see: public pages pass
/// <see cref="CommonageDbContext.PublishedItems"/>, curator pages pass all items.
/// </summary>
public static class ItemSearch
{
    private const int MaxSearchWords = 10;
    private const int MaxSearchTextLength = 500;

    public static IQueryable<ArchiveItem> Filter(this IQueryable<ArchiveItem> items, ItemFilter filter)
    {
        if (filter.Query is { } query)
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

        foreach (var (date, words) in filter.Dates ?? [])
        {
            // Like the year filter: the item's possible dates overlap the date typed. Either end of either can be open.
            var (from, to) = (date.From, date.To);
            items = items.Where(i => ((i.DateFrom != null || i.DateTo != null)
                    && (from == null || i.DateTo == null || i.DateTo >= from)
                    && (to == null || i.DateFrom == null || i.DateFrom <= to))
                || EF.Property<NpgsqlTsVector>(i, CommonageDbContext.SearchVector)
                    .Matches(EF.Functions.ToTsQuery(CommonageDbContext.SearchConfiguration, words)));
        }

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

    /// <param name="query">The query from <see cref="ToSearchQueryAsync"/>, which relevance ranks by.</param>
    public static IQueryable<ArchiveItem> Sort(this IQueryable<ArchiveItem> items, ItemSort sort, string? query)
    {
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
    /// Turns what someone typed into a full-text query for its words and the dates in it (<see cref="SearchDates"/>).
    /// </summary>
    public static async Task<TextSearch> ToSearchAsync(this CommonageDbContext db, string? text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TextSearch.None;
        if (text.Length > MaxSearchTextLength)
            text = text[..MaxSearchTextLength];
        var (words, found) = SearchDates.Extract(text);
        var dates = new List<DateInSearch>();
        // A date always has digits, so it always has words to search for as well.
        foreach (var date in found)
        {
            if (await db.ToSearchQueryAsync(date.Text, cancellationToken) is { } query)
                dates.Add(new DateInSearch(date, query));
        }
        return new TextSearch(await db.ToSearchQueryAsync(words, cancellationToken), dates);
    }

    /// <summary>
    /// Turns what a visitor typed into a full-text query, or null if there is nothing to search for.
    /// The text first loses its accents in the database, exactly as the indexed documents did
    /// (see <see cref="AccentFolding"/>), so "Muhle" and "Mühle" both find "Mühle".
    /// </summary>
    public static async Task<string?> ToSearchQueryAsync(this CommonageDbContext db, string? text, CancellationToken cancellationToken = default)
    {
        if (ToPrefixQuery(text) is null)
            return null;

        text = text!.Normalize();
        if (text.Length > MaxSearchTextLength)
            text = text[..MaxSearchTextLength];
        var folded = await db.Database
            .SqlQuery<string>($"SELECT commonage_fold({text}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        return ToPrefixQuery(folded);
    }

    /// <summary>
    /// Builds a tsquery that requires every word, each matched at its beginning, so "mill schul" finds
    /// "Mill Street School" and "Schulen". Returns null if there is nothing to search for.
    /// Only letters, digits and the marks that belong to letters reach PostgreSQL, so the input cannot break the
    /// query syntax. Without the marks, words in scripts such as Hindi or Thai would fall apart at their vowel signs.
    /// </summary>
    public static string? ToPrefixQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.Append(' '))
        {
            if (char.IsLetterOrDigit(c) || (word.Length > 0 && IsMark(c)))
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

    private static bool IsMark(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
