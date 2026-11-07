using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Identity;

/// <summary>
/// Accounts of curators and admins. Commonage sends no emails, so a new account gets a setup link that the admin
/// passes on; the person chooses their own password with it.
/// </summary>
public static class Accounts
{
    /// <summary>A disabled account is locked out until this date, which is how Identity keeps people from logging in.</summary>
    public static readonly DateTimeOffset DisabledUntil = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public const int MinimumPasswordLength = 12;

    public static bool IsDisabled(IdentityUser user) => user.LockoutEnd >= DisabledUntil;

    /// <summary>
    /// Creates a link with which the person sets a new password. Earlier links stop working, and the account is
    /// logged out everywhere within a minute. The link works once and for <c>DataProtectionTokenProviderOptions.TokenLifespan</c>.
    /// </summary>
    public static async Task<string> CreateSetupLinkAsync(UserManager<IdentityUser> users, IdentityUser user, HttpRequest request)
    {
        await users.UpdateSecurityStampAsync(user);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return $"{request.Scheme}://{request.Host}{request.PathBase}/Account/SetPassword?user={Uri.EscapeDataString(user.Id)}&code={code}";
    }

    public static string? DecodeSetupCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Identity's messages in the visitor's language. Only the ones the app's forms can show are translated.</summary>
public class LocalizedIdentityErrorDescriber(IStringLocalizer<SharedResource> localizer) : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = localizer.Plural("The password must be at least {0, plural, one {# character} other {# characters}} long.", length) };

    public override IdentityError PasswordRequiresDigit() =>
        new() { Code = nameof(PasswordRequiresDigit), Description = localizer["The password must contain a digit."] };

    public override IdentityError PasswordRequiresLower() =>
        new() { Code = nameof(PasswordRequiresLower), Description = localizer["The password must contain a lowercase letter."] };

    public override IdentityError PasswordRequiresUpper() =>
        new() { Code = nameof(PasswordRequiresUpper), Description = localizer["The password must contain an uppercase letter."] };

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = localizer["The password must contain a character that is not a letter or digit, such as - or !."] };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        new() { Code = nameof(PasswordRequiresUniqueChars), Description = localizer.Plural("The password must contain at least {0, plural, one {# different character} other {# different characters}}.", uniqueChars) };

    public override IdentityError PasswordMismatch() =>
        new() { Code = nameof(PasswordMismatch), Description = localizer["The current password is incorrect."] };

    public override IdentityError InvalidToken() =>
        new() { Code = nameof(InvalidToken), Description = localizer["This link is no longer valid. Please ask an admin for a new one."] };

    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = localizer["There is already an account with this email address."] };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = localizer["There is already an account with this email address."] };

    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = localizer["Please enter a valid email address."] };

    public override IdentityError InvalidUserName(string? userName) =>
        new() { Code = nameof(InvalidUserName), Description = localizer["Please enter a valid email address."] };
}
