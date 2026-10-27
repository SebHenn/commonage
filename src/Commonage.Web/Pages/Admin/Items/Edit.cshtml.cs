using System.ComponentModel.DataAnnotations;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

// Upload requests may carry several large scans. Each file is still checked against Uploads:MaxFileSizeMegabytes.
// Authorization runs before the body is read, so anonymous requests are rejected without accepting a large upload.
[RequestSizeLimit(MaxUploadRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
public class EditModel(CommonageDbContext db, MediaUploadService uploads, IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const long MaxUploadRequestBytes = 1024L * 1024 * 1024;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? ItemId { get; private set; }
    public ItemStatus? CurrentStatus { get; private set; }
    public SelectList CollectionOptions { get; private set; } = null!;
    public IReadOnlyList<MediaFile> MediaFiles { get; private set; } = [];

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(300, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(20000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Please choose a collection.")]
        [Display(Name = "Collection")]
        public int? CollectionId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id, int? collectionId)
    {
        if (id is not null)
        {
            var item = await db.ArchiveItems.FindAsync(id);
            if (item is null)
                return NotFound();

            Input = new InputModel { Title = item.Title, Description = item.Description, CollectionId = item.CollectionId };
            await ShowStateAsync(item);
        }
        else
        {
            Input.CollectionId = collectionId;
        }

        await LoadCollectionsAsync();
        return Page();
    }

    /// <param name="command">"publish" or "unpublish" changes the status; anything else only saves.</param>
    public async Task<IActionResult> OnPostAsync(int? id, string? command)
    {
        ArchiveItem? item = null;
        if (id is not null)
        {
            item = await db.ArchiveItems.FindAsync(id);
            if (item is null)
                return NotFound();
        }

        if (Input.CollectionId is not null && !await db.Collections.AnyAsync(c => c.Id == Input.CollectionId))
            ModelState.AddModelError("Input.CollectionId", localizer["Please choose a collection."]);

        if (!ModelState.IsValid)
        {
            await ShowStateAsync(item);
            await LoadCollectionsAsync();
            return Page();
        }

        var now = DateTime.UtcNow;
        if (item is null)
        {
            item = new ArchiveItem { Title = "", Status = ItemStatus.Draft, CreatedAt = now };
            db.ArchiveItems.Add(item);
        }

        item.Title = Input.Title.Trim();
        item.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        item.CollectionId = Input.CollectionId!.Value;
        item.UpdatedAt = now;

        switch (command)
        {
            case "publish":
                item.Publish(now);
                TempData["Message"] = localizer["The item is published."].Value;
                break;
            case "unpublish":
                item.Unpublish();
                TempData["Message"] = localizer["The item is no longer public."].Value;
                break;
            default:
                TempData["Message"] = localizer["Your changes are saved."].Value;
                break;
        }

        await db.SaveChangesAsync();
        return RedirectToPage(new { id = item.Id });
    }

    public async Task<IActionResult> OnPostUploadAsync(int id, List<IFormFile> files, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.FindAsync([id], cancellationToken);
        if (item is null)
            return NotFound();

        var errors = new List<string>();
        var uploaded = 0;
        foreach (var file in files)
        {
            var error = await uploads.AddAsync(item, file, cancellationToken);
            if (error == UploadError.None)
                uploaded++;
            else
                errors.Add(DescribeUploadError(error, file.FileName));
        }

        if (files.Count == 0)
            errors.Add(localizer["Please choose at least one photo."]);
        if (uploaded > 0)
            TempData["Message"] = localizer["Photos uploaded: {0}. Previews are being created.", uploaded].Value;
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteMediaAsync(int id, int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId && f.ArchiveItemId == id, cancellationToken);
        if (file is null)
            return NotFound();

        await uploads.DeleteAsync(file, cancellationToken);
        TempData["Message"] = localizer["The photo is deleted."].Value;
        return RedirectToPage(new { id });
    }

    private string DescribeUploadError(UploadError error, string fileName) => error switch
    {
        UploadError.Empty => localizer["{0} is empty.", fileName],
        UploadError.TooLarge => localizer["{0} is too large.", fileName],
        _ => localizer["{0} is not a supported photo format. Please use JPEG, PNG, TIFF or WebP.", fileName],
    };

    private async Task ShowStateAsync(ArchiveItem? item)
    {
        ItemId = item?.Id;
        CurrentStatus = item?.Status;
        if (item is not null)
            MediaFiles = await db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).OrderBy(f => f.Id).ToListAsync();
    }

    private async Task LoadCollectionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
    }
}
