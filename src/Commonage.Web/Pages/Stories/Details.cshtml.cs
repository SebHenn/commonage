using Commonage.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Stories;

/// <summary>
/// A story as visitors read it. A draft, a story in the bin, one whose blocks are all hidden and an unknown number all give the
/// same <c>NotFound()</c>. No date of change is shown and no ETag or Last-Modified is sent: neither may follow block or item data.
/// </summary>
public class DetailsModel(CommonageDbContext db) : PageModel
{
    public int StoryId { get; private set; }
    public StoryBody Body { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var story = await db.VisibleStories.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Id, s.Title, s.Summary })
            .FirstOrDefaultAsync(cancellationToken);
        if (story is null)
            return NotFound();

        var blocks = await VisibleStoryBlocks.LoadAsync(db, story.Id, cancellationToken);
        // Blocks that changed between the two queries: the same answer as for every other story nobody may read.
        if (blocks.Count == 0)
            return NotFound();

        StoryId = story.Id;
        Body = new StoryBody(story.Title, story.Summary, blocks);
        return Page();
    }
}
