using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Pages.Admin.Items;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Tags;

/// <summary>Every tag of the archive, to tidy them up: what kind each is, how often it is used, and the unused ones to delete.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const int PageSize = 100;

    /// <param name="ItemCount">Items outside the bin, as the curators' item list finds them.</param>
    public record TagRow(int Id, string Name, TagKind Kind, int ItemCount);

    [BindProperty(SupportsGet = true, Name = "kind")]
    public TagKind? Kind { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true, Name = "unused")]
    public bool UnusedOnly { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public PagedList<TagRow> Tags { get; private set; } = PagedList<TagRow>.Empty;

    public int UnusedCount { get; private set; }

    public bool IsFiltered => Kind is not null || !string.IsNullOrWhiteSpace(Text) || UnusedOnly;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Without the filter for the bin, so that a tag only items in the bin have is not taken for unused.
        var tags = db.Tags.IgnoreQueryFilters().AsNoTracking();
        if (Kind is { } kind)
            tags = tags.Where(t => t.Kind == kind);
        if (Text?.Trim() is { Length: > 0 } text)
        {
            var pattern = LikePattern.Containing(text);
            tags = tags.Where(t => EF.Functions.ILike(t.Name, pattern));
        }
        if (UnusedOnly)
            tags = tags.Where(t => !t.Items.Any());

        Tags = await tags
            .OrderBy(t => t.NormalizedName)
            .Select(t => new TagRow(t.Id, t.Name, t.Kind, t.Items.Count(i => i.DeletedAt == null)))
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
        UnusedCount = await db.UnusedTags().CountAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteUnusedAsync(CancellationToken cancellationToken)
    {
        var unused = await db.UnusedTags().OrderBy(t => t.NormalizedName).ToListAsync(cancellationToken);
        if (unused.Count > 0)
        {
            db.Tags.RemoveRange(unused);
            audit.Record(AuditAction.UnusedTagsDeleted, ItemFields.ListOf(unused.Select(t => t.Name)));
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["Message"] = localizer.Plural("{0, plural, =0 {No tags were unused.} one {# unused tag deleted.} other {# unused tags deleted.}}", unused.Count);
        return RedirectToPage();
    }
}
