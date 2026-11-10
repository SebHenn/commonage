using Commonage.Web.Data;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages;

public class SearchModel(CommonageDbContext db) : PageModel
{
    public const int PageSize = 24;
    private const int MaxTagSuggestions = 20;

    public record TagCount(string Name, int Count);

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tag { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Collection { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemSort Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public PagedList<ItemSummary> Results { get; private set; } = PagedList<ItemSummary>.Empty;
    public IReadOnlyList<TagCount> Tags { get; private set; } = [];
    public SelectList CollectionOptions { get; private set; } = null!;

    /// <summary>The order actually used: without search text there is nothing to rank by relevance.</summary>
    public ItemSort EffectiveSort => Sort == ItemSort.Relevance && Query is null ? ItemSort.RecentlyAdded : Sort;

    /// <summary>The full-text query made from the words of <see cref="Text"/>; null when there is nothing to search for.</summary>
    public string? Query { get; private set; }

    /// <summary>Dates in <see cref="Text"/>, such as "1950s", which also find what is dated within them.</summary>
    public IReadOnlyList<DateInSearch> Dates { get; private set; } = [];

    /// <summary>The decades of what was found, while no period is chosen.</summary>
    public IReadOnlyList<DecadeCount> Decades { get; private set; } = [];

    public bool HasFilters => ActiveFilterCount > 0;

    public int ActiveFilterCount =>
        (Tag is null ? 0 : 1) + (Collection is null ? 0 : 1) + (From is null ? 0 : 1) + (To is null ? 0 : 1);

    public string? CollectionTitle { get; private set; }

    /// <summary>When nothing was found: tags that do lead somewhere.</summary>
    public IReadOnlyList<string> PopularTags { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Years outside the range people search for are ignored instead of causing an error.
        if (From is < 1000 or > 2999)
            From = null;
        if (To is < 1000 or > 2999)
            To = null;
        if (From > To)
            (From, To) = (To, From);
        if (string.IsNullOrWhiteSpace(Tag))
            Tag = null;

        var search = await db.ToSearchAsync(Text, cancellationToken);
        (Query, Dates) = (search.Query, search.Dates);
        var filter = new ItemFilter(Query, Tag, Collection, From, To, Dates);
        var matches = db.PublishedItems.Filter(filter);

        Results = await matches
            .Sort(Sort, Query)
            .Select(ItemSummary.FromItem)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);

        // Tags that would narrow the current results down further.
        Tags = await matches
            .SelectMany(i => i.Tags)
            .GroupBy(t => t.Name)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(MaxTagSuggestions)
            .Select(g => new TagCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        // A timeline of one decade, or of a chosen period, would say nothing new.
        if (From is null && To is null && Dates.Count == 0)
        {
            Decades = await DecadeTimeline.LoadAsync(matches, cancellationToken);
            if (Decades.Count < 2)
                Decades = [];
        }

        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync(cancellationToken);
        CollectionOptions = new SelectList(collections, nameof(Domain.Collection.Id), nameof(Domain.Collection.Title), Collection);
        CollectionTitle = collections.FirstOrDefault(c => c.Id == Collection)?.Title;

        if (Results.TotalCount == 0)
        {
            PopularTags = await db.PublishedItems
                .SelectMany(i => i.Tags)
                .GroupBy(t => t.Name)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => g.Key)
                .Take(MaxTagSuggestions / 2)
                .ToListAsync(cancellationToken);
        }
    }
}
