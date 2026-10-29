using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.Collections;

public class DetailsModel(CommonageDbContext db) : PageModel
{
    public Collection Collection { get; private set; } = null!;
    public PagedList<ItemSummary> Items { get; private set; } = PagedList<ItemSummary>.Empty;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.FindAsync([id], cancellationToken);
        if (collection is null)
            return NotFound();

        Collection = collection;
        Items = await db.PublishedItems
            .Filter(new ItemFilter(CollectionId: id))
            .Sort(ItemSort.RecentlyAdded, text: null)
            .Select(ItemSummary.FromItem)
            .ToPagedListAsync(PageNumber, SearchModel.PageSize, cancellationToken);
        return Page();
    }
}
