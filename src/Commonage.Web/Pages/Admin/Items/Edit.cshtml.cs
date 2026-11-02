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
    private const int MaxTagsPerItem = 30;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? ItemId { get; private set; }
    public ItemStatus? CurrentStatus { get; private set; }

    /// <summary>The period the saved date stands for, shown so curators can check how it was understood.</summary>
    public DateRange? CurrentDateRange { get; private set; }
    public SelectList CollectionOptions { get; private set; } = null!;
    public IReadOnlyList<MediaFile> MediaFiles { get; private set; } = [];

    /// <summary>Who sent the item in, for items that came through a contribution link.</summary>
    public Contribution? Contribution { get; private set; }

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

        [StringLength(DateRange.MaxTextLength, ErrorMessage = "The date can be at most {1} characters long.")]
        [Display(Name = "Date")]
        public string? Date { get; set; }

        [Display(Name = "Tags")]
        public string? Tags { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id, int? collectionId)
    {
        if (id is not null)
        {
            var item = await FindItemAsync(id.Value);
            if (item is null)
                return NotFound();

            Input = new InputModel
            {
                Title = item.Title,
                Description = item.Description,
                CollectionId = item.CollectionId,
                Date = item.DateText,
                Tags = string.Join(", ", item.Tags.Select(t => t.Name).Order(StringComparer.CurrentCultureIgnoreCase)),
            };
            await ShowStateAsync(item);
        }
        else
        {
            Input.CollectionId = collectionId;
        }

        await LoadCollectionsAsync();
        return Page();
    }

    /// <param name="command">"publish", "unpublish", "accept" or "reject" changes the status; anything else only saves.</param>
    public async Task<IActionResult> OnPostAsync(int? id, string? command)
    {
        ArchiveItem? item = null;
        if (id is not null)
        {
            item = await FindItemAsync(id.Value);
            if (item is null)
                return NotFound();
        }

        if (Input.CollectionId is not null && !await db.Collections.AnyAsync(c => c.Id == Input.CollectionId))
            ModelState.AddModelError("Input.CollectionId", localizer["Please choose a collection."]);
        if (!string.IsNullOrWhiteSpace(Input.Date) && !DateRange.TryParse(Input.Date, out _))
            ModelState.AddModelError("Input.Date", localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."]);

        var tagNames = Tag.ParseList(Input.Tags);
        if (tagNames.Any(name => name.Length > Tag.MaxLength))
            ModelState.AddModelError("Input.Tags", localizer["A tag can be at most {0} characters long.", Tag.MaxLength]);
        if (tagNames.Count > MaxTagsPerItem)
            ModelState.AddModelError("Input.Tags", localizer["An item can have at most {0} tags.", MaxTagsPerItem]);

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
        item.TrySetDate(Input.Date);
        item.SetTags(await FindOrCreateTagsAsync(tagNames));
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
            case "accept" when item.Status is ItemStatus.Submitted or ItemStatus.Rejected:
                item.Accept(now);
                TempData["Message"] = localizer["The contribution is accepted as a draft."].Value;
                break;
            case "reject" when item.Status == ItemStatus.Submitted:
                item.Reject(now);
                TempData["Message"] = localizer["The contribution is rejected. It stays hidden until you delete it."].Value;
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
                errors.Add(UploadErrorMessages.Describe(localizer, error, file.FileName));
        }

        if (files.Count == 0)
            errors.Add(localizer["Please choose at least one photo."]);
        if (uploaded > 0)
            TempData["Message"] = localizer["Photos uploaded: {0}. Previews are being created.", uploaded].Value;
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return RedirectToPage(new { id });
    }

    /// <summary>Deletes a contribution that will not be published, such as spam, with all its files.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.Include(i => i.MediaFiles).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();
        if (item.Status is not (ItemStatus.Submitted or ItemStatus.Rejected))
            return BadRequest();

        foreach (var file in item.MediaFiles.ToList())
            await uploads.DeleteAsync(file, cancellationToken);
        db.ArchiveItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["The contribution is deleted."].Value;
        return RedirectToPage("/Admin/Index");
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

    private async Task ShowStateAsync(ArchiveItem? item)
    {
        ItemId = item?.Id;
        CurrentStatus = item?.Status;
        Contribution = item?.Contribution;
        if (item is not null)
        {
            MediaFiles = await db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).OrderBy(f => f.Id).ToListAsync();
            if (item.DateText is not null)
                CurrentDateRange = new DateRange(item.DateFrom, item.DateTo);
        }
    }

    private Task<ArchiveItem?> FindItemAsync(int id) =>
        db.ArchiveItems
            .Include(i => i.Tags)
            .Include(i => i.Contribution).ThenInclude(c => c!.ContributionLink)
            .FirstOrDefaultAsync(i => i.Id == id);

    /// <summary>Reuses existing tags regardless of case, so "mill street" joins the existing tag "Mill Street".</summary>
    private async Task<List<Tag>> FindOrCreateTagsAsync(IReadOnlyList<string> names)
    {
        var normalizedNames = names.Select(Tag.Normalize).ToList();
        var existing = await db.Tags.Where(t => normalizedNames.Contains(t.NormalizedName)).ToDictionaryAsync(t => t.NormalizedName);
        return names
            .Select(name => existing.GetValueOrDefault(Tag.Normalize(name)) ?? new Tag { Name = name, NormalizedName = Tag.Normalize(name) })
            .ToList();
    }

    private async Task LoadCollectionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
    }
}
