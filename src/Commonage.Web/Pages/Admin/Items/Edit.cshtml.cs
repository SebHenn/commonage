using System.ComponentModel.DataAnnotations;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Items;

public class EditModel(CommonageDbContext db, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? ItemId { get; private set; }
    public ItemStatus? CurrentStatus { get; private set; }
    public SelectList CollectionOptions { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(300, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(20000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Please choose a collection.")]
        [Display(Name = "Collection")]
        public int? CollectionId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id, int? collectionId)
    {
        if (id is not null)
        {
            var item = await db.ArchiveItems.FindAsync(id);
            if (item is null)
                return NotFound();

            Input = new InputModel { Title = item.Title, Description = item.Description, CollectionId = item.CollectionId };
            ShowState(item);
        }
        else
        {
            Input.CollectionId = collectionId;
        }

        await LoadCollectionsAsync();
        return Page();
    }

    /// <param name="command">"publish" or "unpublish" changes the status; anything else only saves.</param>
    public async Task<IActionResult> OnPostAsync(int? id, string? command)
    {
        ArchiveItem? item = null;
        if (id is not null)
        {
            item = await db.ArchiveItems.FindAsync(id);
            if (item is null)
                return NotFound();
        }

        if (Input.CollectionId is not null && !await db.Collections.AnyAsync(c => c.Id == Input.CollectionId))
            ModelState.AddModelError("Input.CollectionId", localizer["Please choose a collection."]);

        if (!ModelState.IsValid)
        {
            ShowState(item);
            await LoadCollectionsAsync();
            return Page();
        }

        var now = DateTime.UtcNow;
        if (item is null)
        {
            item = new ArchiveItem { Title = "", Status = ItemStatus.Draft, CreatedAt = now };
            db.ArchiveItems.Add(item);
        }

        item.Title = Input.Title.Trim();
        item.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        item.CollectionId = Input.CollectionId!.Value;
        item.UpdatedAt = now;

        switch (command)
        {
            case "publish":
                item.Publish(now);
                TempData["Message"] = localizer["The item is published."].Value;
                break;
            case "unpublish":
                item.Unpublish();
                TempData["Message"] = localizer["The item is no longer public."].Value;
                break;
            default:
                TempData["Message"] = localizer["Your changes are saved."].Value;
                break;
        }

        await db.SaveChangesAsync();
        return RedirectToPage(new { id = item.Id });
    }

    private void ShowState(ArchiveItem? item)
    {
        ItemId = item?.Id;
        CurrentStatus = item?.Status;
    }

    private async Task LoadCollectionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
    }
}
