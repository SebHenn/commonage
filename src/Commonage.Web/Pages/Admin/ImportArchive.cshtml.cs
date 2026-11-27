using Commonage.Web.Import;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin;

/// <summary>
/// Reads the ZIP file of another Commonage archive's export, or one this archive made, into this archive as drafts. First a
/// dry run lists what would come in and what would not; only after that does an admin choose to go on. The page names the
/// titles of items that are not published here, so it is never cached.
/// </summary>
[AcceptsUploads]
[RequestSizeLimit(ImportStaging.MaxZipBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = ImportStaging.MaxZipBytes)]
public class ImportArchiveModel(
    ExportImport import,
    ImportStaging staging,
    ArchiveHealth health,
    TimeProvider clock,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    /// <summary>How many lines a list on the page shows. The rest is counted.</summary>
    public const int ShownIssues = 200;

    [BindProperty]
    public Guid? StagingId { get; set; }

    public ExportPlan? Plan { get; private set; }
    public bool CanUpload => health.CanAcceptUploads();

    public IEnumerable<(PlannedExportItem Item, ExportIssue Issue)> Problems => IssuesOf(ExportIssueSeverity.Problem);
    public IEnumerable<(PlannedExportItem Item, ExportIssue Issue)> LeftOut => IssuesOf(ExportIssueSeverity.LeftOut);
    public IEnumerable<(PlannedExportItem Item, ExportIssue Issue)> Notes => IssuesOf(ExportIssueSeverity.Note);

    private IEnumerable<(PlannedExportItem, ExportIssue)> IssuesOf(ExportIssueSeverity severity) =>
        Plan!.Items.SelectMany(i => i.Issues.Where(x => x.Severity == severity).Select(x => (i, x)));

    public string Describe(ExportIssue issue) => ExportImport.Describe(localizer, issue);

    public string DescribeRefusal() => ExportImport.Describe(localizer, Plan!.Refusal, Plan.FoundVersion);

    public void OnGet()
    {
        NoStore();
        staging.DeleteOld(TimeSpan.FromDays(1), clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The dry run: keeps what was sent, reads it and shows what would happen.</summary>
    public async Task<IActionResult> OnPostAsync(IFormFile? export, CancellationToken cancellationToken)
    {
        NoStore();
        if (export is null || export.Length == 0)
            ModelState.AddModelError("export", localizer["Please choose the ZIP file of an export."]);
        if (!ModelState.IsValid)
            return Page();

        staging.DeleteOld(TimeSpan.FromDays(1), clock.GetUtcNow().UtcDateTime);
        var id = staging.Create();
        try
        {
            await using var target = ImportStaging.CreateOwnerOnlyFile(staging.ExportPath(id));
            await export!.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }
        catch
        {
            staging.Delete(id);
            throw;
        }

        // The ZIP file is closed before it is deleted, which Windows needs.
        using (var source = ImportSources.OpenZip(staging.ExportPath(id), staging.WorkDirectory(id)))
        {
            try
            {
                Plan = source is null
                    ? new ExportPlan { Refusal = ExportRefusal.NotZip }
                    : await import.PlanAsync(source, cancellationToken);
            }
            catch
            {
                source?.Dispose();
                staging.Delete(id);
                throw;
            }
        }

        if (Plan.Refusal != ExportRefusal.None)
        {
            staging.Delete(id);
            return Page();
        }

        StagingId = id;
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (StagingId is not { } id || !staging.ExportExists(id))
        {
            TempData["Error"] = localizer["The files for this import are no longer there. Please choose them again."].Value;
            return RedirectToPage();
        }

        if (!await ImportLock.OneAtATime.WaitAsync(0, cancellationToken))
        {
            TempData["Error"] = localizer["Another import is running. Please wait until it has finished."].Value;
            return RedirectToPage();
        }

        IImportSource? source = null;
        var started = false;
        try
        {
            source = ImportSources.OpenZip(staging.ExportPath(id), staging.WorkDirectory(id));
            if (source is null)
            {
                TempData["Error"] = localizer["The files for this import are no longer there. Please choose them again."].Value;
                return RedirectToPage();
            }

            // Looked at again, because the archive may have changed since the dry run.
            var plan = await import.PlanAsync(source, cancellationToken);
            if (!plan.CanImport)
            {
                TempData["Error"] = localizer["There is nothing left to import."].Value;
                started = true;
                return RedirectToPage();
            }

            if (!health.CanAcceptUploads(plan.ReadyBytes))
            {
                TempData["Error"] = localizer["The disk is nearly full, so no photos can be added at the moment. Tell the person who runs the server."].Value;
                return RedirectToPage();
            }

            // Closing the tab must not stop the run between an item and its photos, so it is not given the request's token. A very
            // large import can outlast what a browser or a proxy waits for; it goes on, and the activity log has the result.
            // Whatever way it ends, the copy is of no more use: what came in is there, and a second run would find it so.
            started = true;
            var result = await import.RunAsync(plan, source, CancellationToken.None);
            TempData["Message"] = localizer["Drafts created: {0}. Photos added: {1}. Previews are being created.", result.Items, result.Files].Value;
            var problems = new List<string>();
            if (result.People > 0)
                TempData["Message"] += " " + localizer.Plural("{0, plural, one {# person was created, who only curators see.} other {# people were created, who only curators see.}}", result.People);
            if (result.FilesFailed > 0)
                problems.Add(localizer["Photos that could not be added: {0}. Their items stay drafts with the photos that did arrive. Add the missing photos by hand, or put the draft in the bin and run the import again.", result.FilesFailed].Value);
            if (result.ItemsFailed > 0)
                problems.Add(localizer.Plural("{0, plural, one {# item} other {# items}} could not be created.", result.ItemsFailed));
            if (result.ItemsNotTried > 0)
                problems.Add(localizer.Plural("The import stopped early, because the disk or the database kept failing, or the server was shutting down. {0, plural, one {# item was} other {# items were}} not tried. What came in stays as drafts. Choose the same file again for the rest.", result.ItemsNotTried));
            if (problems.Count > 0)
                TempData["Error"] = string.Join(" ", problems);
            return LocalRedirect(Items.AddManyModel.DraftsUrl);
        }
        finally
        {
            // The ZIP file is closed before its folder is removed, which Windows needs.
            source?.Dispose();
            if (started)
                staging.Delete(id);
            ImportLock.OneAtATime.Release();
        }
    }

    public IActionResult OnPostCancel()
    {
        if (StagingId is { } id)
            staging.Delete(id);
        return RedirectToPage();
    }

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";
}
