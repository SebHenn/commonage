using Commonage.Web.Data;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.Stories;

/// <summary>The stories visitors may read. Only <c>VisibleStories</c> and its covers feed this page, so a hidden story leaves no trace in it.</summary>
public class IndexModel(CommonageDbContext db) : PageModel
{
    public PagedList<StoryCard> Stories { get; private set; } = PagedList<StoryCard>.Empty;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Stories = await db.LoadPageAsync(PageNumber, cancellationToken);
}
