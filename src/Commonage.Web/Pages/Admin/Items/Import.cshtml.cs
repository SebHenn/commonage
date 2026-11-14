using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Import;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

/// <summary>
/// Brings a spreadsheet and a ZIP file or folder of scans into the archive as drafts. First a dry run lists what is wrong
/// and changes nothing; only after that does a curator choose to import the rows that are in order.
/// </summary>
[AcceptsUploads]
[RequestSizeLimit(ImportStaging.MaxZipBytes + ImportStaging.MaxCsvBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = ImportStaging.MaxZipBytes + ImportStaging.MaxCsvBytes)]
public class ImportModel(
    CommonageDbContext db,
    ItemImport import,
    ImportStaging staging,
    ArchiveHealth health,
    TimeProvider clock,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    /// <summary>An import runs one at a time: two of them would each be sure that an item is not there yet.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>How many problems the page lists. The rest is counted.</summary>
    public const int ShownIssues = 200;

    [BindProperty]
    public int? CollectionId { get; set; }

    /// <summary>The name of a folder on the media volume, as the listing shows it.</summary>
    [BindProperty]
    public string? Folder { get; set; }

    [BindProperty]
    public Guid? StagingId { get; set; }

    [BindProperty]
    public string? SheetName { get; set; }

    public SelectList CollectionOptions { get; private set; } = null!;
    public IReadOnlyList<string> Folders { get; private set; } = [];
    public ImportPlan? Plan { get; private set; }
    public bool CanUpload => health.CanAcceptUploads();

    public IEnumerable<(PlannedRow Row, ImportIssue Issue)> Problems =>
        Plan!.Rows.Where(r => r.HasProblem).SelectMany(r => r.Issues.Where(i => i.Problem is not null).Select(i => (r, i)));

    public IEnumerable<(PlannedRow Row, ImportIssue Issue)> LeftOut =>
        Plan!.Rows.Where(r => r.IsLeftOut).SelectMany(r => r.Issues.Select(i => (r, i)));

    public string Describe(ImportIssue issue) => ItemImport.Describe(localizer, issue);

    public async Task OnGetAsync()
    {
        staging.DeleteOld(TimeSpan.FromDays(1), clock.GetUtcNow().UtcDateTime);
        await LoadOptionsAsync();
    }

    public IActionResult OnGetTemplate() =>
        File(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(ItemImport.TemplateCsv())).ToArray(),
            "text/csv; charset=utf-8", "commonage-import-template.csv");

    /// <summary>The dry run: keeps what was sent, reads it and shows what would happen.</summary>
    public async Task<IActionResult> OnPostAsync(IFormFile? sheet, IFormFile? scans, CancellationToken cancellationToken)
    {
        await LoadOptionsAsync();
        if (sheet is null || sheet.Length == 0)
            ModelState.AddModelError("sheet", localizer["Please choose the spreadsheet, saved as CSV."]);
        else if (sheet.Length > ImportStaging.MaxCsvBytes)
            ModelState.AddModelError("sheet", localizer["The spreadsheet is too large."]);
        if (scans is { Length: > 0 } && !string.IsNullOrEmpty(Folder))
            ModelState.AddModelError("scans", localizer["Please choose either a ZIP file or a folder, not both."]);
        if (!string.IsNullOrEmpty(Folder) && staging.FolderPath(Folder) is null)
            ModelState.AddModelError("Folder", localizer["This folder is not there any more."]);
        if (!ModelState.IsValid)
            return Page();

        staging.DeleteOld(TimeSpan.FromDays(1), clock.GetUtcNow().UtcDateTime);
        var id = staging.Create();
        try
        {
            await using (var target = System.IO.File.Create(staging.CsvPath(id)))
                await sheet!.CopyToAsync(target, cancellationToken);
            if (scans is { Length: > 0 })
            {
                await using var target = System.IO.File.Create(staging.ZipPath(id));
                await scans.CopyToAsync(target, cancellationToken);
            }
        }
        catch
        {
            staging.Delete(id);
            throw;
        }

        StagingId = id;
        SheetName = Path.GetFileName(sheet!.FileName) is { Length: > 0 } name ? (name.Length <= 100 ? name : name[..100]) : null;
        using var source = OpenSource(id);
        if (scans is { Length: > 0 } && source is null)
        {
            staging.Delete(id);
            StagingId = null;
            ModelState.AddModelError("scans", localizer["This is not a ZIP file, or it holds too many files."]);
            return Page();
        }

        Plan = await import.PlanAsync(await System.IO.File.ReadAllBytesAsync(staging.CsvPath(id), cancellationToken), source, CollectionId, cancellationToken);
        if (Plan.Error != CsvError.None || Plan.NoUsableColumns)
        {
            // Nothing to go on with: start from the form again, with the reason shown. The ZIP must be closed to be deleted on Windows.
            source?.Dispose();
            staging.Delete(id);
            StagingId = null;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (StagingId is not { } id || !staging.Exists(id))
        {
            TempData["Error"] = localizer["The files for this import are no longer there. Please choose them again."].Value;
            return RedirectToPage();
        }

        if (!await OneAtATime.WaitAsync(0, cancellationToken))
        {
            TempData["Error"] = localizer["Another import is running. Please wait until it has finished."].Value;
            return RedirectToPage();
        }

        try
        {
            using var source = OpenSource(id);
            // Looked at again, because the archive may have changed since the dry run.
            var plan = await import.PlanAsync(await System.IO.File.ReadAllBytesAsync(staging.CsvPath(id), cancellationToken), source, CollectionId, cancellationToken);
            if (!plan.CanImport)
            {
                TempData["Error"] = localizer["There is nothing left to import."].Value;
                staging.Delete(id);
                return RedirectToPage();
            }

            if (!health.CanAcceptUploads(plan.ReadyBytes))
            {
                TempData["Error"] = localizer["The disk is nearly full, so no photos can be added at the moment. Tell the person who runs the server."].Value;
                return RedirectToPage();
            }

            var result = await import.RunAsync(plan, source, SheetName ?? "", cancellationToken);
            staging.Delete(id);
            TempData["Message"] = localizer["Drafts created: {0}. Photos added: {1}. Previews are being created.", result.Items, result.Files].Value;
            if (result.FilesFailed > 0)
                TempData["Error"] = localizer["Photos that could not be added, because the files had changed: {0}", result.FilesFailed].Value;
            return LocalRedirect(AddManyModel.DraftsUrl);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    public IActionResult OnPostCancel()
    {
        if (StagingId is { } id)
            staging.Delete(id);
        return RedirectToPage();
    }

    private IImportSource? OpenSource(Guid id)
    {
        if (System.IO.File.Exists(staging.ZipPath(id)))
            return ImportSources.OpenZip(staging.ZipPath(id), staging.WorkDirectory(id));
        return staging.FolderPath(Folder) is { } folder ? ImportSources.OpenFolder(folder) : null;
    }

    private async Task LoadOptionsAsync()
    {
        Folders = staging.Folders();
        CollectionOptions = new SelectList(await db.Collections.OrderBy(c => c.Title).ToListAsync(), nameof(Collection.Id), nameof(Collection.Title));
    }
}
