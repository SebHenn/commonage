using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.People;

/// <summary>
/// "This is the same person as ...": the person of the address goes into another one. The other (the target) keeps its data and gets
/// the items and the names. Three steps, all plain pages: find the target by number or name, read what will happen, confirm. The
/// stricter of the two choices of who may see a person wins, so a merge never shows more than either person showed, and the page
/// says so before anything happens. The merged person goes to the bin and keeps its links, which the bin's filter hides from every page.
/// </summary>
public class MergeModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    public Person Source { get; private set; } = null!;
    public Person? Target { get; private set; }
    public bool TargetInBin { get; private set; }

    /// <summary>What the person asked for: a number or a name.</summary>
    public string? With { get; private set; }

    public IReadOnlyList<PersonMatch> Matches { get; private set; } = [];
    public bool NothingFound { get; private set; }

    public int SourceItems { get; private set; }
    public int NewItems { get; private set; }
    public int SourceNamesCarried { get; private set; }

    /// <summary>The choice the merged person will have, and whether it differs from the target's now.</summary>
    public PersonDisclosure Result { get; private set; }

    public bool ResultStricterThanTarget => Result != Target!.Disclosure;
    public bool TargetHidden => Target!.Disclosure == PersonDisclosure.Hidden;

    /// <summary>The stricter of two choices: only curators, then may be living, then has died.</summary>
    public static PersonDisclosure Stricter(PersonDisclosure a, PersonDisclosure b) => Rank(a) <= Rank(b) ? a : b;

    /// <summary>How much visitors see of a person: nothing, name and text, then dates too.</summary>
    public static int Rank(PersonDisclosure value) => value switch
    {
        PersonDisclosure.Hidden => 0,
        PersonDisclosure.MayBeShown => 1,
        _ => 2,
    };

    public async Task<IActionResult> OnGetAsync(int id, string? with, int? target, CancellationToken cancellationToken)
    {
        if (!await LoadSourceAsync(id, cancellationToken))
            return NotFound();
        With = with?.Trim();

        if (target is { } targetId)
        {
            if (!await LoadTargetAsync(targetId, cancellationToken))
                return NotFound();
            await SummariseAsync(cancellationToken);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(With))
            return Page();

        if (With.Length <= 9 && With.All(char.IsAsciiDigit) && int.TryParse(With, out var number))
        {
            if (number != id && await db.People.IgnoreQueryFilters().AnyAsync(p => p.Id == number, cancellationToken))
                return RedirectToPage(new { id, target = number });
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
            return RedirectToPage(new { id, target = matches[0].Id });
        Matches = matches;
        NothingFound = matches.Count == 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, int target, CancellationToken cancellationToken)
    {
        if (!await LoadSourceAsync(id, cancellationToken))
            return NotFound();
        if (target == id || !await LoadTargetAsync(target, cancellationToken))
            return NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        var targetPerson = Target!;

        // The items: the ones the target has already are not linked twice. Items in the bin move too, as they would come back with it.
        var sourceLinks = await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == id).ToListAsync(cancellationToken);
        var already = (await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == target).Select(l => l.ArchiveItemId).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var link in sourceLinks)
        {
            if (already.Add(link.ArchiveItemId))
                db.ItemPeople.Add(new ItemPerson { ArchiveItemId = link.ArchiveItemId, PersonId = target, CreatedAt = now });
        }

        // The names: this person's name and other names become other names of the target, never shown to visitors, without doubles.
        var known = new HashSet<string> { targetPerson.FoldedName };
        foreach (var name in targetPerson.Names)
            known.Add(name.FoldedName);
        foreach (var (text, kind, language) in CarriedNames())
        {
            if (targetPerson.Names.Count >= Person.MaxNames)
                break;
            if (known.Add(await db.FoldAsync(text, cancellationToken)))
                targetPerson.Names.Add(new PersonName { Name = text, Kind = kind, Language = language, ShownToVisitors = false });
        }

        var result = Stricter(targetPerson.Disclosure, Source.Disclosure);
        if (result != targetPerson.Disclosure)
            targetPerson.SetDisclosure(result, now);
        targetPerson.Touch(now);

        // This person: hidden and in the bin. Its links stay on it (the bin's filter hides them), so a restore brings the items back.
        Source.SetDisclosure(PersonDisclosure.Hidden, now);
        Source.MoveToBin(now);
        Source.Touch(now);

        audit.Record(AuditAction.PeopleMerged, null, person: targetPerson);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Message"] = localizer["Someone else changed this person meanwhile. Nothing was merged; look at it again."].Value;
            return RedirectToPage(new { id });
        }

        TempData["Message"] = localizer["The two people are merged."].Value;
        return TargetInBin ? RedirectToPage("Index") : RedirectToPage("Edit", new { id = target });
    }

    private IEnumerable<(string Name, PersonNameKind Kind, string? Language)> CarriedNames()
    {
        yield return (Source.Name, PersonNameKind.Other, null);
        foreach (var name in Source.Names.OrderBy(n => n.Id))
            yield return (name.Name, name.Kind, name.Language);
    }

    private async Task<bool> LoadSourceAsync(int id, CancellationToken cancellationToken)
    {
        var person = await db.People.Include(p => p.Names).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
            return false;
        Source = person;
        return true;
    }

    /// <summary>The target may be in the bin: a person is found by number there, and the result stays in the bin.</summary>
    private async Task<bool> LoadTargetAsync(int id, CancellationToken cancellationToken)
    {
        var person = await db.People.IgnoreQueryFilters().Include(p => p.Names).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null || person.Id == Source.Id)
            return false;
        Target = person;
        TargetInBin = person.DeletedAt is not null;
        Result = Stricter(person.Disclosure, Source.Disclosure);
        return true;
    }

    private async Task SummariseAsync(CancellationToken cancellationToken)
    {
        var sourceItems = await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == Source.Id).Select(l => l.ArchiveItemId).ToListAsync(cancellationToken);
        var targetItems = await db.ItemPeople.IgnoreQueryFilters().Where(l => l.PersonId == Target!.Id).Select(l => l.ArchiveItemId).ToListAsync(cancellationToken);
        SourceItems = sourceItems.Count;
        NewItems = sourceItems.Except(targetItems).Count();
        var known = new HashSet<string> { Target!.FoldedName };
        foreach (var name in Target.Names)
            known.Add(name.FoldedName);
        var carried = 0;
        foreach (var (text, _, _) in CarriedNames())
        {
            if (Target.Names.Count + carried >= Person.MaxNames)
                break;
            if (known.Add(await db.FoldAsync(text, cancellationToken)))
                carried++;
        }

        SourceNamesCarried = carried;
    }
}
