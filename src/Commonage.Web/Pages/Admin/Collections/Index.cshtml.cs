using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Collections;

/// <summary>Every collection, in the order visitors find them, which curators change here one step at a time.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit) : PageModel
{
    public record CollectionRow(int Id, string Title, int ItemCount);

    public IReadOnlyList<CollectionRow> Collections { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Collections = await InOrder()
            .Select(c => new CollectionRow(c.Id, c.Title, c.Items.Count))
            .ToListAsync(cancellationToken);
    }

    public Task<IActionResult> OnPostMoveEarlierAsync(int id, CancellationToken cancellationToken) => MoveAsync(id, -1, cancellationToken);

    public Task<IActionResult> OnPostMoveLaterAsync(int id, CancellationToken cancellationToken) => MoveAsync(id, 1, cancellationToken);

    /// <summary>
    /// Swaps the collection with its neighbour and numbers them all again from 0, so collections that still share a number,
    /// as all did before they had an order, end up where the list showed them.
    /// </summary>
    private async Task<IActionResult> MoveAsync(int id, int step, CancellationToken cancellationToken)
    {
        var all = await InOrder().ToListAsync(cancellationToken);
        var at = all.FindIndex(c => c.Id == id);
        if (at < 0)
            return NotFound();

        var to = at + step;
        if (to >= 0 && to < all.Count)
        {
            (all[at], all[to]) = (all[to], all[at]);
            for (var position = 0; position < all.Count; position++)
                all[position].Position = position;
            audit.Record(AuditAction.CollectionMoved, all[to].Title);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Back to the same row, so moving one several steps does not mean finding it again each time.
        return Redirect($"{Url.Page("/Admin/Collections/Index")}#collection-{id}");
    }

    private IQueryable<Collection> InOrder() => db.Collections.OrderBy(c => c.Position).ThenBy(c => c.Title).ThenBy(c => c.Id);
}
