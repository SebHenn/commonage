using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Commonage.Web.Identity;
using Commonage.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commonage.Tests;

/// <summary>
/// The login keys are in the database and encrypted with COMMONAGE_LOGIN_KEY from outside it. Each test has a database of
/// its own, because they remove and replace keys.
/// </summary>
[Collection(nameof(AppCollection))]
public partial class LoginKeyTests(CommonageAppFactory app)
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(LoginKeys.KeyBytes));

    private async Task<string> NewDatabaseAsync()
    {
        var name = "keys_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name }.ConnectionString;
    }

    private WebApplicationFactory<Program> Archive(string database, string? key, string? environment = null) =>
        app.WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", database);
            b.UseSetting(LoginKeys.Setting, key ?? "");
            if (environment is not null)
                b.UseEnvironment(environment);
        });

    private static async Task<(int Encrypted, int Readable)> KeysAsync(WebApplicationFactory<Program> archive)
    {
        using var scope = archive.Services.CreateScope();
        var xml = await scope.ServiceProvider.GetRequiredService<Commonage.Web.Data.CommonageDbContext>().DataProtectionKeys
            .Select(k => k.Xml ?? "").ToListAsync();
        return (xml.Count(x => x.Contains("encryptedKey")), xml.Count(x => x.Contains("requiresEncryption=\"true\"")));
    }

    private static string Protect(WebApplicationFactory<Program> archive, string text) =>
        archive.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect(text);

    private static bool CanRead(WebApplicationFactory<Program> archive, string payload)
    {
        try
        {
            archive.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(payload);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    [Fact]
    public async Task With_a_key_nothing_in_the_database_can_be_used_to_log_in()
    {
        var database = await NewDatabaseAsync();
        var key = NewKey();
        await using var archive = Archive(database, key);
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var payload = Protect(archive, "a login");

        var (encrypted, readable) = await KeysAsync(archive);

        Assert.True(await Browser.IsLoggedInAsync(admin));
        Assert.True(encrypted > 0);
        Assert.Equal(0, readable);
        // The same database with the same key, as after a restore: logins still work.
        await using var restored = Archive(database, key);
        Assert.True(CanRead(restored, payload));
        // With another key, nothing from before can be read, and new logins work.
        await using var other = Archive(database, NewKey());
        Assert.False(CanRead(other, payload));
        using var again = other.CreateClient();
        await Browser.LogInAsCuratorAsync(again);
        Assert.True(await Browser.IsLoggedInAsync(again));
    }

    [Fact]
    public async Task Setting_a_key_later_removes_the_keys_that_could_be_read()
    {
        var database = await NewDatabaseAsync();
        await using var before = Archive(database, null);
        using (var admin = before.CreateClient())
            await Browser.LogInAsCuratorAsync(admin);
        var payload = Protect(before, "a login from before");
        Assert.True((await KeysAsync(before)).Readable > 0);

        await using var after = Archive(database, NewKey());
        var (_, readable) = await KeysAsync(after);

        Assert.Equal(0, readable);
        Assert.False(CanRead(after, payload));
        using var again = after.CreateClient();
        await Browser.LogInAsCuratorAsync(again);
        Assert.True(await Browser.IsLoggedInAsync(again));
        Assert.True((await KeysAsync(after)).Encrypted > 0);
    }

    [Fact]
    public async Task After_losing_the_key_an_app_is_turned_off_on_the_server_and_set_up_again()
    {
        var database = await NewDatabaseAsync();
        await using var archive = Archive(database, NewKey());
        using (var admin = archive.CreateClient())
        {
            await Browser.LogInAsCuratorAsync(admin);
            var setup = await admin.GetStringAsync("/Account/TwoFactor");
            var key = Regex.Match(setup, @"<code class=""setup-key"">([a-z2-7 ]+)</code>").Groups[1].Value;
            await Browser.PostFormAsync(admin, "/Account/TwoFactor?handler=Enable", new() { ["Input.Code"] = TwoFactorTests.CodeFor(key) }, "/Account/TwoFactor");
        }

        await using var lost = Archive(database, NewKey());
        using (var locked = lost.CreateClient())
        {
            await Browser.LogInAsCuratorAsync(locked);
            Assert.False(await Browser.IsLoggedInAsync(locked));
        }
        using (var scope = lost.Services.CreateScope())
            Assert.Equal(0, await TwoFactorReset.RunAsync(scope.ServiceProvider, CommonageAppFactory.AdminEmail, TextWriter.Null));
        using var back = lost.CreateClient();
        await Browser.LogInAsCuratorAsync(back);

        Assert.True(await Browser.IsLoggedInAsync(back));
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("c2hvcnQ=")]
    public void A_key_that_cannot_be_used_stops_the_app_with_a_clear_message(string key)
    {
        using var archive = app.WithWebHostBuilder(b => b.UseSetting(LoginKeys.Setting, key));

        var exception = Record.Exception(() => archive.Services);

        Assert.NotNull(exception);
        var messages = string.Join(" ", Chain(exception));
        Assert.Contains("openssl rand -base64 32", messages);
    }

    private static IEnumerable<string> Chain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            yield return exception.Message;
    }

    [Fact]
    public async Task On_a_server_without_a_key_admins_are_told()
    {
        var database = await NewDatabaseAsync();
        await using var without = Archive(database, null, "Production");
        using var admin = without.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await Browser.LogInAsCuratorAsync(admin);
        var notice = await admin.GetStringAsync("/Admin");

        await using var with = Archive(database, NewKey(), "Production");
        using var admin2 = with.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await Browser.LogInAsCuratorAsync(admin2);

        Assert.Contains("Whoever runs the server should set COMMONAGE_LOGIN_KEY", notice);
        Assert.DoesNotContain("COMMONAGE_LOGIN_KEY", await admin2.GetStringAsync("/Admin"));
    }
}
