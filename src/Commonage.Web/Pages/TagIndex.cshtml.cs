using System.Globalization;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages;

/// <summary>Every place, or every person, that published items are tagged with, from A to Z.</summary>
public class TagIndexModel(CommonageDbContext db) : PageModel
{
    /// <summary>Below this many, the letters to jump to would only be in the way.</summary>
    public const int LettersFrom = 20;

    public record Entry(string Name, int Count);

    public record Letter(string Initial, IReadOnlyList<Entry> Tags);

    public TagKind Kind { get; private set; }

    public IReadOnlyList<Letter> Letters { get; private set; } = [];

    public int Total { get; private set; }

    public async Task<IActionResult> OnGetAsync(string kind, CancellationToken cancellationToken)
    {
        if (TagKinds.FromSlug(kind) is not { } found)
            return NotFound();
        Kind = found;

        // Only what visitors can see: a name on drafts alone is not shown.
        var tags = await db.PublishedItems
            .SelectMany(i => i.Tags)
            .Where(t => t.Kind == Kind)
            .GroupBy(t => t.Name)
            .Select(g => new Entry(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var order = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        Letters = tags
            .OrderBy(t => t.Name, order)
            .GroupBy(t => InitialOf(t.Name))
            .Select(g => new Letter(g.Key, g.ToList()))
            .ToList();
        Total = tags.Count;
        return Page();
    }

    /// <summary>The letter a name is listed under: "Öhringen" under O, as most dictionaries do, and digits or signs under #.</summary>
    public static string InitialOf(string name)
    {
        var first = StringInfo.GetNextTextElement(name).Normalize(NormalizationForm.FormD);
        return first.Length > 0 && char.IsLetter(first[0]) ? first[0].ToString().ToUpper(CultureInfo.CurrentCulture) : "#";
    }
}
