using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

// Upload requests may carry several large scans. Each file is still checked against Uploads:MaxFileSizeMegabytes.
// Authorization runs before the body is read, so anonymous requests are rejected without accepting a large upload.
[AcceptsUploads]
[RequestSizeLimit(MaxUploadRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
public class EditModel(
    CommonageDbContext db,
    MediaUploadService uploads,
    AuditLog audit,
    ArchiveHealth health,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const long MaxUploadRequestBytes = 1024L * 1024 * 1024;
    private const int MaxTagsPerItem = 30;
    private const int HistoryLength = 20;

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

    /// <summary>The latest entries of the activity log about this item, newest first.</summary>
    public IReadOnlyList<AuditEntry> History { get; private set; } = [];

    /// <summary>When the disk is nearly full, curators are told so instead of being offered the upload form.</summary>
    public bool CanUpload => health.CanAcceptUploads();

    /// <summary>The item as someone else saved it while this person was editing it. Their own text stays in the form.</summary>
    public ArchiveItem? SavedByOthers { get; private set; }

    /// <summary>Whether the form holds text that is not saved, even though the page was just loaded.</summary>
    public bool FormHasUnsavedText => SavedByOthers is not null || !ModelState.IsValid;

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

        /// <summary>The item's version when the form was opened, to notice changes made by someone else since.</summary>
        public uint? Version { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id, int? collectionId)
    {
        if (id is not null)
        {
            var item = await FindItemAsync(id.Value);
            if (item is null)
            {
                // Links in the activity log and the contributor search still lead somewhere after a deletion.
                if (!await db.BinnedItems.AnyAsync(i => i.Id == id))
                    return NotFound();
                TempData["Message"] = localizer["This item is in the bin."].Value;
                return RedirectToPage("/Admin/Bin");
            }

            Input = new InputModel
            {
                Title = item.Title,
                Description = item.Description,
                CollectionId = item.CollectionId,
                Date = item.DateText,
                Tags = string.Join(", ", item.Tags.Select(t => t.Name).Order(StringComparer.CurrentCultureIgnoreCase)),
                Version = item.Version,
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

        if (command == "publish" && item is { CanBePublished: false })
            return BadRequest();

        // If someone else saved the item since this form was opened, saving fails instead of overwriting their work.
        if (item is not null && Input.Version is { } version)
            db.Entry(item).Property(i => i.Version).OriginalValue = version;

        var now = DateTime.UtcNow;
        var isNew = item is null;
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
        audit.Record(isNew ? AuditAction.ItemCreated : AuditAction.ItemChanged, item.Title, item);

        switch (command)
        {
            case "publish":
                item.Publish(now);
                audit.Record(AuditAction.ItemPublished, item.Title, item);
                TempData["Message"] = localizer["The item is published."].Value;
                break;
            case "unpublish" when item.Status == ItemStatus.Published:
                item.Unpublish();
                audit.Record(AuditAction.ItemUnpublished, item.Title, item);
                TempData["Message"] = localizer["The item is no longer public."].Value;
                break;
            case "accept" when item.Status is ItemStatus.Submitted or ItemStatus.Rejected:
                item.Accept(now);
                audit.Record(AuditAction.ContributionAccepted, item.Title, item);
                TempData["Message"] = localizer["The contribution is accepted as a draft."].Value;
                break;
            case "reject" when item.Status == ItemStatus.Submitted:
                item.Reject(now);
                audit.Record(AuditAction.ContributionRejected, item.Title, item);
                TempData["Message"] = localizer["The contribution is rejected. It stays hidden until you delete it."].Value;
                break;
            default:
                TempData["Message"] = localizer["Your changes are saved."].Value;
                break;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData.Remove("Message");
            return await ShowChangesByOthersAsync(item.Id);
        }
        return RedirectToPage(new { id = item.Id });
    }

    /// <summary>Nothing was saved. Shows what the others saved, and keeps what this person typed in the form.</summary>
    private async Task<IActionResult> ShowChangesByOthersAsync(int id)
    {
        db.ChangeTracker.Clear();
        var current = await FindItemAsync(id);
        if (current is null)
            return NotFound();

        SavedByOthers = current;
        // The next save is a deliberate decision to replace the other version, so it may go through.
        ModelState.Remove("Input.Version");
        Input.Version = current.Version;
        await ShowStateAsync(current);
        await LoadCollectionsAsync();
        return Page();
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
        {
            audit.Record(AuditAction.PhotosAdded, item.Title, item);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Message"] = localizer["Photos uploaded: {0}. Previews are being created.", uploaded].Value;
        }
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return RedirectToPage(new { id });
    }

    /// <summary>Moves the item to the bin, where it can be restored or deleted for good, see <see cref="ArchiveBin"/>.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();

        item.MoveToBin(DateTime.UtcNow);
        audit.Record(AuditAction.ItemMovedToBin, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["The item is in the bin. You can restore it there for {0} days.", ArchiveBin.RetentionDays].Value;
        return RedirectToPage("/Admin/Index");
    }

    /// <summary>Hides a contribution for good because the contributor withdrew their consent.</summary>
    public async Task<IActionResult> OnPostWithdrawAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.Include(i => i.Contribution).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();
        if (item.Contribution is null)
            return BadRequest();

        var now = DateTime.UtcNow;
        item.Withdraw(now);
        item.UpdatedAt = now;
        audit.Record(AuditAction.ContributionWithdrawn, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["The contribution is marked as withdrawn. It is hidden and cannot be published again."].Value;
        return RedirectToPage(new { id });
    }

    /// <summary>Removes the contributor's name and email address, for example because they asked for it.</summary>
    public async Task<IActionResult> OnPostRemoveContributorDetailsAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.Include(i => i.Contribution).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();
        if (item.Contribution is null)
            return BadRequest();

        item.Contribution.RemovePersonalDetails(DateTime.UtcNow);
        audit.Record(AuditAction.ContributorDetailsRemoved, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer["The contributor's name and email address are removed."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteMediaAsync(int id, int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.Include(f => f.ArchiveItem).FirstOrDefaultAsync(f => f.Id == mediaFileId && f.ArchiveItemId == id, cancellationToken);
        if (file is null)
            return NotFound();

        file.DeletedAt = DateTime.UtcNow;
        audit.Record(AuditAction.PhotoMovedToBin, file.ArchiveItem.Title, file.ArchiveItem);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer["The photo is in the bin. You can restore it there for {0} days.", ArchiveBin.RetentionDays].Value;
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
            History = await db.AuditEntries
                .Where(e => e.ArchiveItemId == item.Id)
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
                .Take(HistoryLength)
                .ToListAsync();
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
