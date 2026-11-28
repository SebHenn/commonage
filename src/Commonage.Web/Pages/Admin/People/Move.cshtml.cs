using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>
/// The split: items ticked on a person's page go to another person, or to a new person who starts as seen by curators only. Three
/// steps, all plain pages: say where to, read what will happen, confirm. A link the target already has is not made twice. Other
/// names that are carried along start hidden from visitors. Only <see cref="ItemPerson"/> rows are saved, never an item.
/// </summary>
public class MoveModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    /// <summary>The most items one move takes, so that the address of the confirmation stays short enough.</summary>
    public const int MaxItems = 500;

    public Person Source { get; private set; } = null!;
    public Person? Target { get; private set; }

    /// <summary>The name of the new person, when the items go to one.</summary>
    public string? NewName { get; private set; }

    public string? With { get; private set; }
    public IReadOnlyList<PersonMatch> Matches { get; private set; } = [];
    public bool NothingFound { get; private set; }

    public IReadOnlyList<int> ItemIds { get; private set; } = [];
    public IReadOnlyList<MovedItem> Items { get; private set; } = [];
    public IReadOnlyList<PersonName> SourceNames { get; private set; } = [];

    public record MovedItem(int Id, string Title, bool AlreadyThere);

    public int NewLinks => Items.Count(i => !i.AlreadyThere);

    /// <summary>What visitors will see of the person on these items afterwards, compared with now.</summary>
    public bool ShowsMore => MergeModel.Rank(TargetDisclosure) > MergeModel.Rank(Source.Disclosure);

    public bool ShowsLess => MergeModel.Rank(TargetDisclosure) < MergeModel.Rank(Source.Disclosure);

    private PersonDisclosure TargetDisclosure => Target?.Disclosure ?? PersonDisclosure.Hidden;

    public async Task<IActionResult> OnGetAsync(int id, int[] items, string? with, int? target, string? newName, CancellationToken cancellationToken)
    {
        if (!await LoadSourceAsync(id, cancellationToken))
            return NotFound();
        var early = await LoadItemsAsync(items, cancellationToken);
        if (early is not null)
            return early;

        With = with?.Trim();
        if (target is { } targetId)
        {
            if (!await LoadTargetAsync(targetId, cancellationToken))
                return NotFound();
            await SummariseAsync(cancellationToken);
            return Page();
        }

        if (newName is not null)
        {
            var name = Person.Clean(newName);
            if (name is null || name.Length > Person.NameMaxLength)
            {
                ModelState.AddModelError("newName", localizer["Please enter a name."].Value);
                return Page();
            }

            NewName = name;
            return Page();
        }

        if (string.IsNullOrWhiteSpace(With))
            return Page();

        if (With.Length <= 9 && With.All(char.IsAsciiDigit) && int.TryParse(With, out var number))
        {
            if (number != id && await db.People.AnyAsync(p => p.Id == number, cancellationToken))
                return RedirectToPage(new { id, items = ItemIds, target = number });
            NothingFound = true;
            return Page();
        }

        if (With.Length > Person.NameMaxLength)
        {
            NothingFound = true;
            return Page();
        }

        var matches = (await db.FindByNameAsync(With, cancellationToken)).Where(m => m.Id != id).ToList();
        if (matches.Count == 1)
            return RedirectToPage(new { id, items = ItemIds, target = matches[0].Id });
        Matches = matches;
        NothingFound = matches.Count == 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, int[] items, int? target, string? newName, int[] names, CancellationToken cancellationToken)
    {
        if (!await LoadSourceAsync(id, cancellationToken))
            return NotFound();
        var early = await LoadItemsAsync(items, cancellationToken);
        if (early is not null)
            return early;

        var now = clock.GetUtcNow().UtcDateTime;
        Person targetPerson;
        var created = false;
        if (target is { } targetId)
        {
            if (!await LoadTargetAsync(targetId, cancellationToken))
                return NotFound();
            targetPerson = Target!;
        }
        else
        {
            var name = Person.Clean(newName);
            if (name is null || name.Length > Person.NameMaxLength)
            {
                TempData["Message"] = localizer["Please enter a name."].Value;
                return RedirectToPage(new { id, items = ItemIds });
            }

            // A new person is seen by curators only, whatever the person they come from is.
            targetPerson = db.AddHiddenPerson(name, now);
            created = true;
        }

        // The links: the ones the target has already are not made twice. Items in the bin move too, as they would come back with it.
        var moving = ItemIds.ToList();
        var sourceLinks = await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == id && moving.Contains(l.ArchiveItemId)).ToListAsync(cancellationToken);
        var already = created
            ? new HashSet<int>()
            : (await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == targetPerson.Id && moving.Contains(l.ArchiveItemId)).Select(l => l.ArchiveItemId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var link in sourceLinks)
        {
            db.ItemPeople.Remove(link);
            if (already.Add(link.ArchiveItemId))
                db.ItemPeople.Add(new ItemPerson { ArchiveItemId = link.ArchiveItemId, Person = targetPerson, CreatedAt = now });
        }

        // The names chosen to go along, never shown to visitors, without doubles.
        var known = new HashSet<string> { await db.FoldAsync(targetPerson.Name, cancellationToken) };
        foreach (var name in targetPerson.Names)
            known.Add(name.FoldedName);
        foreach (var name in SourceNames.Where(n => names.Contains(n.Id)))
        {
            if (targetPerson.Names.Count >= Person.MaxNames)
                break;
            if (known.Add(await db.FoldAsync(name.Name, cancellationToken)))
                targetPerson.Names.Add(new PersonName { Name = name.Name, Kind = name.Kind, Language = name.Language, ShownToVisitors = false });
        }

        targetPerson.Touch(now);
        if (created)
            audit.Record(AuditAction.PersonCreated, null, person: targetPerson);
        audit.Record(AuditAction.PersonItemsMoved, null, person: Source);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone moved or linked the same items at the same moment; nothing was changed.
            db.ChangeTracker.Clear();
            TempData["Message"] = localizer["Someone else changed these items meanwhile. Nothing was moved; look at the person again."].Value;
            return RedirectToPage("Edit", new { id });
        }

        TempData["Message"] = localizer.Plural("{0, plural, one {# item is moved to this person.} other {# items are moved to this person.}}", sourceLinks.Count);
        return RedirectToPage("Edit", new { id = targetPerson.Id });
    }

    private async Task<bool> LoadSourceAsync(int id, CancellationToken cancellationToken)
    {
        var person = await db.People.Include(p => p.Names).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return false;
        Source = person;
        SourceNames = person.Names.OrderBy(n => n.Id).ToList();
        return true;
    }

    private async Task<bool> LoadTargetAsync(int id, CancellationToken cancellationToken)
    {
        // Not a person in the bin: items moved there would be gone from every page with nothing to show for it.
        var person = await db.People.Include(p => p.Names).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null || person.Id == Source.Id)
            return false;
        Target = person;
        return true;
    }

    /// <summary>Reads the ticked items. Returns a redirect when nothing usable was ticked.</summary>
    private async Task<IActionResult?> LoadItemsAsync(int[] ticked, CancellationToken cancellationToken)
    {
        var ids = ticked.Distinct().ToList();
        if (ids.Count > MaxItems)
        {
            TempData["Message"] = localizer.Plural("You can move at most {0, plural, one {# item} other {# items}} at once.", MaxItems);
            return RedirectToPage("Edit", new { id = Source.Id });
        }

        // Only items this person is linked to, the bin included.
        var linked = await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == Source.Id && ids.Contains(l.ArchiveItemId))
            .Select(l => new { l.ArchiveItemId, l.ArchiveItem.Title }).OrderBy(l => l.Title).ThenBy(l => l.ArchiveItemId).ToListAsync(cancellationToken);
        if (linked.Count == 0)
        {
            TempData["Message"] = localizer["Tick the items to move first."].Value;
            return RedirectToPage("Edit", new { id = Source.Id });
        }

        ItemIds = linked.Select(l => l.ArchiveItemId).ToList();
        Items = linked.Select(l => new MovedItem(l.ArchiveItemId, l.Title, false)).ToList();
        return null;
    }

    private async Task SummariseAsync(CancellationToken cancellationToken)
    {
        if (Target is null)
            return;
        var ids = ItemIds.ToList();
        var there = (await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == Target.Id && ids.Contains(l.ArchiveItemId))
            .Select(l => l.ArchiveItemId).ToListAsync(cancellationToken)).ToHashSet();
        Items = Items.Select(i => i with { AlreadyThere = there.Contains(i.Id) }).ToList();
    }
}
