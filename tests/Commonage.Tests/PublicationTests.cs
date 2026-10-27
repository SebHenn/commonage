using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public partial class PublicationTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Visitors_see_published_items_but_never_drafts()
    {
        var (collectionId, draftId, publishedId) = await SeedCollectionWithDraftAndPublishedItemAsync();
        using var visitor = app.CreateClient();

        var draftPage = await visitor.GetAsync($"/items/{draftId}");
        var publishedPage = await visitor.GetAsync($"/items/{publishedId}");
        var collectionPage = await visitor.GetStringAsync($"/collections/{collectionId}");

        Assert.Equal(HttpStatusCode.NotFound, draftPage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishedPage.StatusCode);
        Assert.Contains("Published letter", collectionPage);
        Assert.DoesNotContain("Draft letter", collectionPage);
    }

    [Fact]
    public async Task Curator_pages_require_login()
    {
        using var visitor = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await visitor.GetAsync("/Admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task A_curator_can_create_and_publish_an_item_that_visitors_then_see()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var collectionTitle = $"Mill Street {Guid.NewGuid():N}";
        await Browser.PostFormAsync(curator, "/Admin/Collections/Edit", new() { ["Input.Title"] = collectionTitle });
        var collectionId = await FindCollectionIdAsync(collectionTitle);

        var itemPage = await Browser.PostFormAsync(curator, "/Admin/Items/Edit", new()
        {
            ["Input.Title"] = "The mill in winter",
            ["Input.CollectionId"] = collectionId.ToString(),
            ["command"] = "publish",
        });

        var itemId = int.Parse(ItemIdInUrl().Match(itemPage.RequestMessage!.RequestUri!.AbsolutePath).Groups[1].Value);
        using var visitor = app.CreateClient();
        var publicPage = await visitor.GetStringAsync($"/items/{itemId}");
        Assert.Contains("The mill in winter", publicPage);
    }

    private async Task<(int CollectionId, int DraftId, int PublishedId)> SeedCollectionWithDraftAndPublishedItemAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = "Letters", CreatedAt = now };
        var draft = new ArchiveItem { Title = "Draft letter", Collection = collection, CreatedAt = now, UpdatedAt = now };
        var published = new ArchiveItem { Title = "Published letter", Collection = collection, CreatedAt = now, UpdatedAt = now };
        published.Publish(now);
        db.AddRange(collection, draft, published);
        await db.SaveChangesAsync();
        return (collection.Id, draft.Id, published.Id);
    }

    private async Task<int> FindCollectionIdAsync(string title)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        return db.Collections.Single(c => c.Title == title).Id;
    }

    [GeneratedRegex(@"/Admin/Items/Edit/(\d+)")]
    private static partial Regex ItemIdInUrl();
}
