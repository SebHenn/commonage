using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ActivityLogTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Curators_actions_are_logged_with_their_name_and_kept_after_the_item_is_deleted()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, curatorEmail, _) = await app.CreateAccountAsync(admin);
        var contribution = await app.CreateContributionAsync(ItemStatus.Submitted);
        var page = $"/Admin/Items/Edit/{contribution.Id}";

        await Browser.PostFormAsync(curator, page, new()
        {
            ["Input.Title"] = contribution.Title,
            ["Input.CollectionId"] = contribution.CollectionId.ToString(),
            ["command"] = "publish",
        });
        var history = await curator.GetStringAsync(page);
        await Browser.PostFormAsync(curator, $"{page}?handler=Delete", new(), page);
        await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=DeleteItem&id={contribution.Id}", new(), "/Admin/Bin");

        var entries = await app.WithDbAsync(db => db.AuditEntries
            .Where(e => e.Subject == contribution.Title)
            .OrderBy(e => e.Id)
            .ToListAsync());
        Assert.Equal([AuditAction.ItemChanged, AuditAction.ItemPublished, AuditAction.ItemMovedToBin, AuditAction.ItemDeleted], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(curatorEmail, e.UserName));
        Assert.All(entries, e => Assert.Null(e.ArchiveItemId));
        Assert.Contains("Published the item", history);
        Assert.Contains($"Deleted the item for good: {contribution.Title}", await admin.GetStringAsync("/Admin/Activity"));
        curator.Dispose();
    }

    [Fact]
    public async Task A_contribution_is_logged_without_the_contributors_details()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var collectionId = await app.WithDbAsync(async db =>
        {
            var collection = new Collection { Title = TestData.Unique("Contributions"), CreatedAt = DateTime.UtcNow };
            db.Add(collection);
            await db.SaveChangesAsync();
            return collection.Id;
        });
        var linkTitle = TestData.Unique("Share your photos");
        await Browser.PostFormAsync(curator, "/Admin/Contributions/Edit", new()
        {
            ["Input.Title"] = linkTitle,
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.IsActive"] = "true",
        });
        var token = await app.WithDbAsync(db => db.ContributionLinks.Where(l => l.Title == linkTitle).Select(l => l.Token).SingleAsync());
        using var visitor = app.CreateClient();
        var title = TestData.Unique("School photo");

        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new()
        {
            ["Input.Title"] = title,
            ["Input.Name"] = "Grete Private",
            ["Input.Email"] = "grete@example.org",
            ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
            ["Input.Consent"] = "true",
        });

        var entry = await app.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.Subject == title));
        Assert.Equal(AuditAction.ContributionReceived, entry.Action);
        Assert.Null(entry.UserName);
        Assert.NotNull(entry.ArchiveItemId);
        Assert.False(await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Subject!.Contains("Grete") || e.Subject!.Contains("grete@"))));
        Assert.True(await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == AuditAction.ContributionLinkCreated && e.Subject == linkTitle)));
    }
}
