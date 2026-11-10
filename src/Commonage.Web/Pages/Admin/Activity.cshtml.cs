using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

/// <summary>The activity log: who changed what in the archive, newest first, narrowed down by who, what and a title.</summary>
public class ActivityModel(CommonageDbContext db) : PageModel
{
    private const int PageSize = 100;

    /// <summary>Values of the "who" filter that are not accounts. Account names are email addresses, so they never look like these.</summary>
    public const string Contributors = "contributors", Automatic = "automatic";

    public PagedList<AuditEntry> Entries { get; private set; } = PagedList<AuditEntry>.Empty;

    /// <summary>Everyone the log names, including deleted accounts, whose names it keeps.</summary>
    public IReadOnlyList<string> People { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "who")]
    public string? Who { get; set; }

    [BindProperty(SupportsGet = true, Name = "what")]
    public AuditKind? What { get; set; }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    public bool IsFiltered => !string.IsNullOrEmpty(Who) || What is not null || !string.IsNullOrWhiteSpace(Text);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        People = await db.AuditEntries.AsNoTracking()
            .Where(e => e.UserName != null)
            .Select(e => e.UserName!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        var entries = db.AuditEntries.AsNoTracking();
        entries = Who switch
        {
            null or "" => entries,
            Contributors => entries.Where(e => e.UserName == null && e.Action == AuditAction.ContributionReceived),
            Automatic => entries.Where(e => e.UserName == null && e.Action != AuditAction.ContributionReceived),
            var name => entries.Where(e => e.UserName == name),
        };
        if (What is { } kind)
        {
            var actions = Enum.GetValues<AuditAction>().Where(a => AuditText.KindOf(a) == kind).ToList();
            entries = entries.Where(e => actions.Contains(e.Action));
        }
        if (Text?.Trim() is { Length: > 0 } text)
        {
            var pattern = LikePattern.Containing(text);
            entries = entries.Where(e => EF.Functions.ILike(e.Subject!, pattern));
        }

        Entries = await entries
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
