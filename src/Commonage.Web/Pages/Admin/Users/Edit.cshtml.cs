using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Users;

/// <summary>
/// Creates an account, or changes one. Admins cannot change, disable or delete their own account here, so there is
/// always at least one admin left who can.
/// </summary>
public class EditModel(
    UserManager<IdentityUser> users,
    CommonageDbContext db,
    AuditLog audit,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IdentityUser? Account { get; private set; }
    public AccountState State { get; private set; }
    public bool IsOwnAccount { get; private set; }

    /// <summary>Shown once, right after it was created. It is never stored by the app.</summary>
    public string? SetupLink { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter an email address.")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [StringLength(256, ErrorMessage = "The email address can be at most {1} characters long.")]
        [Display(Name = "Email address")]
        public string Email { get; set; } = "";

        [Display(Name = "Role")]
        public string Role { get; set; } = Roles.Curator;
    }

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (id is null)
            return Page();

        if (await LoadAsync(id) is not { } account)
            return NotFound();
        Input.Email = account.Email ?? "";
        Input.Role = await RoleOfAsync(account);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!Roles.All.Contains(Input.Role))
            ModelState.AddModelError("Input.Role", localizer["Please choose a role."]);
        if (!ModelState.IsValid)
            return Page();

        var email = Input.Email.Trim();
        var account = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true };
        var result = await users.CreateAsync(account);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError("Input.Email", error.Description);
            return Page();
        }

        await users.AddToRoleAsync(account, Input.Role);
        audit.Record(AuditAction.AccountCreated, $"{email} ({Input.Role})");
        await db.SaveChangesAsync();
        return await ShowSetupLinkAsync(account);
    }

    public async Task<IActionResult> OnPostRoleAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount || !Roles.All.Contains(Input.Role))
            return BadRequest();

        var current = await users.GetRolesAsync(account);
        if (!(current.Count == 1 && current[0] == Input.Role))
        {
            await users.RemoveFromRolesAsync(account, current);
            await users.AddToRoleAsync(account, Input.Role);
            audit.Record(AuditAction.AccountRoleChanged, $"{account.Email} ({Input.Role})");
            await db.SaveChangesAsync();
        }

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDisableAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount)
            return BadRequest();

        await users.SetLockoutEnabledAsync(account, true);
        await users.SetLockoutEndDateAsync(account, Accounts.DisabledUntil);
        // Ends the person's current logins within a minute.
        await users.UpdateSecurityStampAsync(account);
        audit.Record(AuditAction.AccountDisabled, account.Email);
        await db.SaveChangesAsync();

        TempData["Message"] = localizer["The account is disabled. The person is logged out within a minute."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostEnableAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount)
            return BadRequest();

        await users.SetLockoutEndDateAsync(account, null);
        await users.ResetAccessFailedCountAsync(account);
        audit.Record(AuditAction.AccountEnabled, account.Email);
        await db.SaveChangesAsync();

        TempData["Message"] = localizer["The account is enabled again."].Value;
        return RedirectToPage(new { id });
    }

    /// <summary>For a person who lost their setup link or forgot their password.</summary>
    public async Task<IActionResult> OnPostSetupLinkAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount)
            return BadRequest();

        audit.Record(AuditAction.SetupLinkCreated, account.Email);
        await db.SaveChangesAsync();
        return await ShowSetupLinkAsync(account);
    }

    /// <summary>For someone who lost their phone and their recovery codes. Commonage sends no emails, so an admin is the way back in.</summary>
    public async Task<IActionResult> OnPostTurnOffTwoFactorAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount)
            return BadRequest();

        await TwoFactorReset.TurnOffAsync(users, account);
        audit.Record(AuditAction.TwoFactorTurnedOff, account.Email);
        await db.SaveChangesAsync();

        TempData["Message"] = localizer["The authenticator app is turned off for this account. The person logs in with their password now."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (await LoadForChangeAsync(id) is not { } account)
            return NotFound();
        if (IsOwnAccount)
            return BadRequest();

        audit.Record(AuditAction.AccountDeleted, account.Email);
        await db.SaveChangesAsync();
        await users.DeleteAsync(account);

        TempData["Message"] = localizer["The account is deleted."].Value;
        return RedirectToPage("Index");
    }

    private async Task<IActionResult> ShowSetupLinkAsync(IdentityUser account)
    {
        SetupLink = await Accounts.CreateSetupLinkAsync(users, account, Request);
        await LoadAsync(account.Id);
        Input.Email = account.Email ?? "";
        Input.Role = await RoleOfAsync(account);
        // Shown on this response only, so the link is not kept in the browser's history or in a cookie.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    private async Task<IdentityUser?> LoadAsync(string id)
    {
        Account = await users.FindByIdAsync(id);
        if (Account is not null)
        {
            State = AccountStates.Of(Account);
            IsOwnAccount = Account.Id == users.GetUserId(User);
        }
        return Account;
    }

    /// <summary>Loads the account without the form's values, which only the create and role handlers use.</summary>
    private async Task<IdentityUser?> LoadForChangeAsync(string id)
    {
        ModelState.Clear();
        return await LoadAsync(id);
    }

    private async Task<string> RoleOfAsync(IdentityUser account)
    {
        var roles = await users.GetRolesAsync(account);
        return roles.Contains(Roles.Admin) ? Roles.Admin : Roles.Curator;
    }
}
