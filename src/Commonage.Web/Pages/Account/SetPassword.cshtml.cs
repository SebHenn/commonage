using System.ComponentModel.DataAnnotations;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Account;

/// <summary>Where a setup link leads: the person chooses their password. The link works once.</summary>
public class SetPasswordModel(UserManager<IdentityUser> users, IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Email { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a password.")]
        [StringLength(200, ErrorMessage = "The password can be at most {1} characters long.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Please enter the password again.")]
        [Compare(nameof(Password), ErrorMessage = "The two passwords are not the same.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password again")]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(string? user, string? code)
    {
        // The link's code is checked when the form is sent; checking it here would not make it any safer.
        if (await users.FindByIdAsync(user ?? "") is not { } account || Accounts.DecodeSetupCode(code) is null)
            return InvalidLink();

        Email = account.Email;
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? user, string? code)
    {
        var account = await users.FindByIdAsync(user ?? "");
        var token = Accounts.DecodeSetupCode(code);
        if (account is null || token is null)
            return InvalidLink();

        Email = account.Email;
        if (!ModelState.IsValid)
            return Page();

        var result = await users.ResetPasswordAsync(account, token, Input.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
                return InvalidLink();
            foreach (var error in result.Errors)
                ModelState.AddModelError("Input.Password", error.Description);
            return Page();
        }

        TempData["Message"] = localizer["Your password is set. You can log in now."].Value;
        return RedirectToPage("Login");
    }

    private IActionResult InvalidLink()
    {
        TempData["Error"] = localizer["This link is no longer valid. Please ask an admin for a new one."].Value;
        return RedirectToPage("Login");
    }
}
