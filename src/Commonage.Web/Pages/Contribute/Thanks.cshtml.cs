using Commonage.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Pages.Contribute;

public class ThanksModel(CommonageDbContext db) : PageModel
{
    public string Token { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string token)
    {
        if (!await db.ContributionLinks.AnyAsync(l => l.Token == token && l.IsActive))
            return NotFound();

        Token = token;
        return Page();
    }
}
