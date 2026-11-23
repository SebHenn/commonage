using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Stories;

/// <summary>Shows curators a story as visitors would see it, whatever its status. The blocks come the way public ones will: only what visitors may see.</summary>
public class PreviewModel(CommonageDbContext db) : PageModel
{
    public int StoryId { get; private set; }
    public StoryStatus Status { get; private set; }
    public StoryBody Body { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var story = await db.Stories.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (story is null)
            return NotFound();

        StoryId = story.Id;
        Status = story.Status;
        Body = new StoryBody(story.Title, story.Summary, await VisibleStoryBlocks.LoadAsync(db, id, cancellationToken));
        return Page();
    }
}
