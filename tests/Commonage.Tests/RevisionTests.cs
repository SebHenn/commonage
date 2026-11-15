using System.Net;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class RevisionTests(CommonageAppFactory app)
{
    private async Task<ArchiveItem> DraftAsync(string description)
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == item.Id);
            stored.Status = ItemStatus.Draft;
            stored.Description = description;
            return await db.SaveChangesAsync();
        });
        return item;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, ArchiveItem item, string description) =>
        Browser.SendFormAsync(client, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = item.Title, ["Input.CollectionId"] = item.CollectionId.ToString(), ["Input.Description"] = description,
        });

    private Task<List<ItemRevision>> RevisionsAsync(int itemId) =>
        app.WithDbAsync(db => db.ItemRevisions.AsNoTracking().Where(r => r.ArchiveItemId == itemId).OrderBy(r => r.Id).ToListAsync());

    [Fact]
    public async Task Changing_a_text_keeps_the_earlier_one_and_who_changed_it_and_a_curator_can_put_it_back()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await DraftAsync("The first text");

        await SaveAsync(curator, item, "The first text");
        Assert.Empty(await RevisionsAsync(item.Id));

        await SaveAsync(curator, item, "The second text");
        var revision = Assert.Single(await RevisionsAsync(item.Id));
        Assert.Equal(RevisionField.Description, revision.Field);
        Assert.Equal("The first text", revision.OldValue);
        Assert.Equal(CommonageAppFactory.AdminEmail, revision.UserName);
        Assert.Contains("The first text", await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}"));

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}?handler=RestoreText&revisionId={revision.Id}", new(), $"/Admin/Items/Edit/{item.Id}");

        var stored = await app.WithDbAsync(db => db.ArchiveItems.AsNoTracking().SingleAsync(i => i.Id == item.Id));
        Assert.Equal("The first text", stored.Description);
        // What was there before the restore is kept in turn, so nothing is lost by going back.
        Assert.Equal(["The first text", "The second text"], (await RevisionsAsync(item.Id)).Select(r => r.OldValue));
    }

    [Fact]
    public async Task A_helper_sees_earlier_texts_but_cannot_put_one_back()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var item = await DraftAsync("Before");
        await SaveAsync(admin, item, "After");
        var revision = Assert.Single(await RevisionsAsync(item.Id));

        Assert.Contains("Before", await helper.GetStringAsync($"/Admin/Items/Edit/{item.Id}"));
        var attempt = await Browser.SendFormAsync(helper, $"/Admin/Items/Edit/{item.Id}?handler=RestoreText&revisionId={revision.Id}", new(), $"/Admin/Items/Edit/{item.Id}");

        Assert.Contains(attempt.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.Found });
        var stored = await app.WithDbAsync(db => db.ArchiveItems.AsNoTracking().SingleAsync(i => i.Id == item.Id));
        Assert.Equal("After", stored.Description);
    }

    [Fact]
    public async Task An_earlier_text_is_never_on_a_public_page_and_a_binned_item_cannot_be_restored_into()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await DraftAsync("Secret first draft words");
        await SaveAsync(curator, item, "Public words");
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == item.Id)).Publish(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        var revision = Assert.Single(await RevisionsAsync(item.Id));

        using var visitor = app.CreateClient();
        var page = await visitor.GetStringAsync($"/items/{item.Id}");
        Assert.Contains("Public words", page);
        Assert.DoesNotContain("Secret first draft words", page);
        Assert.DoesNotContain($"/items/{item.Id}", await visitor.GetStringAsync("/search?q=Secret+first+draft+words"));

        var editPage = $"/Admin/Items/Edit/{item.Id}";
        await Browser.PostFormAsync(curator, $"{editPage}?handler=Delete", new(), editPage);
        var restore = await Browser.SendFormAsync(curator, $"{editPage}?handler=RestoreText&revisionId={revision.Id}", new(), editPage);
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
    }

    [Fact]
    public async Task Setting_a_date_for_many_items_keeps_the_date_they_had()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await DraftAsync("Words");
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == item.Id)).TrySetDate("1950");
            return await db.SaveChangesAsync();
        });
        var page = await curator.GetStringAsync("/Admin");

        await curator.PostAsync("/Admin/Items/Bulk?handler=Apply", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Token(page), ["Action"] = "SetDate", ["Date"] = "1960", ["Ids"] = item.Id.ToString(),
        }));

        var revision = Assert.Single(await RevisionsAsync(item.Id));
        Assert.Equal((RevisionField.Date, "1950"), (revision.Field, revision.OldValue));
    }

    [Fact]
    public async Task A_revision_is_not_part_of_the_item_and_goes_when_the_item_does()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var item = await DraftAsync("Text before");
        await SaveAsync(curator, item, "Text after");
        Assert.Single(await RevisionsAsync(item.Id));

        await app.WithDbAsync(async db =>
        {
            db.ArchiveItems.Remove(await db.ArchiveItems.IgnoreQueryFilters().SingleAsync(i => i.Id == item.Id));
            return await db.SaveChangesAsync();
        });

        Assert.Empty(await RevisionsAsync(item.Id));
    }
}
