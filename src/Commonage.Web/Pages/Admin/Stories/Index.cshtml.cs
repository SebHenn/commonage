using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Stories;

/// <summary>Every story, in the order visitors will find them, which curators change here one step at a time.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit, TimeProvider clock) : PageModel
{
    public record StoryRow(int Id, string Title, StoryStatus Status, int BlockCount, int HiddenCount);

    public IReadOnlyList<StoryRow> Stories { get; private set; } = [];

    [BindProperty]
    [Required(ErrorMessage = "Please enter a title.")]
    [StringLength(Story.TitleMaxLength, ErrorMessage = "The title can be at most {1} characters long.")]
    [Display(Name = "Title of the new story")]
    public string? NewTitle { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var title = NewTitle?.Trim();
        if (!ModelState.IsValid || string.IsNullOrEmpty(title))
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        // Last in the list: one more than the highest number, which stays right while the others are renumbered on a move.
        var position = await db.Stories.AnyAsync(cancellationToken) ? await db.Stories.MaxAsync(s => s.Position, cancellationToken) + 1 : 0;
        var story = new Story { Title = title, Status = StoryStatus.Draft, CreatedAt = now, UpdatedAt = now, Position = position };
        db.Stories.Add(story);
        audit.Record(AuditAction.StoryCreated, story.Title, story: story);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("Edit", new { id = story.Id });
    }

    public Task<IActionResult> OnPostMoveEarlierAsync(int id, CancellationToken cancellationToken) => MoveAsync(id, -1, cancellationToken);

    public Task<IActionResult> OnPostMoveLaterAsync(int id, CancellationToken cancellationToken) => MoveAsync(id, 1, cancellationToken);

    /// <summary>Swaps the story with its neighbour and numbers them all again from 0. A move does not touch the story's own text or version.</summary>
    private async Task<IActionResult> MoveAsync(int id, int step, CancellationToken cancellationToken)
    {
        var all = await db.Stories.OrderBy(s => s.Position).ThenBy(s => s.Id).ToListAsync(cancellationToken);
        var at = all.FindIndex(s => s.Id == id);
        if (at < 0)
            return NotFound();

        var to = at + step;
        if (to >= 0 && to < all.Count)
        {
            (all[at], all[to]) = (all[to], all[at]);
            for (var position = 0; position < all.Count; position++)
                all[position].Position = position;
            audit.Record(AuditAction.StoriesArranged, all[to].Title, story: all[to]);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Redirect($"{Url.Page("/Admin/Stories/Index")}#story-{id}");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Stories.OrderBy(s => s.Position).ThenBy(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Status,
                Blocks = s.Blocks.Count,
                // An item block that visitors do not see: counted here, under /Admin, and nowhere else.
                Hidden = s.Blocks.Count(b => b.Kind == StoryBlockKind.Item && !db.VisibleItems.Any(i => i.Id == b.ArchiveItemId)),
            })
            .ToListAsync(cancellationToken);
        Stories = rows.Select(r => new StoryRow(r.Id, r.Title, r.Status, r.Blocks, r.Hidden)).ToList();
    }
}
