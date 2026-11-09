using Commonage.Web.Identity;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Commonage.Web.Pages.Account;

public class AccessDeniedModel(ArchiveProfile profile, UserManager<IdentityUser> users) : PageModel
{
    /// <summary>Someone the archive only keeps out until they set up an authenticator app is sent to do that.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        if (await TwoFactorIfRequiredHandler.OnlyNeedsSetupAsync(profile, users, User))
            return RedirectToPage("TwoFactor");
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Page();
    }
}
