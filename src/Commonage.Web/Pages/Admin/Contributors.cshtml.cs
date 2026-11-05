using Commonage.Web.Data;
using Commonage.Web.Domain;
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var text = Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // The typed text is matched literally, so % and _ are not wildcards.
        var pattern = "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
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
