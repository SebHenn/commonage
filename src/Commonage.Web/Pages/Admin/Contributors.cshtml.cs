using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin;

/// <summary>
/// Finds everything a person has sent in or is named as the photographer or author of, by name or email address, for
/// when they ask what the archive holds about them, or ask for it to be corrected or deleted.
/// </summary>
public class ContributorsModel(CommonageDbContext db) : PageModel
{
    private const int MaxResults = 200;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    public IReadOnlyList<Contribution> Results { get; private set; } = [];

    /// <summary>Items that name the person in what curators wrote, which is not always a contribution.</summary>
    public IReadOnlyList<ArchiveItem> NamedInItems { get; private set; } = [];

    /// <summary>Notes from visitors with this name or email address.</summary>
    public IReadOnlyList<VisitorNote> Notes { get; private set; } = [];

    /// <summary>People whose name, other names or note for curators contain the text.</summary>
    public IReadOnlyList<PersonHit> People { get; private set; } = [];

    public record PersonHit(int Id, string Name, bool InBin);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var text = Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var pattern = LikePattern.Containing(text);
        // Items in the bin included: until they are gone for good, the archive still holds this person's data.
        Results = await db.Contributions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(c => c.ArchiveItem)
            .Where(c => EF.Functions.ILike(c.ContributorName!, pattern)
                || EF.Functions.ILike(c.ContributorEmail!, pattern)
                || EF.Functions.ILike(c.RightsHolder!, pattern)
                || EF.Functions.ILike(c.ReferenceCode!, pattern))
            .OrderBy(c => c.ContributorName).ThenByDescending(c => c.SubmittedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        Notes = await db.VisitorNotes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(n => n.ArchiveItem)
            .Where(n => EF.Functions.ILike(n.Name!, pattern) || EF.Functions.ILike(n.Email!, pattern))
            .OrderByDescending(n => n.ReceivedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        // People in the bin included, for the same reason.
        People = await db.People
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => EF.Functions.ILike(p.Name, pattern)
                || EF.Functions.ILike(p.CuratorNote!, pattern)
                || db.PersonNames.IgnoreQueryFilters().Any(n => n.PersonId == p.Id && EF.Functions.ILike(n.Name, pattern)))
            .OrderBy(p => p.SortName ?? p.Name).ThenBy(p => p.Id)
            .Take(MaxResults)
            .Select(p => new PersonHit(p.Id, p.Name, p.DeletedAt != null))
            .ToListAsync(cancellationToken);

        NamedInItems = await db.ArchiveItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => EF.Functions.ILike(i.RightsHolder!, pattern)
                || EF.Functions.ILike(i.Credit!, pattern)
                || EF.Functions.ILike(i.ReceivedFrom!, pattern)
                || EF.Functions.ILike(i.InternalNotes!, pattern))
            .OrderByDescending(i => i.UpdatedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);
    }
}
