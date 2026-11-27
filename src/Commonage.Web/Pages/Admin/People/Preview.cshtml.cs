using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>
/// Shows curators a person as visitors would see them once the person is shown, whatever the choice is now. It goes through
/// the same projection as the public pages, so what is not for visitors is not on it either.
/// </summary>
public class PreviewModel(CommonageDbContext db) : PageModel
{
    public enum Standing
    {
        /// <summary>Only curators see the person.</summary>
        Hidden,

        /// <summary>The person is shown, but no item of theirs is public, so visitors cannot find them.</summary>
        NoPublicItem,

        /// <summary>Visitors see the person.</summary>
        Shown,
    }

    public int PersonId { get; private set; }
    public Standing State { get; private set; }
    public PersonView View { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return NotFound();

        // A hidden person gets the same projection; the banner says that visitors do not see it.
        PersonId = person.Id;
        var asVisitors = await VisiblePeople.ViewAsync(db, db.People, person.PublicId, 1, cancellationToken);
        View = asVisitors!;
        State = person.IsHidden ? Standing.Hidden : View.Items.TotalCount == 0 ? Standing.NoPublicItem : Standing.Shown;
        return Page();
    }
}
