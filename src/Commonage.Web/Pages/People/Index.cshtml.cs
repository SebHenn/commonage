using System.Globalization;
using System.Text;
using Commonage.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.People;

/// <summary>
/// The people visitors may see, from A to Z. Everything on the page, the names, the letters and the counts, comes from
/// <c>VisiblePeople</c> and the items visitors may see, so a person or item that is not public changes nothing on it.
/// </summary>
public class IndexModel(CommonageDbContext db) : PageModel
{
    /// <summary>Below this many, the letters to jump to would only be in the way.</summary>
    public const int LettersFrom = 20;

    public record Letter(string Initial, IReadOnlyList<PersonIndexEntry> Entries);

    public IReadOnlyList<Letter> Letters { get; private set; } = [];

    public int Total { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var people = await VisiblePeople.IndexAsync(db, cancellationToken);
        Total = people.Count;
        // The list is in the order of the name a curator filed it under; the letter is that of the name it is shown with.
        Letters = people
            .GroupBy(p => Pages.TagIndexModel.InitialOf(p.Name))
            .Select(g => new Letter(g.Key, g.ToList()))
            .ToList();
    }
}
