using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Contributions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Items;

/// <summary>
/// "Do you know more about this?": a visitor tells the curators something about a published item, corrects it, or asks
/// for it to be taken down. Open to anyone, so it is protected like the contribution form, and only exists while the
/// archive has turned it on.
/// </summary>
[RequestSizeLimit(MaxRequestBytes)]
public class TellModel(
    CommonageDbContext db,
    AuditLog audit,
    FormTimer formTimer,
    ArchiveProfile profile,
    TimeProvider clock,
    ILogger<TellModel> logger) : PageModel
{
    /// <summary>A message and a few short fields; anything much larger is not from this form.</summary>
    public const long MaxRequestBytes = 64 * 1024;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>A field people never see, see the contribution form.</summary>
    [BindProperty]
    public string? Website { get; set; }

    /// <summary>When the form was shown, see <see cref="FormTimer"/>.</summary>
    [BindProperty]
    public string? FormStarted { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    public ArchiveItem Item { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "Please choose what you would like to tell us.")]
        public VisitorNoteKind? Kind { get; set; }

        [Required(ErrorMessage = "Please write your message.")]
        [StringLength(VisitorNote.MessageMaxLength, ErrorMessage = "The message can be at most {1} characters long.")]
        [Display(Name = "Your message")]
        public string Message { get; set; } = "";

        [StringLength(Contribution.NameMaxLength, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Your name (optional)")]
        public string? Name { get; set; }

        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [StringLength(Contribution.EmailMaxLength, ErrorMessage = "The email address can be at most {1} characters long.")]
        [Display(Name = "Your email address (optional)")]
        public string? Email { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();

        FormStarted = formTimer.Start();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
            return NotFound();

        // Spam gets the same answer as a real note, so its sender learns nothing from trying.
        if (!string.IsNullOrEmpty(Website) || !formTimer.WasFilledInByAPerson(FormStarted))
        {
            logger.LogInformation("Discarded an automated-looking note about item {ItemId}.", id);
            return Thanks(id);
        }

        if (!ModelState.IsValid)
            return Page();

        var now = clock.GetUtcNow().UtcDateTime;
        db.VisitorNotes.Add(new VisitorNote
        {
            ArchiveItem = Item,
            Kind = Input.Kind!.Value,
            Message = Input.Message.Trim(),
            Name = string.IsNullOrWhiteSpace(Input.Name) ? null : Input.Name.Trim(),
            Email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim(),
            ReceivedAt = now,
        });
        // Names the item, never the visitor: the log is kept longer than personal details should be.
        audit.Record(AuditAction.VisitorNoteReceived, Item.Title, Item);
        await db.SaveChangesAsync(cancellationToken);
        return Thanks(id);
    }

    private RedirectToPageResult Thanks(int id) => RedirectToPage("/Items/Tell", new { id, sent = true });

    /// <summary>Only a published item, and only while the archive asks visitors.</summary>
    private async Task<bool> LoadAsync(int id, CancellationToken cancellationToken)
    {
        if (!(await profile.GetAsync()).AcceptVisitorNotes)
            return false;
        if (await db.PublishedItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken) is not { } item)
            return false;
        Item = item;
        return true;
    }
}
