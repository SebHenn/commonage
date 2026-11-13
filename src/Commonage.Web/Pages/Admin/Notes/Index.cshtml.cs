using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Notes;

public enum NoteFilter
{
    Open,
    Handled,
    All,
}

/// <summary>What visitors sent through "Do you know more about this?": open notes first, to be dealt with and marked so.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit, TimeProvider clock, IStringLocalizer<SharedResource> localizer) : PageModel
{
    public const int PageSize = 25;

    [BindProperty(SupportsGet = true)]
    public NoteFilter Show { get; set; }

    /// <summary>Only the notes about one item, when coming from its page.</summary>
    [BindProperty(SupportsGet = true)]
    public int? Item { get; set; }

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    public PagedList<VisitorNote> Notes { get; private set; } = PagedList<VisitorNote>.Empty;

    public int OpenCount { get; private set; }

    public string? ItemTitle { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var notes = db.VisitorNotes.AsNoTracking().Include(n => n.ArchiveItem).AsQueryable();
        if (Item is { } item)
        {
            notes = notes.Where(n => n.ArchiveItemId == item);
            ItemTitle = await db.ArchiveItems.Where(i => i.Id == item).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken);
        }
        OpenCount = await notes.CountAsync(n => n.HandledAt == null, cancellationToken);

        notes = Show switch
        {
            NoteFilter.Handled => notes.Where(n => n.HandledAt != null),
            NoteFilter.All => notes,
            _ => notes.Where(n => n.HandledAt == null),
        };
        // Requests to take something down first: they are the ones that cannot wait.
        Notes = await notes
            .OrderBy(n => n.HandledAt != null)
            .ThenByDescending(n => n.HandledAt == null && n.Kind == VisitorNoteKind.Removal)
            .ThenByDescending(n => n.ReceivedAt).ThenByDescending(n => n.Id)
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }

    public Task<IActionResult> OnPostHandledAsync(int noteId, CancellationToken cancellationToken) =>
        ChangeAsync(noteId, AuditAction.VisitorNoteHandled, note => note.HandledAt ??= Now(), localizer["The note is marked as dealt with."], cancellationToken);

    public Task<IActionResult> OnPostReopenAsync(int noteId, CancellationToken cancellationToken) =>
        ChangeAsync(noteId, AuditAction.VisitorNoteReopened, note => note.HandledAt = null, localizer["The note is open again."], cancellationToken);

    public Task<IActionResult> OnPostRemoveDetailsAsync(int noteId, CancellationToken cancellationToken) =>
        ChangeAsync(noteId, AuditAction.VisitorNoteDetailsRemoved, note => note.RemovePersonalDetails(Now()), localizer["The name and email address are removed from the note."], cancellationToken);

    public async Task<IActionResult> OnPostDeleteAsync(int noteId, CancellationToken cancellationToken)
    {
        if (await db.VisitorNotes.Include(n => n.ArchiveItem).FirstOrDefaultAsync(n => n.Id == noteId, cancellationToken) is not { } note)
            return NotFound();

        db.VisitorNotes.Remove(note);
        audit.Record(AuditAction.VisitorNoteDeleted, note.ArchiveItem.Title, note.ArchiveItem);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer["The note is deleted."].Value;
        return Back();
    }

    private async Task<IActionResult> ChangeAsync(int noteId, AuditAction action, Action<VisitorNote> change, LocalizedString message, CancellationToken cancellationToken)
    {
        if (await db.VisitorNotes.Include(n => n.ArchiveItem).FirstOrDefaultAsync(n => n.Id == noteId, cancellationToken) is not { } note)
            return NotFound();

        change(note);
        // The item, never the visitor: the log is kept longer than their details.
        audit.Record(action, note.ArchiveItem.Title, note.ArchiveItem);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = message.Value;
        return Back();
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private RedirectToPageResult Back() =>
        RedirectToPage("/Admin/Notes/Index", new { show = Show == NoteFilter.Open ? null : Show.ToString(), item = Item });
}
