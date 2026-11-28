using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>
/// The step after "Add a person" on an item's page, and the place where people are linked to and taken off an item. It is a page
/// of its own so that it works without JavaScript: one match links at once, several ask which one is meant, none asks whether to
/// make a new person, who starts as seen by curators only. Everything here saves only <see cref="ItemPerson"/> rows (and a new
/// person), never the item, so an item form that is open stays savable.
/// </summary>
public class AddToItemModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    public enum Step
    {
        Ask,
        Choose,
        MakeNew,
    }

    public int ItemId { get; private set; }
    public string ItemTitle { get; private set; } = "";
    public Step Current { get; private set; }
    public IReadOnlyList<PersonMatch> Matches { get; private set; } = [];

    /// <summary>The name that was typed, as it will be used for a new person.</summary>
    [BindProperty]
    [Required(ErrorMessage = "Please enter a name.")]
    [StringLength(Person.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
    [Display(Name = "Name")]
    public string? Name { get; set; }

    public async Task<IActionResult> OnGetAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!await LoadItemAsync(itemId, cancellationToken))
            return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!await LoadItemAsync(itemId, cancellationToken))
            return NotFound();

        var name = Person.Clean(Name);
        if (!ModelState.IsValid || name is null)
        {
            if (name is null && ModelState.IsValid)
                ModelState.AddModelError(nameof(Name), localizer["Please enter a name."].Value);
            return Page();
        }

        Name = name;
        var matches = await db.FindByNameAsync(name, cancellationToken);
        if (matches.Count == 1)
        {
            var person = await db.People.FirstAsync(p => p.Id == matches[0].Id, cancellationToken);
            return await LinkAndGoBackAsync(itemId, person, cancellationToken);
        }

        if (matches.Count > 1)
        {
            Matches = matches;
            Current = Step.Choose;
            return Page();
        }

        Current = Step.MakeNew;
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(int itemId, int personId, CancellationToken cancellationToken)
    {
        if (!await LoadItemAsync(itemId, cancellationToken))
            return NotFound();
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
            return NotFound();
        return await LinkAndGoBackAsync(itemId, person, cancellationToken);
    }

    /// <summary>Makes a new person who only curators see, and links them. Asked for by the "make a new person" step.</summary>
    public async Task<IActionResult> OnPostCreateAsync(int itemId, CancellationToken cancellationToken)
    {
        if (!await LoadItemAsync(itemId, cancellationToken))
            return NotFound();
        var name = Person.Clean(Name);
        if (name is null || name.Length > Person.NameMaxLength)
            return BadRequest();

        var now = clock.GetUtcNow().UtcDateTime;
        var person = db.AddHiddenPerson(name, now);
        audit.Record(AuditAction.PersonCreated, null, person: person);
        db.ItemPeople.Add(new ItemPerson { ArchiveItemId = itemId, Person = person, CreatedAt = now });
        // The line is about the person only: the item's own history, which helpers read, says nothing of people.
        audit.Record(AuditAction.PersonLinked, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer["The person is added. Only curators can see them until you choose otherwise."].Value;
        return Back(itemId);
    }

    public async Task<IActionResult> OnPostRemoveAsync(int itemId, int personId, CancellationToken cancellationToken)
    {
        var link = await db.ItemPeople.FirstOrDefaultAsync(l => l.ArchiveItemId == itemId && l.PersonId == personId, cancellationToken);
        if (link is null)
            return NotFound();

        var person = await db.People.FirstAsync(p => p.Id == personId, cancellationToken);
        db.ItemPeople.Remove(link);
        audit.Record(AuditAction.PersonUnlinked, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        return Back(itemId);
    }

    private async Task<IActionResult> LinkAndGoBackAsync(int itemId, Person person, CancellationToken cancellationToken)
    {
        var outcome = await db.LinkAsync(itemId, person, clock.GetUtcNow().UtcDateTime, cancellationToken);
        if (outcome == PeopleEditing.LinkOutcome.Linked)
        {
            audit.Record(AuditAction.PersonLinked, null, person: person);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (outcome == PeopleEditing.LinkOutcome.TooMany)
        {
            TempData["Message"] = localizer.Plural("An item can have at most {0, plural, one {# person} other {# people}}.", Person.MaxPerItem);
        }

        return Back(itemId);
    }

    private IActionResult Back(int itemId) => Redirect($"{Url.Page("/Admin/Items/Edit", new { id = itemId })}#people");

    private async Task<bool> LoadItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var item = await db.ArchiveItems.AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.Id, i.Title }).FirstOrDefaultAsync(cancellationToken);
        if (item is null)
            return false;
        ItemId = item.Id;
        ItemTitle = item.Title;
        return true;
    }
}
