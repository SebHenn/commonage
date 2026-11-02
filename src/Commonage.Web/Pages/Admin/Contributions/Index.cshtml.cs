using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Contributions;

public class IndexModel(CommonageDbContext db) : PageModel
{
    public record LinkRow(int Id, string Title, string CollectionTitle, string Token, bool IsActive, int ContributionCount, int WaitingCount);

    public IReadOnlyList<LinkRow> Links { get; private set; } = [];
    public bool HasCollections { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        HasCollections = await db.Collections.AnyAsync(cancellationToken);
        Links = await db.ContributionLinks
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.CreatedAt)
            .Select(l => new LinkRow(
                l.Id,
                l.Title,
                l.Collection.Title,
                l.Token,
                l.IsActive,
                l.Contributions.Count,
                l.Contributions.Count(c => c.ArchiveItem.Status == ItemStatus.Submitted)))
            .ToListAsync(cancellationToken);
    }

    public string AddressOf(LinkRow link) => Url.Page("/Contribute/Index", null, new { token = link.Token }, Request.Scheme)!;
}
