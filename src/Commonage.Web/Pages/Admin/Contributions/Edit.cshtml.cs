using System.ComponentModel.DataAnnotations;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Contributions;

public class EditModel(CommonageDbContext db, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public int? LinkId { get; private set; }
    public SelectList CollectionOptions { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(ContributionLink.TitleMaxLength, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(ContributionLink.IntroductionMaxLength, ErrorMessage = "The introduction can be at most {1} characters long.")]
        [Display(Name = "Introduction")]
        public string? Introduction { get; set; }

        [Required(ErrorMessage = "Please choose a collection.")]
        [Display(Name = "Collection")]
        public int? CollectionId { get; set; }

        [Display(Name = "Open: the link accepts contributions")]
        public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is not null)
        {
            var link = await db.ContributionLinks.FindAsync(id);
            if (link is null)
                return NotFound();

            Input = new InputModel
            {
                Title = link.Title,
                Introduction = link.Introduction,
                CollectionId = link.CollectionId,
                IsActive = link.IsActive,
            };
            LinkId = link.Id;
        }

        await LoadCollectionsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        ContributionLink? link = null;
        if (id is not null)
        {
            link = await db.ContributionLinks.FindAsync(id);
            if (link is null)
                return NotFound();
        }

        if (Input.CollectionId is not null && !await db.Collections.AnyAsync(c => c.Id == Input.CollectionId))
            ModelState.AddModelError("Input.CollectionId", localizer["Please choose a collection."]);

        if (!ModelState.IsValid)
        {
            LinkId = id;
            await LoadCollectionsAsync();
            return Page();
        }

        if (link is null)
        {
            link = new ContributionLink { Title = "", Token = ContributionLink.NewToken(), CreatedAt = DateTime.UtcNow };
            db.ContributionLinks.Add(link);
        }

        link.Title = Input.Title.Trim();
        link.Introduction = string.IsNullOrWhiteSpace(Input.Introduction) ? null : Input.Introduction.Trim();
        link.CollectionId = Input.CollectionId!.Value;
        link.IsActive = Input.IsActive;
        await db.SaveChangesAsync();

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage("Index");
    }

    private async Task LoadCollectionsAsync()
    {
        var collections = await db.Collections.OrderBy(c => c.Title).ToListAsync();
        CollectionOptions = new SelectList(collections, nameof(Collection.Id), nameof(Collection.Title));
    }
}
