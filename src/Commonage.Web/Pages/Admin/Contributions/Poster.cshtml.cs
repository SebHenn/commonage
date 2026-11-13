using Commonage.Web.Data;
using Commonage.Web.Identity;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Admin.Contributions;

/// <summary>
/// A sheet to print and pin up: what the link asks for, a QR code that opens it and the address in words for those who
/// cannot scan. It holds only what a contributor sees on the form anyway.
/// </summary>
public class PosterModel(CommonageDbContext db, ArchiveProfile profile) : PageModel
{
    public string Title { get; private set; } = "";
    public string? Introduction { get; private set; }
    public string Address { get; private set; } = "";
    public bool IsActive { get; private set; }
    public string? ArchiveName { get; private set; }
    public IHtmlContent Qr { get; private set; } = HtmlString.Empty;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var link = await db.ContributionLinks.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (link is null)
            return NotFound();

        var identity = await profile.GetAsync();
        Title = link.Title;
        Introduction = link.Introduction;
        IsActive = link.IsActive;
        ArchiveName = identity.NameIsSet ? identity.Name : null;
        Address = Url.Page("/Contribute/Index", null, new { token = link.Token }, Request.Scheme)!;
        Qr = QrCode.Svg(Address, "qr-code");
        return Page();
    }
}
