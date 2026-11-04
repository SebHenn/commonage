using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages;

/// <summary>The pages a group writes about itself. A page nobody wrote does not exist, except the privacy page.</summary>
public class InfoModel(ArchiveProfile profile, IStringLocalizer<SharedResource> localizer) : PageModel
{
    public string Heading { get; private set; } = "";
    public string? Text { get; private set; }
    public IReadOnlyList<string> DefaultParagraphs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        var identity = await profile.GetAsync();
        var kind = slug switch
        {
            "about" => ArchiveTextKind.About,
            "contact" => ArchiveTextKind.Contact,
            "privacy" => ArchiveTextKind.Privacy,
            _ => ArchiveTextKind.Imprint,
        };

        Heading = HeadingOf(localizer, kind);
        Text = identity.Text(kind);
        if (Text is null)
        {
            if (kind != ArchiveTextKind.Privacy)
                return NotFound();
            DefaultParagraphs = PrivacyText.Paragraphs(localizer);
        }
        return Page();
    }

    public static string HeadingOf(IStringLocalizer localizer, ArchiveTextKind kind) => kind switch
    {
        ArchiveTextKind.About => localizer["About this archive"],
        ArchiveTextKind.Contact => localizer["Contact"],
        ArchiveTextKind.Privacy => localizer["Privacy"],
        _ => localizer["Imprint"],
    };
}

/// <summary>What Commonage itself stores. A group adapts it to what it does beyond that, and it is not legal advice.</summary>
public static class PrivacyText
{
    public static IReadOnlyList<string> Paragraphs(IStringLocalizer localizer) =>
    [
        localizer["This archive stores what people send in through its contribution links: what they wrote, their files, the name they gave, their email address if they gave one, and what they agreed to. Only the archive's curators see names and email addresses, unless someone chose to be named."],
        localizer["Photos are published without location data or other embedded details. The original files stay private."],
        localizer["This site only sets the cookies it needs: one for the chosen language, one to protect forms, and one to keep curators logged in. There are no analytics and no third-party services. Visitors' addresses are held in memory for a short time, to limit abuse of forms and logins."],
        localizer["Anyone can ask for their details, or what they sent in, to be changed or removed. The contact page says how to reach the archive. Backups keep older copies until they are replaced."],
    ];
}
