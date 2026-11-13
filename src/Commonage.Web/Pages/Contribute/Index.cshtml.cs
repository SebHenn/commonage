using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Contributions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Pages.Contribute;

/// <summary>
/// The form behind a contribution link. Anyone with the link can use it, so everything sent in stays private
/// as <see cref="ItemStatus.Submitted"/> until a curator publishes it. With JavaScript, contribute.js sends it and asks
/// for an answer in JSON, so that the chosen photos stay chosen when something needs correcting.
/// </summary>
[AcceptsUploads]
[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
public class IndexModel(
    CommonageDbContext db,
    MediaUploadService uploads,
    AuditLog audit,
    FormTimer formTimer,
    ArchiveHealth health,
    ArchiveProfile profile,
    IOptions<ContributionOptions> options,
    IOptions<UploadOptions> uploadOptions,
    IStringLocalizer<SharedResource> localizer,
    ILogger<IndexModel> logger) : PageModel
{
    public const int MaxFiles = 10;

    // Lower than for curators, because anyone can send this form. Each file is also checked against
    // Uploads:MaxFileSizeMegabytes.
    public const long MaxRequestBytes = 500L * 1024 * 1024;

    /// <summary>What contribute.js checks before anything is sent. The same limit is checked here again.</summary>
    public long MaxFileBytes => uploadOptions.Value.MaxFileSizeBytes;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Stays the same for as long as this form is open, see <see cref="SendingFingerprints"/>.</summary>
    [BindProperty]
    public string? SendingKey { get; set; }

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>A field people never see. Programs that fill in every field give themselves away with it.</summary>
    [BindProperty]
    public string? Website { get; set; }

    /// <summary>When the form was shown, see <see cref="FormTimer"/>.</summary>
    [BindProperty]
    public string? FormStarted { get; set; }

    public ContributionLink Link { get; private set; } = null!;

    /// <summary>The link exists but a curator closed it. The page says so, instead of "not found" that sounds like a mistake.</summary>
    public bool Closed { get; private set; }

    /// <summary>When the disk is nearly full, people are told so before they fill in the form.</summary>
    public bool CanTakeContributions => health.CanAcceptUploads();

    public ArchiveIdentity Identity { get; private set; } = null!;

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(300, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(DateRange.MaxTextLength, ErrorMessage = "The date can be at most {1} characters long.")]
        [Display(Name = "When was it? (optional)")]
        public string? Date { get; set; }

        [StringLength(20000, ErrorMessage = "The description can be at most {1} characters long.")]
        [Display(Name = "What do you know about it? (optional)")]
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

        [Display(Name = "May others reuse it? (optional)")]
        public ReuseOffer? Reuse { get; set; }

        public bool Consent { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        if (await FindLinkAsync(token) is not { } link)
            return await ClosedOrNotFoundAsync(token);

        Link = link;
        Identity = await profile.GetAsync();
        FormStarted = formTimer.Start();
        SendingKey = Guid.NewGuid().ToString("N");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken cancellationToken)
    {
        if (await FindLinkAsync(token) is not { } link)
            return await ClosedOrNotFoundAsync(token);

        // Spam gets the same answer as a real contribution, so its sender learns nothing from trying.
        if (!string.IsNullOrEmpty(Website) || !formTimer.WasFilledInByAPerson(FormStarted))
        {
            logger.LogInformation("Discarded an automated-looking contribution to link {LinkId}.", link.Id);
            return Sent(token, Contribution.NewReferenceCode());
        }

        if (!Input.Consent)
            ModelState.AddModelError("Input.Consent", localizer["Please agree, so that we can include what you send in."]);
        if (!string.IsNullOrWhiteSpace(Input.Date) && !DateRange.TryParse(Input.Date, out _))
            ModelState.AddModelError("Input.Date", localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."]);
        if (Files.Count > MaxFiles)
            ModelState.AddModelError("Files", localizer.Plural("You can send at most {0, plural, one {# photo} other {# photos}} at once.", MaxFiles));
        foreach (var file in Files)
        {
            if (await uploads.CheckAsync(file, cancellationToken) is var error and not UploadError.None)
                ModelState.AddModelError("Files", UploadErrorMessages.Describe(localizer, error, file.FileName));
        }

        var linkIsFull = Files.Count > 0 && await WouldExceedDailyLimitAsync(link, cancellationToken);
        if (linkIsFull)
            ModelState.AddModelError("Files", localizer["A lot has been sent through this link today. Please try again tomorrow, or send this without photos."]);

        var sentByScript = Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
        if (!ModelState.IsValid)
        {
            if (sentByScript)
            {
                return BadRequest(new
                {
                    errors = ModelState
                        .Where(field => field.Value is { Errors.Count: > 0 })
                        .ToDictionary(field => field.Key, field => field.Value!.Errors.Select(error => error.ErrorMessage)),
                });
            }

            // Browsers do not keep chosen files when a form is shown again.
            if (Files.Count > 0 && !linkIsFull)
                ModelState.AddModelError("Files", localizer["Please choose your photos again."]);
            Link = link;
            Identity = await profile.GetAsync();
            return Page();
        }

        var fingerprint = SendingFingerprints.Of(SendingKey, link.Id, [Input.Title, Input.Date, Input.Description, Input.Rights.ToString(), Input.RightsHolder, Input.Reuse.ToString()], Files);
        if (await AlreadySentAsync(fingerprint) is { } reference)
            return Sent(token, reference);

        // Nobody can offer what they do not know to be theirs.
        var reuse = Input.Rights == RightsStatement.Unknown ? null : Input.Reuse;

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
                ConsentText = reuse switch
                {
                    ReuseOffer.AnyUseWithCredit => $"{ConsentStatement} {localizer["I also allow anyone to reuse it for any purpose, with credit (CC BY 4.0)."]}",
                    ReuseOffer.NonCommercialWithCredit => $"{ConsentStatement} {localizer["I also allow anyone to reuse it for non-commercial purposes, with credit (CC BY-NC 4.0)."]}",
                    _ => ConsentStatement,
                },
                Reuse = reuse,
                ConsentCoversSharing = true,
                ReferenceCode = Contribution.NewReferenceCode(),
                SendingFingerprint = fingerprint,
                SubmittedAt = now,
            },
        };
        item.TrySetDate(Input.Date);
        db.ArchiveItems.Add(item);
        audit.Record(AuditAction.ContributionReceived, item.Title, item);

        // Everything has arrived by now. Whether the sender is still connected no longer matters: stopping halfway would
        // leave a contribution without all its photos, and sending again would be taken for the same one.
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException) when (fingerprint is not null)
        {
            // Sent twice at the same moment, and the other one got there first.
            db.ChangeTracker.Clear();
            if (await AlreadySentAsync(fingerprint) is { } sameMoment)
                return Sent(token, sameMoment);
            throw;
        }

        try
        {
            foreach (var file in Files)
                await uploads.AddAsync(item, file, CancellationToken.None);
        }
        catch
        {
            // Not all photos are stored, and the person is about to see an error. Sending again has to really send them.
            await db.Contributions
                .IgnoreQueryFilters()
                .Where(c => c.ArchiveItemId == item.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.SendingFingerprint, (string?)null), CancellationToken.None);
            throw;
        }

        return Sent(token, item.Contribution.ReferenceCode);
    }

    private IActionResult Sent(string token, string reference)
    {
        TempData["Reference"] = reference;
        return Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
            ? new JsonResult(new { thanksUrl = Url.Page("Thanks", new { token }) })
            : RedirectToPage("Thanks", new { token });
    }

    /// <summary>The reference of the same form sent earlier, also when that contribution is in the bin by now.</summary>
    private async Task<string?> AlreadySentAsync(string? fingerprint) =>
        fingerprint is null
            ? null
            : await db.Contributions
                .IgnoreQueryFilters()
                .Where(c => c.SendingFingerprint == fingerprint)
                .Select(c => c.ReferenceCode)
                .FirstOrDefaultAsync(CancellationToken.None);

    /// <summary>Shown next to the consent checkbox and stored with the contribution, in the contributor's language.</summary>
    public string ConsentStatement => localizer[
        "I agree that the archive may keep what I send in, describe it and publish it on this website, also where a link to it is shared and in its list of what is new. If I chose so above, my name is shown with it. I can ask the archive to take it down at any time, but it cannot reach copies others have made."];

    /// <summary>Counts what is in the bin too, because it still takes up disk space.</summary>
    private async Task<bool> WouldExceedDailyLimitAsync(ContributionLink link, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var received = await db.MediaFiles
            .IgnoreQueryFilters()
            .Where(f => f.ArchiveItem.Contribution!.ContributionLinkId == link.Id && f.UploadedAt >= since)
            .SumAsync(f => f.SizeBytes, cancellationToken);
        return received + Files.Sum(f => f.Length) > options.Value.MaxUploadMegabytesPerLinkPerDay * 1024L * 1024L;
    }

    /// <summary>Only a closed link of this archive gets the page; the page shows nothing from the link, not even its heading.</summary>
    private async Task<IActionResult> ClosedOrNotFoundAsync(string token)
    {
        if (!await db.ContributionLinks.AnyAsync(l => l.Token == token))
            return NotFound();

        Closed = true;
        Identity = await profile.GetAsync();
        Response.StatusCode = StatusCodes.Status410Gone;
        return Page();
    }

    private Task<ContributionLink?> FindLinkAsync(string token) =>
        db.ContributionLinks.FirstOrDefaultAsync(l => l.Token == token && l.IsActive);
}
