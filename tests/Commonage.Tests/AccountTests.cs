using System.Net;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class AccountTests(CommonageAppFactory app)
{
    [Fact]
    public async Task A_new_curator_sets_a_password_with_the_setup_link_and_can_manage_items_but_not_accounts()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        var (curator, _, email, _) = await app.CreateAccountAsync(admin);

        Assert.True(await Browser.IsLoggedInAsync(curator));
        Assert.Equal(HttpStatusCode.OK, (await curator.GetAsync("/Admin/Contributors")).StatusCode);
        foreach (var adminPage in new[] { "/Admin/Users", "/Admin/Export", "/Admin/Activity" })
            Assert.Equal(HttpStatusCode.Forbidden, (await curator.GetAsync(adminPage)).StatusCode);
        Assert.DoesNotContain("/Admin/Users", await curator.GetStringAsync("/Admin"));
        Assert.Contains(email, await admin.GetStringAsync("/Admin/Users"));
        curator.Dispose();
    }

    [Fact]
    public async Task A_setup_link_works_once()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var created = await Browser.PostFormAsync(admin, "/Admin/Users/Edit?handler=Create", new()
        {
            ["Input.Email"] = $"once-{Guid.NewGuid():N}@example.org",
            ["Input.Role"] = "Curator",
        }, pageUrl: "/Admin/Users/Edit");
        var setupLink = TestData.SetupLinkIn(await created.Content.ReadAsStringAsync());
        using var person = app.CreateClient();
        var fields = new Dictionary<string, string> { ["Input.Password"] = "First-password-1", ["Input.ConfirmPassword"] = "First-password-1" };

        var first = await Browser.PostFormAsync(person, setupLink, new(fields));
        var second = await Browser.SendFormAsync(person, setupLink, new(fields) { ["Input.Password"] = "Second-password-1", ["Input.ConfirmPassword"] = "Second-password-1" },
            pageUrl: "/Account/Login");

        Assert.Contains("Your password is set", await first.Content.ReadAsStringAsync());
        Assert.Contains("This link is no longer valid", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_disabled_account_cannot_log_in_until_it_is_enabled_again()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, id, email, password) = await app.CreateAccountAsync(admin);
        curator.Dispose();

        await Browser.PostFormAsync(admin, $"/Admin/Users/Edit/{id}?handler=Disable", new(), pageUrl: $"/Admin/Users/Edit/{id}");
        using var attempt = app.CreateClient();
        var refused = await Browser.SendFormAsync(attempt, "/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        await Browser.PostFormAsync(admin, $"/Admin/Users/Edit/{id}?handler=Enable", new(), pageUrl: $"/Admin/Users/Edit/{id}");
        await Browser.LogInAsync(attempt, email, password);

        Assert.Contains("This account is disabled", await refused.Content.ReadAsStringAsync());
        Assert.True(await Browser.IsLoggedInAsync(attempt));
        var log = await admin.GetStringAsync("/Admin/Activity");
        Assert.Contains($"Disabled the account: {email}", log);
        Assert.Contains($"Enabled the account: {email}", log);
    }

    [Fact]
    public async Task Admins_cannot_lock_themselves_out()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var ownId = await app.WithDbAsync(db => db.Users.Where(u => u.Email == CommonageAppFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        var page = $"/Admin/Users/Edit/{ownId}";

        var demote = await Browser.SendFormAsync(admin, $"{page}?handler=Role", new() { ["Input.Role"] = "Curator" }, page);
        var disable = await Browser.SendFormAsync(admin, $"{page}?handler=Disable", new(), page);
        var delete = await Browser.SendFormAsync(admin, $"{page}?handler=Delete", new(), page);

        Assert.All([demote, disable, delete], response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Contains("This is your own account", await admin.GetStringAsync(page));
    }

    [Fact]
    public async Task An_admin_can_promote_a_curator_and_delete_an_account()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, id, email, _) = await app.CreateAccountAsync(admin);
        var page = $"/Admin/Users/Edit/{id}";

        await Browser.PostFormAsync(admin, $"{page}?handler=Role", new() { ["Input.Role"] = "Admin" }, page);
        var roles = await app.WithDbAsync(db => db.UserRoles.Where(r => r.UserId == id).Join(db.Roles, r => r.RoleId, r => r.Id, (_, r) => r.Name).ToListAsync());
        await Browser.PostFormAsync(admin, $"{page}?handler=Delete", new(), page);

        Assert.Equal(["Admin"], roles);
        Assert.False(await app.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == id)));
        Assert.True(await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == AuditAction.AccountDeleted && e.Subject == email)));
        curator.Dispose();
    }

    [Fact]
    public async Task Curators_change_their_own_password()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, email, password) = await app.CreateAccountAsync(admin);

        var wrong = await Browser.SendFormAsync(curator, "/Account/Password", new()
        {
            ["Input.CurrentPassword"] = "not-my-password",
            ["Input.Password"] = "Changed-password-1",
            ["Input.ConfirmPassword"] = "Changed-password-1",
        });
        await Browser.PostFormAsync(curator, "/Account/Password", new()
        {
            ["Input.CurrentPassword"] = password,
            ["Input.Password"] = "Changed-password-1",
            ["Input.ConfirmPassword"] = "Changed-password-1",
        });
        using var again = app.CreateClient();
        await Browser.LogInAsync(again, email, "Changed-password-1");

        Assert.Contains("The current password is incorrect", await wrong.Content.ReadAsStringAsync());
        Assert.True(await Browser.IsLoggedInAsync(again));
        curator.Dispose();
    }
}
