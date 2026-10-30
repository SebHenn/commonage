using System.ComponentModel.DataAnnotations;
using Commonage.Web.Contributions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Contribute;

/// <summary>
/// The form behind a contribution link. Anyone with the link can use it, so everything sent in stays private
/// as <see cref="ItemStatus.Submitted"/> until a curator publishes it.
/// </summary>
[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
public class IndexModel(
    CommonageDbContext db,
    MediaUploadService uploads,
    FormTimer formTimer,
    IStringLocalizer<SharedResource> localizer,
    ILogger<IndexModel> logger) : PageModel
{
    public const int MaxFiles = 10;

    // Lower than for curators, because anyone can send this form. Each file is also checked against
    // Uploads:MaxFileSizeMegabytes.
    private const long MaxRequestBytes = 500L * 1024 * 1024;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>A field people never see. Programs that fill in every field give themselves away with it.</summary>
    [BindProperty]
    public string? Website { get; set; }

    /// <summary>When the form was shown, see <see cref="FormTimer"/>.</summary>
    [BindProperty]
    public string? FormStarted { get; set; }

    public ContributionLink Link { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(300, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(DateRange.MaxTextLength, ErrorMessage = "The date can be at most {1} characters long.")]
        [Display(Name = "When was it?")]
        public string? Date { get; set; }

        [StringLength(20000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "What do you know about it?")]
        public string? Description { get; set; }

        [Required(ErrorMessage = "Please enter your name.")]
        [StringLength(Contribution.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Your name")]
        public string Name { get; set; } = "";

        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [StringLength(Contribution.EmailMaxLength, ErrorMessage = "The email address can be at most {1} characters long.")]
        [Display(Name = "Your email address (optional)")]
        public string? Email { get; set; }

        [Display(Name = "Show my name with what I send in")]
        public bool ShowName { get; set; }

        [Required(ErrorMessage = "Please tell us who made it.")]
        public RightsStatement? Rights { get; set; }

        [StringLength(Contribution.RightsHolderMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Name of the photographer or author, if known")]
        public string? RightsHolder { get; set; }

        public bool Consent { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        if (await FindLinkAsync(token) is not { } link)
            return NotFound();

        Link = link;
        FormStarted = formTimer.Start();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken cancellationToken)
    {
        if (await FindLinkAsync(token) is not { } link)
            return NotFound();

        // Spam gets the same answer as a real contribution, so its sender learns nothing from trying.
        if (!string.IsNullOrEmpty(Website) || !formTimer.WasFilledInByAPerson(FormStarted))
        {
            logger.LogInformation("Discarded an automated-looking contribution to link {LinkId}.", link.Id);
            return RedirectToPage("Thanks", new { token });
        }

        if (!Input.Consent)
            ModelState.AddModelError("Input.Consent", localizer["Please agree, so that we can include what you send in."]);
        if (!string.IsNullOrWhiteSpace(Input.Date) && !DateRange.TryParse(Input.Date, out _))
            ModelState.AddModelError("Input.Date", localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."]);
        if (Files.Count > MaxFiles)
            ModelState.AddModelError("Files", localizer["You can send at most {0} photos at once.", MaxFiles]);
        foreach (var file in Files)
        {
            if (await uploads.CheckAsync(file, cancellationToken) is var error and not UploadError.None)
                ModelState.AddModelError("Files", UploadErrorMessages.Describe(localizer, error, file.FileName));
        }

        if (!ModelState.IsValid)
        {
            // Browsers do not keep chosen files when a form is shown again.
            if (Files.Count > 0)
                ModelState.AddModelError("Files", localizer["Please choose your photos again."]);
            Link = link;
            return Page();
        }

        var now = DateTime.UtcNow;
        var item = new ArchiveItem
        {
            Title = Input.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim(),
            CollectionId = link.CollectionId,
            Status = ItemStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            Contribution = new Contribution
            {
                ContributionLink = link,
                ContributorName = Input.Name.Trim(),
                ContributorEmail = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim(),
                ShowName = Input.ShowName,
                Rights = Input.Rights!.Value,
                RightsHolder = string.IsNullOrWhiteSpace(Input.RightsHolder) ? null : Input.RightsHolder.Trim(),
                ConsentText = ConsentStatement,
                SubmittedAt = now,
            },
        };
        item.TrySetDate(Input.Date);
        db.ArchiveItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var file in Files)
            await uploads.AddAsync(item, file, cancellationToken);

        return RedirectToPage("Thanks", new { token });
    }

    /// <summary>Shown next to the consent checkbox and stored with the contribution, in the contributor's language.</summary>
    public string ConsentStatement => localizer[
        "I agree that the archive may keep what I send in, describe it and publish it on this website. If I chose so above, my name is shown with it. I can ask the archive to take it down at any time."];

    private Task<ContributionLink?> FindLinkAsync(string token) =>
        db.ContributionLinks.FirstOrDefaultAsync(l => l.Token == token && l.IsActive);
}
