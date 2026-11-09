using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>Logging in with an authenticator app, as a phone would make the codes.</summary>
[Collection(nameof(AppCollection))]
public partial class TwoFactorTests(CommonageAppFactory app)
{
    /// <summary>The code an authenticator app shows for this key now (RFC 6238: HMAC-SHA1, 30 seconds, six digits).</summary>
    internal static string CodeFor(string key, int stepsFromNow = 0)
    {
        var bytes = Base32(key.Replace(" ", "").ToUpperInvariant());
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepsFromNow;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counter);
        var hash = HMACSHA1.HashData(bytes, counter);
        var offset = hash[^1] & 0x0f;
        var number = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(text.TrimEnd('=').Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }

    [GeneratedRegex(@"<code class=""setup-key"">([a-z2-7 ]+)</code>")]
    private static partial Regex SetupKey();

    [GeneratedRegex(@"<li><code>([A-Z0-9]{5}-[A-Z0-9]{5})</code></li>")]
    private static partial Regex RecoveryCode();

    /// <summary>A new curator who has set up an app. Returns its key and recovery codes.</summary>
    private async Task<(HttpClient Client, string Id, string Email, string Password, string Key, List<string> Codes)> CuratorWithAppAsync()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (client, id, email, password) = await app.CreateAccountAsync(admin);
        var setup = await client.GetStringAsync("/Account/TwoFactor");
        var key = SetupKey().Match(setup).Groups[1].Value;
        Assert.NotEmpty(key);

        var enabled = await (await Browser.PostFormAsync(client, "/Account/TwoFactor?handler=Enable", new() { ["Input.Code"] = CodeFor(key) },
            pageUrl: "/Account/TwoFactor")).Content.ReadAsStringAsync();
        var codes = RecoveryCode().Matches(enabled).Select(m => m.Groups[1].Value).ToList();
        return (client, id, email, password, key, codes);
    }

    private async Task<HttpResponseMessage> LogInWithCodeAsync(HttpClient client, string email, string password, string code)
    {
        var afterPassword = await Browser.SendFormAsync(client, "/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal("/Account/LoginTwoFactor", afterPassword.RequestMessage!.RequestUri!.AbsolutePath);
        return await Browser.SendFormAsync(client, "/Account/LoginTwoFactor", new() { ["Input.Code"] = code });
    }

    [Fact]
    public async Task After_setting_up_an_app_a_curator_needs_its_code_to_log_in()
    {
        var (client, _, email, password, key, codes) = await CuratorWithAppAsync();
        client.Dispose();

        using var later = app.CreateClient();
        var wrong = await LogInWithCodeAsync(later, email, password, "000000");
        Assert.Contains("This code is not right.", await wrong.Content.ReadAsStringAsync());
        Assert.False(await Browser.IsLoggedInAsync(later));

        await Browser.SendFormAsync(later, "/Account/LoginTwoFactor", new() { ["Input.Code"] = CodeFor(key) });

        Assert.True(await Browser.IsLoggedInAsync(later));
        Assert.Equal(TwoFactor.RecoveryCodeCount, codes.Distinct().Count());
        Assert.Contains("You have 10 recovery codes left.", await later.GetStringAsync("/Account/TwoFactor"));
        Assert.Contains(AuditAction.TwoFactorEnabled, await app.WithDbAsync(db => db.AuditEntries.Where(e => e.UserName == email).Select(e => e.Action).ToListAsync()));
    }

    [Fact]
    public async Task The_password_alone_no_longer_opens_the_management_pages()
    {
        var (client, _, email, password, _, _) = await CuratorWithAppAsync();
        client.Dispose();
        using var thief = app.CreateClient();

        await Browser.SendFormAsync(thief, "/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });

        Assert.False(await Browser.IsLoggedInAsync(thief));
        Assert.Equal("/Account/Login", (await thief.GetAsync("/Admin")).RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_recovery_code_works_once_in_any_spelling()
    {
        var (client, _, email, password, _, codes) = await CuratorWithAppAsync();
        client.Dispose();

        using var first = app.CreateClient();
        // Typed without the dash, in lower case.
        await LogInWithCodeAsync(first, email, password, codes[0].Replace("-", "").ToLowerInvariant());
        Assert.True(await Browser.IsLoggedInAsync(first));
        Assert.Contains("You have 9 recovery codes left.", await first.GetStringAsync("/Account/TwoFactor"));

        using var second = app.CreateClient();
        var again = await LogInWithCodeAsync(second, email, password, codes[0]);
        Assert.Contains("This recovery code is not right, or it has been used already.", await again.Content.ReadAsStringAsync());
        Assert.False(await Browser.IsLoggedInAsync(second));
        var used = await app.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.Action == AuditAction.RecoveryCodeUsed && e.Subject == email));
        Assert.Equal(email, used.UserName);
    }

    [Fact]
    public async Task The_secret_and_the_recovery_codes_are_encrypted_in_the_database()
    {
        var (client, id, _, _, key, codes) = await CuratorWithAppAsync();
        client.Dispose();

        var stored = await app.WithDbAsync(db => db.UserTokens.Where(t => t.UserId == id).Select(t => t.Value).ToListAsync());

        Assert.Equal(2, stored.Count);
        var all = string.Join("\n", stored);
        Assert.DoesNotContain(key.Replace(" ", "").ToUpperInvariant(), all.ToUpperInvariant());
        Assert.All(codes, code => Assert.DoesNotContain(code, all));
    }

    [Fact]
    public async Task Turning_it_off_or_new_codes_need_the_password()
    {
        var (client, _, email, password, _, codes) = await CuratorWithAppAsync();

        var refused = await Browser.SendFormAsync(client, "/Account/TwoFactor?handler=TurnOff", new() { ["Input.Password"] = "not it" }, "/Account/TwoFactor");
        Assert.Contains("The password is not right.", await refused.Content.ReadAsStringAsync());

        var renewed = await (await Browser.PostFormAsync(client, "/Account/TwoFactor?handler=RecoveryCodes", new() { ["Input.Password"] = password },
            "/Account/TwoFactor")).Content.ReadAsStringAsync();
        var newCodes = RecoveryCode().Matches(renewed).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(TwoFactor.RecoveryCodeCount, newCodes.Count);
        Assert.Empty(newCodes.Intersect(codes));

        await Browser.PostFormAsync(client, "/Account/TwoFactor?handler=TurnOff", new() { ["Input.Password"] = password }, "/Account/TwoFactor");

        Assert.True(await Browser.IsLoggedInAsync(client), "Turning it off must not log out the person who did it.");
        using var later = app.CreateClient();
        await Browser.LogInAsync(later, email, password);
        Assert.True(await Browser.IsLoggedInAsync(later));
        client.Dispose();
    }

    [Fact]
    public async Task An_admin_turns_it_off_for_someone_who_lost_their_phone()
    {
        var (client, id, email, password, oldKey, _) = await CuratorWithAppAsync();
        client.Dispose();
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        Assert.Contains("<td>Yes</td>", await admin.GetStringAsync("/Admin/Users"));

        var page = await (await Browser.PostFormAsync(admin, $"/Admin/Users/Edit/{id}?handler=TurnOffTwoFactor", new(), $"/Admin/Users/Edit/{id}")).Content.ReadAsStringAsync();

        Assert.Contains("The authenticator app is turned off for this account.", page);
        using var person = app.CreateClient();
        await Browser.LogInAsync(person, email, password);
        Assert.True(await Browser.IsLoggedInAsync(person));
        // Whoever has the lost phone cannot use it: setting up again starts with a new key.
        var newKey = SetupKey().Match(await person.GetStringAsync("/Account/TwoFactor")).Groups[1].Value;
        Assert.NotEmpty(newKey);
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(AuditAction.TwoFactorTurnedOff, await app.WithDbAsync(db => db.AuditEntries.Where(e => e.Subject == email).OrderBy(e => e.Id).Select(e => e.Action).LastAsync()));
        var own = await app.WithDbAsync(db => db.Users.Where(u => u.Email == CommonageAppFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Browser.SendFormAsync(admin, $"/Admin/Users/Edit/{own}?handler=TurnOffTwoFactor", new(), $"/Admin/Users/Edit/{own}")).StatusCode);
    }

    [Fact]
    public async Task On_the_server_the_last_admin_gets_back_in()
    {
        var (client, _, email, password, _, _) = await CuratorWithAppAsync();
        client.Dispose();
        var output = new StringWriter();

        int found, unknown;
        using (var scope = app.Services.CreateScope())
        {
            found = await TwoFactorReset.RunAsync(scope.ServiceProvider, email, output);
            unknown = await TwoFactorReset.RunAsync(scope.ServiceProvider, "nobody@example.org", output);
        }

        Assert.Equal((0, 1), (found, unknown));
        Assert.Contains($"{email} can log in with their password now", output.ToString());
        Assert.Contains("No account has the email address nobody@example.org.", output.ToString());
        using var person = app.CreateClient();
        await Browser.LogInAsync(person, email, password);
        Assert.True(await Browser.IsLoggedInAsync(person));
        Assert.Equal($"{email} (on the server)",
            await app.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.TwoFactorTurnedOff).OrderBy(e => e.Id).Select(e => e.Subject).LastAsync()));
    }

    [Fact]
    public async Task When_the_archive_requires_it_everyone_sets_it_up_before_going_on()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, _, _) = await app.CreateAccountAsync(admin);
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "photo.jpg", TestData.Photo);
        var file = await app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).Select(f => f.Id).SingleAsync());
        var created = await RequireAsync(true);
        try
        {
            var redirected = await curator.GetAsync("/Admin");

            Assert.Equal("/Account/TwoFactor", redirected.RequestMessage!.RequestUri!.AbsolutePath);
            var setup = await redirected.Content.ReadAsStringAsync();
            Assert.Contains("This archive requires an authenticator app for everyone who manages it.", setup);
            Assert.DoesNotContain("Set up on a new phone", setup);
            // The original file is not sent: the request ends on the setup page instead.
            var refused = await curator.GetAsync($"/admin/media/{file}/original");
            Assert.Equal("/Account/TwoFactor", refused.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.Equal("text/html", refused.Content.Headers.ContentType?.MediaType);

            var key = SetupKey().Match(setup).Groups[1].Value;
            await Browser.PostFormAsync(curator, "/Account/TwoFactor?handler=Enable", new() { ["Input.Code"] = CodeFor(key) }, "/Account/TwoFactor");

            Assert.True(await Browser.IsLoggedInAsync(curator));
            Assert.Equal(HttpStatusCode.OK, (await curator.GetAsync($"/admin/media/{file}/original")).StatusCode);
            Assert.Contains("Set up on a new phone", await curator.GetStringAsync("/Account/TwoFactor"));
        }
        finally
        {
            await RequireAsync(false, remove: created);
            curator.Dispose();
        }
    }

    [Fact]
    public async Task A_code_from_the_previous_half_minute_still_counts_and_a_made_up_key_does_not()
    {
        var (client, _, email, password, key, _) = await CuratorWithAppAsync();
        client.Dispose();

        using var slow = app.CreateClient();
        await LogInWithCodeAsync(slow, email, password, CodeFor(key, -1));
        Assert.True(await Browser.IsLoggedInAsync(slow));

        using var guesser = app.CreateClient();
        await LogInWithCodeAsync(guesser, email, password, CodeFor("abcd efgh ijkl mnop qrst uvwx yz23 4567"));
        Assert.False(await Browser.IsLoggedInAsync(guesser));
    }

    /// <summary>Other tests share this archive, so a settings row this test had to create is removed again.</summary>
    private Task<bool> RequireAsync(bool required, bool remove = false) => app.WithDbAsync(async db =>
    {
        var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
        var created = settings is null;
        if (settings is null)
            db.ArchiveSettings.Add(settings = new ArchiveSettings());
        settings.RequireTwoFactor = required;
        if (remove)
            db.ArchiveSettings.Remove(settings);
        await db.SaveChangesAsync();
        app.Services.GetRequiredService<ArchiveProfile>().Forget();
        return created;
    });
}
