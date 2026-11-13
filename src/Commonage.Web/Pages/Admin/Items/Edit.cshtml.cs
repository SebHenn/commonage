using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

/// <summary>What a photo's card shows on the item page.</summary>
public record PhotoCard(MediaFile File, int ItemId, string? Back, string? TypedAltText, string? TypedCaption, IdenticalFile? SameAs, int Number, int Total);

// Upload requests may carry several large scans. Each file is still checked against Uploads:MaxFileSizeMegabytes.
// Authorization runs before the body is read, so anonymous requests are rejected without accepting a large upload.
[AcceptsUploads]
[RequestSizeLimit(MaxUploadRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
public class EditModel(
    CommonageDbContext db,
    MediaUploadService uploads,
    MediaProcessingQueue previews,
    AuditLog audit,
    ArchiveHealth health,
    ArchiveProfile profile,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    /// <summary>The limit for what is sent in one request: a new item's photos, and everything without JavaScript.</summary>
    public const long MaxUploadRequestBytes = 1024L * 1024 * 1024;
    private const int HistoryLength = 20;
    private const int MaxAltTextLength = 1000;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? ItemId { get; private set; }
    public ItemStatus? CurrentStatus { get; private set; }

    /// <summary>When a rejected or withdrawn contribution goes to the bin, if the archive keeps them for a set time.</summary>
    public DateTime? GoesToBinOn { get; private set; }

    /// <summary>The period the saved date stands for, shown so curators can check how it was understood.</summary>
    public DateRange? CurrentDateRange { get; private set; }
    public SelectList CollectionOptions { get; private set; } = null!;
    public IReadOnlyList<MediaFile> MediaFiles { get; private set; } = [];

    /// <summary>Which of the photos the archive holds more than once, by the id of the photo.</summary>
    public IReadOnlyDictionary<int, IdenticalFile> IdenticalFiles { get; private set; } = new Dictionary<int, IdenticalFile>();

    /// <summary>Who sent the item in, for items that came through a contribution link.</summary>
    public Contribution? Contribution { get; private set; }

    /// <summary>Notes from visitors about this item that no curator has dealt with yet.</summary>
    public int OpenNotes { get; private set; }

    /// <summary>The latest entries of the activity log about this item, newest first.</summary>
    public IReadOnlyList<AuditEntry> History { get; private set; } = [];

    /// <summary>When the disk is nearly full, curators are told so instead of being offered the upload form.</summary>
    public bool CanUpload => health.CanAcceptUploads();

    /// <summary>The item as someone else saved it while this person was editing it. Their own text stays in the form.</summary>
    public ArchiveItem? SavedByOthers { get; private set; }

    /// <summary>The list this item was opened from, with its filters, to go on to the next item and to come back to.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Back
    {
        get => _back;
        set => _back = AdminItemList.FilterFrom(value) is null ? null : value;
    }

    private string? _back;

    public int? PreviousId { get; private set; }
    public int? NextId { get; private set; }

    /// <summary>The title as saved, for the heading, while the form may hold another one.</summary>
    public string? SavedTitle { get; private set; }

    /// <summary>What applies when the item has no statement of its own.</summary>
    public string? DefaultRightsLabel { get; private set; }

    /// <summary>The statement in the form is only a suggestion, from what a contributor answered.</summary>
    public bool RightsWereProposed { get; private set; }

    /// <summary>A draft from "Add many photos" that nobody has named yet.</summary>
    public bool IsUntitled => SavedTitle?.Length == 0;

    /// <summary>Going through drafts to name them: type a title, press Enter, on to the next. Enter presses a form's first button.</summary>
    public bool NamingOneAfterAnother => IsUntitled && NextId is not null;

    /// <summary>Filled-in sections stay open, so nothing there goes unnoticed, and empty ones out of the way.</summary>
    public bool CatalogueHasContent => !string.IsNullOrEmpty(Input.RightsUri) || !string.IsNullOrEmpty(Input.RightsHolder)
        || !string.IsNullOrEmpty(Input.Credit) || !string.IsNullOrEmpty(Input.ReferenceNumber) || !string.IsNullOrEmpty(Input.OriginalLocation)
        || !string.IsNullOrEmpty(Input.ReceivedFrom) || !string.IsNullOrEmpty(Input.ReceivedOn) || !string.IsNullOrEmpty(Input.InternalNotes)
        || !ModelState.IsValid;

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

        [Display(Name = "What may others do with it?")]
        public string? RightsUri { get; set; }

        [StringLength(300, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Who holds the rights?")]
        public string? RightsHolder { get; set; }

        [StringLength(300, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Credit")]
        public string? Credit { get; set; }

        [StringLength(100, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Reference number")]
        public string? ReferenceNumber { get; set; }

        [StringLength(300, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Where the original is kept")]
        public string? OriginalLocation { get; set; }

        [StringLength(300, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Received from")]
        public string? ReceivedFrom { get; set; }

        [StringLength(100, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Received when")]
        public string? ReceivedOn { get; set; }

        [StringLength(20000, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Internal notes")]
        public string? InternalNotes { get; set; }

        /// <summary>What each photo shows, by the id of the photo.</summary>
        public Dictionary<int, string?> AltTexts { get; set; } = [];

        /// <summary>What visitors read under each photo, by the id of the photo.</summary>
        public Dictionary<int, string?> Captions { get; set; } = [];

        [Display(Name = "Still needs a transcription")]
        public bool NeedsTranscription { get; set; }

        [Display(Name = "Ask visitors for help with this item")]
        public bool HelpWanted { get; set; }

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
                RightsUri = item.RightsUri ?? (item.Contribution is { } contribution ? RightsOptions.ProposalFor(contribution.Rights, contribution.Reuse) : null),
                RightsHolder = item.RightsHolder,
                Credit = item.Credit,
                ReferenceNumber = item.ReferenceNumber,
                OriginalLocation = item.OriginalLocation,
                ReceivedFrom = item.ReceivedFrom,
                ReceivedOn = item.ReceivedOn,
                InternalNotes = item.InternalNotes,
                NeedsTranscription = item.NeedsTranscription,
                HelpWanted = item.HelpWanted,
            };
            RightsWereProposed = item.RightsUri is null && item.Contribution is not null;
            await ShowStateAsync(item);
        }
        else
        {
            Input.CollectionId = collectionId;
        }

        await LoadDefaultRightsAsync();
        await LoadCollectionsAsync();
        return Page();
    }

    /// <param name="command">"publish", "unpublish", "accept" or "reject" changes the status; anything else only saves.</param>
    /// <param name="then">"next" goes on to the next item of the list, "another" to a new item.</param>
    /// <param name="files">Photos chosen on the form for a new item.</param>
    public async Task<IActionResult> OnPostAsync(int? id, string? command, string? then, List<IFormFile> files)
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
        if (ItemFields.DateError(localizer, Input.Date) is { } dateError)
            ModelState.AddModelError("Input.Date", dateError);

        var tagNames = Tag.ParseList(Input.Tags);
        foreach (var tagError in ItemFields.TagErrors(localizer, tagNames))
            ModelState.AddModelError("Input.Tags", tagError);

        if (ItemFields.RightsError(localizer, Input.RightsUri) is { } rightsError)
            ModelState.AddModelError("Input.RightsUri", rightsError);
        if (!string.IsNullOrWhiteSpace(Input.ReferenceNumber))
        {
            var number = Input.ReferenceNumber.Trim();
            var usedBy = await db.ArchiveItems.IgnoreQueryFilters().Where(i => i.ReferenceNumber == number && i.Id != id).Select(i => i.Title).FirstOrDefaultAsync();
            if (usedBy is not null)
                ModelState.AddModelError("Input.ReferenceNumber", localizer["This reference number is already used by: {0}", ArchiveItem.ShownTitle(localizer, usedBy)]);
        }
        foreach (var (photo, text) in Input.AltTexts)
        {
            if (text is { Length: > MaxAltTextLength })
                ModelState.AddModelError($"Input.AltTexts[{photo}]", localizer.Plural("A photo description can be at most {0, plural, one {# character} other {# characters}} long.", MaxAltTextLength));
        }
        foreach (var (photo, text) in Input.Captions)
        {
            if (text is { Length: > MediaFile.MaxCaptionLength })
                ModelState.AddModelError($"Input.Captions[{photo}]", localizer.Plural("A caption can be at most {0, plural, one {# character} other {# characters}} long.", MediaFile.MaxCaptionLength));
        }

        // Checked before anything is saved, so a new item is not left behind without the photos it was made for.
        var newPhotos = new List<(IFormFile File, ExaminedUpload Examined)>();
        if (item is null)
        {
            foreach (var file in files)
            {
                var examined = await uploads.ExamineAsync(file);
                if (examined.Error == UploadError.None)
                    newPhotos.Add((file, examined));
                else
                    ModelState.AddModelError("files", UploadErrorMessages.Describe(localizer, examined.Error, file.FileName));
            }
        }

        if (!ModelState.IsValid)
        {
            // A browser cannot fill in chosen files again when a form comes back, so it has to be said.
            if (item is null && files.Count > 0)
                ModelState.AddModelError("files", localizer["Please choose the photos again."]);
            await ShowStateAsync(item);
            await LoadDefaultRightsAsync();
            await LoadCollectionsAsync();
            return Page();
        }

        if (command == "publish" && item?.Status == ItemStatus.Withdrawn)
            return BadRequest();

        // If someone else saved the item since this form was opened, saving fails instead of overwriting their work.
        if (item is not null && Input.Version is { } version)
            db.Entry(item).Property(i => i.Version).OriginalValue = version;

        // Worked out before saving, because saving can move this item within the list or out of it.
        await LoadNeighboursAsync(id);
        var wasWaiting = item?.Status == ItemStatus.Submitted;

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
        item.SetTags(await db.FindOrCreateTagsAsync(tagNames));
        item.UpdatedAt = now;
        item.RightsUri = Clean(Input.RightsUri);
        item.RightsHolder = Clean(Input.RightsHolder);
        item.Credit = Clean(Input.Credit);
        item.ReferenceNumber = Clean(Input.ReferenceNumber);
        item.OriginalLocation = Clean(Input.OriginalLocation);
        item.ReceivedFrom = Clean(Input.ReceivedFrom);
        item.ReceivedOn = Clean(Input.ReceivedOn);
        item.InternalNotes = Clean(Input.InternalNotes);
        item.NeedsTranscription = Input.NeedsTranscription;
        item.HelpWanted = Input.HelpWanted;
        if ((Input.AltTexts.Count > 0 || Input.Captions.Count > 0) && id is not null)
        {
            foreach (var file in await db.MediaFiles.Where(f => f.ArchiveItemId == id).ToListAsync())
            {
                if (Input.AltTexts.TryGetValue(file.Id, out var text))
                    file.AltText = Clean(text);
                if (Input.Captions.TryGetValue(file.Id, out var caption))
                    file.Caption = Clean(caption);
            }
        }
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
                TempData["Message"] = (await profile.GetAsync()).RejectedKeptMonths is { } months
                    ? localizer.Plural("The contribution is rejected. It stays hidden, and goes to the bin in {0, plural, one {# month} other {# months}}.", months)
                    : localizer["The contribution is rejected. It stays hidden until you delete it."].Value;
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

        if (newPhotos.Count > 0)
        {
            audit.Record(AuditAction.PhotosAdded, item.Title, item);
            var identical = new List<string>();
            foreach (var (file, examined) in newPhotos)
            {
                if (await uploads.FindIdenticalAsync(examined) is { } same)
                    identical.Add(localizer["{0}: the archive already has this file: {1}", file.FileName, ArchiveItem.ShownTitle(localizer, same.ItemTitle)]);
                await uploads.StoreAsync(item, file, examined);
            }
            TempData["Message"] = $"{TempData["Message"]} {localizer["Photos uploaded: {0}. Previews are being created.", newPhotos.Count].Value}";
            if (identical.Count > 0)
                TempData["Error"] = ItemFields.ListOf(identical);
        }

        // After a decision on a contribution, the next one is what most curators want to see.
        if (wasWaiting && item.Status != ItemStatus.Submitted)
        {
            var next = await db.ArchiveItems
                .Where(i => i.Status == ItemStatus.Submitted)
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
                .Select(i => (int?)i.Id)
                .FirstOrDefaultAsync();
            var queue = $"{AdminItemList.Page}?status={ItemStatus.Submitted}";
            return next is null ? LocalRedirect(queue) : RedirectToPage(new { id = next, back = queue });
        }

        return then switch
        {
            "next" when NextId is not null => RedirectToPage(new { id = NextId, back = Back }),
            "another" => RedirectToPage(new { collectionId = item.CollectionId }),
            _ => RedirectToPage(new { id = item.Id, back = Back }),
        };
    }

    private static string? Clean(string? text) => ItemFields.Clean(text);

    private async Task LoadDefaultRightsAsync() =>
        DefaultRightsLabel = (await profile.GetAsync()).DefaultRightsUri is { } uri ? RightsOptions.Label(localizer, uri) : null;

    private async Task LoadNeighboursAsync(int? id)
    {
        if (id is { } itemId && AdminItemList.FilterFrom(Back) is { } filter)
            (PreviousId, NextId) = await db.NeighboursAsync(filter, itemId, HttpContext.RequestAborted);
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
        await LoadDefaultRightsAsync();
        await LoadCollectionsAsync();
        return Page();
    }

    private IActionResult BackToItem(int id) => RedirectToPage(new { id, back = Back });

    /// <param name="files">One file from uploads.js, which answers it with a line in its queue, or all of them from the form without JavaScript.</param>
    /// <param name="addIdentical">Also add a file the archive already has. Without JavaScript that is what happens, with a warning.</param>
    public async Task<IActionResult> OnPostUploadAsync(int id, List<IFormFile> files, bool addIdentical, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.FindAsync([id], cancellationToken);
        if (item is null)
            return NotFound();

        if (Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            if (files.Count != 1)
                return BadRequest();
            var examined = await uploads.ExamineAsync(files[0], cancellationToken);
            if (examined.Error != UploadError.None)
                return new JsonResult(UploadReply.Refused(UploadErrorMessages.Describe(localizer, examined.Error, files[0].FileName)));
            if (!addIdentical && await uploads.FindIdenticalAsync(examined, cancellationToken) is { } same)
                return new JsonResult(UploadReply.Identical(localizer, same, Url.Page("/Admin/Items/Edit", new { id = same.ItemId })!));

            await audit.RecordPhotosAddedAsync(item, cancellationToken);
            var stored = await uploads.StoreAsync(item, files[0], examined, cancellationToken);
            return new JsonResult(UploadReply.Added(id, null, stored.Id, Url.Page("/Admin/Items/Edit", "PhotoCard", new { id, mediaFileId = stored.Id, back = Back })));
        }

        var errors = new List<string>();
        var identical = new List<string>();
        var uploaded = 0;
        foreach (var file in files)
        {
            var examined = await uploads.ExamineAsync(file, cancellationToken);
            if (examined.Error != UploadError.None)
            {
                errors.Add(UploadErrorMessages.Describe(localizer, examined.Error, file.FileName));
                continue;
            }
            if (await uploads.FindIdenticalAsync(examined, cancellationToken) is { } same)
                identical.Add(localizer["{0}: the archive already has this file: {1}", file.FileName, ArchiveItem.ShownTitle(localizer, same.ItemTitle)]);
            await uploads.StoreAsync(item, file, examined, cancellationToken);
            uploaded++;
        }

        if (files.Count == 0)
            errors.Add(localizer["Please choose at least one photo."]);
        if (uploaded > 0)
        {
            await audit.RecordPhotosAddedAsync(item, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Message"] = localizer["Photos uploaded: {0}. Previews are being created.", uploaded].Value;
        }
        if (identical.Count > 0)
            errors.Add(ItemFields.ListOf(identical));
        if (errors.Count > 0)
            TempData["Error"] = string.Join(" ", errors);
        return BackToItem(id);
    }

    /// <summary>The card of one photo, for a page that just sent it and should not have to reload to show it.</summary>
    public async Task<IActionResult> OnGetPhotoCardAsync(int id, int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId && f.ArchiveItemId == id, cancellationToken);
        if (file is null)
            return NotFound();

        var identical = await FindIdenticalFilesAsync([file]);
        // The page numbers its cards again when it has added this one.
        var total = await db.MediaFiles.CountAsync(f => f.ArchiveItemId == id, cancellationToken);
        return Partial("_PhotoCard", new PhotoCard(file, id, Back, null, null, identical.GetValueOrDefault(file.Id), total, total));
    }

    /// <summary>Puts a photo elsewhere among the item's photos. The first one is the cover. The item's own version stays as it is, so an open form can still be saved.</summary>
    /// <param name="direction">"earlier", "later" or "first".</param>
    public async Task<IActionResult> OnPostMovePhotoAsync(int id, int mediaFileId, string? direction, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.FindAsync([id], cancellationToken);
        if (item is null)
            return NotFound();

        var photos = await db.MediaFiles.Where(f => f.ArchiveItemId == id).OrderBy(f => f.Position).ThenBy(f => f.Id).ToListAsync(cancellationToken);
        var from = photos.FindIndex(f => f.Id == mediaFileId);
        if (from < 0)
            return NotFound();

        var to = direction switch
        {
            "earlier" => Math.Max(from - 1, 0),
            "later" => Math.Min(from + 1, photos.Count - 1),
            "first" => 0,
            _ => from,
        };
        var photo = photos[from];
        photos.RemoveAt(from);
        photos.Insert(to, photo);
        for (var position = 0; position < photos.Count; position++)
            photos[position].Position = position;

        await audit.RecordPhotosArrangedAsync(item, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return PhotoChanged(id, mediaFileId);
    }

    /// <summary>Turns a photo a quarter to the right. Only its previews change, in the background. The original stays exactly as it came.</summary>
    public async Task<IActionResult> OnPostTurnPhotoAsync(int id, int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.Include(f => f.ArchiveItem).FirstOrDefaultAsync(f => f.Id == mediaFileId && f.ArchiveItemId == id, cancellationToken);
        if (file is null)
            return NotFound();
        if (file.Status != ProcessingStatus.Ready)
            return BadRequest();

        file.Rotation = (file.Rotation + 90) % 360;
        await audit.RecordPhotosArrangedAsync(file.ArchiveItem, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        previews.Enqueue(file.Id);
        return PhotoChanged(id, mediaFileId);
    }

    /// <summary>With JavaScript the page changes by itself and keeps what was typed. Without, it comes back at the same photo.</summary>
    private IActionResult PhotoChanged(int id, int mediaFileId) =>
        Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
            ? new JsonResult(new { ok = true })
            : RedirectToPage(null, null, new { id, back = Back }, $"photo-{mediaFileId}");

    /// <summary>Moves the item to the bin, where it can be restored or deleted for good, see <see cref="ArchiveBin"/>.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();

        item.MoveToBin(DateTime.UtcNow);
        audit.Record(AuditAction.ItemMovedToBin, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer.Plural("The item is in the bin. You can restore it there for {0, plural, one {# day} other {# days}}.", ArchiveBin.RetentionDays);
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
        return BackToItem(id);
    }

    /// <summary>Corrects what a contributor typed, for example a misspelt name, or when they ask for a change.</summary>
    public async Task<IActionResult> OnPostContributorDetailsAsync(
        int id, string? contributorName, string? contributorEmail, bool showName, string? contributorRightsHolder, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.Include(i => i.Contribution).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return NotFound();
        if (item.Contribution is not { PersonalDetailsRemovedAt: null } contribution)
            return BadRequest();

        var name = Clean(contributorName);
        var email = Clean(contributorEmail);
        var holder = Clean(contributorRightsHolder);
        if (name is null || name.Length > Contribution.NameMaxLength)
            TempData["Error"] = localizer.Plural("Please enter a name of at most {0, plural, one {# character} other {# characters}}.", Contribution.NameMaxLength);
        else if (email is not null && (email.Length > Contribution.EmailMaxLength || !new EmailAddressAttribute().IsValid(email)))
            TempData["Error"] = localizer["Please enter a valid email address."].Value;
        else if (holder is { Length: > Contribution.RightsHolderMaxLength })
            TempData["Error"] = localizer.Plural("The name can be at most {0, plural, one {# character} other {# characters}} long.", Contribution.RightsHolderMaxLength);
        else
        {
            contribution.ContributorName = name;
            contribution.ContributorEmail = email;
            contribution.ShowName = showName;
            contribution.RightsHolder = holder;
            // The log says that something changed, and never what, because it holds no contributor details.
            audit.Record(AuditAction.ContributorDetailsChanged, item.Title, item);
            await db.SaveChangesAsync(cancellationToken);
            TempData["Message"] = localizer["The contributor's details are corrected."].Value;
        }
        return BackToItem(id);
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
        return BackToItem(id);
    }

    public async Task<IActionResult> OnPostDeleteMediaAsync(int id, int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.Include(f => f.ArchiveItem).FirstOrDefaultAsync(f => f.Id == mediaFileId && f.ArchiveItemId == id, cancellationToken);
        if (file is null)
            return NotFound();

        file.DeletedAt = DateTime.UtcNow;
        audit.Record(AuditAction.PhotoMovedToBin, file.ArchiveItem.Title, file.ArchiveItem);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer.Plural("The photo is in the bin. You can restore it there for {0, plural, one {# day} other {# days}}.", ArchiveBin.RetentionDays);
        return BackToItem(id);
    }

    private async Task ShowStateAsync(ArchiveItem? item)
    {
        ItemId = item?.Id;
        SavedTitle = item?.Title;
        CurrentStatus = item?.Status;
        if (item?.RejectedOrWithdrawnAt is { } setAside && (await profile.GetAsync()).RejectedKeptMonths is { } months)
            GoesToBinOn = setAside.AddMonths(months);
        await LoadNeighboursAsync(item?.Id);
        Contribution = item?.Contribution;
        if (item is not null)
        {
            OpenNotes = await db.VisitorNotes.CountAsync(n => n.ArchiveItemId == item.Id && n.HandledAt == null);
            MediaFiles = await db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).OrderBy(f => f.Position).ThenBy(f => f.Id).ToListAsync();
            IdenticalFiles = await FindIdenticalFilesAsync(MediaFiles);
            History = await db.AuditEntries
                .Where(e => e.ArchiveItemId == item.Id)
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
                .Take(HistoryLength)
                .ToListAsync();
            if (item.DateText is not null)
                CurrentDateRange = new DateRange(item.DateFrom, item.DateTo);
        }
    }

    private async Task<Dictionary<int, IdenticalFile>> FindIdenticalFilesAsync(IReadOnlyList<MediaFile> photos)
    {
        var fingerprints = photos.Select(f => f.Sha256).Distinct().ToList();
        var candidates = await db.MediaFiles
            .Where(f => fingerprints.Contains(f.Sha256))
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.Sha256, f.ArchiveItemId, f.ArchiveItem.Title })
            .ToListAsync();
        var found = new Dictionary<int, IdenticalFile>();
        foreach (var photo in photos)
        {
            if (candidates.FirstOrDefault(c => c.Sha256 == photo.Sha256 && c.Id != photo.Id) is { } other)
                found[photo.Id] = new IdenticalFile(other.ArchiveItemId, other.Title);
        }
        return found;
    }

    private Task<ArchiveItem?> FindItemAsync(int id) =>
        db.ArchiveItems
            .Include(i => i.Tags)
            .Include(i => i.Contribution).ThenInclude(c => c!.ContributionLink)
            .FirstOrDefaultAsync(i => i.Id == id);

    private async Task LoadCollectionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
    }
}
