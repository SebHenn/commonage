using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages;

public class IndexModel(CommonageDbContext db) : PageModel
{
    public record CollectionSummary(int Id, string Title, string? Description, int PublishedItemCount);

    public IReadOnlyList<CollectionSummary> Collections { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Collections = await db.Collections
            .OrderBy(c => c.Title)
            .Select(c => new CollectionSummary(
                c.Id, c.Title, c.Description, c.Items.Count(i => i.Status == ItemStatus.Published)))
            .ToListAsync();
    }
}
