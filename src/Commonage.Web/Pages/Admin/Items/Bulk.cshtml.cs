using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

public enum BulkAction
{
    Publish,
    AddTags,
    SetDate,
    MoveToCollection,
    SetRights,
}

/// <summary>An item as the confirmation names it. A row of "Untitled" says nothing, so those also show their file.</summary>
public record BulkExample(string Title, string? FileName);

/// <summary>
/// One change to many items. The list sends the choice here, and this page shows how many items it is about and what would
/// happen to them, without JavaScript too. The confirmation then carries those exact items, so a list that changed in the
/// meantime cannot change what was agreed to.
/// </summary>
public class BulkModel(
    CommonageDbContext db,
    AuditLog audit,
    ArchiveProfile profile,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const int ExamplesShown = 8;

    [BindProperty]
    public BulkAction Action { get; set; }

    /// <summary>The items concerned, as one value: a whole list can be longer than the number of form fields a request may have.</summary>
    [BindProperty]
    public string Ids { get; set; } = "";

    [BindProperty]
    public string? Back
    {
        get => _back;
        set => _back = AdminItemList.FilterFrom(value) is null ? null : value;
    }

    private string? _back;

    [BindProperty]
    public string? Tags { get; set; }

    [BindProperty]
    public string? Date { get; set; }

    [BindProperty]
    public int? CollectionId { get; set; }

    [BindProperty]
    public string? RightsUri { get; set; }

    public int Count { get; private set; }

    /// <summary>The items that will change. Only publishing leaves some out.</summary>
    public int WillChange { get; private set; }

    public int WithoutTitle { get; private set; }
    public int NotDrafts { get; private set; }

    /// <summary>Items that already hold a date or a statement, which would be replaced.</summary>
    public int WouldBeReplaced { get; private set; }

    public IReadOnlyList<BulkExample> Examples { get; private set; } = [];
    public SelectList CollectionOptions { get; private set; } = null!;
    public string? DefaultRightsLabel { get; private set; }

    public string BackUrl => Back ?? AdminItemList.Page;

    public IActionResult OnGet() => LocalRedirect(AdminItemList.Page);

    /// <summary>The first step, from the list: what was chosen, and what the change would do.</summary>
    /// <param name="scope">"all" for everything in the list's filter, otherwise the ticked items.</param>
    public async Task<IActionResult> OnPostAsync(string? scope, List<int> selected, CancellationToken cancellationToken)
    {
        // The form on the list has no way to know about these: nothing is changed or validated yet on this step.
        ModelState.Clear();
        var ids = selected;
        if (scope == "all")
        {
            var query = await db.QueryAsync(AdminItemList.FilterFrom(Back) ?? new AdminItemFilter(), cancellationToken);
            ids = await query.Select(i => i.Id).ToListAsync(cancellationToken);
        }

        if (ids.Count == 0)
        {
            TempData["Error"] = localizer["Please tick at least one item."].Value;
            return LocalRedirect(BackUrl);
        }

        Ids = string.Join(',', ids);
        await ShowAsync(ids, cancellationToken);
        return Page();
    }

    /// <summary>The second step: the change, to exactly the items that were shown.</summary>
    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        var ids = ParseIds();
        var tagNames = Tag.ParseList(Tags);
        switch (Action)
        {
            case BulkAction.AddTags:
                if (tagNames.Count == 0)
                    ModelState.AddModelError(nameof(Tags), localizer["Please enter at least one tag."]);
                foreach (var error in ItemFields.TagErrors(localizer, tagNames))
                    ModelState.AddModelError(nameof(Tags), error);
                break;
            case BulkAction.SetDate:
                if (string.IsNullOrWhiteSpace(Date))
                    ModelState.AddModelError(nameof(Date), localizer["Please enter a date."]);
                else if (Date.Trim().Length > DateRange.MaxTextLength)
                    ModelState.AddModelError(nameof(Date), localizer["The date can be at most {0} characters long.", DateRange.MaxTextLength]);
                else if (ItemFields.DateError(localizer, Date) is { } dateError)
                    ModelState.AddModelError(nameof(Date), dateError);
                break;
            case BulkAction.MoveToCollection:
                if (CollectionId is null || !await db.Collections.AnyAsync(c => c.Id == CollectionId, cancellationToken))
                    ModelState.AddModelError(nameof(CollectionId), localizer["Please choose a collection."]);
                break;
            case BulkAction.SetRights:
                if (ItemFields.RightsError(localizer, RightsUri) is { } rightsError)
                    ModelState.AddModelError(nameof(RightsUri), rightsError);
                break;
        }

        if (!ModelState.IsValid)
        {
            await ShowAsync(ids, cancellationToken);
            return Page();
        }

        var items = await db.ArchiveItems
            .Include(i => i.Tags)
            .Include(i => i.Contribution)
            .Where(i => ids.Contains(i.Id))
            .OrderBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var tags = Action == BulkAction.AddTags ? await db.FindOrCreateTagsAsync(tagNames, cancellationToken) : [];

        var now = DateTime.UtcNow;
        var changed = 0;
        foreach (var item in items)
        {
            if (!Apply(item, tags, now))
                continue;
            changed++;
            item.UpdatedAt = now;
            audit.Record(Action == BulkAction.Publish ? AuditAction.ItemPublished : AuditAction.ItemChanged, item.Title, item);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Saved as one, so all of it or none. Going through the list again sees what the other person did.
            TempData["Error"] = localizer["Someone else changed one of these items just now, so nothing was changed. Please try again."].Value;
            return LocalRedirect(BackUrl);
        }

        TempData["Message"] = localizer["Items changed: {0}. Left as they were: {1}.", changed, items.Count - changed].Value;
        return LocalRedirect(BackUrl);
    }

    /// <summary>Returns whether the item changed. An item that has it already is not worth a line in its history.</summary>
    private bool Apply(ArchiveItem item, List<Tag> tags, DateTime now)
    {
        switch (Action)
        {
            case BulkAction.Publish:
                // A contribution waits for a curator's decision on that contribution, and that takes looking at it.
                if (!IsPublishable(item))
                    return false;
                item.Publish(now);
                return true;
            case BulkAction.AddTags:
                var missing = tags.Where(tag => item.Tags.All(t => t.NormalizedName != tag.NormalizedName)).ToList();
                if (missing.Count == 0 || item.Tags.Count + missing.Count > ItemFields.MaxTagsPerItem)
                    return false;
                item.SetTags([.. item.Tags, .. missing]);
                return true;
            case BulkAction.SetDate:
                if (item.DateText == Date!.Trim())
                    return false;
                return item.TrySetDate(Date);
            case BulkAction.MoveToCollection:
                if (item.CollectionId == CollectionId)
                    return false;
                item.CollectionId = CollectionId!.Value;
                return true;
            case BulkAction.SetRights:
                var statement = ItemFields.Clean(RightsUri);
                if (item.RightsUri == statement)
                    return false;
                item.RightsUri = statement;
                return true;
            default:
                return false;
        }
    }

    private static bool IsPublishable(ArchiveItem item) => item.Status == ItemStatus.Draft && item.CanBePublished;

    private List<int> ParseIds() =>
        Ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => int.TryParse(id, out var number) ? number : 0).Where(id => id > 0).ToList();

    private async Task ShowAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var items = db.ArchiveItems.Where(i => ids.Contains(i.Id));
        Count = await items.CountAsync(cancellationToken);
        WithoutTitle = await items.CountAsync(i => i.Status == ItemStatus.Draft && i.Title == "", cancellationToken);
        NotDrafts = await items.CountAsync(i => i.Status != ItemStatus.Draft, cancellationToken);
        WillChange = Action == BulkAction.Publish ? Count - WithoutTitle - NotDrafts : Count;
        WouldBeReplaced = Action switch
        {
            BulkAction.SetDate => await items.CountAsync(i => i.DateText != null, cancellationToken),
            BulkAction.SetRights => await items.CountAsync(i => i.RightsUri != null, cancellationToken),
            _ => 0,
        };
        Examples = await items.OrderBy(i => i.Id).Take(ExamplesShown)
            .Select(i => new BulkExample(i.Title, i.MediaFiles.OrderBy(f => f.Id).Select(f => f.OriginalFileName).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync(cancellationToken);
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title), CollectionId);
        DefaultRightsLabel = (await profile.GetAsync()).DefaultRightsUri is { } uri ? RightsOptions.Label(localizer, uri) : null;
    }
}
