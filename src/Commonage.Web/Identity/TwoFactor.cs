using System.Security.Cryptography;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using QRCoder;

namespace Commonage.Web.Identity;

/// <summary>
/// Logging in with an authenticator app as well as the password. The secret the app shares and the recovery codes are
/// encrypted in the database (see <see cref="ProtectedUserStore"/>), and their keys with the server's login key.
/// </summary>
public static class TwoFactor
{
    public const int RecoveryCodeCount = 10;

    /// <summary>"Commonage" or the archive's name, as the app lists it, and the person's email address.</summary>
    public static string AuthenticatorUri(string issuer, string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

    /// <summary>The key in groups of four, to type without losing one's place.</summary>
    public static string Readable(string key) =>
        string.Join(' ', key.Chunk(4).Select(c => new string(c))).ToLowerInvariant();

    /// <summary>
    /// What a person typed: six digits from the app, or a recovery code, with or without its dash or spaces.
    /// Returns the code and whether it is a recovery code.
    /// </summary>
    public static (string Code, bool IsRecoveryCode) Read(string typed)
    {
        var code = new string(typed.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (code.Length == 6 && code.All(char.IsAsciiDigit))
            return (code, false);
        return (code.Length == 10 ? $"{code[..5]}-{code[5..]}" : code, true);
    }
}

/// <summary>Turns off the authenticator app of an account, by an admin or on the server.</summary>
public static class TwoFactorReset
{
    /// <summary>The person logs in with their password only, and can set up an app again. Their other logins end.</summary>
    public static async Task TurnOffAsync(UserManager<IdentityUser> users, IdentityUser user)
    {
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        await users.ResetAccessFailedCountAsync(user);
        if (!Accounts.IsDisabled(user))
            await users.SetLockoutEndDateAsync(user, null);
    }

    /// <summary>"reset-two-factor &lt;email&gt;" on the server: for the only admin, or after the login key was lost.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string email, TextWriter output)
    {
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        if (await users.FindByEmailAsync(email.Trim()) is not { } user)
        {
            output.WriteLine($"No account has the email address {email}.");
            return 1;
        }
        await TurnOffAsync(users, user);
        services.GetRequiredService<Audit.AuditLog>().Record(Domain.AuditAction.TwoFactorTurnedOff, $"{user.Email} (on the server)");
        await services.GetRequiredService<CommonageDbContext>().SaveChangesAsync();
        output.WriteLine($"{user.Email} can log in with their password now, without an authenticator app, and set one up again.");
        return 0;
    }
}

/// <summary>
/// Identity keeps the authenticator secret and the recovery codes as tokens in plain text. This store encrypts them with
/// Data Protection, so a copy of the database alone cannot be used to make codes.
/// </summary>
public class ProtectedUserStore(CommonageDbContext context, IDataProtectionProvider protection, IdentityErrorDescriber? describer = null)
    : UserStore<IdentityUser, IdentityRole, CommonageDbContext>(context, describer)
{
    /// <summary>Where Identity keeps its own tokens: the authenticator key and the recovery codes.</summary>
    private const string IdentityTokens = "[AspNetUserStore]";

    private readonly IDataProtector protector = protection.CreateProtector("Commonage.TwoFactorSecrets");

    public override Task SetTokenAsync(IdentityUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name,
            loginProvider == IdentityTokens && value is not null ? protector.Protect(value) : value, cancellationToken);

    public override async Task<string?> GetTokenAsync(IdentityUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        if (loginProvider != IdentityTokens || value is null)
            return value;
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // The login key changed or was lost. The secret is gone, so codes cannot match; an admin turns it off.
            return null;
        }
    }
}

/// <summary>When an admin requires it, the management pages only open for accounts that log in with an authenticator app.</summary>
public sealed class TwoFactorIfRequired : IAuthorizationRequirement;

public sealed class TwoFactorIfRequiredHandler(ArchiveProfile profile, UserManager<IdentityUser> users)
    : AuthorizationHandler<TwoFactorIfRequired>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorIfRequired requirement)
    {
        if (!(await profile.GetAsync()).RequireTwoFactor || await users.GetUserAsync(context.User) is { TwoFactorEnabled: true })
            context.Succeed(requirement);
    }

    /// <summary>Whether this person is only kept out because they have not set it up yet.</summary>
    public static async Task<bool> OnlyNeedsSetupAsync(ArchiveProfile profile, UserManager<IdentityUser> users, System.Security.Claims.ClaimsPrincipal user) =>
        (await profile.GetAsync()).RequireTwoFactor
        && await users.GetUserAsync(user) is { TwoFactorEnabled: false } account
        && (await users.IsInRoleAsync(account, Roles.Admin) || await users.IsInRoleAsync(account, Roles.Curator));
}

/// <summary>QR codes, drawn as SVG in the page itself, so they need no image address and no script.</summary>
public static class QrCode
{
    public static IHtmlContent Svg(string text, string cssClass = "qr-code")
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (matrix[y][x])
                    path.Append("M").Append(x).Append(',').Append(y).Append("h1v1h-1z");
            }
        }
        // Black on white whatever the colour scheme, because that is what cameras read best. The quiet zone is in the matrix.
        return new HtmlString(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\" class=\"{cssClass}\" aria-hidden=\"true\" focusable=\"false\">" +
            $"<rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>");
    }
}
