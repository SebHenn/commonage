using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Tags;

/// <summary>One tag: its name and kind, merging it into another, and deleting it once no item has it.</summary>
public class EditModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The name of the tag to merge this one into.</summary>
    [BindProperty]
    public string? MergeInto { get; set; }

    public Tag Tag { get; private set; } = null!;

    public TagUse Use { get; private set; } = new(0, 0);

    /// <summary>
    /// The name form came back with what the curator typed, unsaved, so leaving the page asks first. Not when the name belongs to
    /// another tag: merging is what the page then suggests, and it should not ask about the name it replaces.
    /// </summary>
    public bool NameUnsaved { get; private set; }

    /// <summary>The other tags' names, offered while typing which one to merge into.</summary>
    public IReadOnlyList<string> OtherNames { get; private set; } = [];

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(Tag.MaxLength, ErrorMessage = "A tag can be at most {1} characters long.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Kind")]
        public TagKind Kind { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();

        Input = new InputModel { Name = Tag.Name, Kind = Tag.Kind };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();

        // Items take their tags as a list separated by commas, so a name with one would fall apart the next time an item is saved.
        var names = Tag.ParseList(Input.Name);
        if (ModelState.IsValid && names.Count != 1)
            ModelState.AddModelError("Input.Name", localizer["A tag name cannot contain commas or semicolons."]);
        if (!ModelState.IsValid)
        {
            NameUnsaved = true;
            return Page();
        }

        var name = names[0];
        if (await db.OtherTagNamedAsync(Tag, name, cancellationToken) is { } other)
        {
            ModelState.AddModelError("Input.Name", localizer["There is already a tag “{0}”. To make one tag of the two, merge them below.", other.Name]);
            MergeInto = other.Name;
            return Page();
        }

        var oldName = Tag.Name;
        var renamed = !string.Equals(name, oldName, StringComparison.Ordinal);
        if (!renamed && Input.Kind == Tag.Kind)
            return RedirectToPage("/Admin/Tags/Index");

        if (renamed)
            await db.RenameAsync(Tag, name, cancellationToken);
        Tag.Kind = Input.Kind;
        audit.Record(AuditAction.TagChanged, renamed ? $"{oldName} → {name}" : name);
        if (!await SaveAsync(cancellationToken))
        {
            NameUnsaved = true;
            return Page();
        }

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage("/Admin/Tags/Index");
    }

    public async Task<IActionResult> OnPostMergeAsync(int id, CancellationToken cancellationToken)
    {
        // Only the merge field belongs to this form; the name above was not sent with it.
        ModelState.Clear();
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        Input = new InputModel { Name = Tag.Name, Kind = Tag.Kind };

        var intoName = Tag.ParseList(MergeInto).FirstOrDefault();
        if (intoName is null)
        {
            ModelState.AddModelError(nameof(MergeInto), localizer["Please enter the tag to merge this one into."]);
            return Page();
        }
        var into = await db.Tags.FirstOrDefaultAsync(t => t.NormalizedName == Tag.Normalize(intoName), cancellationToken);
        if (into is null)
        {
            ModelState.AddModelError(nameof(MergeInto), localizer["There is no tag “{0}”. To give this tag a new name, change its name above.", intoName]);
            return Page();
        }
        if (into.Id == Tag.Id)
        {
            ModelState.AddModelError(nameof(MergeInto), localizer["Please choose a different tag to merge this one into."]);
            return Page();
        }

        var from = Tag.Name;
        var count = await db.MergeAsync(Tag, into, cancellationToken);
        audit.Record(AuditAction.TagsMerged, $"{from} → {into.Name}");
        if (!await SaveAsync(cancellationToken))
            return Page();

        TempData["Message"] = localizer.Plural("“{1}” is merged into “{2}”, {0, plural, =0 {which no item had.} one {on one item.} other {on # items.}}", count, from, into.Name);
        return RedirectToPage("/Admin/Tags/Edit", new { id = into.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();
        Input = new InputModel { Name = Tag.Name, Kind = Tag.Kind };

        if (!Use.IsUnused)
        {
            ModelState.AddModelError("", localizer["Only a tag that no item has can be deleted. To move its items to another tag, merge it."]);
            return Page();
        }

        db.Tags.Remove(Tag);
        audit.Record(AuditAction.TagDeleted, Tag.Name);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Message"] = localizer["The tag “{0}” is deleted.", Tag.Name].Value;
        return RedirectToPage("/Admin/Tags/Index");
    }

    private async Task<bool> LoadAsync(int id, CancellationToken cancellationToken)
    {
        if (await db.Tags.FindAsync([id], cancellationToken) is not { } tag)
            return false;

        Tag = tag;
        Use = await db.UseOfAsync(id, cancellationToken);
        OtherNames = await db.Tags.AsNoTracking().Where(t => t.Id != id).OrderBy(t => t.NormalizedName).Select(t => t.Name).ToListAsync(cancellationToken);
        return true;
    }

    /// <summary>Saves, unless an item with this tag was changed at the same moment, in which case nothing is changed.</summary>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError("", localizer["An item with this tag was changed at the same moment. Nothing was changed; please try again."]);
            return false;
        }
    }
}
