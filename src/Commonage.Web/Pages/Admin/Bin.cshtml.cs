using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin;

/// <summary>Deleted items and photos: restore them, or delete them for good before their time is up.</summary>
public class BinModel(
    CommonageDbContext db,
    ArchiveBin bin,
    AuditLog audit,
    TimeProvider clock,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public const int MaxShown = 200;

    public IReadOnlyList<ArchiveItem> Items { get; private set; } = [];
    public IReadOnlyList<MediaFile> Photos { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Items = await db.BinnedItems
            .AsNoTracking()
            .Include(i => i.Collection)
            .Include(i => i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready && f.DeletedAt == null).OrderBy(f => f.Position).ThenBy(f => f.Id).Take(1))
            .OrderByDescending(i => i.DeletedAt)
            .Take(MaxShown)
            .ToListAsync(cancellationToken);
        Photos = await db.BinnedPhotos
            .AsNoTracking()
            .Include(f => f.ArchiveItem)
            .OrderByDescending(f => f.DeletedAt)
            .Take(MaxShown)
            .ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRestoreItemAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.BinnedItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();

        item.RestoreFromBin(clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditAction.ItemRestored, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = item.Status == ItemStatus.Published
            ? localizer["The item is restored and public again."].Value
            : localizer["The item is restored."].Value;
        return RedirectToPage("/Admin/Items/Edit", new { id });
    }

    public async Task<IActionResult> OnPostDeleteItemAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.BinnedItems.Include(i => i.MediaFiles).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();

        await bin.DeleteItemAsync(item, AuditAction.ItemDeleted, cancellationToken);
        TempData["Message"] = localizer["The item is deleted for good."].Value;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestorePhotoAsync(int id, CancellationToken cancellationToken)
    {
        var file = await db.BinnedPhotos.Include(f => f.ArchiveItem).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null)
            return NotFound();

        file.DeletedAt = null;
        audit.Record(AuditAction.PhotoRestored, file.ArchiveItem.Title, file.ArchiveItem);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["The photo is restored."].Value;
        return RedirectToPage("/Admin/Items/Edit", new { id = file.ArchiveItemId });
    }

    public async Task<IActionResult> OnPostDeletePhotoAsync(int id, CancellationToken cancellationToken)
    {
        var file = await db.BinnedPhotos.Include(f => f.ArchiveItem).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (file is null)
            return NotFound();

        await bin.DeletePhotoAsync(file, AuditAction.PhotoDeleted, cancellationToken);
        TempData["Message"] = localizer["The photo is deleted for good."].Value;
        return RedirectToPage();
    }
}
