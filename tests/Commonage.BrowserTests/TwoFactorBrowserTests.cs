using System.Security.Cryptography;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Commonage.BrowserTests;

/// <summary>Setting up an authenticator app and logging in with it, with an account of its own so the other tests keep theirs.</summary>
[Collection(nameof(BrowserCollection))]
public class TwoFactorBrowserTests(BrowserFixture app)
{
    private const string Password = "Two-factor-password-1";

    /// <summary>The code an authenticator app shows for this key now (RFC 6238).</summary>
    private static string CodeFor(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(key.Replace(" ", "").ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var bytes = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counter);
        var hash = HMACSHA1.HashData(bytes, counter);
        var offset = hash[^1] & 0x0f;
        var number = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6");
    }

    private async Task<string> NewCuratorAsync()
    {
        var email = $"phone-{Guid.NewGuid():N}@example.org";
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        await users.AddToRoleAsync(user, Roles.Curator);
        return email;
    }

    private static async Task LogInWithPasswordAsync(IPage page, string email)
    {
        await page.GotoAsync("/Account/Login");
        await page.FillAsync("[name='Input.Email']", email);
        await page.FillAsync("[name='Input.Password']", Password);
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login?") && !url.EndsWith("/Account/Login"));
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task A_curator_sets_up_an_app_keeps_the_codes_and_logs_in_with_it(int width)
    {
        var email = await NewCuratorAsync();
        var page = await app.NewPageAsync(width);
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = app.BaseUrl });
        await LogInWithPasswordAsync(page, email);

        await page.GotoAsync("/Account/TwoFactor");
        var qr = await page.Locator(".qr-code").BoundingBoxAsync();
        Assert.True(qr!.Width >= 150 && Math.Abs(qr.Width - qr.Height) < 1, $"The code to scan is {qr.Width} by {qr.Height}.");
        var key = await page.InnerTextAsync(".setup-key");
        await page.FillAsync("[name='Input.Code']", CodeFor(key));
        await page.ClickAsync("main form button[type=submit]");

        await page.WaitForSelectorAsync("#recovery-codes li");
        Assert.Equal(TwoFactor.RecoveryCodeCount, await page.Locator("#recovery-codes li").CountAsync());
        await AccessibilityTests.AssertNoViolationsAsync(page, "The recovery codes");
        await page.ClickAsync("[data-copy-from='recovery-codes']");
        await page.WaitForSelectorAsync("#copy-status:text-is('Copied')");
        var copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        var first = await page.InnerTextAsync("#recovery-codes li:first-child");
        Assert.Contains(first, copied);
        Assert.Equal(TwoFactor.RecoveryCodeCount, copied.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);

        // A new login asks for the code after the password.
        var later = await app.NewPageAsync(width);
        await LogInWithPasswordAsync(later, email);
        Assert.EndsWith("/Account/LoginTwoFactor", new Uri(later.Url).AbsolutePath);
        await AccessibilityTests.AssertNoViolationsAsync(later, "The code after the password");
        await later.FillAsync("[name='Input.Code']", "123");
        await later.ClickAsync("main form button[type=submit]");
        await later.WaitForSelectorAsync("text=This recovery code is not right");
        await AccessibilityTests.AssertNoViolationsAsync(later, "A wrong code");
        await later.FillAsync("[name='Input.Code']", first);
        await later.ClickAsync("main form button[type=submit]");
        await later.WaitForURLAsync(url => url.Contains("/Admin"));
    }
}
