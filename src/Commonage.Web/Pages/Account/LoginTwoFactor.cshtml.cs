using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Account;

/// <summary>The second step of logging in, after the password: a code from the app, or a recovery code.</summary>
public class LoginTwoFactorModel(
    SignInManager<IdentityUser> signIn,
    AuditLog audit,
    CommonageDbContext db,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter the code from your app.")]
        [StringLength(30, ErrorMessage = "Please enter the code from your app.")]
        [Display(Name = "Code from your authenticator app")]
        public string Code { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? RedirectToPage("Login") : Page();

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        // Only someone who gave the right password a few minutes ago gets here.
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is not { } user)
        {
            TempData["Error"] = localizer["That took too long. Please log in again."].Value;
            return RedirectToPage("Login");
        }
        if (!ModelState.IsValid)
            return Page();

        var (code, isRecoveryCode) = TwoFactor.Read(Input.Code);
        var result = isRecoveryCode
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        if (result.Succeeded)
        {
            if (isRecoveryCode)
            {
                audit.RecordFor(user, AuditAction.RecoveryCodeUsed, user.Email);
                await db.SaveChangesAsync();
            }
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/Admin");
        }

        ModelState.AddModelError("Input.Code",
            result.IsLockedOut ? localizer["Too many failed attempts. Please try again in a few minutes."]
            : isRecoveryCode ? localizer["This recovery code is not right, or it has been used already."]
            : localizer["This code is not right. The app shows a new one every 30 seconds: please type the one it shows now."]);
        return Page();
    }
}
