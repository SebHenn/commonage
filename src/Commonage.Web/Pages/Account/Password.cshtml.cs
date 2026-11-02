using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Account;

/// <summary>Logged-in curators and admins change their own password.</summary>
public class PasswordModel(
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    CommonageDbContext db,
    AuditLog audit,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = "";

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

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        if (await users.GetUserAsync(User) is not { } account)
            return Challenge();

        var result = await users.ChangePasswordAsync(account, Input.CurrentPassword, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                var field = error.Code == nameof(IdentityErrorDescriber.PasswordMismatch) ? "Input.CurrentPassword" : "Input.Password";
                ModelState.AddModelError(field, error.Description);
            }
            return Page();
        }

        audit.Record(AuditAction.PasswordChanged, subject: null);
        await db.SaveChangesAsync();
        // Changing the password ends other logins; this one continues.
        await signIn.RefreshSignInAsync(account);
        TempData["Message"] = localizer["Your password is changed."].Value;
        return RedirectToPage("/Admin/Index");
    }
}
