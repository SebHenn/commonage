using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Collections;

public class EditModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? CollectionId { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(200, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(4000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "Description")]
        public string? Description { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
            return Page();

        var collection = await db.Collections.FindAsync(id);
        if (collection is null)
            return NotFound();

        Input = new InputModel { Title = collection.Title, Description = collection.Description };
        CollectionId = collection.Id;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        Collection? collection = null;
        if (id is not null)
        {
            collection = await db.Collections.FindAsync(id);
            if (collection is null)
                return NotFound();
        }

        if (!ModelState.IsValid)
        {
            CollectionId = id;
            return Page();
        }

        var isNew = collection is null;
        if (collection is null)
        {
            collection = new Collection { Title = "", CreatedAt = DateTime.UtcNow };
            db.Collections.Add(collection);
        }

        collection.Title = Input.Title.Trim();
        collection.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        audit.Record(isNew ? AuditAction.CollectionCreated : AuditAction.CollectionChanged, collection.Title);
        await db.SaveChangesAsync();

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage("/Admin/Collections/Index");
    }
}
