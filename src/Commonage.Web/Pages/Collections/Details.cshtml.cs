using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Collections;

public class DetailsModel(CommonageDbContext db) : PageModel
{
    public Collection Collection { get; private set; } = null!;

    /// <summary>The years its published items are dated in.</summary>
    public YearSpan Years { get; private set; } = YearSpan.None;
    public PagedList<ItemSummary> Items { get; private set; } = PagedList<ItemSummary>.Empty;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.FindAsync([id], cancellationToken);
        if (collection is null)
            return NotFound();

        Collection = collection;
        Years = await db.Collections.Where(c => c.Id == id).ToCards().Select(c => c.Years).FirstAsync(cancellationToken);
        Items = await db.PublishedItems
            .Filter(new ItemFilter(CollectionId: id))
            .Sort(collection.Sort.ToItemSort(), query: null)
            .Select(ItemSummary.FromItem)
            .ToPagedListAsync(PageNumber, SearchModel.PageSize, cancellationToken);
        return Page();
    }
}
