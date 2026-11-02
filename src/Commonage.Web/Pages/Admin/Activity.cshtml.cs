using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

/// <summary>The activity log: who changed what in the archive, newest first.</summary>
public class ActivityModel(CommonageDbContext db) : PageModel
{
    private const int PageSize = 100;

    public PagedList<AuditEntry> Entries { get; private set; } = PagedList<AuditEntry>.Empty;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Entries = await db.AuditEntries
            .AsNoTracking()
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
