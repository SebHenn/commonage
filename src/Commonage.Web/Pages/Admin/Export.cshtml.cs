using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Export;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;

namespace Commonage.Web.Pages.Admin;

/// <summary>Downloads the whole archive as a ZIP file. Only admins may, because it can include contributors' personal data.</summary>
public class ExportModel(ArchiveExporter exporter, AuditLog audit, CommonageDbContext db) : PageModel
{
    [BindProperty]
    public bool PublishedOnly { get; set; }

    [BindProperty]
    public bool IncludePersonalData { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Logged before the download starts: a download that breaks off has still handed out data.
        audit.Record(IncludePersonalData ? AuditAction.ArchiveExportedWithPersonalData : AuditAction.ArchiveExported, subject: null);
        await db.SaveChangesAsync(cancellationToken);

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = exporter.FileName }.ToString();
        Response.Headers.CacheControl = "private, no-store";
        // The ZIP library still flushes compressed entries synchronously when they are closed, even through its async API.
        // Allowed for this one rare admin request; the alternative, a temporary file, would need as much disk as the archive.
        HttpContext.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
        await exporter.WriteAsync(Response.Body, new ExportOptions(PublishedOnly, IncludePersonalData), cancellationToken);
        return new EmptyResult();
    }
}
