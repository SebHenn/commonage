using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Items;

public class DetailsModel(CommonageDbContext db, ArchiveProfile profile, IStringLocalizer<SharedResource> localizer) : PageModel
{
    public string Address { get; private set; } = "";

    public string Citation { get; private set; } = "";

    public LinkPreview Preview { get; private set; } = null!;

    public ArchiveItem Item { get; private set; } = null!;

    public ItemSurroundings Surroundings { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.PublishedItems
            .Include(i => i.Collection)
            .Include(i => i.Contribution)
            .Include(i => i.Tags.OrderBy(t => t.Name))
            .Include(i => i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Position).ThenBy(f => f.Id))
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();

        Item = item;
        Surroundings = await db.LoadSurroundingsAsync(item, cancellationToken);

        Address = $"{Request.BaseAddress()}/items/{item.Id}";
        var identity = await profile.GetAsync();
        // Whoever made it, what it is, when it is from and who keeps it, then where to find it and when someone looked.
        Citation = string.Join(". ", new[] { item.Credit, item.Title, item.DateText, identity.Name }.Where(part => !string.IsNullOrWhiteSpace(part)))
            + $". {Address} ({localizer["accessed {0}", Dates.Day(DateTime.UtcNow)]})";
        // A title is what the link itself says already. The rest depends on what the contributor agreed to.
        var photo = item.MayTravel ? item.MediaFiles.FirstOrDefault() : null;
        Preview = new LinkPreview(
            item.Title,
            item.MayTravel ? LinkPreview.Shorten(item.Description) : null,
            Address,
            photo is null ? null : $"{Request.BaseAddress()}/media/{photo.Id}/card",
            photo?.AltText ?? (photo is null ? null : item.Title),
            "article");
        return Page();
    }
}
