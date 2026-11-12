using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Collections;

/// <summary>A collection's title and description, the order of its items, its cover, and deleting it once it is empty.</summary>
public class EditModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    /// <summary>How many photos are offered as a cover. A collection of thousands would otherwise make a page of thousands.</summary>
    public const int MaxCoverChoices = 100;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? CollectionId { get; private set; }

    /// <summary>Photos the cover can be chosen from: those of the collection's items, published ones first.</summary>
    public IReadOnlyList<CoverChoice> CoverChoices { get; private set; } = [];

    /// <summary>What still uses the collection. Only an unused one can be deleted.</summary>
    public CollectionUse Use { get; private set; } = new(0, 0, 0);

    public record CoverChoice(int MediaFileId, string ItemTitle, bool Published);

    public record CollectionUse(int Items, int InBin, int Links)
    {
        public bool IsUnused => Items == 0 && InBin == 0 && Links == 0;
    }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(200, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(4000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Display(Name = "Order of the items")]
        public CollectionSort Sort { get; set; }

        [Display(Name = "Cover")]
        public int? CoverMediaFileId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken cancellationToken)
    {
        if (id is null)
            return Page();

        var collection = await db.Collections.FindAsync([id], cancellationToken);
        if (collection is null)
            return NotFound();

        Input = new InputModel
        {
            Title = collection.Title,
            Description = collection.Description,
            Sort = collection.Sort,
            CoverMediaFileId = collection.CoverMediaFileId,
        };
        await LoadAsync(collection, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, CancellationToken cancellationToken)
    {
        Collection? collection = null;
        if (id is not null)
        {
            collection = await db.Collections.FindAsync([id], cancellationToken);
            if (collection is null)
                return NotFound();
        }

        // Only a photo of one of the collection's own items. A new collection has none yet.
        if (Input.CoverMediaFileId is { } cover && cover != collection?.CoverMediaFileId
            && (id is null || !await db.MediaFiles.AnyAsync(f => f.Id == cover && f.ArchiveItem.CollectionId == id, cancellationToken)))
            ModelState.AddModelError("Input.CoverMediaFileId", localizer["Please choose one of the photos shown."]);

        if (!ModelState.IsValid)
        {
            if (collection is not null)
                await LoadAsync(collection, cancellationToken);
            return Page();
        }

        var isNew = collection is null;
        if (collection is null)
        {
            // A new collection comes after the others.
            var last = await db.Collections.MaxAsync(c => (int?)c.Position, cancellationToken) ?? -1;
            collection = new Collection { Title = "", CreatedAt = clock.GetUtcNow().UtcDateTime, Position = last + 1 };
            db.Collections.Add(collection);
        }

        collection.Title = Input.Title.Trim();
        collection.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        collection.Sort = Input.Sort;
        collection.CoverMediaFileId = Input.CoverMediaFileId;
        audit.Record(isNew ? AuditAction.CollectionCreated : AuditAction.CollectionChanged, collection.Title);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage("/Admin/Collections/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        var collection = await db.Collections.FindAsync([id], cancellationToken);
        if (collection is null)
            return NotFound();

        Input = new InputModel
        {
            Title = collection.Title,
            Description = collection.Description,
            Sort = collection.Sort,
            CoverMediaFileId = collection.CoverMediaFileId,
        };
        await LoadAsync(collection, cancellationToken);
        if (!Use.IsUnused)
        {
            ModelState.AddModelError("", localizer["Only an empty collection can be deleted. Move its items to another collection first."]);
            return Page();
        }

        db.Collections.Remove(collection);
        audit.Record(AuditAction.CollectionDeleted, collection.Title);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // An item or link was added to it at the same moment. The database refuses, so nothing is lost.
            db.ChangeTracker.Clear();
            await LoadAsync(collection, cancellationToken);
            ModelState.AddModelError("", localizer["Only an empty collection can be deleted. Move its items to another collection first."]);
            return Page();
        }

        TempData["Message"] = localizer["The collection “{0}” is deleted.", collection.Title].Value;
        return RedirectToPage("/Admin/Collections/Index");
    }

    private async Task LoadAsync(Collection collection, CancellationToken cancellationToken)
    {
        var id = collection.Id;
        CollectionId = id;
        Use = new CollectionUse(
            await db.ArchiveItems.CountAsync(i => i.CollectionId == id, cancellationToken),
            await db.ArchiveItems.IgnoreQueryFilters().CountAsync(i => i.CollectionId == id && i.DeletedAt != null, cancellationToken),
            await db.ContributionLinks.CountAsync(l => l.CollectionId == id, cancellationToken));

        // The chosen one first, so it is always offered, then published items' photos, newest item first, each item's in order.
        var current = collection.CoverMediaFileId;
        CoverChoices = await db.MediaFiles
            .Where(f => f.ArchiveItem.CollectionId == id && f.Status == ProcessingStatus.Ready)
            .OrderByDescending(f => f.Id == current)
            .ThenByDescending(f => f.ArchiveItem.Status == ItemStatus.Published)
            .ThenByDescending(f => f.ArchiveItemId).ThenBy(f => f.Position).ThenBy(f => f.Id)
            .Take(MaxCoverChoices)
            .Select(f => new CoverChoice(f.Id, f.ArchiveItem.Title, f.ArchiveItem.Status == ItemStatus.Published))
            .ToListAsync(cancellationToken);
    }
}
