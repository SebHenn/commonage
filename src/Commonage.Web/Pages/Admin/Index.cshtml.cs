using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

public class IndexModel(CommonageDbContext db, ArchiveHealth health) : PageModel
{
    private const int PageSize = 50;

    public PagedList<AdminItemRow> Items { get; private set; } = PagedList<AdminItemRow>.Empty;
    public bool HasCollections { get; private set; }

    /// <summary>Contributions nobody has reviewed yet.</summary>
    public int WaitingCount { get; private set; }

    public ArchiveHealth Health => health;

    public SelectList CollectionOptions { get; private set; } = null!;

    /// <summary>This list with its filters, so an item opened from it can go on to the next one and come back.</summary>
    public string ListUrl => Request.Path + Request.QueryString;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "collection")]
    public int? CollectionId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tag { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemNeed? Needs { get; set; }

    [BindProperty(SupportsGet = true)]
    public AdminItemSort? Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public AdminItemFilter Filter => new(Text, Status, CollectionId, Tag, Needs, Sort);

    public bool HasFilters => Text is not null || Status is not null || CollectionId is not null || Tag is not null || Needs is not null;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync(cancellationToken);
        HasCollections = collections.Count > 0;
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title), CollectionId);
        WaitingCount = await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Submitted, cancellationToken);

        var items = await db.QueryAsync(Filter, cancellationToken);
        Items = await items
            .Select(i => new AdminItemRow(
                i.Id,
                i.Title,
                i.Collection.Title,
                i.Status,
                i.UpdatedAt,
                i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault(),
                i.MediaFiles.Count))
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
