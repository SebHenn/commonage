using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin;

/// <summary>Makes the archive the group's own, without touching any files on the server.</summary>
[AcceptsUploads]
[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
public class SettingsModel(
    CommonageDbContext db,
    ArchiveProfile profile,
    MediaProcessingQueue previews,
    LogoImages logos,
    AuditLog audit,
    TimeProvider clock,
    SupportedLanguages languages,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    private const long MaxRequestBytes = LogoImages.MaxBytes + (1024 * 1024);

    // The archive's standard blue, shown in the colour picker while no colour is chosen.
    private const string StandardColor = "#0d6efd";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Logo { get; set; }

    public Guid? CurrentLogoId { get; private set; }
    public IReadOnlyList<string> TimeZones { get; private set; } = [];
    public IReadOnlyList<string> Languages => languages.Codes;

    /// <summary>Languages dates can also be read in: those with words of their own, and those of the interface.</summary>
    public IReadOnlyList<string> DateLanguageChoices =>
        DateVocabulary.All.Select(v => v.Language).Concat(languages.Codes).Distinct().Where(SupportedLanguages.AllLanguages.Contains).ToList();

    public class InputModel
    {
        [StringLength(200, ErrorMessage = "The name can be at most {1} characters long.")]
        [Display(Name = "Name of the archive")]
        public string? Name { get; set; }

        [StringLength(300, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Tagline")]
        public string? Tagline { get; set; }

        [StringLength(ArchiveText.MaxLength, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Welcome text on the start page")]
        public string? Intro { get; set; }

        [StringLength(ArchiveText.MaxLength, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "About this archive")]
        public string? About { get; set; }

        [StringLength(ArchiveText.MaxLength, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Contact")]
        public string? Contact { get; set; }

        [StringLength(ArchiveText.MaxLength, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Privacy")]
        public string? Privacy { get; set; }

        [Display(Name = "We have read this text, and it is right for our archive.")]
        public bool PrivacyReviewed { get; set; }

        [StringLength(ArchiveText.MaxLength, ErrorMessage = "This text can be at most {1} characters long.")]
        [Display(Name = "Imprint")]
        public string? Imprint { get; set; }

        [Display(Name = "Our archive needs no imprint.")]
        public bool ImprintNotNeeded { get; set; }

        [Display(Name = "Colour of buttons and links")]
        public string? AccentColor { get; set; }

        [Display(Name = "Use the standard colour")]
        public bool UseStandardColor { get; set; }

        [Display(Name = "Remove the logo")]
        public bool RemoveLogo { get; set; }

        [Display(Name = "Language of the archive")]
        public string DefaultLanguage { get; set; } = "en";

        [Display(Name = "Language of the material")]
        public string? ContentLanguage { get; set; }

        public List<string> OtherContentLanguages { get; set; } = [];

        [Display(Name = "Time zone")]
        public string? TimeZone { get; set; }

        [Display(Name = "What may others do with your items, unless an item says otherwise?")]
        public string? DefaultRightsUri { get; set; }

        [Display(Name = "Let search engines list this archive")]
        public bool AllowSearchEngines { get; set; } = true;

        [Display(Name = "Everyone who manages the archive must log in with an authenticator app")]
        public bool RequireTwoFactor { get; set; }

        [Display(Name = "Let visitors open photos at a large size")]
        public bool LargePhotos { get; set; } = true;
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new ArchiveSettings();
        var texts = await db.ArchiveTexts.AsNoTracking().ToListAsync(cancellationToken);
        string? Text(ArchiveTextKind kind) => texts.FirstOrDefault(t => t.Kind == kind)?.Text;

        Input = new InputModel
        {
            Name = Text(ArchiveTextKind.Name),
            Tagline = Text(ArchiveTextKind.Tagline),
            Intro = Text(ArchiveTextKind.Intro),
            About = Text(ArchiveTextKind.About),
            Contact = Text(ArchiveTextKind.Contact),
            Privacy = Text(ArchiveTextKind.Privacy) ?? DefaultPrivacy,
            PrivacyReviewed = settings.PrivacyReviewed,
            Imprint = Text(ArchiveTextKind.Imprint),
            ImprintNotNeeded = settings.ImprintNotNeeded,
            AccentColor = settings.AccentColor ?? StandardColor,
            UseStandardColor = settings.AccentColor is null,
            DefaultLanguage = settings.DefaultLanguage,
            // Before an admin chose, dates were read in English and German. Saving without a change keeps that.
            ContentLanguage = settings.ContentLanguage ?? settings.DefaultLanguage,
            OtherContentLanguages = settings.ContentLanguage is null
                ? DateLanguages.BeforeTheSetting.Where(l => l != settings.DefaultLanguage).ToList()
                : settings.OtherContentLanguages?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
            TimeZone = settings.TimeZone,
            AllowSearchEngines = settings.AllowSearchEngines,
            RequireTwoFactor = settings.RequireTwoFactor,
            LargePhotos = settings.LargePhotos,
            DefaultRightsUri = settings.DefaultRightsUri,
        };
        ShowState(settings);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ArchiveSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new ArchiveSettings();
            db.ArchiveSettings.Add(settings);
        }

        if (!languages.Offers(Input.DefaultLanguage))
            ModelState.AddModelError("Input.DefaultLanguage", localizer["Please choose one of the languages."]);
        // A form from before these fields existed leaves them as they are.
        if (Input.ContentLanguage is not null && Input.ContentLanguage != DateLanguages.Several && !SupportedLanguages.AllLanguages.Contains(Input.ContentLanguage))
            ModelState.AddModelError("Input.ContentLanguage", localizer["Please choose one of the languages."]);
        if (Input.OtherContentLanguages.Any(l => !DateLanguageChoices.Contains(l)))
            ModelState.AddModelError("Input.OtherContentLanguages", localizer["Please choose one of the languages."]);
        if (!string.IsNullOrEmpty(Input.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(Input.TimeZone, out _))
            ModelState.AddModelError("Input.TimeZone", localizer["Please choose one of the time zones."]);

        if (!string.IsNullOrEmpty(Input.DefaultRightsUri) && !RightsOptions.IsKnown(Input.DefaultRightsUri))
            ModelState.AddModelError("Input.DefaultRightsUri", localizer["Please choose one of the statements."]);

        string? accent = null;
        if (!Input.UseStandardColor && !string.Equals(Input.AccentColor, StandardColor, StringComparison.OrdinalIgnoreCase))
        {
            if (!AccentColor.TryParse(Input.AccentColor, out var color))
                ModelState.AddModelError("Input.AccentColor", localizer["Please choose a colour."]);
            else if (AccentColor.ContrastWithWhite(color) < AccentColor.MinimumContrast)
                ModelState.AddModelError("Input.AccentColor", localizer["This colour is too light: white text on it, and text in it, would be hard to read. A similar colour that works is {0}.", AccentColor.SuggestReadable(color)]);
            else
                accent = Input.AccentColor!.ToLowerInvariant();
        }

        Guid? newLogo = null;
        if (ModelState.IsValid && Logo is not null && !Input.RemoveLogo)
        {
            newLogo = await logos.SaveAsync(Logo, cancellationToken);
            if (newLogo is null)
                ModelState.AddModelError("Logo", localizer["The logo must be a JPEG, PNG, TIFF or WebP image of at most {0} MB.", LogoImages.MaxBytes / 1024 / 1024]);
        }

        if (!ModelState.IsValid)
        {
            ShowState(settings);
            return Page();
        }

        var oldLogo = settings.LogoId;
        if (newLogo is not null || Input.RemoveLogo)
            settings.LogoId = newLogo;

        settings.DefaultLanguage = Input.DefaultLanguage;
        if (Input.ContentLanguage is not null)
        {
            settings.ContentLanguage = Input.ContentLanguage;
            var others = Input.OtherContentLanguages.Where(l => l != Input.ContentLanguage).Distinct().ToList();
            settings.OtherContentLanguages = others.Count == 0 ? null : string.Join(',', others);
        }
        settings.AccentColor = accent;
        settings.TimeZone = string.IsNullOrEmpty(Input.TimeZone) ? null : Input.TimeZone;
        settings.AllowSearchEngines = Input.AllowSearchEngines;
        settings.RequireTwoFactor = Input.RequireTwoFactor;
        var largePhotosSwitchedOn = Input.LargePhotos && !settings.LargePhotos;
        settings.LargePhotos = Input.LargePhotos;
        settings.DefaultRightsUri = string.IsNullOrEmpty(Input.DefaultRightsUri) ? null : Input.DefaultRightsUri;
        settings.PrivacyReviewed = Input.PrivacyReviewed;
        settings.ImprintNotNeeded = Input.ImprintNotNeeded;

        // Only one language can be written here so far. Texts for more languages come with the languages themselves.
        db.ArchiveTexts.RemoveRange(await db.ArchiveTexts.ToListAsync(cancellationToken));
        void Keep(ArchiveTextKind kind, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                db.ArchiveTexts.Add(new ArchiveText { Language = Input.DefaultLanguage, Kind = kind, Text = text.Trim() });
        }
        Keep(ArchiveTextKind.Name, Input.Name);
        Keep(ArchiveTextKind.Tagline, Input.Tagline);
        Keep(ArchiveTextKind.Intro, Input.Intro);
        Keep(ArchiveTextKind.About, Input.About);
        Keep(ArchiveTextKind.Contact, Input.Contact);
        Keep(ArchiveTextKind.Imprint, Input.Imprint);
        // The text as it came stays out of the database, so it follows each reader's language and later corrections.
        if (Normalize(Input.Privacy) != Normalize(DefaultPrivacy))
            Keep(ArchiveTextKind.Privacy, Input.Privacy);

        audit.Record(AuditAction.SettingsChanged, null);
        await db.SaveChangesAsync(cancellationToken);
        profile.Forget();
        if (largePhotosSwitchedOn)
            previews.LookThroughOlderFilesAgain();
        if (oldLogo is { } old && old != settings.LogoId)
            logos.Delete(old);

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreTestedAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ArchiveSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new ArchiveSettings();
            db.ArchiveSettings.Add(settings);
        }
        settings.RestoreTestedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record(AuditAction.RestoreTested, null);
        await db.SaveChangesAsync(cancellationToken);
        profile.Forget();
        return RedirectToPage("/Admin/Index");
    }

    private string DefaultPrivacy => string.Join("\n\n", PrivacyText.Paragraphs(localizer));

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private void ShowState(ArchiveSettings settings)
    {
        CurrentLogoId = settings.LogoId;
        TimeZones = TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
