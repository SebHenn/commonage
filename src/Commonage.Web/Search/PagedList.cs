using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <summary>Page information for the shared pager, independent of the item type.</summary>
public interface IPagination
{
    int Page { get; }
    int TotalPages { get; }
    int TotalCount { get; }
}

public class PagedList<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount) : IPagination
{
    public IReadOnlyList<T> Items { get; } = items;
    public int Page { get; } = page;
    public int TotalCount { get; } = totalCount;
    public int TotalPages { get; } = Math.Max(1, (totalCount + pageSize - 1) / pageSize);

    public static PagedList<T> Empty { get; } = new([], 1, 1, 0);
}

public static class PagedListExtensions
{
    /// <summary>Loads one page. Page numbers outside the available pages show the nearest page.</summary>
    public static async Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (totalCount + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, totalPages);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedList<T>(items, page, pageSize, totalCount);
    }
}
