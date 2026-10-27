using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

public class IndexModel(CommonageDbContext db) : PageModel
{
    public IReadOnlyList<ArchiveItem> Items { get; private set; } = [];
    public bool HasCollections { get; private set; }

    public async Task OnGetAsync()
    {
        HasCollections = await db.Collections.AnyAsync();
        Items = await db.ArchiveItems
            .Include(i => i.Collection)
            .OrderByDescending(i => i.UpdatedAt)
            .ToListAsync();
    }
}
