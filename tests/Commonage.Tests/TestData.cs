using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>Sets up records and accounts for tests that share one database. Every name is unique, so tests never see each other's data.</summary>
public static partial class TestData
{
    public static readonly byte[] Photo = File.ReadAllBytes(Path.Combine("Fixtures", "photo-with-gps.jpg"));

    public static async Task<T> WithDbAsync<T>(this CommonageAppFactory app, Func<CommonageDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<CommonageDbContext>());
    }

    public static string Unique(string text) => $"{text} {Guid.NewGuid():N}";

    /// <summary>Creates an item that came in through a contribution link, in a collection of its own.</summary>
    public static Task<ArchiveItem> CreateContributionAsync(
        this CommonageAppFactory app, ItemStatus status, string name = "Erika Muster", string? email = "erika@example.org") =>
        app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var item = new ArchiveItem
            {
                Title = Unique("Flood in Mill Street"),
                Collection = new Collection { Title = Unique("Contributions"), CreatedAt = now },
                Status = ItemStatus.Submitted,
                CreatedAt = now,
                UpdatedAt = now,
                Contribution = new Contribution
                {
                    ContributorName = name,
                    ContributorEmail = email,
                    ShowName = true,
                    Rights = RightsStatement.PermissionFromRightsHolder,
                    RightsHolder = "Heinz Muster",
                    ConsentText = "I agree.",
                    SubmittedAt = now,
                },
            };
            if (status == ItemStatus.Published)
                item.Publish(now);
            else if (status == ItemStatus.Rejected)
                item.Reject(now);
            db.ArchiveItems.Add(item);
            await db.SaveChangesAsync();
            return item;
        });

    /// <summary>
    /// Creates an account through the admin pages and sets its password through the setup link, as a new curator would.
    /// Returns a client that is logged in with it.
    /// </summary>
    public static async Task<(HttpClient Client, string Id, string Email, string Password)> CreateAccountAsync(
        this CommonageAppFactory app, HttpClient admin, string role = "Curator")
    {
        var email = $"curator-{Guid.NewGuid():N}@example.org";
        const string password = "Curator-password-1";
        var created = await Browser.PostFormAsync(admin, "/Admin/Users/Edit?handler=Create", new()
        {
            ["Input.Email"] = email,
            ["Input.Role"] = role,
        }, pageUrl: "/Admin/Users/Edit");
        var setupLink = SetupLinkIn(await created.Content.ReadAsStringAsync());

        var client = app.CreateClient();
        await Browser.PostFormAsync(client, setupLink, new()
        {
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        });
        await Browser.LogInAsync(client, email, password);
        var id = await app.WithDbAsync(db => Task.FromResult(db.Users.Single(u => u.Email == email).Id));
        return (client, id, email, password);
    }

    /// <summary>The path and query of the setup link an admin page shows.</summary>
    public static string SetupLinkIn(string page)
    {
        var match = SetupLink().Match(page);
        Assert.True(match.Success, "The page shows no setup link.");
        return new Uri(WebUtility.HtmlDecode(match.Groups[1].Value)).PathAndQuery;
    }

    [GeneratedRegex(@"id=""setup-link""[^>]*value=""([^""]+)""")]
    private static partial Regex SetupLink();
}
