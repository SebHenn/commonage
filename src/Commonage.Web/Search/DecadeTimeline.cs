using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <param name="Width">How long its bar is, in twentieths of the longest, for the CSS classes decade-bar-1 to decade-bar-20.</param>
public record DecadeCount(int Decade, int Count, int Width)
{
    public int LastYear => Decade + 9;
}

/// <param name="KeepSearch">Whether a decade's link keeps the search it shows, rather than starting a new one.</param>
public record DecadeTimelineView(IReadOnlyList<DecadeCount> Decades, bool KeepSearch);

/// <summary>
/// How many items each decade has. An item counts in every decade its possible dates touch, exactly as the year filter finds
/// it, so a decade's number is what tapping it shows: the tram of 1930–1955 is in the 1930s, 1940s and 1950s.
/// </summary>
public static class DecadeTimeline
{
    public const int MaxDecades = 40;
    public const int Widths = 20;

    public static async Task<IReadOnlyList<DecadeCount>> LoadAsync(IQueryable<ArchiveItem> items, CancellationToken cancellationToken)
    {
        // One row for each pair of first and last decade, which is few even in a large archive.
        var spans = await items
            .Where(i => i.DateFrom != null || i.DateTo != null)
            .GroupBy(i => new
            {
                From = i.DateFrom == null ? (int?)null : i.DateFrom.Value.Year / 10 * 10,
                To = i.DateTo == null ? (int?)null : i.DateTo.Value.Year / 10 * 10,
            })
            .Select(g => new { g.Key.From, g.Key.To, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var ends = spans.SelectMany(s => new[] { s.From, s.To }).OfType<int>().ToList();
        if (ends.Count == 0)
            return [];
        var last = ends.Max();
        var first = Math.Max(ends.Min(), last - ((MaxDecades - 1) * 10));

        var counts = new Dictionary<int, int>();
        foreach (var span in spans)
        {
            // An open end, as in "before 1945", reaches to the edge of the timeline, as it does in the year filter.
            for (var decade = Math.Max(span.From ?? first, first); decade <= (span.To ?? last); decade += 10)
                counts[decade] = counts.GetValueOrDefault(decade) + span.Count;
        }

        var most = counts.Values.DefaultIfEmpty(0).Max();
        var result = new List<DecadeCount>();
        for (var decade = first; decade <= last; decade += 10)
        {
            var count = counts.GetValueOrDefault(decade);
            result.Add(new DecadeCount(decade, count, count == 0 ? 0 : Math.Max(1, (int)Math.Round(count * (double)Widths / most))));
        }
        return result;
    }
}
