using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin;

public record GettingStartedStep(string Text, bool Done, string? Page);

public record GettingStarted(IReadOnlyList<GettingStartedStep> Steps, bool RestoreTested);

public class IndexModel(CommonageDbContext db, ArchiveHealth health, ArchiveProfile profile, IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const int PageSize = 50;

    public PagedList<AdminItemRow> Items { get; private set; } = PagedList<AdminItemRow>.Empty;
    public bool HasCollections { get; private set; }

    /// <summary>Contributions nobody has reviewed yet.</summary>
    public int WaitingCount { get; private set; }

    public ArchiveHealth Health => health;

    public GettingStarted GettingStarted { get; private set; } = new([], false);

    public SelectList CollectionOptions { get; private set; } = null!;

    /// <summary>This list with its filters, so an item opened from it can go on to the next one and come back.</summary>
    public string ListUrl => Request.Path + Request.QueryString;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemStatus? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "collection")]
    public int? CollectionId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tag { get; set; }

    [BindProperty(SupportsGet = true)]
    public ItemNeed? Needs { get; set; }

    [BindProperty(SupportsGet = true)]
    public AdminItemSort? Sort { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public AdminItemFilter Filter => new(Text, Status, CollectionId, Tag, Needs, Sort);

    private async Task<GettingStarted> LoadGettingStartedAsync(CancellationToken cancellationToken)
    {
        var identity = await profile.GetAsync();
        const string settings = "/Admin/Settings";
        return new GettingStarted(
        [
            new(localizer["Give the archive its name"], identity.NameIsSet, settings),
            new(localizer["Say how people can reach you"], identity.Text(ArchiveTextKind.Contact) is not null, settings),
            new(localizer["Read the privacy text and adapt it"], identity.PrivacyReviewed, settings),
            new(localizer["Write an imprint, or note that you need none"], identity.Text(ArchiveTextKind.Imprint) is not null || identity.ImprintNotNeeded, settings),
            new(localizer["Create the first collection"], HasCollections, "/Admin/Collections/Edit"),
            new(localizer["Create the first contribution link"], await db.ContributionLinks.AnyAsync(cancellationToken), "/Admin/Contributions/Edit"),
            new(localizer["Set up nightly backups (docs/deployment.md)"], health.LastBackupAt is not null, null),
            new(localizer["Test restoring a backup, on another machine (docs/deployment.md)"], identity.RestoreTestedAt is not null, null),
        ], identity.RestoreTestedAt is not null);
    }

    public bool HasFilters => Text is not null || Status is not null || CollectionId is not null || Tag is not null || Needs is not null;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync(cancellationToken);
        HasCollections = collections.Count > 0;
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title), CollectionId);
        WaitingCount = await db.ArchiveItems.CountAsync(i => i.Status == ItemStatus.Submitted, cancellationToken);
        GettingStarted = await LoadGettingStartedAsync(cancellationToken);

        var items = await db.QueryAsync(Filter, cancellationToken);
        Items = await items
            .Select(i => new AdminItemRow(
                i.Id,
                i.Title,
                i.Collection.Title,
                i.Status,
                i.UpdatedAt,
                i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault(),
                i.MediaFiles.Count,
                i.Title == "" ? i.MediaFiles.OrderBy(f => f.Id).Select(f => f.OriginalFileName).FirstOrDefault() : null,
                // Worth a look before a decision on a contribution. Contributors are never told.
                i.Status == ItemStatus.Submitted && i.MediaFiles.Any(f => db.MediaFiles.Any(other => other.Sha256 == f.Sha256 && other.ArchiveItemId != i.Id))))
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
