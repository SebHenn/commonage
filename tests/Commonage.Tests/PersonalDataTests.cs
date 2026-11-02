using System.Net;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>What curators do when a contributor asks what the archive holds about them, or asks for it to be removed.</summary>
[Collection(nameof(AppCollection))]
public class PersonalDataTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Curators_find_a_persons_contributions_by_name_or_email_address()
    {
        var email = $"find-{Guid.NewGuid():N}@example.org";
        var first = await app.CreateContributionAsync(ItemStatus.Published, name: "Findable Person", email: email);
        var second = await app.CreateContributionAsync(ItemStatus.Submitted, name: "Findable Person", email: null);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var byEmail = await curator.GetStringAsync($"/Admin/Contributors?q={email[..20].ToUpperInvariant()}");
        var byName = await curator.GetStringAsync("/Admin/Contributors?q=findable%20person");
        var wildcard = await curator.GetStringAsync("/Admin/Contributors?q=%25");

        Assert.Contains(first.Title, byEmail);
        Assert.DoesNotContain(second.Title, byEmail);
        Assert.Contains(first.Title, byName);
        Assert.Contains(second.Title, byName);
        Assert.Contains("No contributions match", wildcard);
    }

    [Fact]
    public async Task Removing_a_contributors_details_keeps_the_item_but_forgets_who_sent_it()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published, name: "Forget Me", email: "forget@example.org");
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        using var visitor = app.CreateClient();
        var page = $"/Admin/Items/Edit/{item.Id}";
        var creditedBefore = await visitor.GetStringAsync($"/items/{item.Id}");

        await Browser.PostFormAsync(curator, $"{page}?handler=RemoveContributorDetails", new(), page);

        var contribution = await app.WithDbAsync(db => db.Contributions.SingleAsync(c => c.ArchiveItemId == item.Id));
        Assert.Contains("Contributed by Forget Me", creditedBefore);
        Assert.DoesNotContain("Forget Me", await visitor.GetStringAsync($"/items/{item.Id}"));
        Assert.Equal((null, null, false), (contribution.ContributorName, contribution.ContributorEmail, contribution.ShowName));
        Assert.NotNull(contribution.PersonalDetailsRemovedAt);
        Assert.Equal("Heinz Muster", contribution.RightsHolder);
        Assert.Contains("Name and email address removed", await curator.GetStringAsync(page));
        Assert.DoesNotContain(item.Title, await curator.GetStringAsync("/Admin/Contributors?q=forget"));
    }

    [Fact]
    public async Task A_withdrawn_contribution_is_hidden_and_cannot_be_published_again()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        using var visitor = app.CreateClient();
        var page = $"/Admin/Items/Edit/{item.Id}";

        await Browser.PostFormAsync(curator, $"{page}?handler=Withdraw", new(), page);
        var publishAgain = await Browser.SendFormAsync(curator, page, new()
        {
            ["Input.Title"] = item.Title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["command"] = "publish",
        });

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, publishAgain.StatusCode);
        Assert.Equal(ItemStatus.Withdrawn, await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Status).SingleAsync()));
        Assert.DoesNotContain("value=\"publish\"", await curator.GetStringAsync(page));
    }

    [Fact]
    public async Task Deleting_a_published_item_removes_its_page_and_all_its_files()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published);
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var page = $"/Admin/Items/Edit/{item.Id}";
        await Browser.UploadAsync(curator, page, "Upload", "flood.jpg", TestData.Photo);
        var file = await app.WithDbAsync(db => db.MediaFiles.SingleAsync(f => f.ArchiveItemId == item.Id));

        await Browser.PostFormAsync(curator, $"{page}?handler=Delete", new(), page);

        using var visitor = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
        Assert.False(await app.WithDbAsync(db => db.Contributions.AnyAsync(c => c.ArchiveItemId == item.Id)));
        var storage = app.Services.GetRequiredService<IFileStorage>();
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead(file.OriginalKey));
    }
}
