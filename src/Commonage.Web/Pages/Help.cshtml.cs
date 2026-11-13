using Commonage.Web.Data;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages;

/// <summary>The published items curators hope visitors know more about. Only while the archive asks visitors, as each item then has its form.</summary>
public class HelpModel(CommonageDbContext db, ArchiveProfile profile) : PageModel
{
    public const int PageSize = 24;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public PagedList<ItemSummary> Items { get; private set; } = PagedList<ItemSummary>.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!(await profile.GetAsync()).AcceptVisitorNotes)
            return NotFound();

        Items = await db.PublishedItems
            .Where(i => i.HelpWanted)
            .OrderByDescending(i => i.PublishedAt)
            .ThenByDescending(i => i.Id)
            .Select(ItemSummary.FromItem)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
        return Page();
    }
}
