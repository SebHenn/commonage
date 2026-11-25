using Commonage.Web.Data;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages;

public class IndexModel(CommonageDbContext db, Commonage.Web.Operations.ArchiveProfile profile) : PageModel
{
    public Commonage.Web.Operations.ArchiveIdentity Identity { get; private set; } = null!;

    private const int MaxTags = 30;

    private const int RecentCount = 8;

    public IReadOnlyList<CollectionCard> Collections { get; private set; } = [];

    public IReadOnlyList<ItemSummary> Recent { get; private set; } = [];

    /// <summary>The first stories visitors may read, and whether there are more than shown. No count: a count would tell of hidden ones.</summary>
    public IReadOnlyList<StoryCard> Stories { get; private set; } = [];
    public bool MoreStories { get; private set; }

    private const int StoryCount = 3;

    /// <summary>How many published items each decade has.</summary>
    public IReadOnlyList<DecadeCount> Decades { get; private set; } = [];

    /// <summary>The most used tags of published items, in alphabetical order.</summary>
    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>The kinds with a page of their own, places and people, that published items have tags of.</summary>
    public IReadOnlyList<Domain.TagKind> TagKinds { get; private set; } = [];

    /// <summary>Whether to invite visitors to "Can you help?": only while the archive asks them, and only if an item waits.</summary>
    public bool HelpWanted { get; private set; }

    public async Task OnGetAsync()
    {
        Identity = await profile.GetAsync();
        HelpWanted = Identity.AcceptVisitorNotes && await db.VisibleItems.AnyAsync(i => i.HelpWanted);
        Collections = await db.LoadCollectionCardsAsync(HttpContext.RequestAborted);
        Recent = await db.VisibleItems
            .OrderByDescending(i => i.PublishedAt)
            .ThenByDescending(i => i.Id)
            .Take(RecentCount)
            .Select(ItemSummary.FromItem)
            .ToListAsync();

        var stories = await db.LoadFirstAsync(StoryCount + 1, HttpContext.RequestAborted);
        MoreStories = stories.Count > StoryCount;
        Stories = stories.Take(StoryCount).ToList();

        Decades = await DecadeTimeline.LoadAsync(db.VisibleItems, HttpContext.RequestAborted);

        var popularTags = await db.VisibleItems
            .SelectMany(i => i.Tags)
            .GroupBy(t => t.Name)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .Take(MaxTags)
            .ToListAsync();
        Tags = popularTags.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        TagKinds = await db.VisibleItems
            .SelectMany(i => i.Tags)
            .Where(t => t.Kind != Domain.TagKind.Topic)
            .Select(t => t.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync();
    }
}
