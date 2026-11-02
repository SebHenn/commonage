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
        var item = await db.PublishedItems
            .Include(i => i.Collection)
            .Include(i => i.Contribution)
            .Include(i => i.Tags.OrderBy(t => t.Name))
            .Include(i => i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Id))
            .FirstOrDefaultAsync(i => i.Id == id);
        if (item is null)
            return NotFound();

        Item = item;
        return Page();
    }
}
