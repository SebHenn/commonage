using Commonage.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Collections;

public class IndexModel(CommonageDbContext db) : PageModel
{
    public record CollectionRow(int Id, string Title, int ItemCount);

    public IReadOnlyList<CollectionRow> Collections { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Collections = await db.Collections
            .OrderBy(c => c.Title)
            .Select(c => new CollectionRow(c.Id, c.Title, c.Items.Count))
            .ToListAsync();
    }
}
