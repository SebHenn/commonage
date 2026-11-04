using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ContributionTests(CommonageAppFactory app)
{
    private static readonly byte[] Photo = File.ReadAllBytes(Path.Combine("Fixtures", "photo-with-gps.jpg"));

    [Fact]
    public async Task A_contribution_stays_private_until_a_curator_publishes_it()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var collectionId = await CreateCollectionAsync();
        var linkTitle = $"Share your photos {Guid.NewGuid():N}";
        await Browser.PostFormAsync(curator, "/Admin/Contributions/Edit", new()
        {
            ["Input.Title"] = linkTitle,
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.IsActive"] = "true",
        });
        var token = await WithDbAsync(db => db.ContributionLinks.Where(l => l.Title == linkTitle).Select(l => l.Token).SingleAsync());
        using var visitor = app.CreateClient();

        var form = await visitor.GetStringAsync($"/contribute/{token}");
        var fields = ValidFields("Flood in Mill Street");
        fields["Input.Date"] = "um 1962";
        fields["Input.Email"] = "contributor@example.org";
        var sent = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", fields, ("flood.jpg", Photo));

        Assert.Contains(linkTitle, form);
        Assert.Contains("Thank you", await sent.Content.ReadAsStringAsync());
        var item = await WithDbAsync(db => db.ArchiveItems
            .Include(i => i.Contribution)
            .Include(i => i.MediaFiles)
            .SingleAsync(i => i.Contribution!.ContributionLink!.Token == token));
        Assert.Equal(ItemStatus.Submitted, item.Status);
        Assert.Equal(collectionId, item.CollectionId);
        Assert.Equal("um 1962", item.DateText);
        Assert.Single(item.MediaFiles);
        Assert.Equal(("Ada Contributor", "contributor@example.org", RightsStatement.CreatedByContributor),
            (item.Contribution!.ContributorName, item.Contribution.ContributorEmail, item.Contribution.Rights));
        Assert.Contains("publish it on this website", item.Contribution.ConsentText);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{item.MediaFiles[0].Id}/preview")).StatusCode);
        Assert.DoesNotContain("Flood in Mill Street", await visitor.GetStringAsync($"/collections/{collectionId}"));

        var waiting = await curator.GetStringAsync("/Admin?status=Submitted");
        var review = await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}");
        Assert.Contains("Flood in Mill Street", waiting);
        Assert.Contains("Ada Contributor", review);
        Assert.Contains("contributor@example.org", review);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = "Flood in Mill Street",
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.Date"] = "um 1962",
            ["command"] = "publish",
        });

        var published = await visitor.GetStringAsync($"/items/{item.Id}");
        Assert.Contains("Contributed by Ada Contributor", published);
        Assert.NotNull(await WithDbAsync(db => db.Contributions.Where(c => c.ArchiveItemId == item.Id).Select(c => c.ReviewedAt).SingleAsync()));
    }

    [Fact]
    public async Task Closed_and_unknown_links_show_not_found()
    {
        var closed = await CreateLinkAsync(isActive: false);
        using var visitor = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/contribute/{closed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/contribute/{ContributionLink.NewToken()}")).StatusCode);
    }

    [Fact]
    public async Task Name_rights_and_consent_are_required()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();

        var response = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new() { ["Input.Title"] = "Old school" });

        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Please enter your name.", page);
        Assert.Contains("Please tell us who made it.", page);
        Assert.Contains("Please agree, so that we can include what you send in.", page);
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task A_rejected_contribution_stays_hidden_and_can_be_deleted_with_its_photos()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields("Advertising"), ("spam.jpg", Photo));
        var item = await WithDbAsync(db => db.ArchiveItems.Include(i => i.MediaFiles).SingleAsync(i => i.Contribution!.ContributionLink!.Token == token));
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = "Advertising",
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["command"] = "reject",
        });
        var statusAfterReject = await WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Status).SingleAsync());
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{item.Id}?handler=Delete", [], pageUrl: $"/Admin/Items/Edit/{item.Id}");
        await Browser.PostFormAsync(curator, $"/Admin/Bin?handler=DeleteItem&id={item.Id}", [], pageUrl: "/Admin/Bin");

        Assert.Equal(ItemStatus.Rejected, statusAfterReject);
        Assert.False(await WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == item.Id)));
        Assert.Equal(0, await CountContributionsAsync(token));
        var storage = app.Services.GetRequiredService<IFileStorage>();
        Assert.Throws<FileNotFoundException>(() => storage.OpenRead(item.MediaFiles[0].OriginalKey));
    }

    [Fact]
    public async Task Automated_submissions_are_thanked_but_not_saved()
    {
        var token = await CreateLinkAsync();
        using var visitor = app.CreateClient();
        var honeypot = ValidFields("Cheap watches");
        honeypot["Website"] = "https://spam.example";

        var caughtByHoneypot = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", honeypot);
        await using var patientServer = app.WithWebHostBuilder(b => b.UseSetting("Contributions:MinimumFillSeconds", "60"));
        using var fastVisitor = patientServer.CreateClient();
        var caughtByTimer = await Browser.SendFormWithFilesAsync(fastVisitor, $"/contribute/{token}", ValidFields("Cheap watches"));

        Assert.Contains("Thank you", await caughtByHoneypot.Content.ReadAsStringAsync());
        Assert.Contains("Thank you", await caughtByTimer.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountContributionsAsync(token));
    }

    [Fact]
    public async Task Too_many_submissions_from_one_address_are_refused()
    {
        var token = await CreateLinkAsync();
        await using var strictServer = app.WithWebHostBuilder(b => b.UseSetting("Contributions:MaxSubmissionsPerHour", "2"));
        using var visitor = strictServer.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add((await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ValidFields($"Photo {i}"))).StatusCode);

        Assert.Equal([HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(2, await CountContributionsAsync(token));
    }

    private static Dictionary<string, string> ValidFields(string title) => new()
    {
        ["Input.Title"] = title,
        ["Input.Name"] = "Ada Contributor",
        ["Input.ShowName"] = "true",
        ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
        ["Input.Consent"] = "true",
    };

    private Task<int> CreateCollectionAsync() => WithDbAsync(async db =>
    {
        var collection = new Collection { Title = $"Contributions {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        db.Add(collection);
        await db.SaveChangesAsync();
        return collection.Id;
    });

    private async Task<string> CreateLinkAsync(bool isActive = true)
    {
        var collectionId = await CreateCollectionAsync();
        return await WithDbAsync(async db =>
        {
            var link = new ContributionLink
            {
                Title = "Share your photos",
                CollectionId = collectionId,
                Token = ContributionLink.NewToken(),
                IsActive = isActive,
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(link);
            await db.SaveChangesAsync();
            return link.Token;
        });
    }

    private Task<int> CountContributionsAsync(string token) =>
        WithDbAsync(db => db.Contributions.CountAsync(c => c.ContributionLink!.Token == token));

    private async Task<T> WithDbAsync<T>(Func<CommonageDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<CommonageDbContext>());
    }
}
