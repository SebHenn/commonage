using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Collections;

public class DetailsModel(CommonageDbContext db) : PageModel
{
    public Collection Collection { get; private set; } = null!;
    public IReadOnlyList<ArchiveItem> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var collection = await db.Collections.FindAsync(id);
        if (collection is null)
            return NotFound();

        Collection = collection;
        Items = await db.PublishedItems
            .Where(i => i.CollectionId == id)
            .OrderByDescending(i => i.PublishedAt)
            .ToListAsync();
        return Page();
    }
}
