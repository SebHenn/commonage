using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Items;

public class DetailsModel(CommonageDbContext db) : PageModel
{
    public ArchiveItem Item { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await db.PublishedItems.Include(i => i.Collection).FirstOrDefaultAsync(i => i.Id == id);
        if (item is null)
            return NotFound();

        Item = item;
        return Page();
    }
}
