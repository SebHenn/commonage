using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Pages.Admin.Items;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>
/// One person, for curators: the person's own form, who may see them (a separate form, with a step that says what visitors
/// will see), other names and the items they are linked to. Unlinking and the names save only their own rows.
/// </summary>
public class EditModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    public Person Person { get; private set; } = null!;

    public InputModel Input { get; private set; } = new();

    /// <summary>The person as another curator saved them, when the form was sent from an older version.</summary>
    public Person? SavedByOthers { get; private set; }

    public IReadOnlyList<PersonName> Names { get; private set; } = [];
    public IReadOnlyList<LinkedItem> Items { get; private set; } = [];

    /// <summary>The choice that waits for a confirmation: a public one is never made by one click.</summary>
    public PersonDisclosure? ConfirmChoice { get; private set; }

    public int VisibleItemCount => Items.Count(i => i.State == ItemState.Shown);

    /// <summary>Whether visitors would see a linked item, and if not, why. Curators see this under /Admin only.</summary>
    public enum ItemState
    {
        Shown,
        Draft,
        PublicFrom,
        InBin,
        Withdrawn,
    }

    public record LinkedItem(int Id, string Title, string? DateText, ItemState State, DateOnly? PublicFrom);

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(Person.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [StringLength(Person.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Sort name")]
        public string? SortName { get; set; }

        [StringLength(DateRange.MaxTextLength, ErrorMessage = "The date can be at most {1} characters long.")]
        [Display(Name = "Born")]
        public string? Born { get; set; }

        [StringLength(DateRange.MaxTextLength, ErrorMessage = "The date can be at most {1} characters long.")]
        [Display(Name = "Died")]
        public string? Died { get; set; }

        [StringLength(Person.BiographyMaxLength, ErrorMessage = "The text can be at most {1} characters long.")]
        [Display(Name = "Biography")]
        public string? Biography { get; set; }

        [StringLength(Person.NoteMaxLength, ErrorMessage = "The text can be at most {1} characters long.")]
        [Display(Name = "Note for curators")]
        public string? CuratorNote { get; set; }

        public uint? Version { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return NotFound();

        Input = FromPerson(person);
        await LoadAsync(person, cancellationToken);
        return Page();
    }

    private static InputModel FromPerson(Person person) => new()
    {
        Name = person.Name,
        SortName = person.SortName,
        Born = person.BornText,
        Died = person.DiedText,
        Biography = person.Biography,
        CuratorNote = person.CuratorNote,
        Version = person.Version,
    };

    /// <summary>Saves the person's own form. If someone else saved the person since it was opened, nothing is overwritten.</summary>
    public async Task<IActionResult> OnPostAsync(int id, [Bind(Prefix = "Input")] InputModel input, CancellationToken cancellationToken)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return NotFound();

        Input = input;
        if (ItemFields.DateError(localizer, input.Born) is { } bornError)
            ModelState.AddModelError("Input.Born", bornError);
        if (ItemFields.DateError(localizer, input.Died) is { } diedError)
            ModelState.AddModelError("Input.Died", diedError);
        var name = Person.Clean(input.Name);
        if (name is null && ModelState.GetValidationState("Input.Name") != Microsoft.AspNetCore.Mvc.ModelBinding.ModelValidationState.Invalid)
            ModelState.AddModelError("Input.Name", localizer["Please enter a name."].Value);
        if (!ModelState.IsValid)
        {
            await LoadAsync(person, cancellationToken);
            return Page();
        }

        if (input.Version is { } version)
            db.Entry(person).Property(p => p.Version).OriginalValue = version;

        var sortName = Person.Clean(input.SortName);
        var biography = ItemFields.Clean(input.Biography);
        var note = ItemFields.Clean(input.CuratorNote);
        var born = ItemFields.Clean(input.Born);
        var died = ItemFields.Clean(input.Died);
        var changed = name != person.Name || sortName != person.SortName || biography != person.Biography || note != person.CuratorNote
            || born != person.BornText || died != person.DiedText;
        if (changed)
        {
            person.Name = name!;
            person.SortName = sortName;
            person.Biography = biography;
            person.CuratorNote = note;
            if (born != person.BornText)
                person.TrySetBorn(born);
            if (died != person.DiedText)
                person.TrySetDied(died);
            person.Touch(clock.GetUtcNow().UtcDateTime);
            audit.Record(AuditAction.PersonChanged, null, person: person);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (current is null)
                return NotFound();

            SavedByOthers = current;
            // The next save is a deliberate decision to replace the other version, so it may go through.
            ModelState.Remove("Input.Version");
            Input = new InputModel
            {
                Name = input.Name, SortName = input.SortName, Born = input.Born, Died = input.Died,
                Biography = input.Biography, CuratorNote = input.CuratorNote, Version = current.Version,
            };
            await LoadAsync(current, cancellationToken);
            return Page();
        }

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Who may see the person. Hidden is one click. A public choice first shows what visitors will see, and is made by a second
    /// request that says it was confirmed: this works without JavaScript, and a request without that answer never publishes a person.
    /// </summary>
    public async Task<IActionResult> OnPostDisclosureAsync(int id, PersonDisclosure choice, bool confirmed, CancellationToken cancellationToken)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return NotFound();
        if (!Enum.IsDefined(choice))
            return BadRequest();

        if (choice != PersonDisclosure.Hidden && !confirmed && person.Disclosure != choice)
        {
            Input = FromPerson(person);
            ConfirmChoice = choice;
            await LoadAsync(person, cancellationToken);
            return Page();
        }

        if (person.Disclosure != choice || person.NeedsReview)
        {
            person.SetDisclosure(choice, clock.GetUtcNow().UtcDateTime);
            audit.Record(AuditAction.PersonDisclosureChanged, null, person: person);
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddNameAsync(int id, string? name, PersonNameKind kind, string? language, CancellationToken cancellationToken)
    {
        var person = await db.People.Include(p => p.Names).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return NotFound();
        if (!Enum.IsDefined(kind))
            return BadRequest();

        var clean = Person.Clean(name);
        if (clean is null)
            ModelState.AddModelError("otherName", localizer["Please enter a name."].Value);
        else if (clean.Length > Person.NameMaxLength)
            ModelState.AddModelError("otherName", localizer.Plural("The name can be at most {0, plural, one {# character} other {# characters}} long.", Person.NameMaxLength));
        if (person.Names.Count >= Person.MaxNames)
            ModelState.AddModelError("otherName", localizer.Plural("A person can have at most {0, plural, one {# other name} other {# other names}}.", Person.MaxNames));
        if (!Person.TryCleanLanguage(language, out var cleanLanguage))
            ModelState.AddModelError("language", localizer["This is not a language code. Examples: de, en, ar, zh-Hans."].Value);
        if (!ModelState.IsValid)
        {
            TypedOtherName = name;
            TypedLanguage = language;
            Input = FromPerson(person);
            await LoadAsync(person, cancellationToken);
            return Page();
        }

        person.Names.Add(new PersonName { Name = clean!, Kind = kind, Language = cleanLanguage, ShownToVisitors = false });
        audit.Record(AuditAction.PersonNameAdded, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        return Redirect($"{Url.Page("/Admin/People/Edit", new { id })}#other-names");
    }

    public string? TypedOtherName { get; private set; }
    public string? TypedLanguage { get; private set; }

    /// <summary>Whether visitors see an other name. Changing it is as serious as the person's own choice: it only has an effect while the person is shown.</summary>
    public async Task<IActionResult> OnPostShowNameAsync(int id, int nameId, bool shown, CancellationToken cancellationToken)
    {
        var name = await db.PersonNames.FirstOrDefaultAsync(n => n.Id == nameId && n.PersonId == id, cancellationToken);
        if (name is null)
            return NotFound();

        if (name.ShownToVisitors != shown)
        {
            name.ShownToVisitors = shown;
            var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            audit.Record(AuditAction.PersonChanged, null, person: person);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Redirect($"{Url.Page("/Admin/People/Edit", new { id })}#other-names");
    }

    public async Task<IActionResult> OnPostRemoveNameAsync(int id, int nameId, CancellationToken cancellationToken)
    {
        var name = await db.PersonNames.FirstOrDefaultAsync(n => n.Id == nameId && n.PersonId == id, cancellationToken);
        if (name is null)
            return NotFound();

        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        db.PersonNames.Remove(name);
        audit.Record(AuditAction.PersonNameRemoved, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        return Redirect($"{Url.Page("/Admin/People/Edit", new { id })}#other-names");
    }

    public async Task<IActionResult> OnPostUnlinkAsync(int id, int itemId, CancellationToken cancellationToken)
    {
        // Also for an item in the bin: the link stays with it there, and a curator may want it gone.
        var link = await db.ItemPeople.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.PersonId == id && l.ArchiveItemId == itemId, cancellationToken);
        if (link is null)
            return NotFound();

        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        db.ItemPeople.Remove(link);
        audit.Record(AuditAction.PersonUnlinked, null, person: person);
        await db.SaveChangesAsync(cancellationToken);
        return Redirect($"{Url.Page("/Admin/People/Edit", new { id })}#items");
    }

    private async Task LoadAsync(Person person, CancellationToken cancellationToken)
    {
        Person = person;
        Names = await db.PersonNames.AsNoTracking().Where(n => n.PersonId == person.Id).OrderBy(n => n.Id).ToListAsync(cancellationToken);
        var today = db.Today;
        var rows = await db.ItemPeople.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PersonId == person.Id)
            .OrderBy(l => l.ArchiveItem.Title).ThenBy(l => l.ArchiveItemId)
            .Select(l => new { l.ArchiveItemId, l.ArchiveItem.Title, l.ArchiveItem.DateText, l.ArchiveItem.Status, l.ArchiveItem.PublicFrom, l.ArchiveItem.DeletedAt })
            .ToListAsync(cancellationToken);
        Items = rows.Select(r => new LinkedItem(r.ArchiveItemId, r.Title, r.DateText, r.DeletedAt is not null ? ItemState.InBin
            : r.Status is ItemStatus.Rejected or ItemStatus.Withdrawn ? ItemState.Withdrawn
            : r.Status != ItemStatus.Published ? ItemState.Draft
            : r.PublicFrom is { } from && from > today ? ItemState.PublicFrom
            : ItemState.Shown, r.PublicFrom)).ToList();
    }
}
