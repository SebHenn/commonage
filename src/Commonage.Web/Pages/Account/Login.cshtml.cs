using System.ComponentModel.DataAnnotations;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Account;

public class LoginModel(SignInManager<IdentityUser> signInManager, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your email address.")]
        [Display(Name = "Email")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!ModelState.IsValid)
            return Page();

        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/Admin");

        // Only someone who knows the password learns that the account is disabled.
        var disabled = result.IsLockedOut
            && await signInManager.UserManager.FindByNameAsync(Input.Email) is { } user
            && Accounts.IsDisabled(user)
            && await signInManager.UserManager.CheckPasswordAsync(user, Input.Password);
        ModelState.AddModelError(string.Empty,
            disabled ? localizer["This account is disabled. Please ask an admin."]
            : result.IsLockedOut ? localizer["Too many failed attempts. Please try again in a few minutes."]
            : localizer["The email address or password is incorrect."]);
        return Page();
    }
}
