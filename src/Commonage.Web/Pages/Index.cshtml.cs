using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages;

public class IndexModel(CommonageDbContext db, Commonage.Web.Operations.ArchiveProfile profile) : PageModel
{
    public Commonage.Web.Operations.ArchiveIdentity Identity { get; private set; } = null!;

    private const int MaxTags = 30;

    public record CollectionSummary(int Id, string Title, string? Description, int PublishedItemCount);

    public IReadOnlyList<CollectionSummary> Collections { get; private set; } = [];

    /// <summary>The most used tags of published items, in alphabetical order.</summary>
    public IReadOnlyList<string> Tags { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Identity = await profile.GetAsync();
        Collections = await db.Collections
            .OrderBy(c => c.Title)
            .Select(c => new CollectionSummary(
                c.Id, c.Title, c.Description, c.Items.Count(i => i.Status == ItemStatus.Published)))
            .ToListAsync();

        var popularTags = await db.PublishedItems
            .SelectMany(i => i.Tags)
            .GroupBy(t => t.Name)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .Take(MaxTags)
            .ToListAsync();
        Tags = popularTags.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
