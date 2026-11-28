using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>Every person, for curators: who visitors may be shown, and how many items each is linked to. Helpers have no access.</summary>
public class IndexModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    public const int PageSize = 50;

    /// <summary>The longest text a search or a suggestion looks for; a name is at most 200 characters.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>Whom a list shows: everyone, or those with one choice of who may see them.</summary>
    public enum Filter
    {
        All,
        Hidden,
        Deceased,
        MayBeShown,
    }

    public record PersonRow(int Id, string Name, string FoldedName, PersonDisclosure Disclosure, int Items, int VisibleItems, IReadOnlyList<string> ItemTitles);

    public PagedList<PersonRow> People { get; private set; } = PagedList<PersonRow>.Empty;

    [BindProperty(SupportsGet = true, Name = QueryUrls.PageKey)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Text { get; set; }

    [BindProperty(SupportsGet = true, Name = "who")]
    public Filter Who { get; set; }

    /// <summary>The list of people carried over from tags that nobody has chosen a disclosure for (<c>?review=1</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "review")]
    public bool Review { get; set; }

    /// <summary>A public choice for several people waits for a confirmation, as it does for one person.</summary>
    public PersonDisclosure? ConfirmChoice { get; private set; }

    public IReadOnlyList<PersonRow> ConfirmPeople { get; private set; } = [];

    public bool IsFiltered => Who != Filter.All || Review || !string.IsNullOrWhiteSpace(Text);

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
        var matches = await db.SuggestAsync(Capped(q), 8, cancellationToken);
        return new JsonResult(matches.Select(m => m.Name).Distinct().ToList());
    }

    private static string Capped(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length <= MaxSearchLength ? trimmed : trimmed[..MaxSearchLength].TrimEnd();
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

    /// <summary>The most people one request changes: a page of the list.</summary>
    public const int MaxBulk = 200;

    /// <summary>
    /// "Only curators" or "Has died" for the ticked people of the review list. Only people nobody has chosen for yet are changed, one audit
    /// line each. "Has died" shows the people to visitors, so it asks first, with a second request that says it was confirmed.
    /// </summary>
    public async Task<IActionResult> OnPostBulkReviewAsync(int[]? selected, PersonDisclosure choice, bool confirmed, CancellationToken cancellationToken)
    {
        if (choice is not (PersonDisclosure.Hidden or PersonDisclosure.Deceased))
            return BadRequest();
        var ids = (selected ?? []).Distinct().Take(MaxBulk).ToList();
        if (ids.Count == 0)
        {
            TempData["Message"] = localizer["Tick the people first."].Value;
            return RedirectToPage(new { review = 1 });
        }

        var people = await db.People.Where(p => ids.Contains(p.Id) && p.DisclosureChangedAt == null).OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        if (choice == PersonDisclosure.Deceased && !confirmed && people.Count > 0)
        {
            Review = true;
            ConfirmChoice = choice;
            ConfirmPeople = people.Select(p => new PersonRow(p.Id, p.Name, p.FoldedName, p.Disclosure, 0, 0, [])).ToList();
            await LoadAsync(cancellationToken);
            return Page();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var person in people)
        {
            person.SetDisclosure(choice, now);
            audit.Record(AuditAction.PersonDisclosureChanged, null, person: person);
        }

        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer.Plural("{0, plural, one {# person is reviewed.} other {# people are reviewed.}}", people.Count);
        return RedirectToPage(new { review = 1 });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IQueryable<Person> people = db.People.AsNoTracking();
        if (Review)
            people = people.Where(p => p.DisclosureChangedAt == null);
        people = Who switch
        {
            Filter.Hidden => people.Where(p => p.Disclosure == PersonDisclosure.Hidden),
            Filter.Deceased => people.Where(p => p.Disclosure == PersonDisclosure.Deceased),
            Filter.MayBeShown => people.Where(p => p.Disclosure == PersonDisclosure.MayBeShown),
            _ => people,
        };
        if (Capped(Text) is { Length: > 0 } text)
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
                p.Links.Count(l => db.VisibleItems.Any(i => i.Id == l.ArchiveItemId)),
                p.Links.OrderBy(l => l.ArchiveItem.Title).ThenBy(l => l.ArchiveItemId).Take(5).Select(l => l.ArchiveItem.Title).ToList()))
            .ToPagedListAsync(PageNumber, PageSize, cancellationToken);
    }
}
