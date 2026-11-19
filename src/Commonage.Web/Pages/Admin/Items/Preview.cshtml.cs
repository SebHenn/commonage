using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Items;

/// <summary>Shows curators an item as visitors would see it, whatever its status. Public pages still only show published items.</summary>
public class PreviewModel(CommonageDbContext db) : PageModel
{
    public ArchiveItem Item { get; private set; } = null!;

    /// <summary>Today in the archive's time zone, to tell whether an embargo is still on.</summary>
    public DateOnly Today => db.Today;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var items = db.ArchiveItems.AsQueryable();
        // A helper only sees what is a draft, as in the item list.
        if (!User.MayManage())
            items = items.Where(i => i.Status == ItemStatus.Draft);
        if (User.MayManage())
            items = items.Include(i => i.Contribution);
        var item = await items
            .Include(i => i.Tags.OrderBy(t => t.Name))
            .Include(i => i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Position).ThenBy(f => f.Id))
            .FirstOrDefaultAsync(i => i.Id == id);
        if (item is null)
            return NotFound();

        Item = item;
        return Page();
    }
}
