using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

public class IndexModel(CommonageDbContext db) : PageModel
{
    private const int PageSize = 50;

    public PagedList<ArchiveItem> Items { get; private set; } = PagedList<ArchiveItem>.Empty;
    public bool HasCollections { get; private set; }

    /// <summary>Contributions nobody has reviewed yet.</summary>
    public int WaitingCount { get; private set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        HasCollections = await db.Collections.AnyAsync(cancellationToken);
        WaitingCount = await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Submitted, cancellationToken);

        // Curators see every item, drafts included.
        var items = db.ArchiveItems.Filter(new ItemFilter(await db.ToSearchQueryAsync(Text, cancellationToken)));
        if (Status is { } status)
            items = items.Where(i => i.Status == status);

        Items = await items
            .Include(i => i.Collection)
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Id)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
