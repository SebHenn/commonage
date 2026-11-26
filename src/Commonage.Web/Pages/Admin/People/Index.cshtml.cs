using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>Every person, for curators: who visitors may be shown, and how many items each is linked to. Helpers have no access.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit, TimeProvider clock) : PageModel
{
    public const int PageSize = 50;

    /// <summary>Whom a list shows: everyone, or those with one choice of who may see them.</summary>
    public enum Filter
    {
        All,
        Hidden,
        Deceased,
        MayBeShown,
    }

    public record PersonRow(int Id, string Name, string FoldedName, PersonDisclosure Disclosure, int Items, int VisibleItems);

    public PagedList<PersonRow> People { get; private set; } = PagedList<PersonRow>.Empty;

    /// <summary>Folded names of the people's tags that still show on visitors' pages. A person of the same name is told about it.</summary>
    public HashSet<string> PublicTagNames { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true, Name = "who")]
    public Filter Who { get; set; }

    public bool IsFiltered => Who != Filter.All || !string.IsNullOrWhiteSpace(Text);

    [BindProperty]
    [Required(ErrorMessage = "Please enter a name.")]
    [StringLength(Person.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
    [Display(Name = "Name of the new person")]
    public string? NewName { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    /// <summary>Names that begin with what a curator has typed, for the suggestions of the item page. Only names, no more.</summary>
    public async Task<IActionResult> OnGetSuggestAsync(string? q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new JsonResult(Array.Empty<string>());
        var matches = await db.SuggestAsync(q.Trim(), 8, cancellationToken);
        return new JsonResult(matches.Select(m => m.Name).Distinct().ToList());
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var name = Person.Clean(NewName);
        if (!ModelState.IsValid || name is null)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var person = db.AddHiddenPerson(name, clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditAction.PersonCreated, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("Edit", new { id = person.Id });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IQueryable<Person> people = db.People.AsNoTracking();
        people = Who switch
        {
            Filter.Hidden => people.Where(p => p.Disclosure == PersonDisclosure.Hidden),
            Filter.Deceased => people.Where(p => p.Disclosure == PersonDisclosure.Deceased),
            Filter.MayBeShown => people.Where(p => p.Disclosure == PersonDisclosure.MayBeShown),
            _ => people,
        };
        if (Text?.Trim() is { Length: > 0 } text)
        {
            var pattern = PeopleEditing.LikePrefix(await db.FoldAsync(text, cancellationToken));
            people = people.Where(p => EF.Functions.Like(p.FoldedName, pattern, "\\") || p.Names.Any(n => EF.Functions.Like(n.FoldedName, pattern, "\\")));
        }

        People = await people
            .OrderBy(p => p.SortName ?? p.Name).ThenBy(p => p.Id)
            .Select(p => new PersonRow(
                p.Id, p.Name, p.FoldedName, p.Disclosure,
                p.Links.Count,
                // Linked items visitors may see: counted here, under /Admin, and nowhere else.
                p.Links.Count(l => db.VisibleItems.Any(i => i.Id == l.ArchiveItemId))))
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
        PublicTagNames = await db.PublicPersonTagNamesAsync(cancellationToken);
    }
}
