using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Collections;

public class DetailsModel(CommonageDbContext db) : PageModel
{
    public Collection Collection { get; private set; } = null!;

    /// <summary>The years its published items are dated in.</summary>
    public YearSpan Years { get; private set; } = YearSpan.None;
    public PagedList<ItemSummary> Items { get; private set; } = PagedList<ItemSummary>.Empty;

    /// <summary>Open links whose curators chose to invite visitors here. Only a heading and an address, nothing about who sent what.</summary>
    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];

    public record Invitation(string Title, string Token);

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.FindAsync([id], cancellationToken);
        if (collection is null)
            return NotFound();

        Collection = collection;
        Years = await db.Collections.Where(c => c.Id == id).ToCards().Select(c => c.Years).FirstAsync(cancellationToken);
        Items = await db.PublishedItems
            .Filter(new ItemFilter(CollectionId: id))
            .Sort(collection.Sort.ToItemSort(), query: null)
            .Select(ItemSummary.FromItem)
            .ToPagedListAsync(PageNumber, SearchModel.PageSize, cancellationToken);
        Invitations = await db.ContributionLinks
            .Where(l => l.CollectionId == id && l.IsActive && l.InviteOnCollectionPage)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new Invitation(l.Title, l.Token))
            .ToListAsync(cancellationToken);
        return Page();
    }
}
