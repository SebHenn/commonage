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

/// <summary>
/// A shoebox of scans: one draft per file, sharing a collection, date, tags and rights statement. The drafts have no title
/// yet, see <see cref="ArchiveItem.IsUntitled"/>. With JavaScript, uploads.js sends one file per request, so there is no limit
/// to how many; without it, everything goes in one request of at most <see cref="MaxRequestBytes"/>.
/// </summary>
[AcceptsUploads]
[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
public class AddManyModel(
    CommonageDbContext db,
    MediaUploadService uploads,
    AuditLog audit,
    ArchiveHealth health,
    ArchiveProfile profile,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public const long MaxRequestBytes = 1024L * 1024 * 1024;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public SelectList CollectionOptions { get; private set; } = null!;
    public string? DefaultRightsLabel { get; private set; }

    public bool CanUpload => health.CanAcceptUploads();

    /// <summary>Where the new drafts are waiting for their titles.</summary>
    public static string DraftsUrl => $"{AdminItemList.Page}?status={ItemStatus.Draft}&needs={ItemNeed.Title}";

    public class InputModel
    {
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
    }

    public async Task OnGetAsync(int? collectionId)
    {
        Input.CollectionId = collectionId;
        await LoadOptionsAsync();
    }

    /// <param name="files">One file from uploads.js, or all of them from the form without JavaScript.</param>
    /// <param name="addIdentical">Also make drafts from files the archive already has.</param>
    public async Task<IActionResult> OnPostAsync(List<IFormFile> files, bool addIdentical, CancellationToken cancellationToken)
    {
        var sentByQueue = Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

        var tagNames = Tag.ParseList(Input.Tags);
        if (Input.CollectionId is not null && !await db.Collections.AnyAsync(c => c.Id == Input.CollectionId, cancellationToken))
            ModelState.AddModelError("Input.CollectionId", localizer["Please choose a collection."]);
        if (ItemFields.DateError(localizer, Input.Date) is { } dateError)
            ModelState.AddModelError("Input.Date", dateError);
        foreach (var tagError in ItemFields.TagErrors(localizer, tagNames))
            ModelState.AddModelError("Input.Tags", tagError);
        if (ItemFields.RightsError(localizer, Input.RightsUri) is { } rightsError)
            ModelState.AddModelError("Input.RightsUri", rightsError);
        if (files.Count == 0)
            ModelState.AddModelError("files", localizer["Please choose at least one photo."]);

        if (!ModelState.IsValid)
        {
            if (sentByQueue)
                return BadRequest(new { errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) });
            await LoadOptionsAsync();
            return Page();
        }

        var tags = await db.FindOrCreateTagsAsync(tagNames, cancellationToken);

        if (sentByQueue)
        {
            if (files.Count != 1)
                return BadRequest();
            var examined = await uploads.ExamineAsync(files[0], cancellationToken);
            if (examined.Error != UploadError.None)
                return new JsonResult(UploadReply.Refused(UploadErrorMessages.Describe(localizer, examined.Error, files[0].FileName)));
            if (!addIdentical && await uploads.FindIdenticalAsync(examined, cancellationToken) is { } same)
                return new JsonResult(UploadReply.Identical(localizer, same, ItemUrl(same.ItemId)));

            var (item, file) = await AddDraftAsync(files[0], examined, tags, cancellationToken);
            return new JsonResult(UploadReply.Added(item.Id, ItemUrl(item.Id), file.Id));
        }

        var added = 0;
        var refused = new List<string>();
        var left = new List<string>();
        foreach (var upload in files)
        {
            var examined = await uploads.ExamineAsync(upload, cancellationToken);
            if (examined.Error != UploadError.None)
                refused.Add(UploadErrorMessages.Describe(localizer, examined.Error, upload.FileName));
            else if (!addIdentical && await uploads.FindIdenticalAsync(examined, cancellationToken) is not null)
                left.Add(upload.FileName);
            else
            {
                await AddDraftAsync(upload, examined, tags, cancellationToken);
                added++;
            }
        }

        if (added > 0)
            TempData["Message"] = localizer["Drafts created: {0}. Previews are being created.", added].Value;
        var problems = new List<string>(refused.Take(5));
        if (left.Count > 0)
            problems.Add(localizer["Left out, because the archive already has them: {0}", ItemFields.ListOf(left)]);
        if (problems.Count > 0)
            TempData["Error"] = string.Join(" ", problems);
        return added > 0 ? LocalRedirect(DraftsUrl) : RedirectToPage(new { collectionId = Input.CollectionId });
    }

    private string? ItemUrl(int id) => Url.Page("/Admin/Items/Edit", new { id, back = DraftsUrl });

    private async Task<(ArchiveItem Item, MediaFile File)> AddDraftAsync(IFormFile upload, ExaminedUpload examined, List<Tag> tags, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var item = new ArchiveItem
        {
            Title = "",
            Status = ItemStatus.Draft,
            CollectionId = Input.CollectionId!.Value,
            RightsUri = ItemFields.Clean(Input.RightsUri),
            CreatedAt = now,
            UpdatedAt = now,
        };
        item.TrySetDate(Input.Date);
        item.SetTags(tags);
        db.ArchiveItems.Add(item);
        audit.Record(AuditAction.ItemCreated, item.Title, item);
        audit.Record(AuditAction.PhotosAdded, item.Title, item);
        var file = await uploads.StoreAsync(item, upload, examined, cancellationToken);
        return (item, file);
    }

    private async Task LoadOptionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
        DefaultRightsLabel = (await profile.GetAsync()).DefaultRightsUri is { } uri ? RightsOptions.Label(localizer, uri) : null;
    }
}
