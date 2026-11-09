using System.ComponentModel.DataAnnotations;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Account;

/// <summary>Setting up an authenticator app, new recovery codes, and turning it off.</summary>
public class TwoFactorModel(
    UserManager<IdentityUser> users,
    SignInManager<IdentityUser> signIn,
    ArchiveProfile profile,
    AuditLog audit,
    CommonageDbContext db,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Enabled { get; private set; }
    public bool Required { get; private set; }
    public int RecoveryCodesLeft { get; private set; }
    public string? Key { get; private set; }
    public IHtmlContent? QrCode { get; private set; }

    /// <summary>Shown once, on the response that created them. They are not kept anywhere they could be read again.</summary>
    public IReadOnlyList<string> NewRecoveryCodes { get; private set; } = [];

    public class InputModel
    {
        [StringLength(30, ErrorMessage = "Please enter the code from your app.")]
        [Display(Name = "Code from your authenticator app")]
        public string? Code { get; set; }

        [StringLength(200, ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Your password")]
        public string? Password { get; set; }
    }

    public async Task<IActionResult> OnGetAsync() =>
        await users.GetUserAsync(User) is { } user ? await ShowAsync(user) : Challenge();

    public async Task<IActionResult> OnPostEnableAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
            return Challenge();
        if (user.TwoFactorEnabled)
            return RedirectToPage();

        var (code, _) = TwoFactor.Read(Input.Code ?? "");
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            ModelState.AddModelError("Input.Code", localizer["This code is not right. The app shows a new one every 30 seconds: please type the one it shows now."]);
            return await ShowAsync(user);
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactor.RecoveryCodeCount);
        audit.Record(AuditAction.TwoFactorEnabled, user.Email);
        await db.SaveChangesAsync();
        // The change ends other logins. This one stays.
        await signIn.RefreshSignInAsync(user);
        return await ShowAsync(user, codes);
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
            return Challenge();
        if (!user.TwoFactorEnabled)
            return RedirectToPage();
        if (!await PasswordIsRightAsync(user))
            return await ShowAsync(user);

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactor.RecoveryCodeCount);
        audit.Record(AuditAction.RecoveryCodesCreated, user.Email);
        await db.SaveChangesAsync();
        return await ShowAsync(user, codes);
    }

    /// <summary>
    /// Turns it off, or, where the archive requires it, starts again with a new key, for example on a new phone.
    /// The old phone stops working either way.
    /// </summary>
    public async Task<IActionResult> OnPostTurnOffAsync()
    {
        if (await users.GetUserAsync(User) is not { } user)
            return Challenge();
        if (!user.TwoFactorEnabled)
            return RedirectToPage();
        if (!await PasswordIsRightAsync(user))
            return await ShowAsync(user);

        await TwoFactorReset.TurnOffAsync(users, user);
        audit.Record(AuditAction.TwoFactorDisabled, user.Email);
        await db.SaveChangesAsync();
        await signIn.RefreshSignInAsync(user);
        TempData["Message"] = (await profile.GetAsync()).RequireTwoFactor
            ? localizer["The old app no longer works. Set up the new one now: this archive requires it."].Value
            : localizer["You log in with your password only now."].Value;
        return RedirectToPage();
    }

    private async Task<bool> PasswordIsRightAsync(IdentityUser user)
    {
        // Counts towards locking the account, like a login, so it cannot be used to guess the password.
        var result = await signIn.CheckPasswordSignInAsync(user, Input.Password ?? "", lockoutOnFailure: true);
        if (result.Succeeded)
            return true;
        ModelState.AddModelError("Input.Password", result.IsLockedOut
            ? localizer["Too many failed attempts. Please try again in a few minutes."]
            : localizer["The password is not right."]);
        return false;
    }

    private async Task<IActionResult> ShowAsync(IdentityUser user, IEnumerable<string>? newCodes = null)
    {
        Enabled = user.TwoFactorEnabled;
        var identity = await profile.GetAsync();
        Required = identity.RequireTwoFactor;
        NewRecoveryCodes = newCodes?.ToList() ?? [];
        if (Enabled)
        {
            RecoveryCodesLeft = await users.CountRecoveryCodesAsync(user);
        }
        else
        {
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
                await signIn.RefreshSignInAsync(user);
            }
            Key = TwoFactor.Readable(key!);
            QrCode = Identity.QrCode.Svg(TwoFactor.AuthenticatorUri(identity.Name, user.Email ?? user.UserName ?? "", key!));
        }
        // The key and the codes must not stay in the browser's cache or history.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }
}
